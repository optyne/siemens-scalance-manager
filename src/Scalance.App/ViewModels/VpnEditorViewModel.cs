using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.App.ViewModels;

public sealed partial class VpnEditorViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    [ObservableProperty] private string? selectedDeviceName;
    [ObservableProperty] private bool featureSupported;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private VpnTunnelRow? selectedTunnel;

    public ObservableCollection<VpnTunnelRow> Tunnels { get; } = new();
    public IReadOnlyList<string> IkeEncryptionChoices { get; } = new[] { "aes128", "aes256", "3des" };
    public IReadOnlyList<string> IkeHashChoices { get; } = new[] { "sha1", "sha256", "sha384", "sha512", "md5" };
    public IReadOnlyList<string> DhGroupChoices { get; } = new[] { "2", "5", "14", "15", "16", "19", "20" };
    public IReadOnlyList<VpnAuthMode> AuthModes { get; } = new[] { VpnAuthMode.Psk, VpnAuthMode.Certificate };

    public VpnEditorViewModel(DeviceOperationsService ops, DeviceSelection selection, OperationLog log)
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
        FeatureSupported = d is not null && CapabilityMatrix.Supports(d.Model, DeviceCapability.IpsecVpn);
        Tunnels.Clear();
        SelectedTunnel = null;
        StatusMessage = d is null ? "未選取設備。"
            : FeatureSupported ? $"就緒 — {d.Name}。請按「載入」。"
            : "此設備不支援 IPsec VPN 功能。";
        NotifyCommands();
    }

    private bool CanUse() => _selection.Current is not null && FeatureSupported && !IsBusy;
    private bool CanEdit() => CanUse() && SelectedTunnel is not null;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task LoadAsync()
    {
        var d = _selection.Current;
        if (d is null) return;
        IsBusy = true; StatusMessage = "正在載入 VPN 通道...";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.GetVpnTunnelsAsync();
            Tunnels.Clear();
            if (r.Success && r.Value is not null)
            {
                foreach (var t in r.Value) Tunnels.Add(VpnTunnelRow.From(t));
                StatusMessage = $"已載入 {Tunnels.Count} 條通道。";
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
    private void AddTunnel()
    {
        var t = new VpnTunnelRow
        {
            Name = $"tunnel{Tunnels.Count + 1}",
            Enabled = true,
            RemoteEndpoint = "",
            LocalSubnet = "192.168.1.0/24",
            RemoteSubnet = "192.168.2.0/24",
            AuthMode = VpnAuthMode.Psk,
            IkeEncryption = "aes256",
            IkeHash = "sha256",
            DhGroup = "14",
            IkeLifetime = 28800,
            EspEncryption = "aes256",
            EspHash = "sha256",
            PfsGroup = "14",
            EspLifetime = 3600,
        };
        Tunnels.Add(t);
        SelectedTunnel = t;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveTunnel()
    {
        if (SelectedTunnel is null) return;
        Tunnels.Remove(SelectedTunnel);
        SelectedTunnel = null;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ApplyAsync()
    {
        var d = _selection.Current;
        if (d is null || SelectedTunnel is null) return;
        IsBusy = true; StatusMessage = $"正在套用通道 {SelectedTunnel.Name}...";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.SetVpnTunnelAsync(SelectedTunnel.ToModel());
            var isDryRun = driver is Scalance.Drivers.ScalanceCliDriverBase cli && cli.DryRun;
            DryRunPreview.LogIfDryRun(driver, _log, "VPN");
            StatusMessage = !r.Success ? $"失敗：{r.Message}"
                : isDryRun ? "⚠ DryRun：指令已規劃但未送出。請取消勾選 DryRun 後重新套用。"
                : "通道已套用。";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSelectedTunnelChanged(VpnTunnelRow? value) => NotifyCommands();
    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"VPN: {value}");
    }

    private void NotifyCommands()
    {
        LoadCommand.NotifyCanExecuteChanged();
        AddTunnelCommand.NotifyCanExecuteChanged();
        RemoveTunnelCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class VpnTunnelRow : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private bool enabled;
    [ObservableProperty] private string remoteEndpoint = "";
    [ObservableProperty] private string localSubnet = "";
    [ObservableProperty] private string remoteSubnet = "";
    [ObservableProperty] private VpnAuthMode authMode = VpnAuthMode.Psk;
    [ObservableProperty] private string? preSharedKey;
    [ObservableProperty] private string? localCertificateName;
    [ObservableProperty] private string ikeEncryption = "aes256";
    [ObservableProperty] private string ikeHash = "sha256";
    [ObservableProperty] private string dhGroup = "14";
    [ObservableProperty] private int ikeLifetime = 28800;
    [ObservableProperty] private string espEncryption = "aes256";
    [ObservableProperty] private string espHash = "sha256";
    [ObservableProperty] private string? pfsGroup = "14";
    [ObservableProperty] private int espLifetime = 3600;

    public static VpnTunnelRow From(VpnTunnel t) => new()
    {
        Name = t.Name,
        Enabled = t.Enabled,
        RemoteEndpoint = t.RemoteEndpoint,
        LocalSubnet = t.LocalSubnet,
        RemoteSubnet = t.RemoteSubnet,
        AuthMode = t.AuthMode,
        PreSharedKey = t.PreSharedKey,
        LocalCertificateName = t.LocalCertificateName,
        IkeEncryption = t.Ike.Encryption,
        IkeHash = t.Ike.Hash,
        DhGroup = t.Ike.DhGroup,
        IkeLifetime = t.Ike.LifetimeSeconds,
        EspEncryption = t.Esp.Encryption,
        EspHash = t.Esp.Hash,
        PfsGroup = t.Esp.PfsGroup,
        EspLifetime = t.Esp.LifetimeSeconds,
    };

    public VpnTunnel ToModel() => new()
    {
        Name = Name,
        Enabled = Enabled,
        RemoteEndpoint = RemoteEndpoint,
        LocalSubnet = LocalSubnet,
        RemoteSubnet = RemoteSubnet,
        AuthMode = AuthMode,
        PreSharedKey = PreSharedKey,
        LocalCertificateName = LocalCertificateName,
        Ike = new IkeSettings
        {
            Encryption = IkeEncryption,
            Hash = IkeHash,
            DhGroup = DhGroup,
            LifetimeSeconds = IkeLifetime,
        },
        Esp = new EspSettings
        {
            Encryption = EspEncryption,
            Hash = EspHash,
            PfsGroup = PfsGroup,
            LifetimeSeconds = EspLifetime,
        },
    };
}
