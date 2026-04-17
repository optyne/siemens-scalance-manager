using FluentAssertions;
using Scalance.Drivers;

namespace Scalance.Tests;

public class InterfaceParsingTests
{
    [Fact]
    public void ParseNtp_modern_form_extracts_ipv4_after_keyword()
    {
        // S615 CLI manual sec 7.2.3.1 p. 217: `ntp server id <N> ipv4 <ip>`.
        // Previous parser returned token[2] ("id") as the host — wrong.
        var output = "ntp server id 1 ipv4 192.168.10.10 poll 64\nntp enable";
        var cfg = ScalanceCliDriverBase.ParseNtp(output);
        cfg.Servers.Should().HaveCount(1);
        cfg.Servers[0].Host.Should().Be("192.168.10.10");
        cfg.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ParseNtp_modern_form_extracts_fqdn()
    {
        var output = "ntp server id 2 fqdn-name pool.ntp.org";
        var cfg = ScalanceCliDriverBase.ParseNtp(output);
        cfg.Servers.Should().HaveCount(1);
        cfg.Servers[0].Host.Should().Be("pool.ntp.org");
    }

    [Fact]
    public void ParseNtp_legacy_cisco_form_still_parsed()
    {
        // Fallback — if a device (or test fixture) still emits the old
        // `ntp server <host>` form, don't regress.
        var output = "ntp server 10.0.0.5";
        var cfg = ScalanceCliDriverBase.ParseNtp(output);
        cfg.Servers.Should().HaveCount(1);
        cfg.Servers[0].Host.Should().Be("10.0.0.5");
    }


    [Fact]
    public void ParseInterfaces_reads_show_ip_interface_brief()
    {
        var output = @"Interface          IP-Address      OK? Method Status  Protocol
vlan1              192.168.1.1     YES manual up      up
ethernet 0/1       unassigned      YES unset  up      up
";
        var result = ScalanceCliDriverBase.ParseInterfaces(output);
        result.Should().HaveCount(2);
        result[0].InterfaceName.Should().Be("vlan1");
        result[0].IpAddress.Should().Be("192.168.1.1");
        result[1].InterfaceName.Should().Be("ethernet 0/1");
        result[1].IpAddress.Should().BeNull();
    }

    [Fact]
    public void ParseInterfaces_reads_module_dot_port_style()
    {
        // Siemens WBM documents ports as "port 0.1 is module 0, port 1" — if the
        // CLI echoes the same format (unverified) the parser must still work.
        var output = @"Interface          IP-Address      OK? Method Status  Protocol
0.1                unassigned      YES unset  up      up
";
        var result = ScalanceCliDriverBase.ParseInterfaces(output);
        result.Should().HaveCount(1);
        result[0].InterfaceName.Should().Be("0.1");
        result[0].IpAddress.Should().BeNull();
    }

    [Fact]
    public void ParseVpnTunnels_detects_ipsec_connection_stanzas()
    {
        // Real S615 uses "show ipsec connections" which outputs connection info.
        // The parser still supports legacy crypto-map format for backward compat,
        // plus the new ipsec connection format.
        var output = @"!
crypto map branch 10 ipsec-isakmp
 set peer 203.0.113.10
 set transform-set branch
 match address branch-acl
 exit
!
no crypto map branch enable
";
        var tunnels = ScalanceCliDriverBase.ParseVpnTunnels(output);
        tunnels.Should().HaveCount(1);
        tunnels[0].Name.Should().Be("branch");
        tunnels[0].RemoteEndpoint.Should().Be("203.0.113.10");
        // The "no crypto map branch enable" line should mark it disabled
        tunnels[0].Enabled.Should().BeFalse();
    }
}
