using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Scalance.App.Services;
using Scalance.App.Views;
using Scalance.Core.Models;
using Scalance.Data;
using Scalance.Protocols.Dcp;

namespace Scalance.App.ViewModels;

/// <summary>
/// PROFINET DCP Identify-All discovery plus Set-IP and Flash-LED write
/// operations. DCP writes are fire-and-forget L2 frames; unlike IP-based
/// writes they bypass routing entirely. DryRun, when enabled, logs the raw
/// frame bytes that WOULD have been sent instead of actually sending — the
/// same safety posture as the CLI driver layer.
/// </summary>
public sealed partial class DiscoveryViewModel : ObservableObject
{
    private readonly DcpDiscoveryService _discovery;
    private readonly OperationLog _log;
    private readonly IServiceProvider _services;

    public ObservableCollection<DcpCaptureAdapter> Adapters { get; } = new();
    public ObservableCollection<DcpIdentifyResponse> Results { get; } = new();

    [ObservableProperty] private DcpCaptureAdapter? selectedAdapter;
    [ObservableProperty] private DcpIdentifyResponse? selectedDevice;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private int timeoutSeconds = 3;
    [ObservableProperty] private bool dryRun = true;

    public DiscoveryViewModel(DcpDiscoveryService discovery, OperationLog log, IServiceProvider services)
    {
        _discovery = discovery;
        _log = log;
        _services = services;
        RefreshAdapters();
    }

    [RelayCommand]
    private void RefreshAdapters()
    {
        Adapters.Clear();
        foreach (var a in _discovery.ListAdapters()) Adapters.Add(a);
        SelectedAdapter ??= Adapters.FirstOrDefault();
        StatusMessage = _discovery.NpcapMissing
            ? "尚未安裝 Npcap。請至 https://npcap.com 安裝以啟用 DCP 探索。"
            : Adapters.Count == 0
                ? "找不到可用的網卡。"
                : $"共 {Adapters.Count} 張網卡可用。";
        DiscoverCommand.NotifyCanExecuteChanged();
    }

    private bool CanDiscover() => SelectedAdapter is not null && !IsBusy && !_discovery.NpcapMissing;

