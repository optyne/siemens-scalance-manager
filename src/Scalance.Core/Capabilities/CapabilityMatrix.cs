using Scalance.Core.Models;

namespace Scalance.Core.Capabilities;

public static class CapabilityMatrix
{
    private static readonly Dictionary<DeviceModelKind, DeviceCapability> Map = new()
    {
        [DeviceModelKind.S610] =
            DeviceCapability.ReadStatus |
            DeviceCapability.SnmpV2c |
            DeviceCapability.WbmHttps |
            DeviceCapability.Ipv4Addressing |
            DeviceCapability.Firewall |
            DeviceCapability.IpsecVpn |
            DeviceCapability.ConfigBackup,
            // S610 無 SSH-CLI，AdminPasswordChange / DnsClient / BasicWizard 目前只能走 WBM，尚未實作。

        [DeviceModelKind.S615] =
            DeviceCapability.ReadStatus |
            DeviceCapability.SnmpV2c | DeviceCapability.SnmpV3 |
            DeviceCapability.SshCli | DeviceCapability.WbmHttps |
            DeviceCapability.VlanManagement |
            DeviceCapability.NtpClient |
            DeviceCapability.Ipv4Addressing | DeviceCapability.Dhcp |
            DeviceCapability.Routing | DeviceCapability.Firewall |
            DeviceCapability.IpsecVpn | DeviceCapability.OpenVpn |
            DeviceCapability.ConfigBackup | DeviceCapability.FirmwareUpdate |
            DeviceCapability.AdminPasswordChange | DeviceCapability.DnsClient |
            DeviceCapability.BasicWizard | DeviceCapability.SyslogClient,

        // S615 already advertises VlanManagement above (it has an integrated L2 switch).

        [DeviceModelKind.Xc200] = ManagedSwitchCaps(),
        [DeviceModelKind.Xb200] = ManagedSwitchCaps(),
        [DeviceModelKind.Xf200Ba] = ManagedSwitchCaps(),
        [DeviceModelKind.Xp200] = ManagedSwitchCaps(),
        [DeviceModelKind.Xr300Wg] = ManagedSwitchCaps() | DeviceCapability.Routing
    };

    private static DeviceCapability ManagedSwitchCaps() =>
        DeviceCapability.ReadStatus |
        DeviceCapability.SnmpV2c | DeviceCapability.SnmpV3 |
        DeviceCapability.SshCli | DeviceCapability.WbmHttps |
        DeviceCapability.VlanManagement |
        DeviceCapability.NtpClient |
        DeviceCapability.Ipv4Addressing | DeviceCapability.Dhcp |
        DeviceCapability.ConfigBackup | DeviceCapability.FirmwareUpdate |
        DeviceCapability.AdminPasswordChange | DeviceCapability.DnsClient |
        DeviceCapability.BasicWizard | DeviceCapability.SyslogClient;

    public static DeviceCapability For(DeviceModelKind model) =>
        Map.TryGetValue(model, out var caps) ? caps : DeviceCapability.None;

    public static bool Supports(DeviceModelKind model, DeviceCapability required) =>
        (For(model) & required) == required;
}
