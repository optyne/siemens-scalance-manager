using System.Collections.ObjectModel;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;
using Scalance.Data;

namespace Scalance.App.ViewModels;

public sealed partial class ConnectionStatusViewModel : ObservableObject
{
    private readonly DeviceRepository _repo;
    private readonly DeviceOperationsService _ops;
    private readonly OperationLog _log;

    public ObservableCollection<DeviceConnectionInfo> Devices { get; } = new();

    [ObservableProperty] private DeviceConnectionInfo? selectedDevice;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? connectionGuide;

    public ConnectionStatusViewModel(DeviceRepository repo, DeviceOperationsService ops, OperationLog log)
    {
        _repo = repo;
        _ops = ops;
        _log = log;
    }

    partial void OnSelectedDeviceChanged(DeviceConnectionInfo? value)
    {
        ConnectionGuide = value is null ? null : BuildConnectionGuide(value);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = "正在檢查所有設備的連線狀態...";
        _log.Info("連線狀態：開始檢查所有設備。");

        try
        {
            var devices = await _repo.ListAsync(ct);

            // Run all TCP reachability tests in parallel
            var tasks = devices.Select(d => CheckDeviceAsync(d, ct)).ToArray();
            var results = await Task.WhenAll(tasks);

            Devices.Clear();
            foreach (var info in results)
                Devices.Add(info);

            StatusMessage = $"檢查完成：{results.Count(r => r.IsReachable)} / {results.Length} 台設備可達。";
            _log.Info(StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "檢查已取消。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"檢查失敗：{ex.Message}";
            _log.Error(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task<DeviceConnectionInfo> CheckDeviceAsync(Device device, CancellationToken ct)
    {
        var caps = CapabilityMatrix.For(device.Model);
        string connectionMethod = DetermineConnectionMethod(caps, device);
        int testPort = DetermineTestPort(caps, device);

        bool reachable = await TcpReachableAsync(device.Host, testPort, ct);

        string warning = "";
        if (!device.CredentialId.HasValue)
            warning = "尚未設定認證資訊";
        else if (device.LastSeenAt is null)
            warning = "首次連線需變更密碼";

        return new DeviceConnectionInfo
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            Host = device.Host,
            Model = device.Model.ToString(),
            IsReachable = reachable,
            ConnectionMethod = connectionMethod,
            CurrentVlans = reachable ? "需要連線後載入" : "—",
            Warning = warning,
            LastChecked = DateTimeOffset.Now,
            SshPort = device.SshPort,
            HttpsPort = device.HttpsPort,
            SnmpPort = device.SnmpPort,
            Capabilities = caps
        };
    }

    private static string DetermineConnectionMethod(DeviceCapability caps, Device device)
    {
        var methods = new List<string>();
        if (caps.HasFlag(DeviceCapability.SshCli))
            methods.Add($"SSH:{device.SshPort}");
        if (caps.HasFlag(DeviceCapability.SnmpV2c) || caps.HasFlag(DeviceCapability.SnmpV3))
            methods.Add($"SNMP:{device.SnmpPort}");
        if (caps.HasFlag(DeviceCapability.WbmHttps))
            methods.Add($"HTTPS:{device.HttpsPort}");
        return methods.Count > 0 ? string.Join(", ", methods) : "—";
    }

    private static int DetermineTestPort(DeviceCapability caps, Device device)
    {
        // Prefer SSH for reachability test, then HTTPS, then SNMP
        if (caps.HasFlag(DeviceCapability.SshCli)) return device.SshPort;
        if (caps.HasFlag(DeviceCapability.WbmHttps)) return device.HttpsPort;
        return device.SnmpPort;
    }

    private static async Task<bool> TcpReachableAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(host, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(2000, ct));
            return completed == connectTask && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildConnectionGuide(DeviceConnectionInfo info)
    {
        var lines = new List<string>
        {
            $"\u26a0 你可以透過以下方式連線到此設備 ({info.DeviceName})："
        };

        if (info.Capabilities.HasFlag(DeviceCapability.SshCli))
            lines.Add($"  \u2022 SSH: {info.Host}:{info.SshPort}");
        if (info.Capabilities.HasFlag(DeviceCapability.WbmHttps))
            lines.Add($"  \u2022 HTTPS: {info.Host}:{info.HttpsPort}");
        if (info.Capabilities.HasFlag(DeviceCapability.SnmpV2c) || info.Capabilities.HasFlag(DeviceCapability.SnmpV3))
            lines.Add($"  \u2022 SNMP: {info.Host}:{info.SnmpPort}");

        if (!string.IsNullOrEmpty(info.Warning))
            lines.Add($"\u26a0 {info.Warning}");

        if (!info.IsReachable)
            lines.Add("\u274c \u7121\u6cd5\u9023\u7dda\u5230\u6b64\u8a2d\u5099\uff0c\u8acb\u6aa2\u67e5\u7db2\u8def\u8a2d\u5b9a\u3002");

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed partial class DeviceConnectionInfo : ObservableObject
{
    public Guid DeviceId { get; init; }

    [ObservableProperty] private string deviceName = "";
    [ObservableProperty] private string host = "";
    [ObservableProperty] private string model = "";
    [ObservableProperty] private bool isReachable;
    [ObservableProperty] private string connectionMethod = "";
    [ObservableProperty] private string currentVlans = "";
    [ObservableProperty] private string warning = "";
    [ObservableProperty] private DateTimeOffset lastChecked;

    // Used for building connection guide — not displayed directly in grid
    public int SshPort { get; init; }
    public int HttpsPort { get; init; }
    public int SnmpPort { get; init; }
    public DeviceCapability Capabilities { get; init; }

    public string LastCheckedDisplay => LastChecked.LocalDateTime.ToString("HH:mm:ss");
    public string ReachableDisplay => IsReachable ? "\u2705" : "\u274c";
}
