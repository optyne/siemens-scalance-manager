namespace Scalance.Core.Capabilities;

[Flags]
public enum DeviceCapability : long
{
    None            = 0,
    ReadStatus      = 1 << 0,
    SnmpV2c         = 1 << 1,
    SnmpV3          = 1 << 2,
    SshCli          = 1 << 3,
    WbmHttps        = 1 << 4,
    VlanManagement  = 1 << 5,
    NtpClient       = 1 << 6,
    Ipv4Addressing  = 1 << 7,
    Dhcp            = 1 << 8,
    Routing         = 1 << 9,
    Firewall        = 1 << 10,
    IpsecVpn        = 1 << 11,
    OpenVpn         = 1 << 12,
    ConfigBackup    = 1 << 13,
    FirmwareUpdate  = 1 << 14,
    AdminPasswordChange = 1 << 15,
    DnsClient       = 1 << 16,
    BasicWizard     = 1 << 17
}
