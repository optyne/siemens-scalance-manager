using System.Net.Sockets;
using Scalance.Core.Abstractions;
using Scalance.Core.Models;
using Scalance.Drivers;

namespace Scalance.App.Services;

public sealed class DeviceOperationsService
{
    private readonly IDeviceDriverFactory _factory;
    private readonly ICredentialStore _credentials;

    /// <summary>
    /// When true (the default), newly opened CLI drivers operate in DryRun mode:
    /// inferred-syntax writes (VLAN / Interface / VPN) build the planned command
    /// list without executing it. NTP is always executed (validated). Toggled
    /// from the UI via MainViewModel.DryRun.
    /// </summary>
    public bool DryRun { get; set; } = true;

    public DeviceOperationsService(IDeviceDriverFactory factory, ICredentialStore credentials)
    {
        _factory = factory;
        _credentials = credentials;
    }

    public async Task<OperationResult<DeviceStatus>> TestAndFetchStatusAsync(Device device, CancellationToken ct = default)
    {
        var reachable = await QuickReachabilityCheckAsync(device, ct);
        if (!reachable)
            return OperationResult<DeviceStatus>.Fail($"設備 {device.Host}:{device.SshPort} 無法連線（SSH 埠不通）。請檢查網路、IP、或設備電源。");

        var credential = device.CredentialId.HasValue
            ? await _credentials.GetAsync(device.CredentialId.Value, ct) ?? Credential.Empty
            : Credential.Empty;

        await using var driver = _factory.Create(device.Model);
        ApplyDryRun(driver);
        var connect = await driver.ConnectAsync(device, credential, ct);
        if (!connect.Success)
            return OperationResult<DeviceStatus>.Fail(connect.Message ?? "Connect failed.");
        return await driver.GetStatusAsync(ct);
    }

    public async Task<IDeviceDriver> OpenAsync(Device device, CancellationToken ct = default)
    {
        var reachable = await QuickReachabilityCheckAsync(device, ct);
        if (!reachable)
            throw new InvalidOperationException(
                $"設備 {device.Host}:{device.SshPort} 無法連線（SSH 埠不通，1.5 秒內無回應）。\n" +
                "可能原因：\n" +
                "• 本機 IP 與設備不在同一子網\n" +
                "• 設備 SSH 未啟用（預設應啟用）\n" +
                "• 設備斷電或網線未接\n" +
                "• 防火牆阻擋\n" +
                "請到「連線狀態」分頁檢查可達性，或透過探索分頁用 DCP 變更設備 IP。");

        var credential = device.CredentialId.HasValue
            ? await _credentials.GetAsync(device.CredentialId.Value, ct) ?? Credential.Empty
            : Credential.Empty;
        var driver = _factory.Create(device.Model);
        ApplyDryRun(driver);
        var res = await driver.ConnectAsync(device, credential, ct);
        if (!res.Success)
        {
            await driver.DisposeAsync();
            throw new InvalidOperationException(res.Message ?? "Connect failed.");
        }
        return driver;
    }

    /// <summary>
    /// 快速測試 SSH 埠（1.5 秒逾時）— S615 / X-series 實際讀寫都走 SSH，所以只測
    /// SSH 埠。WBM 或 SNMP 通但 SSH 不通的情況下，後續操作仍會失敗，早點失敗好過等 10 秒。
    /// </summary>
    private static async Task<bool> QuickReachabilityCheckAsync(Device device, CancellationToken ct)
    {
        return await TcpPing(device.Host, device.SshPort, 1500, ct);
    }

    private static async Task<bool> TcpPing(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(timeoutMs, ct);
            var done = await Task.WhenAny(connectTask, timeoutTask);
            return done == connectTask && tcp.Connected;
        }
        catch { return false; }
    }

    private void ApplyDryRun(IDeviceDriver driver)
    {
        if (driver is ScalanceCliDriverBase cli) cli.DryRun = DryRun;
    }

    /// <summary>
    /// Runs <paramref name="action"/> against each device in parallel (bounded by
    /// <paramref name="maxParallel"/>). Each device gets its own driver that is
    /// disposed after its action completes. Failures are captured per-device,
    /// not thrown — the returned list always has one entry per input device.
    /// </summary>
    public async Task<IReadOnlyList<BulkDeviceResult>> BulkApplyAsync(
        IReadOnlyList<Device> devices,
        Func<IDeviceDriver, Device, CancellationToken, Task<OperationResult>> action,
        int maxParallel = 4,
        IProgress<BulkDeviceResult>? progress = null,
        CancellationToken ct = default)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));
        var results = new BulkDeviceResult[devices.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        var tasks = new List<Task>(devices.Count);

        for (int i = 0; i < devices.Count; i++)
        {
            int idx = i;
            var d = devices[idx];
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    IDeviceDriver? driver = null;
                    try
                    {
                        driver = await OpenAsync(d, ct);
                        var r = await action(driver, d, ct);
                        results[idx] = new BulkDeviceResult(d.Id, d.Name, r.Success, r.Message, sw.Elapsed);
                    }
                    catch (Exception ex)
                    {
                        results[idx] = new BulkDeviceResult(d.Id, d.Name, false, ex.Message, sw.Elapsed);
                    }
                    finally
                    {
                        if (driver is not null) await driver.DisposeAsync();
                    }
                    progress?.Report(results[idx]);
                }
                finally { gate.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Changes a device's admin password via SSH-CLI and, on success, writes
    /// the new password back into the encrypted credential store so subsequent
    /// connections keep working. No-op on devices without a stored credential.
    /// </summary>
    public async Task<OperationResult> ChangeAdminPasswordAsync(
        Device device, string username, string newPassword, CancellationToken ct = default)
    {
        await using var driver = await OpenAsync(device, ct);
        var r = await driver.SetAdminPasswordAsync(username, newPassword, ct);
        if (!r.Success) return r;

        if (DryRun) return r; // nothing was actually sent to the device
        if (!device.CredentialId.HasValue) return r;

        var existing = await _credentials.GetAsync(device.CredentialId.Value, ct);
        if (existing is null) return r;
        var updated = existing with { Username = username, Password = newPassword };
        await _credentials.UpdateAsync(device.CredentialId.Value, updated, ct);
        return r;
    }
}

public sealed record BulkDeviceResult(
    Guid DeviceId, string DeviceName, bool Success, string? Message, TimeSpan Elapsed);
