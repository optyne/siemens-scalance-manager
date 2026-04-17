using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Abstractions;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;
using Scalance.Data;

namespace Scalance.App.ViewModels;

public enum BulkAction
{
    ChangeAdminPassword,
    SetDns,
    AddVlan
}

public sealed partial class BulkOpsViewModel : ObservableObject
{
    private readonly DeviceRepository _repo;
    private readonly DeviceOperationsService _ops;
    private readonly OperationLog _log;

    public ObservableCollection<BulkDeviceRow> Devices { get; } = new();
    public ObservableCollection<BulkResultRow> Results { get; } = new();
    public IReadOnlyList<BulkAction> AvailableActions { get; } =
        new[] { BulkAction.ChangeAdminPassword, BulkAction.SetDns, BulkAction.AddVlan };

    [ObservableProperty] private BulkAction selectedAction = BulkAction.SetDns;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private int maxParallel = 4;

    // Password action
    [ObservableProperty] private string adminUsername = "admin";
    [ObservableProperty] private string newPassword = "";
    [ObservableProperty] private string confirmPassword = "";

    // DNS action
    [ObservableProperty] private string dns1 = "";
    [ObservableProperty] private string dns2 = "";
    [ObservableProperty] private string domainName = "";

    // VLAN action
    [ObservableProperty] private int vlanId = 10;
    [ObservableProperty] private string vlanName = "";
    [ObservableProperty] private string vlanPorts = "";

    public BulkOpsViewModel(DeviceRepository repo, DeviceOperationsService ops, OperationLog log)
    {
        _repo = repo;
        _ops = ops;
        _log = log;
        _ = ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        Devices.Clear();
        foreach (var d in await _repo.ListAsync())
            Devices.Add(new BulkDeviceRow(d));
        StatusMessage = $"已載入 {Devices.Count} 台設備。";
    }

    [RelayCommand] private void SelectAll() { foreach (var d in Devices) d.IsSelected = true; }
    [RelayCommand] private void SelectNone() { foreach (var d in Devices) d.IsSelected = false; }

    private bool CanApply() => !IsBusy && Devices.Any(d => d.IsSelected);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var selected = Devices.Where(d => d.IsSelected).Select(d => d.Model).ToList();
        if (selected.Count == 0) { StatusMessage = "請先勾選至少一台設備。"; return; }

        var required = SelectedAction switch
        {
            BulkAction.ChangeAdminPassword => DeviceCapability.AdminPasswordChange,
            BulkAction.SetDns => DeviceCapability.DnsClient,
            BulkAction.AddVlan => DeviceCapability.VlanManagement,
            _ => DeviceCapability.None
        };
        var eligible = selected.Where(d => CapabilityMatrix.Supports(d.Model, required)).ToList();
        var skipped = selected.Count - eligible.Count;

        if (SelectedAction == BulkAction.ChangeAdminPassword)
        {
            if (string.IsNullOrEmpty(NewPassword) || NewPassword != ConfirmPassword)
            { StatusMessage = "密碼為空或兩次不一致。"; return; }
        }

        IsBusy = true;
        Results.Clear();
        StatusMessage = $"執行中 … 目標 {eligible.Count} 台" + (skipped > 0 ? $"（跳過 {skipped} 台不支援）" : "");

        var progress = new Progress<BulkDeviceResult>(r =>
        {
            Results.Add(new BulkResultRow(r));
            _log.Info($"Bulk {SelectedAction} [{r.DeviceName}]: {(r.Success ? "OK" : "FAIL")} — {r.Message}");
        });

        Func<IDeviceDriver, Device, CancellationToken, Task<OperationResult>> action = SelectedAction switch
        {
            BulkAction.ChangeAdminPassword => async (drv, dev, ct) =>
            {
                var r = await drv.SetAdminPasswordAsync(AdminUsername, NewPassword, ct);
                DryRunPreview.LogIfDryRun(drv, _log, $"Pwd[{dev.Name}]");
                return r;
            },
            BulkAction.SetDns => async (drv, dev, ct) =>
            {
                var cfg = new DnsConfig
                {
                    Servers = new[] { Dns1, Dns2 }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList(),
                    DomainName = string.IsNullOrWhiteSpace(DomainName) ? null : DomainName.Trim()
                };
                var r = await drv.SetDnsAsync(cfg, ct);
                DryRunPreview.LogIfDryRun(drv, _log, $"DNS[{dev.Name}]");
                return r;
            },
            BulkAction.AddVlan => async (drv, dev, ct) =>
            {
                var existing = await drv.GetVlansAsync(ct);
                var list = existing.Value?.ToList() ?? new List<Vlan>();
                if (list.Any(v => v.Id == VlanId))
                    return OperationResult.Fail($"VLAN {VlanId} 已存在。");
                var ports = VlanPorts.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.TryParse(p.Trim(), out var i) ? (int?)i : null)
                    .Where(i => i.HasValue)
                    .Select(i => new VlanPortMembership(i!.Value, VlanMemberMode.Tagged))
                    .ToList();
                list.Add(new Vlan
                {
                    Id = VlanId,
                    Name = string.IsNullOrWhiteSpace(VlanName) ? $"VLAN{VlanId}" : VlanName,
                    Ports = ports
                });
                var r = await drv.SetVlansAsync(list, ct);
                DryRunPreview.LogIfDryRun(drv, _log, $"VLAN[{dev.Name}]");
                return r;
            },
            _ => (_, _, _) => Task.FromResult(OperationResult.Fail("Unknown action."))
        };

        try
        {
            var all = await _ops.BulkApplyAsync(eligible, action, MaxParallel, progress);
            int ok = all.Count(r => r.Success), fail = all.Count - ok;
            StatusMessage = $"完成：成功 {ok} / 失敗 {fail}" + (skipped > 0 ? $" / 跳過 {skipped}" : "");
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    partial void OnIsBusyChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();
}

public sealed partial class BulkDeviceRow : ObservableObject
{
    public Device Model { get; }
    [ObservableProperty] private bool isSelected;
    public string Name => Model.Name;
    public string Host => Model.Host;
    public string ModelName => Model.Model.ToString();
    public BulkDeviceRow(Device model) { Model = model; }
}

public sealed class BulkResultRow
{
    public string DeviceName { get; }
    public string Status { get; }
    public string Message { get; }
    public string Elapsed { get; }
    public BulkResultRow(BulkDeviceResult r)
    {
        DeviceName = r.DeviceName;
        Status = r.Success ? "成功" : "失敗";
        Message = r.Message ?? "";
        Elapsed = $"{r.Elapsed.TotalSeconds:F1}s";
    }
}
