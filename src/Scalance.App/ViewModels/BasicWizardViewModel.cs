using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.App.ViewModels;

/// <summary>
/// In-app replacement for the WBM Basic Wizard. Applies hostname + VLAN1 IP +
/// DNS + NTP + admin password to the currently selected device in one call via
/// <see cref="Core.Abstractions.IDeviceDriver.ApplyBasicWizardAsync"/>.
/// </summary>
public sealed partial class BasicWizardViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    [ObservableProperty] private string? selectedDeviceName;
    [ObservableProperty] private bool featureSupported;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;

    [ObservableProperty] private string hostname = "";
    [ObservableProperty] private string interfaceName = "vlan1";
    [ObservableProperty] private bool dhcpEnabled;
    [ObservableProperty] private string ipAddress = "";
    [ObservableProperty] private string subnetMask = "255.255.255.0";
    [ObservableProperty] private string defaultGateway = "";

    [ObservableProperty] private string dns1 = "";
    [ObservableProperty] private string dns2 = "";
    [ObservableProperty] private string domainName = "";

    [ObservableProperty] private string ntpServer = "";
    [ObservableProperty] private string? timezone;

    [ObservableProperty] private string adminUsername = "admin";
    [ObservableProperty] private string newPassword = "";
    [ObservableProperty] private string confirmPassword = "";

    public BasicWizardViewModel(DeviceOperationsService ops, DeviceSelection selection, OperationLog log)
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
        FeatureSupported = d is not null && CapabilityMatrix.Supports(d.Model, DeviceCapability.BasicWizard);
        StatusMessage = d is null ? "未選取設備。"
            : FeatureSupported ? $"就緒 — {d.Name}。填完後按「套用 Basic Wizard」。"
            : "此設備不支援 Basic Wizard（S610 需使用 WBM）。";
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply() => _selection.Current is not null && FeatureSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var d = _selection.Current;
        if (d is null) return;

        if (!string.IsNullOrEmpty(NewPassword) && NewPassword != ConfirmPassword)
        {
            StatusMessage = "兩次輸入的密碼不一致。";
            return;
        }

        IsBusy = true;
        StatusMessage = "正在套用 Basic Wizard...";
        try
        {
            var cfg = new BasicWizardConfig
            {
                Hostname = string.IsNullOrWhiteSpace(Hostname) ? null : Hostname.Trim(),
                AdminUsername = AdminUsername,
                NewAdminPassword = string.IsNullOrEmpty(NewPassword) ? null : NewPassword
            };

            if (!string.IsNullOrWhiteSpace(IpAddress) || DhcpEnabled)
            {
                cfg.Interface = new InterfaceIpConfig
                {
                    InterfaceName = InterfaceName,
                    DhcpEnabled = DhcpEnabled,
                    IpAddress = DhcpEnabled ? "" : IpAddress.Trim(),
                    SubnetMask = DhcpEnabled ? null : SubnetMask,
                    DefaultGateway = string.IsNullOrWhiteSpace(DefaultGateway) ? null : DefaultGateway.Trim()
                };
            }

            var dnsServers = new[] { Dns1, Dns2 }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();
            if (dnsServers.Count > 0 || !string.IsNullOrWhiteSpace(DomainName))
            {
                cfg.Dns = new DnsConfig
                {
                    Servers = dnsServers,
                    DomainName = string.IsNullOrWhiteSpace(DomainName) ? null : DomainName.Trim()
                };
            }

            if (!string.IsNullOrWhiteSpace(NtpServer))
            {
                cfg.Ntp = new NtpConfig
                {
                    Enabled = true,
                    Timezone = Timezone,
                    Servers = { new NtpServer(NtpServer.Trim()) }
                };
            }

            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.ApplyBasicWizardAsync(cfg);
            DryRunPreview.LogIfDryRun(driver, _log, "BasicWizard");
            StatusMessage = r.Success ? "已套用 Basic Wizard。" : $"失敗：{r.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    partial void OnIsBusyChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"BasicWizard: {value}");
    }
}
