using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.App.ViewModels;

public sealed partial class NtpEditorViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    [ObservableProperty] private bool enabled;
    [ObservableProperty] private string? timezone;
    [ObservableProperty] private int pollIntervalSeconds = 64;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string newServerHost = "";
    [ObservableProperty] private string? selectedDeviceName;
    [ObservableProperty] private bool featureSupported;

    public ObservableCollection<NtpServerRow> Servers { get; } = new();

    public NtpEditorViewModel(DeviceOperationsService ops, DeviceSelection selection, OperationLog log)
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
        FeatureSupported = d is not null && CapabilityMatrix.Supports(d.Model, DeviceCapability.NtpClient);
        Servers.Clear();
        StatusMessage = d is null ? "未選取設備。"
            : FeatureSupported ? $"就緒 — {d.Name}。請按「載入」。"
            : "此設備不支援 NTP 用戶端功能。";
        LoadCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private bool CanUse() => _selection.Current is not null && FeatureSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task LoadAsync()
    {
        var d = _selection.Current;
        if (d is null) return;
        IsBusy = true;
        StatusMessage = "正在載入 NTP 設定...";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.GetNtpAsync();
            if (r.Success && r.Value is not null)
            {
                Enabled = r.Value.Enabled;
                Timezone = r.Value.Timezone;
                PollIntervalSeconds = r.Value.PollIntervalSeconds;
                Servers.Clear();
                foreach (var s in r.Value.Servers)
                    Servers.Add(new NtpServerRow(s.Host, s.Port, s.Preferred));
                StatusMessage = $"已載入 {Servers.Count} 台 NTP 伺服器。";
            }
            else
            {
                StatusMessage = $"載入失敗：{r.Message}";
            }
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task ApplyAsync()
    {
        var d = _selection.Current;
        if (d is null) return;
        IsBusy = true;
        StatusMessage = "正在套用 NTP 設定...";
        try
        {
            var cfg = new NtpConfig
            {
                Enabled = Enabled,
                Timezone = Timezone,
                PollIntervalSeconds = PollIntervalSeconds,
                Servers = Servers.Select(s => new NtpServer(s.Host, s.Port, s.Preferred)).ToList()
            };
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.SetNtpAsync(cfg);
            DryRunPreview.LogIfDryRun(driver, _log, "NTP");
            StatusMessage = r.Success ? "已套用。" : $"失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void AddServer()
    {
        if (string.IsNullOrWhiteSpace(NewServerHost)) return;
        Servers.Add(new NtpServerRow(NewServerHost.Trim(), 123, false));
        NewServerHost = "";
    }

    [RelayCommand]
    private void RemoveServer(NtpServerRow row) => Servers.Remove(row);

    partial void OnIsBusyChanged(bool value)
    {
        LoadCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"NTP: {value}");
    }
}

public sealed partial class NtpServerRow : ObservableObject
{
    [ObservableProperty] private string host;
    [ObservableProperty] private int port;
    [ObservableProperty] private bool preferred;

    public NtpServerRow(string host, int port, bool preferred)
    {
        this.host = host;
        this.port = port;
        this.preferred = preferred;
    }
}
