using FluentAssertions;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.Tests;

public class CapabilityMatrixTests
{
    [Fact]
    public void S615_supports_IpsecVpn_and_Ssh()
    {
        CapabilityMatrix.Supports(DeviceModelKind.S615, DeviceCapability.IpsecVpn).Should().BeTrue();
        CapabilityMatrix.Supports(DeviceModelKind.S615, DeviceCapability.SshCli).Should().BeTrue();
    }

    [Fact]
    public void S610_does_not_support_Vlan_Management()
    {
        CapabilityMatrix.Supports(DeviceModelKind.S610, DeviceCapability.VlanManagement).Should().BeFalse();
    }

    [Fact]
    public void Xc200_supports_Vlan_but_not_IpsecVpn()
    {
        CapabilityMatrix.Supports(DeviceModelKind.Xc200, DeviceCapability.VlanManagement).Should().BeTrue();
        CapabilityMatrix.Supports(DeviceModelKind.Xc200, DeviceCapability.IpsecVpn).Should().BeFalse();
    }

    [Theory]
    [InlineData(DeviceModelKind.Xb200)]
    [InlineData(DeviceModelKind.Xc200)]
    [InlineData(DeviceModelKind.Xf200Ba)]
    [InlineData(DeviceModelKind.Xp200)]
    [InlineData(DeviceModelKind.Xr300Wg)]
    public void All_managed_switches_support_Snmp_and_Ntp(DeviceModelKind model)
    {
        CapabilityMatrix.Supports(model, DeviceCapability.SnmpV2c).Should().BeTrue();
        CapabilityMatrix.Supports(model, DeviceCapability.NtpClient).Should().BeTrue();
    }

    [Theory]
    [InlineData(DeviceModelKind.S615)]
    [InlineData(DeviceModelKind.Xb200)]
    [InlineData(DeviceModelKind.Xc200)]
    [InlineData(DeviceModelKind.Xp200)]
    public void Cli_capable_devices_expose_Vlan_and_Ipv4(DeviceModelKind model)
    {
        CapabilityMatrix.Supports(model, DeviceCapability.SshCli).Should().BeTrue();
        CapabilityMatrix.Supports(model, DeviceCapability.VlanManagement).Should().BeTrue();
        CapabilityMatrix.Supports(model, DeviceCapability.Ipv4Addressing).Should().BeTrue();
    }

    [Fact]
    public void S615_is_the_only_Cli_driver_advertising_IpsecVpn()
    {
        CapabilityMatrix.Supports(DeviceModelKind.S615, DeviceCapability.IpsecVpn).Should().BeTrue();
        CapabilityMatrix.Supports(DeviceModelKind.Xc200, DeviceCapability.IpsecVpn).Should().BeFalse();
        CapabilityMatrix.Supports(DeviceModelKind.Xr300Wg, DeviceCapability.IpsecVpn).Should().BeFalse();
    }

    [Theory]
    [InlineData(DeviceModelKind.S615)]
    [InlineData(DeviceModelKind.Xb200)]
    [InlineData(DeviceModelKind.Xc200)]
    [InlineData(DeviceModelKind.Xf200Ba)]
    [InlineData(DeviceModelKind.Xp200)]
    [InlineData(DeviceModelKind.Xr300Wg)]
    public void Cli_capable_devices_expose_BasicWizard_Dns_PasswordChange(DeviceModelKind model)
    {
        CapabilityMatrix.Supports(model, DeviceCapability.BasicWizard).Should().BeTrue();
        CapabilityMatrix.Supports(model, DeviceCapability.DnsClient).Should().BeTrue();
        CapabilityMatrix.Supports(model, DeviceCapability.AdminPasswordChange).Should().BeTrue();
    }

    [Fact]
    public void S610_does_not_advertise_new_cli_features()
    {
        CapabilityMatrix.Supports(DeviceModelKind.S610, DeviceCapability.BasicWizard).Should().BeFalse();
        CapabilityMatrix.Supports(DeviceModelKind.S610, DeviceCapability.DnsClient).Should().BeFalse();
        CapabilityMatrix.Supports(DeviceModelKind.S610, DeviceCapability.AdminPasswordChange).Should().BeFalse();
    }
}
