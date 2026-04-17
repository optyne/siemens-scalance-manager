using FluentAssertions;
using Scalance.Core.Models;
using Scalance.Drivers;

namespace Scalance.Tests;

public class ScalanceCliCommandsTests
{
    [Fact]
    public void BuildSetVlans_wraps_commands_with_configure_and_write()
    {
        var vlans = new List<Vlan>
        {
            new Vlan { Id = 10, Name = "office", Ports = { new VlanPortMembership(1, VlanMemberMode.Untagged) } }
        };

        var cmds = ScalanceCliCommands.BuildSetVlans(vlans);

        cmds.First().Should().Be("configure terminal");
        cmds.Last().Should().Be("write memory");
        cmds.Should().Contain("end");
        // S615 CLI manual p. 242: must be in dot1q-vlan mode before vlan commands.
        cmds.Should().Contain("base bridge-mode dot1q-vlan");
        cmds.Should().Contain("vlan 10");
        cmds.Should().Contain("name office");
        // Verified S615 CLI manual sec 8.1.4.5 p. 266 + sec 3.7.5 p. 57:
        // physical ports use the "fa" (fast-ethernet) interface-type with
        // "0/N" identifier. WBM's "0.N" format is wrong for CLI.
        cmds.Should().Contain("ports () untagged (fa 0/1)");
    }

    [Fact]
    public void BuildSetVlans_emits_dot1q_before_vlan_commands()
    {
        var vlans = new List<Vlan>
        {
            new Vlan { Id = 10, Name = "office" }
        };

        var cmds = ScalanceCliCommands.BuildSetVlans(vlans).ToList();
        var dot1qIdx = cmds.IndexOf("base bridge-mode dot1q-vlan");
        var vlanIdx = cmds.IndexOf("vlan 10");

        dot1qIdx.Should().BeGreaterThan(0, "dot1q mode setup must come after configure terminal");
        dot1qIdx.Should().BeLessThan(vlanIdx, "dot1q mode must precede vlan commands");
    }

    [Theory]
    [InlineData(1, "0.1")]
    [InlineData(8, "0.8")]
    [InlineData(101, "1.1")]
    [InlineData(216, "2.16")]
    public void FormatPortId_emits_WBM_dotted_format(int raw, string expected)
    {
        // WBM display format (M.P) — used by UI layer only.
        ScalanceCliCommands.FormatPortId(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, "0/1")]
    [InlineData(8, "0/8")]
    [InlineData(101, "1/1")]
    public void FormatCliPortId_emits_slashed_format(int raw, string expected)
    {
        // CLI format (M/N) — paired with "fa" keyword per S615 manual sec 3.7.5.
        ScalanceCliCommands.FormatCliPortId(raw).Should().Be(expected);
    }

    [Fact]
    public void BuildSetVlans_emits_ports_when_port_is_tagged_in_multiple_vlans()
    {
        var vlans = new List<Vlan>
        {
            new Vlan { Id = 10, Name = "red",  Ports = { new VlanPortMembership(2, VlanMemberMode.Tagged) } },
            new Vlan { Id = 20, Name = "blue", Ports = { new VlanPortMembership(2, VlanMemberMode.Tagged) } },
        };

        var cmds = ScalanceCliCommands.BuildSetVlans(vlans);

        // S615 CLI manual sec 8.1.4.5 p. 266 + sec 3.7.5: "fa 0/N" CLI form.
        cmds.Should().Contain("ports (fa 0/2)");
        cmds.Should().Contain("vlan 10");
        cmds.Should().Contain("vlan 20");
    }

    [Fact]
    public void BuildSetInterface_for_dhcp_emits_ip_address_dhcp()
    {
        var cfg = new InterfaceIpConfig { InterfaceName = "vlan1", DhcpEnabled = true };
        var cmds = ScalanceCliCommands.BuildSetInterface(cfg);

        cmds.Should().Contain("interface vlan1");
        cmds.Should().Contain("ip address dhcp");
        cmds.Last().Should().Be("write memory");
    }

    [Fact]
    public void BuildSetInterface_static_uses_prefix_length_to_derive_mask()
    {
        var cfg = new InterfaceIpConfig
        {
            InterfaceName = "vlan1",
            DhcpEnabled = false,
            IpAddress = "192.168.1.1",
            PrefixLength = 24,
            DefaultGateway = "192.168.1.254"
        };
        var cmds = ScalanceCliCommands.BuildSetInterface(cfg);

        cmds.Should().Contain("ip address 192.168.1.1 255.255.255.0");
        // Real S615 syntax: default gateway via ip route (PH_SCALANCE-S615-CLI_76 p. 331)
        cmds.Should().Contain("ip route 0.0.0.0 0.0.0.0 192.168.1.254");
    }