    [RelayCommand(CanExecute = nameof(CanDiscover))]
    private async Task DiscoverAsync()
    {
        if (SelectedAdapter is null) return;
        IsBusy = true;
        Results.Clear();
        StatusMessage = $"正在 {SelectedAdapter.Description} 送出 DCP Identify-All...";
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 30));
            var found = await _discovery.DiscoverAsync(SelectedAdapter.Name, timeout);
            foreach (var r in found) Results.Add(r);
            StatusMessage = _discovery.NpcapMissing
                ? "Npcap 無法使用 — 探索中止。"
                : $"發現 {Results.Count} 台設備。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"探索失敗：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private bool CanActOnDevice() =>
        SelectedAdapter is not null && SelectedDevice is not null && !IsBusy && !_discovery.NpcapMissing;

    [RelayCommand(CanExecute = nameof(CanActOnDevice))]
    private async Task FlashLedAsync()
    {
        if (SelectedAdapter is null || SelectedDevice is null) return;
        var dstMac = DcpDiscoveryService.ParseMac(SelectedDevice.SourceMac);
        var label = $"閃燈 → {SelectedDevice.NameOfStation ?? SelectedDevice.SourceMac}";

        if (DryRun)
        {
            _log.Warn($"{label}：DryRun — 未實際送出 DCP 閃燈封包。");
            LogFrameBytes("flash", DcpFrame.BuildFlashLedRequest(new byte[6], dstMac, 0));
            StatusMessage = "DryRun：閃燈封包已記錄，未實際送出。";
            return;
        }

        IsBusy = true;
        StatusMessage = $"{label}...";
        try
        {
            var rsp = await _discovery.FlashLedAsync(SelectedAdapter.Name, dstMac, TimeSpan.FromSeconds(2));
            if (rsp is null) { _log.Warn($"{label}：無回應（逾時）。"); StatusMessage = "無回應。"; }
            else if (rsp.Success) { _log.Info($"{label}：成功。"); StatusMessage = "LED 閃燈中。"; }
            else { _log.Error($"{label}：{rsp}"); StatusMessage = rsp.ErrorMessage ?? "失敗。"; }
        }
        catch (Exception ex)
        {
            _log.Error($"{label} 失敗：{ex.Message}");
            StatusMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanActOnDevice))]
    private async Task SetIpAsync()
    {
        if (SelectedAdapter is null || SelectedDevice is null) return;

        var dialog = new SetIpDialog(SelectedDevice);
        if (dialog.ShowDialog() != true) return;

        if (!IPAddress.TryParse(dialog.IpText, out var ip) ||
            !IPAddress.TryParse(dialog.MaskText, out var mask) ||
            !IPAddress.TryParse(dialog.GatewayText, out var gw))
        {
            StatusMessage = "IP / 遮罩 / 閘道格式錯誤。";
            return;
        }

        var dstMac = DcpDiscoveryService.ParseMac(SelectedDevice.SourceMac);
        var save = dialog.SavePermanent;
        var label = $"設定 IP → {SelectedDevice.NameOfStation ?? SelectedDevice.SourceMac}：{ip}/{mask} 閘道={gw} 永久儲存={save}";

        if (DryRun)
        {
            _log.Warn($"{label}：DryRun — 未實際送出封包。");
            LogFrameBytes("set-ip", DcpFrame.BuildSetIpRequest(new byte[6], dstMac, 0, ip, mask, gw, save));
            StatusMessage = "DryRun：設定 IP 封包已記錄，未實際送出。";
            return;
        }

        IsBusy = true;
        StatusMessage = $"{label}...";
        try
        {
            var rsp = await _discovery.SetIpAsync(SelectedAdapter.Name, dstMac, ip, mask, gw, save, TimeSpan.FromSeconds(3));
            if (rsp is null) { _log.Warn($"{label}：無回應（逾時）。"); StatusMessage = "無回應。"; }
            else if (rsp.Success)
            {
                _log.Info($"{label}：成功。");
                StatusMessage = "IP 已套用。請重新執行探索以刷新清單。";
                SelectedDevice.IpAddress = ip;
                SelectedDevice.SubnetMask = mask;
                SelectedDevice.Gateway = gw;
            }
            else { _log.Error($"{label}：{rsp}"); StatusMessage = rsp.ErrorMessage ?? "失敗。"; }
        }
        catch (Exception ex)
        {
            _log.Error($"{label} 失敗：{ex.Message}");
            StatusMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanActOnDevice))]
    private async Task AddToDevicesAsync()
    {
        if (SelectedDevice is null || SelectedAdapter is null) return;

        var adapterIp = GetAdapterIpAddress(SelectedAdapter);
        var deviceIp = SelectedDevice.IpAddress;
        var needsIpChange = adapterIp is not null && deviceIp is not null && !SameSubnet(adapterIp, deviceIp);

        if (needsIpChange)
        {
            var suggestedIp = SuggestIpInSubnet(adapterIp!);
            var adapterMask = GetAdapterSubnetMask(SelectedAdapter) ?? IPAddress.Parse("255.255.255.0");

            var msg = $"此設備目前 IP 為 {deviceIp}，與你的網卡 {adapterIp} 不在同一子網。\n\n" +
                      $"建議將設備 IP 改為 {suggestedIp}，這樣才能透過 SSH/SNMP/HTTPS 管理設備。\n\n" +
                      "要立即透過 DCP 變更設備 IP 嗎？";

            var result = MessageBox.Show(msg, "子網不同 — 建議變更 IP",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel) return;

            if (result == MessageBoxResult.Yes)
            {
                var setIpDialog = new SetIpDialog(SelectedDevice);
                setIpDialog.SetSuggestedValues(suggestedIp.ToString(), adapterMask.ToString(), adapterIp!.ToString());

                if (setIpDialog.ShowDialog() != true) return;

                if (!IPAddress.TryParse(setIpDialog.IpText, out var newIp) ||
                    !IPAddress.TryParse(setIpDialog.MaskText, out var newMask) ||
                    !IPAddress.TryParse(setIpDialog.GatewayText, out var newGw))
                {
                    StatusMessage = "IP / 遮罩 / 閘道格式錯誤。";
                    return;
                }

                var dstMac = DcpDiscoveryService.ParseMac(SelectedDevice.SourceMac);
                _log.Info($"探索：正在透過 DCP 變更 {SelectedDevice.NameOfStation ?? SelectedDevice.SourceMac} 的 IP 為 {newIp}...");

                var rsp = await _discovery.SetIpAsync(
                    SelectedAdapter.Name, dstMac, newIp, newMask, newGw,
                    setIpDialog.SavePermanent, TimeSpan.FromSeconds(3));

                if (rsp is null)
                {
                    _log.Warn("探索：DCP Set-IP 無回應（逾時），但仍可嘗試新增設備。");
                }
                else if (rsp.Success)
                {
                    _log.Info($"探索：IP 已成功變更為 {newIp}。");
                    SelectedDevice.IpAddress = newIp;
                    SelectedDevice.SubnetMask = newMask;
                    SelectedDevice.Gateway = newGw;
                }
                else
                {
                    _log.Error($"探索：DCP Set-IP 失敗：{rsp.ErrorMessage}");
                    StatusMessage = $"IP 變更失敗：{rsp.ErrorMessage}";
                    return;
                }
            }
        }

        var model = InferModel(SelectedDevice);
        var defaults = DeviceDefaults.For(model);
        var macSuffix = SelectedDevice.SourceMac.Length >= 5
            ? SelectedDevice.SourceMac[^5..].Replace(":", "")
            : SelectedDevice.SourceMac;

        var device = new Device
        {
            Name = SelectedDevice.NameOfStation
                   ?? $"SCALANCE {defaults.DisplayName} ({macSuffix})",
            Host = SelectedDevice.IpAddress?.ToString() ?? "",
            Model = model,
            PreferredProtocol = defaults.PreferredProtocol,
            SshPort = defaults.SshPort,
            SnmpPort = defaults.SnmpPort,
            HttpsPort = defaults.HttpsPort,
            SnmpVersion = defaults.SnmpVersion,
        };

        var editorVm = _services.GetRequiredService<DeviceEditorViewModel>();
        editorVm.Load(device);
        editorVm.SshUsername = defaults.SshUsername;
        editorVm.SshPassword = defaults.SshPassword;
        editorVm.SnmpCommunityRead = defaults.SnmpCommunityRead;
        editorVm.SnmpCommunityWrite = defaults.SnmpCommunityWrite;

        var dialog = new DeviceEditorWindow { DataContext = editorVm, Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        var repo = _services.GetRequiredService<DeviceRepository>();
        await repo.UpsertAsync(editorVm.ToDevice());

        var deviceList = _services.GetRequiredService<DeviceListViewModel>();
        await deviceList.LoadAsync();

        StatusMessage = $"已將 {device.Name} 新增至設備清單。";
        // OnStatusMessageChanged 會自動寫入 log，不需額外呼叫 _log.Info
    }

    private static DeviceModelKind InferModel(DcpIdentifyResponse r)
    {
        var name = (r.NameOfStation ?? "").ToLowerInvariant();
        if (name.Contains("s615")) return DeviceModelKind.S615;
        if (name.Contains("s610")) return DeviceModelKind.S610;
        if (name.Contains("xc") && name.Contains("200")) return DeviceModelKind.Xc200;
        if (name.Contains("xb") && name.Contains("200")) return DeviceModelKind.Xb200;
        if (name.Contains("xf") && name.Contains("200")) return DeviceModelKind.Xf200Ba;
        if (name.Contains("xp") && name.Contains("200")) return DeviceModelKind.Xp200;
        if (name.Contains("xr") && name.Contains("300")) return DeviceModelKind.Xr300Wg;
        if (r.VendorId == 0x002A)
            return DeviceModelKind.S615;
        return DeviceModelKind.Unknown;
    }

    private static class DeviceDefaults
    {
        public static DefaultSet For(DeviceModelKind model) => model switch
        {
            DeviceModelKind.S615 => new("S615", "admin", "admin", ProtocolKind.Ssh),
            DeviceModelKind.S610 => new("S610", "admin", "admin", ProtocolKind.Ssh),
            DeviceModelKind.Xc200 => new("XC-200", "admin", "admin", ProtocolKind.Snmp),
            DeviceModelKind.Xb200 => new("XB-200", "admin", "admin", ProtocolKind.Snmp),
            DeviceModelKind.Xf200Ba => new("XF-200BA", "admin", "admin", ProtocolKind.Snmp),
            DeviceModelKind.Xp200 => new("XP-200", "admin", "admin", ProtocolKind.Snmp),
            DeviceModelKind.Xr300Wg => new("XR-300WG", "admin", "admin", ProtocolKind.Snmp),
            _ => new("Unknown", "admin", "admin", ProtocolKind.Snmp),
        };

        public sealed record DefaultSet(
            string DisplayName,
            string SshUsername,
            string SshPassword,
            ProtocolKind PreferredProtocol,
            int SshPort = 22,
            int SnmpPort = 161,
            int HttpsPort = 443,
            SnmpVersion SnmpVersion = SnmpVersion.V2c,
            string SnmpCommunityRead = "public",
            string SnmpCommunityWrite = "private");
    }

    private static IPAddress? GetAdapterIpAddress(DcpCaptureAdapter adapter)
    {
        if (adapter.Mac is null) return null;
        var macStr = string.Join(":", adapter.Mac.Select(b => b.ToString("X2")));
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var nicMac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            if (!string.Equals(nicMac, macStr, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address) &&
                    !ua.Address.ToString().StartsWith("169.254"))
                    return ua.Address;
            }
        }
        return null;
    }

    private static IPAddress? GetAdapterSubnetMask(DcpCaptureAdapter adapter)
    {
        if (adapter.Mac is null) return null;
        var macStr = string.Join(":", adapter.Mac.Select(b => b.ToString("X2")));
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var nicMac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            if (!string.Equals(nicMac, macStr, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                    return ua.IPv4Mask;
            }
        }
        return null;
    }

    private static bool SameSubnet(IPAddress a, IPAddress b)
    {
        var ab = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        if (ab.Length != 4 || bb.Length != 4) return false;
        return ab[0] == bb[0] && ab[1] == bb[1] && ab[2] == bb[2];
    }

    private static IPAddress SuggestIpInSubnet(IPAddress adapterIp)
    {
        var bytes = adapterIp.GetAddressBytes();
        bytes[3] = 200;
        return new IPAddress(bytes);
    }

    private void LogFrameBytes(string tag, byte[] frame)
    {
        var hex = string.Concat(frame.Take(Math.Min(frame.Length, 64)).Select(b => b.ToString("X2")));
        _log.Info($"  dcp[{tag}] {frame.Length}B {hex}{(frame.Length > 64 ? "..." : "")}");
    }

    partial void OnIsBusyChanged(bool value)
    {
        DiscoverCommand.NotifyCanExecuteChanged();
        FlashLedCommand.NotifyCanExecuteChanged();
        SetIpCommand.NotifyCanExecuteChanged();
        AddToDevicesCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedAdapterChanged(DcpCaptureAdapter? value)
    {
        DiscoverCommand.NotifyCanExecuteChanged();
        FlashLedCommand.NotifyCanExecuteChanged();
        SetIpCommand.NotifyCanExecuteChanged();
        AddToDevicesCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedDeviceChanged(DcpIdentifyResponse? value)
    {
        FlashLedCommand.NotifyCanExecuteChanged();
        SetIpCommand.NotifyCanExecuteChanged();
        AddToDevicesCommand.NotifyCanExecuteChanged();
    }
    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"探索：{value}");
    }
}
