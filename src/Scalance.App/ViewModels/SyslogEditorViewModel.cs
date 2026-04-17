using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.App.ViewModels;

/// <summary>
/// Operator view for the S615 / X-200 Syslog client (manual sec 13.2 pp. 822-825).
/// Lets the user add and remove Syslog destinations — the device CLI model is
/// strictly additive, so this VM holds an in-memory list and issues one
/// `syslogserver …` or `no syslogserver …` command per row on Apply.
///
/// There is no `show events syslogserver` parser yet, so Load is not offered;
/// the list reflects the VM's session-local state. Recording Apply output to
/// the OperationLog is how the operator audits what was sent.
/// </summary>
public sealed partial class SyslogEditorViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    [ObservableProperty] private string? selectedDeviceName;
    [ObservableProperty] private bool featureSupported;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;

    // Fields for the "add a new server" row.
    [ObservableProperty] private string newHost = "";
    [ObservableProperty] private string newPort = "";   // blank = device default (514)
    [ObservableProperty] private bool newUseTls;

    public ObservableCollection<SyslogServerRow> Servers { get; } = new();

    public SyslogEditorViewModel(DeviceOperationsService ops, DeviceSelection selection, OperationLog log)
    {
        _ops = ops;
        _selection = selection;
        _log = log;
        _selection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceSelection.Current))
                RefreshForSelection();
        };
        RefreshForSelection();
    }

    private void RefreshForSelection()
    {
        var d = _selection.Current;
        SelectedDeviceName = d?.Name;
        FeatureSupported = d is not null
            && CapabilityMatrix.Supports(d.Model, DeviceCapability.SyslogClient);
        StatusMessage = d is null ? "未選取設備。"
            : FeatureSupported ? $"就緒 — {d.Name}。新增伺服器後按「套用」推送。"
            : "此設備不支援 Syslog 用戶端功能。";
        AddCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private bool CanUse() => _selection.Current is not null && FeatureSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void Add()
    {
        var host = (NewHost ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            StatusMessage = "請輸入主機 (IPv4 / IPv6 / FQDN)。";
            return;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(NewPort))
        {
            if (!int.TryParse(NewPort.Trim(), out var p) || p < 1 || p > 65535)
            {
                StatusMessage = "Port 需為 1-65535 的整數，或留空以使用預設值 514。";
                return;
            }
            port = p;
        }

        Servers.Add(new SyslogServerRow(host, port, NewUseTls, pendingApply: true));
        NewHost = "";
        NewPort = "";
        NewUseTls = false;
        StatusMessage = $"已新增 {host}（待套用）。";
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task ApplyAsync()
    {
        var d = _selection.Current;
        if (d is null) return;

        // Rows marked pendingApply still need to be pushed to the device.
        var toAdd = Servers.Where(r => r.PendingApply).ToList();
        var toRemove = Servers.Where(r => r.PendingRemove).ToList();
        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            StatusMessage = "沒有待套用的變更。";
            return;
        }

        IsBusy = true;
        StatusMessage = $"正在套用 {toAdd.Count} 筆新增 / {toRemove.Count} 筆刪除…";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            foreach (var row in toRemove)
            {
                var s = new SyslogServer { Host = row.Host, Port = row.Port, UseTls = row.UseTls };
                var r = await driver.RemoveSyslogServerAsync(s);
                _log.Info($"Syslog remove {row.Host}: {(r.Success ? "OK" : r.Message)}");
                if (r.Success) Servers.Remove(row);
            }
            foreach (var row in toAdd)
            {
                var s = new SyslogServer { Host = row.Host, Port = row.Port, UseTls = row.UseTls };
                var r = await driver.AddSyslogServerAsync(s);
                _log.Info($"Syslog add {row.Host}{(row.Port is int p ? $":{p}" : "")}{(row.UseTls ? " tls" : "")}: {(r.Success ? "OK" : r.Message)}");
                if (r.Success) row.PendingApply = false;
            }
            DryRunPreview.LogIfDryRun(driver, _log, "Syslog");
            StatusMessage = "套用完成。";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Remove(SyslogServerRow? row)
    {
        if (row is null) return;
        if (row.PendingApply)
        {
            // Never pushed to device — just drop it.
            Servers.Remove(row);
        }
        else
        {
            // Schedule a `no syslogserver` on the next Apply.
            row.PendingRemove = true;
        }
        StatusMessage = "變更已排入待套用。";
    }

    partial void OnIsBusyChanged(bool value)
    {
        AddCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"Syslog: {value}");
    }
}

public sealed partial class SyslogServerRow : ObservableObject
{
    [ObservableProperty] private string host;
    [ObservableProperty] private int? port;
    [ObservableProperty] private bool useTls;
    [ObservableProperty] private bool pendingApply;
    [ObservableProperty] private bool pendingRemove;

    public SyslogServerRow(string host, int? port, bool useTls, bool pendingApply)
    {
        this.host = host;
        this.port = port;
        this.useTls = useTls;
        this.pendingApply = pendingApply;
    }

    public string StateDisplay =>
        PendingRemove ? "待刪除" : (PendingApply ? "待新增" : "已套用");
}