    [Fact]
    public void BuildSetInterface_rejects_static_config_without_ip()
    {
        var cfg = new InterfaceIpConfig { InterfaceName = "vlan1", DhcpEnabled = false };
        Action act = () => ScalanceCliCommands.BuildSetInterface(cfg);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetVpnTunnel_produces_ipsec_connection_and_remote_end()
    {
        var t = new VpnTunnel
        {
            Name = "branch",
            Enabled = true,
            RemoteEndpoint = "203.0.113.10",
            LocalSubnet = "192.168.1.0/24",
            RemoteSubnet = "192.168.2.0/24",
            AuthMode = VpnAuthMode.Psk,
            PreSharedKey = "supersecret"
        };

        var cmds = ScalanceCliCommands.BuildSetVpnTunnel(t);

        // Real S615 IPsec syntax (PH_SCALANCE-S615-CLI_76 pp. 697-732)
        cmds.Should().Contain("ipsec");
        cmds.Should().Contain("remote-end name branch-remote");
        cmds.Should().Contain("addr 203.0.113.10");
        cmds.Should().Contain("conn-mode standard");
        cmds.Should().Contain("subnet 192.168.2.0/24");
        cmds.Should().Contain("connection name branch");
        cmds.Should().Contain("rmend name branch-remote");
        cmds.Should().Contain("loc-subnet 192.168.1.0/24");
        cmds.Should().Contain("k-proto ikev2");
        cmds.Should().Contain("operation start");
        cmds.Should().Contain("authentication");
        cmds.Should().Contain("auth psk supersecret");
        cmds.Should().Contain("no shutdown");
        cmds.Last().Should().Be("write memory");
    }

    [Fact]
    public void BuildSetVpnTunnel_disabled_emits_shutdown()
    {
        var t = new VpnTunnel
        {
            Name = "off",
            Enabled = false,
            RemoteEndpoint = "198.51.100.1",
            LocalSubnet = "10.0.0.0/24",
            RemoteSubnet = "10.0.1.0/24",
            AuthMode = VpnAuthMode.Certificate,
        };
        var cmds = ScalanceCliCommands.BuildSetVpnTunnel(t);

        // Real S615 syntax: disabled VPN uses "operation disabled" + "shutdown"
        cmds.Should().Contain("operation disabled");
        cmds.Should().Contain("shutdown");
    }

    [Fact]
    public void BuildSetVpnTunnel_emits_phase1_phase2_subcommands_from_settings()
    {
        var t = new VpnTunnel
        {
            Name = "p1p2",
            Enabled = true,
            RemoteEndpoint = "203.0.113.20",
            LocalSubnet = "10.10.0.0/24",
            RemoteSubnet = "10.20.0.0/24",
            AuthMode = VpnAuthMode.Psk,
            PreSharedKey = "k",
            Ike = new IkeSettings
            {
                Encryption = "aes256cbc",
                Hash = "sha256",
                DhGroup = "14",
                LifetimeMinutes = 480,
            },
            Esp = new EspSettings
            {
                Encryption = "aes256cbc",
                Hash = "sha256",
                PfsGroup = "14",
                LifetimeMinutes = 60,
            },
        };

        var cmds = ScalanceCliCommands.BuildSetVpnTunnel(t).ToList();

        // Phase 1 sub-commands inside cli(config-ipsec-conn-phase1)# (pp. 734-744)
        cmds.Should().Contain("phase 1");
        // "no default-ciphers" must appear so user-supplied values take effect (p. 735-736)
        cmds.Where(c => c == "no default-ciphers").Should().HaveCount(2,
            "both phase 1 and phase 2 must clear default ciphers before custom values are set");
        cmds.Should().Contain("ike-encryption aes256cbc");
        cmds.Should().Contain("ike-auth sha256");
        // Verified p. 742: `ike-keyderivation dhgroup <N>`, NOT plain `ike-keyderivation <N>`.
        cmds.Should().Contain("ike-keyderivation dhgroup 14");
        // Lifetime unit is MINUTES per manual p. 744 — not seconds.
        cmds.Should().Contain("ike-lifetime 480");

        // Phase 2 sub-commands inside cli(config-ipsec-conn-phase2)# (pp. 745-754)
        cmds.Should().Contain("phase 2");
        cmds.Should().Contain("esp-encryption aes256cbc");
        cmds.Should().Contain("esp-auth sha256");
        cmds.Should().Contain("esp-keyderivation dhgroup 14");
        cmds.Should().Contain("lifetime 60");
    }

    [Fact]
    public void BuildSetVpnTunnel_emits_esp_keyderivation_none_for_no_pfs()
    {
        // Manual p. 751: esp-keyderivation {none | dhgroup <N>}.
        // `none` must be sent explicitly to disable PFS — not omitted.
        var t = new VpnTunnel
        {
            Name = "nopfs",
            Enabled = true,
            RemoteEndpoint = "203.0.113.30",
            AuthMode = VpnAuthMode.Psk,
            PreSharedKey = "k",
            Esp = new EspSettings { PfsGroup = "none" },
        };
        var cmds = ScalanceCliCommands.BuildSetVpnTunnel(t);
        cmds.Should().Contain("esp-keyderivation none");
        cmds.Should().NotContain(c => c.StartsWith("esp-keyderivation dhgroup"));
    }

    [Theory]
    [InlineData("aes256")]          // missing cipher-mode suffix
    [InlineData("des")]             // unsupported
    [InlineData("rc4")]             // never supported
    public void BuildSetVpnTunnel_rejects_invalid_encryption(string enc)
    {
        var t = new VpnTunnel
        {
            Name = "x", Enabled = true, RemoteEndpoint = "1.2.3.4",
            AuthMode = VpnAuthMode.Psk, PreSharedKey = "k",
            Ike = new IkeSettings { Encryption = enc }
        };
        var act = () => ScalanceCliCommands.BuildSetVpnTunnel(t);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("3")]               // not in {1,2,5,14,15,16,17,18}
    [InlineData("19")]
    [InlineData("abc")]
    public void BuildSetVpnTunnel_rejects_invalid_dh_group(string dh)
    {
        var t = new VpnTunnel
        {
            Name = "x", Enabled = true, RemoteEndpoint = "1.2.3.4",
            AuthMode = VpnAuthMode.Psk, PreSharedKey = "k",
            Ike = new IkeSettings { DhGroup = dh }
        };
        var act = () => ScalanceCliCommands.BuildSetVpnTunnel(t);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildCreateFirewallRule_uses_0_0_0_0_0_for_wildcard_not_asterisk()
    {
        // Manual sec 12.3.4.31 p. 628: srcip/dstip accept <ip|subnet|range>,
        // where all-addresses is "0.0.0.0/0". The '*' wildcard is NOT valid.
        var rule = new FirewallRule
        {
            From = "vlan 1", To = "Device",
            Action = FirewallAction.Accept,
            SourceCidr = "", DestinationCidr = "",
            Service = "All"
        };
        var cmds = ScalanceCliCommands.BuildCreateFirewallRule(rule);
        cmds.Should().Contain(c => c.Contains("srcip 0.0.0.0/0 dstip 0.0.0.0/0"));
        cmds.Should().NotContain(c => c.Contains("srcip *") || c.Contains("dstip *"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    [InlineData(-1)]
    public void BuildDeleteFirewallRule_rejects_out_of_range_idx(int idx)
    {
        // Manual sec 12.3.4.32 p. 630: idx range 1-128.
        var act = () => ScalanceCliCommands.BuildDeleteFirewallRule(idx);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildDeleteAllFirewallRules_emits_no_ipv4rule_all()
    {
        var cmds = ScalanceCliCommands.BuildDeleteAllFirewallRules();
        cmds.Should().Contain("no ipv4rule all");
        cmds[0].Should().Be("configure terminal");
        cmds[^1].Should().Be("write memory");
    }

    [Fact]
    public void BuildSetPredefinedRule_emits_full_manual_form()
    {
        // Manual sec 12.3.4.57 p. 653:
        //   prerule <svc> ipv4 int vlan <N> enabled|disabled
        var svc = new PredefinedFirewallService
        {
            ServiceName = "https",
            LocalAccess = true,
            ExternalAccess = false
        };
        var cmds = ScalanceCliCommands.BuildSetPredefinedRule(svc);
        cmds.Should().Contain("prerule https ipv4 int vlan 1 enabled");
        cmds.Should().Contain("prerule https ipv4 int vlan 2 disabled");
    }

    [Fact]
    public void BuildSetPredefinedRule_rejects_unknown_service()
    {
        var svc = new PredefinedFirewallService { ServiceName = "madeup" };
        var act = () => ScalanceCliCommands.BuildSetPredefinedRule(svc);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildCreateFirewallRule_rejects_blank_from_or_to()
    {
        // Manual p. 627: `from` / `to` require a valid iftype keyword. Empty
        // values would emit `ipv4rule from  to  srcip …` which the device rejects.
        var blankFrom = new FirewallRule { From = "", To = "Device", Action = FirewallAction.Accept };
        var a1 = () => ScalanceCliCommands.BuildCreateFirewallRule(blankFrom);
        a1.Should().Throw<ArgumentException>();

        var blankTo = new FirewallRule { From = "vlan 1", To = "   ", Action = FirewallAction.Accept };
        var a2 = () => ScalanceCliCommands.BuildCreateFirewallRule(blankTo);
        a2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildCreateFirewallRule_rejects_prior_above_127()
    {
        var rule = new FirewallRule
        {
            From = "vlan 1", To = "Device",
            Action = FirewallAction.Accept, Index = 128
        };
        var act = () => ScalanceCliCommands.BuildCreateFirewallRule(rule);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildSetVpnTunnel_rejects_name_over_122_chars()
    {
        // Manual sec 12.4.3.2 p. 699: connection name max 122 chars.
        var t = new VpnTunnel
        {
            Name = new string('a', 123),
            Enabled = true, RemoteEndpoint = "1.2.3.4",
            AuthMode = VpnAuthMode.Psk, PreSharedKey = "k"
        };
        var act = () => ScalanceCliCommands.BuildSetVpnTunnel(t);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetVpnTunnel_rejects_lifetime_below_10_min()
    {
        // Manual p. 744: ike-lifetime range 10..2500000 minutes.
        var t = new VpnTunnel
        {
            Name = "x", Enabled = true, RemoteEndpoint = "1.2.3.4",
            AuthMode = VpnAuthMode.Psk, PreSharedKey = "k",
            Ike = new IkeSettings { LifetimeMinutes = 5 }
        };
        var act = () => ScalanceCliCommands.BuildSetVpnTunnel(t);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildSetVpnTunnel_certificate_auth_emits_cacert_localcert()
    {
        var t = new VpnTunnel
        {
            Name = "certvpn",
            Enabled = true,
            RemoteEndpoint = "203.0.113.40",
            AuthMode = VpnAuthMode.Certificate,
            LocalCertificateName = "site-a.crt",
        };
        var cmds = ScalanceCliCommands.BuildSetVpnTunnel(t);

        // PH_SCALANCE-S615-CLI_76 p. 727: auth cacert <ca> localcert <local>
        cmds.Should().Contain("auth cacert site-a.crt localcert site-a.crt");
        cmds.Should().NotContain(c => c.StartsWith("auth psk"));
    }

    [Fact]
    public void BuildSetInterface_emits_dnsclient_mode_for_dns_servers()
    {
        var cfg = new InterfaceIpConfig
        {
            InterfaceName = "vlan1",
            DhcpEnabled = false,
            IpAddress = "192.168.1.1",
            PrefixLength = 24,
            DnsServers = { "8.8.8.8", "1.1.1.1" },
        };

        var cmds = ScalanceCliCommands.BuildSetInterface(cfg).ToList();

        // S615 CLI manual sec 9.7 (pp. 408-417): DNS uses dnsclient mode, not "ip name-server".
        cmds.Should().Contain("dnsclient");
        cmds.Should().Contain("no shutdown");
        cmds.Should().Contain("server type manual");
        cmds.Should().Contain("manual srv 8.8.8.8");
        cmds.Should().Contain("manual srv 1.1.1.1");
        cmds.Should().NotContain(c => c.StartsWith("ip name-server"));

        // dnsclient block must come after the interface config and before "end"
        var dnsIdx = cmds.IndexOf("dnsclient");
        var endIdx = cmds.IndexOf("end");
        dnsIdx.Should().BeLessThan(endIdx);
    }

    [Theory]
    [InlineData(0, "0.0.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(24, "255.255.255.0")]
    [InlineData(30, "255.255.255.252")]
    [InlineData(32, "255.255.255.255")]
    public void PrefixToMask_known_values(int prefix, string expected)
    {
        ScalanceCliCommands.PrefixToMask(prefix).Should().Be(expected);
    }

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.252.0", 22)]
    [InlineData("255.0.0.0", 8)]
    public void MaskToPrefix_roundtrips(string mask, int expected)
    {
        ScalanceCliCommands.MaskToPrefix(mask).Should().Be(expected);
    }
}
