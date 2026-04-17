using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.App.ViewModels;

/// <summary>
/// Operator view for the three SNMP agent admin knobs
/// (PH_SCALANCE-S615-CLI_76 sec 9.8 pp. 437-452):
/// enabled, version policy, and listen port. Typical hardening use case —
/// flip version to V3Only and/or move port off 161 — was unreachable from
/// the app until this tab was added.
///
/// There is no symmetric "load current values" flow here because the manual
/// does not promise a stable `show snmp` layout for all three fields;
/// operator provides the desired state and the tab pushes each knob.
/// </summary>
public sealed partial class SnmpAgentViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    [ObservableProperty] private string? selectedDeviceName;
    [ObservableProperty] private bool featureSupported;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;

    [ObservableProperty] private bool agentEnabled = true;
    [ObservableProperty] private bool v3Only;            // false = All, true = V3Only
    [ObservableProperty] private string portText = "161";

    public SnmpAgentViewModel(DeviceOperationsService ops, DeviceSelection selection, OperationLog log)
    {
        _ops = ops; _selection = selection; _log = log;
        _selection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceSelection.Current)) RefreshForSelection();
        };
        RefreshForSelection();
    }

    private void RefreshForSelection()
    {
        var d = _selection.Current;
        SelectedDeviceName = d?.Name;
        FeatureSupported = d is not null
            && CapabilityMatrix.Supports(d.Model, DeviceCapability.SshCli);
        StatusMessage = d is null ? "未選取設備。"
            : FeatureSupported ? $"就緒 — {d.Name}。設定後按對應按鈕推送。"
            : "此設備不支援 SNMP 代理設定（需要 SSH-CLI）。";
        NotifyAll();
    }

    private bool CanUse() => _selection.Current is not null && FeatureSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task ApplyEnabledAsync()
    {
        var d = _selection.Current; if (d is null) return;
        IsBusy = true; StatusMessage = AgentEnabled ? "啟用 snmpagent…" : "停用 snmpagent…";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.SetSnmpAgentEnabledAsync(AgentEnabled);
            DryRunPreview.LogIfDryRun(driver, _log, "SNMP enable");
            StatusMessage = r.Success ? "已套用。" : $"失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task ApplyVersionAsync()
    {
        var d = _selection.Current; if (d is null) return;
        var policy = V3Only ? SnmpAgentVersionPolicy.V3Only : SnmpAgentVersionPolicy.All;
        IsBusy = true; StatusMessage = $"套用 snmp agent version {(V3Only ? "v3only" : "all")}…";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.SetSnmpAgentVersionAsync(policy);
            DryRunPreview.LogIfDryRun(driver, _log, "SNMP version");
            StatusMessage = r.Success ? "已套用。" : $"失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task ResetPortAsync()
    {
        var d = _selection.Current; if (d is null) return;
        IsBusy = true; StatusMessage = "重設 snmpagent port 為預設 161…";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.ResetSnmpAgentPortAsync();
            DryRunPreview.LogIfDryRun(driver, _log, "SNMP port reset");
            if (r.Success) PortText = "161";
            StatusMessage = r.Success ? "已重設為 161。" : $"失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task ApplyPortAsync()
    {
        var d = _selection.Current; if (d is null) return;
        if (!int.TryParse(PortText.Trim(), out var port))
        {
            StatusMessage = "Port 必須是整數（1024-65535）。";
            return;
        }
        IsBusy = true; StatusMessage = $"套用 snmpagent port {port}…";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.SetSnmpAgentPortAsync(port);
            DryRunPreview.LogIfDryRun(driver, _log, "SNMP port");
            StatusMessage = r.Success ? "已套用。" : $"失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void NotifyAll()
    {
        ApplyEnabledCommand.NotifyCanExecuteChanged();
        ApplyVersionCommand.NotifyCanExecuteChanged();
        ApplyPortCommand.NotifyCanExecuteChanged();
        ResetPortCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => NotifyAll();
    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"SNMP: {value}");
    }
}
