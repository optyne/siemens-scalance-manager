using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.App.ViewModels;

/// <summary>
/// Operator view for CLI-driven diagnostics. Currently exposes `ping` from
/// PH_SCALANCE-S615-CLI_76 sec 5.1.8 p. 85-86. Additional read-only checks
/// (traceroute, show version, show running-config) can be added here.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    [ObservableProperty] private string? selectedDeviceName;
    [ObservableProperty] private bool featureSupported;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;

    // Ping inputs.
    [ObservableProperty] private string pingHost = "";
    [ObservableProperty] private string pingSize = "";     // blank = device default (32)
    [ObservableProperty] private string pingCount = "3";
    [ObservableProperty] private string pingTimeout = "1";

    // Traceroute inputs — manual p. 88 accepts ip/ipv6 literals only.
    [ObservableProperty] private string traceHost = "";

    // Output window (raw device response).
    [ObservableProperty] private string output = "";

    public DiagnosticsViewModel(DeviceOperationsService ops, DeviceSelection selection, OperationLog log)
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
        // Ping needs an SSH-CLI capable device; SNMP-only models can't issue
        // the command even though SCALANCE firmware supports it.
        FeatureSupported = d is not null
            && CapabilityMatrix.Supports(d.Model, DeviceCapability.SshCli);
        StatusMessage = d is null ? "未選取設備。"
            : FeatureSupported ? $"就緒 — {d.Name}。"
            : "此設備不支援 CLI 診斷（需要 SSH-CLI 能力）。";
        PingCommand.NotifyCanExecuteChanged();
        TraceRouteCommand.NotifyCanExecuteChanged();
    }

    private bool CanPing()
        => _selection.Current is not null && FeatureSupported && !IsBusy
           && !string.IsNullOrWhiteSpace(PingHost);

    private bool CanTrace()
        => _selection.Current is not null && FeatureSupported && !IsBusy
           && !string.IsNullOrWhiteSpace(TraceHost);

    [RelayCommand(CanExecute = nameof(CanPing))]
    private async Task PingAsync()
    {
        var d = _selection.Current;
        if (d is null) return;

        PingOptions? opts;
        try
        {
            opts = BuildOptions();
        }
        catch (ArgumentException ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        IsBusy = true;
        StatusMessage = $"ping {PingHost}…";
        Output = "";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.PingAsync(PingHost.Trim(), opts);
            Output = r.Success ? (r.Value ?? "") : $"[失敗] {r.Message}";
            StatusMessage = r.Success ? "ping 完成。" : $"失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private PingOptions? BuildOptions()
    {
        var opts = new PingOptions();
        bool any = false;
        if (!string.IsNullOrWhiteSpace(PingSize))
        {
            if (!int.TryParse(PingSize.Trim(), out var sz))
                throw new ArgumentException("Size 需為整數（0-2080）。");
            opts.SizeBytes = sz; any = true;
        }
        if (!string.IsNullOrWhiteSpace(PingCount))
        {
            if (!int.TryParse(PingCount.Trim(), out var c))
                throw new ArgumentException("Count 需為整數（1-10）。");
            opts.Count = c; any = true;
        }
        if (!string.IsNullOrWhiteSpace(PingTimeout))
        {
            if (!int.TryParse(PingTimeout.Trim(), out var t))
                throw new ArgumentException("Timeout 需為整數（1-100）。");
            opts.TimeoutSeconds = t; any = true;
        }
        return any ? opts : null;
    }

    [RelayCommand(CanExecute = nameof(CanTrace))]
    private async Task TraceRouteAsync()
    {
        var d = _selection.Current;
        if (d is null) return;

        IsBusy = true;
        StatusMessage = $"traceroute {TraceHost}…";
        Output = "";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.TraceRouteAsync(TraceHost.Trim());
            Output = r.Success ? (r.Value ?? "") : $"[失敗] {r.Message}";
            StatusMessage = r.Success ? "traceroute 完成。" : $"失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    partial void OnIsBusyChanged(bool value)
    {
        PingCommand.NotifyCanExecuteChanged();
        TraceRouteCommand.NotifyCanExecuteChanged();
    }
    partial void OnPingHostChanged(string value) => PingCommand.NotifyCanExecuteChanged();
    partial void OnTraceHostChanged(string value) => TraceRouteCommand.NotifyCanExecuteChanged();

    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"Diag: {value}");
    }
}
