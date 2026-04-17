using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.App.ViewModels;

public sealed partial class SubnetEditorViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    [ObservableProperty] private string? selectedDeviceName;
    [ObservableProperty] private bool featureSupported;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private InterfaceRow? selectedInterface;

    public ObservableCollection<InterfaceRow> Interfaces { get; } = new();

    public SubnetEditorViewModel(DeviceOperationsService ops, DeviceSelection selection, OperationLog log)
    {
        _ops = ops;
        _selection = selection;
        _log = log;
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
        FeatureSupported = d is not null && CapabilityMatrix.Supports(d.Model, DeviceCapability.Ipv4Addressing);
        Interfaces.Clear();
        SelectedInterface = null;
        StatusMessage = d is null ? "未選取設備。"
            : FeatureSupported ? $"就緒 — {d.Name}。請按「載入」。"
            : "此設備不支援 IPv4 位址管理功能。";
        NotifyCommands();
    }

    private bool CanUse() => _selection.Current is not null && FeatureSupported && !IsBusy;
    private bool CanEdit() => CanUse() && SelectedInterface is not null;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task LoadAsync()
    {
        var d = _selection.Current;
        if (d is null) return;
        IsBusy = true; StatusMessage = "正在載入介面...";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.GetInterfacesAsync();
            Interfaces.Clear();
            if (r.Success && r.Value is not null)
            {
                foreach (var cfg in r.Value)
                    Interfaces.Add(InterfaceRow.From(cfg));
                StatusMessage = $"已載入 {Interfaces.Count} 個介面。";
            }
            else
            {
                StatusMessage = $"載入失敗：{r.Message}";
            }
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ApplyAsync()
    {
        var d = _selection.Current;
        if (d is null || SelectedInterface is null) return;
        IsBusy = true; StatusMessage = $"正在套用 {SelectedInterface.InterfaceName}...";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.SetInterfaceAsync(SelectedInterface.ToModel());
            var isDryRun = driver is Scalance.Drivers.ScalanceCliDriverBase cli && cli.DryRun;
            DryRunPreview.LogIfDryRun(driver, _log, "Subnet");
            StatusMessage = !r.Success ? $"失敗：{r.Message}"
                : isDryRun ? "⚠ DryRun：指令已規劃但未送出。請取消勾選 DryRun 後重新套用。"
                : "介面已套用。";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSelectedInterfaceChanged(InterfaceRow? value) => NotifyCommands();
    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"Subnet: {value}");
    }

    private void NotifyCommands()
    {
        LoadCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class InterfaceRow : ObservableObject
{
    [ObservableProperty] private string interfaceName = "";
    [ObservableProperty] private bool dhcpEnabled;
    [ObservableProperty] private string? ipAddress;
    [ObservableProperty] private string? subnetMask;
    [ObservableProperty] private int? prefixLength;
    [ObservableProperty] private string? defaultGateway;
    [ObservableProperty] private string dnsServersCsv = "";
    [ObservableProperty] private int? vlanId;

    public static InterfaceRow From(InterfaceIpConfig cfg) => new()
    {
        InterfaceName = cfg.InterfaceName,
        DhcpEnabled = cfg.DhcpEnabled,
        IpAddress = cfg.IpAddress,
        SubnetMask = cfg.SubnetMask,
        PrefixLength = cfg.PrefixLength,
        DefaultGateway = cfg.DefaultGateway,
        DnsServersCsv = string.Join(", ", cfg.DnsServers),
        VlanId = cfg.VlanId,
    };

    public InterfaceIpConfig ToModel() => new()
    {
        InterfaceName = InterfaceName,
        DhcpEnabled = DhcpEnabled,
        IpAddress = IpAddress,
        SubnetMask = SubnetMask,
        PrefixLength = PrefixLength,
        DefaultGateway = DefaultGateway,
        DnsServers = (DnsServersCsv ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
        VlanId = VlanId,
    };
}
