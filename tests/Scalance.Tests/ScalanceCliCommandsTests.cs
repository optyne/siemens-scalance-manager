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
        cmds.Last().Should().Be("write startup-config");
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
    [InlineData(0)]
    [InlineData(4095)]
    public void BuildSetVlans_rejects_out_of_range_id(int id)
    {
        // Manual p. 250: vlan-id range 1..4094. Device would reject 0 or 4095+.
        var vlans = new List<Vlan> { new Vlan { Id = id, Name = "x" } };
        var act = () => ScalanceCliCommands.BuildSetVlans(vlans);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildSetVlans_rejects_name_over_32_chars()
    {
        // Manual p. 265: VLAN name max 32 characters.
        var vlans = new List<Vlan> { new Vlan { Id = 10, Name = new string('x', 33) } };
        var act = () => ScalanceCliCommands.BuildSetVlans(vlans);
        act.Should().Throw<ArgumentException>();
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
        cmds.Last().Should().Be("write startup-config");
    }

    [Fact]
    public void BuildSetInterface_static_emits_no_ip_address_before_ip_address()
    {
        // Manual sec 9.1.3.2 p. 339 requires `no ip address` before setting
        // a static address, so a transition from DHCP → static is accepted.
        var cfg = new InterfaceIpConfig
        {
            InterfaceName = "vlan 1",
            DhcpEnabled = false,
            IpAddress = "10.0.0.1",
            PrefixLength = 24,
        };
        var cmds = ScalanceCliCommands.BuildSetInterface(cfg).ToList();
        var noIp = cmds.IndexOf("no ip address");
        var setIp = cmds.FindIndex(c => c.StartsWith("ip address 10.0.0.1 "));
        noIp.Should().BeGreaterThan(0, "no ip address must precede static assignment");
        noIp.Should().BeLessThan(setIp);
    }

    [Fact]
    public void BuildSetInterface_dhcp_clears_static_first()
    {
        // Switching to DHCP while a previous static address is in place must
        // also go through `no ip address` so DHCP is unambiguous.
        var cfg = new InterfaceIpConfig { InterfaceName = "vlan 1", DhcpEnabled = true };
        var cmds = ScalanceCliCommands.BuildSetInterface(cfg).ToList();
        var noIp = cmds.IndexOf("no ip address");
        var dhcp = cmds.IndexOf("ip address dhcp");
        noIp.Should().BeGreaterThan(0);
        noIp.Should().BeLessThan(dhcp);
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

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("1.2.3.256")]
    [InlineData("1.2.3")]
    [InlineData("1.1.1.1;evil")]
    public void BuildSetInterface_rejects_non_ipv4_address(string bad)
    {
        var cfg = new InterfaceIpConfig
        {
            InterfaceName = "vlan 1", DhcpEnabled = false,
            IpAddress = bad, PrefixLength = 24
        };
        var act = () => ScalanceCliCommands.BuildSetInterface(cfg);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetInterface_rejects_non_ipv4_gateway()
    {
        var cfg = new InterfaceIpConfig
        {
            InterfaceName = "vlan 1", DhcpEnabled = false,
            IpAddress = "10.0.0.1", PrefixLength = 24,
            DefaultGateway = "not-an-ip"
        };
        var act = () => ScalanceCliCommands.BuildSetInterface(cfg);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetInterface_rejects_interface_name_with_newline()
    {
        var cfg = new InterfaceIpConfig { InterfaceName = "vlan 1\ninject", DhcpEnabled = true };
        var act = () => ScalanceCliCommands.BuildSetInterface(cfg);
        act.Should().Throw<ArgumentException>();
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
        cmds.Last().Should().Be("write startup-config");
    }

    [Fact]
    public void BuildSetVpnTunnel_disabled_uses_operation_only_not_global_shutdown()
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

        // Manual sec 12.4.3.18 p. 710: `shutdown` disables the WHOLE IPsec
        // subsystem — emitting it in a per-tunnel flow would kill every
        // other active tunnel. Per-tunnel disable must use `operation
        // disabled` only; `no shutdown` stays as the idempotent global-on.
        cmds.Should().Contain("operation disabled");
        cmds.Should().Contain("no shutdown");
        cmds.Should().NotContain("shutdown"); // fluent .NotContain is exact match
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
        cmds[^1].Should().Be("write startup-config");
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

    [Theory]
    [InlineData("dcp")]        // Manual pp. 647-665: no such prerule.
    [InlineData("syslog")]     // Manual pp. 647-665: no such prerule.
    [InlineData("openvpn")]    // Manual pp. 647-665: no such prerule.
    [InlineData("sinemarc")]   // Manual pp. 647-665: no such prerule.
    public void BuildSetPredefinedRule_rejects_services_absent_from_manual(string svcName)
    {
        var svc = new PredefinedFirewallService { ServiceName = svcName };
        var act = () => ScalanceCliCommands.BuildSetPredefinedRule(svc);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("cloudconnector")]  // Manual p. 648.
    [InlineData("vxlan")]           // Manual p. 664.
    public void BuildSetPredefinedRule_accepts_services_listed_in_manual(string svcName)
    {
        var svc = new PredefinedFirewallService { ServiceName = svcName, LocalAccess = true };
        var cmds = ScalanceCliCommands.BuildSetPredefinedRule(svc);
        cmds.Should().Contain(c => c.StartsWith($"prerule {svcName} ipv4 int vlan"));
    }

    // ---- ping (manual sec 5.1.8 p. 86) ----

    [Fact]
    public void FormatPingCommand_ipv4_without_options()
    {
        ScalanceCliCommands.FormatPingCommand("10.0.0.1")
            .Should().Be("ping 10.0.0.1");
    }

    [Fact]
    public void FormatPingCommand_fqdn_adds_keyword()
    {
        ScalanceCliCommands.FormatPingCommand("host.example.com")
            .Should().Be("ping fqdn-name host.example.com");
    }

    [Fact]
    public void FormatPingCommand_appends_options_in_manual_order()
    {
        var line = ScalanceCliCommands.FormatPingCommand("10.0.0.1",
            new PingOptions { SizeBytes = 100, Count = 5, TimeoutSeconds = 2 });
        // Manual p. 86 option order: size, count, timeout.
        line.Should().Be("ping 10.0.0.1 size 100 count 5 timeout 2");
    }

    [Fact]
    public void FormatPingCommand_ipv6_routes_to_ipv6_command()
    {
        // Manual sec 5.1.9 p. 87: IPv6 has a separate `ping ipv6 <addr>`
        // command. Previously our builder sent `ping fqdn-name 2001:db8::1`
        // for IPv6 literals, which the device rejects outright.
        ScalanceCliCommands.FormatPingCommand("2001:db8::1")
            .Should().Be("ping ipv6 2001:db8::1");
    }

    [Fact]
    public void FormatPingCommand_ipv6_uses_count_before_size_per_manual()
    {
        // Manual p. 87 shows the IPv6 option order as count, size, …, timeout
        // — distinct from the IPv4 p. 86 order (size, count, timeout).
        var line = ScalanceCliCommands.FormatPingCommand("::1",
            new PingOptions { SizeBytes = 100, Count = 5, TimeoutSeconds = 2 });
        line.Should().Be("ping ipv6 ::1 count 5 size 100 timeout 2");
    }

    [Theory]
    [InlineData(-1, null, null)]
    [InlineData(2081, null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, 11, null)]
    [InlineData(null, null, 0)]
    [InlineData(null, null, 101)]
    public void FormatPingCommand_rejects_out_of_range_options(int? size, int? count, int? timeout)
    {
        var opts = new PingOptions { SizeBytes = size, Count = count, TimeoutSeconds = timeout };
        var act = () => ScalanceCliCommands.FormatPingCommand("10.0.0.1", opts);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FormatPingCommand_rejects_fqdn_over_100_chars()
    {
        var act = () => ScalanceCliCommands.FormatPingCommand(new string('a', 101));
        act.Should().Throw<ArgumentException>();
    }

    // ---- restart (manual sec 5.3.1 p. 130-131) ----

    [Theory]
    [InlineData(RestartMode.Current, "restart")]
    [InlineData(RestartMode.Memory,  "restart memory")]
    [InlineData(RestartMode.Factory, "restart factory")]
    public void FormatRestartCommand_matches_manual(RestartMode mode, string expected)
    {
        ScalanceCliCommands.FormatRestartCommand(mode).Should().Be(expected);
    }

    // ---- SNMP agent (manual sec 9.8 pp. 437-452) ----

    [Fact]
    public void BuildSetSnmpAgentEnabled_emits_snmpagent_or_no_snmpagent()
    {
        ScalanceCliCommands.BuildSetSnmpAgentEnabled(true)
            .Should().Contain("snmpagent").And.NotContain("no snmpagent");
        ScalanceCliCommands.BuildSetSnmpAgentEnabled(false)
            .Should().Contain("no snmpagent");
    }

    [Theory]
    [InlineData(SnmpAgentVersionPolicy.All,    "snmp agent version all")]
    [InlineData(SnmpAgentVersionPolicy.V3Only, "snmp agent version v3only")]
    public void BuildSetSnmpAgentVersion_maps_policy(SnmpAgentVersionPolicy policy, string expected)
    {
        ScalanceCliCommands.BuildSetSnmpAgentVersion(policy).Should().Contain(expected);
    }

    [Fact]
    public void BuildSetSnmpAgentPort_round_trip()
    {
        ScalanceCliCommands.BuildSetSnmpAgentPort(8161)
            .Should().Contain("snmpagent port 8161");
    }

    [Fact]
    public void BuildResetSnmpAgentPort_emits_no_snmpagent_port()
    {
        // Manual sec 9.8.2.17 p. 452 — only way to restore the default 161,
        // since BuildSetSnmpAgentPort's 1024-65535 range excludes it.
        var cmds = ScalanceCliCommands.BuildResetSnmpAgentPort();
        cmds.Should().Contain("no snmpagent port");
        cmds[0].Should().Be("configure terminal");
        cmds[^1].Should().Be("write startup-config");
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(65536)]
    [InlineData(161)]  // default — but per manual range is 1024-65535, so 161 is out of range!
    public void BuildSetSnmpAgentPort_rejects_out_of_range(int port)
    {
        var act = () => ScalanceCliCommands.BuildSetSnmpAgentPort(port);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- configbackup (manual sec 5.4.3 pp. 140-142) ----

    [Fact]
    public void BuildConfigBackupCreate_emits_global_config_wrapper()
    {
        var cmds = ScalanceCliCommands.BuildConfigBackupCreate("nightly");
        cmds[0].Should().Be("configure terminal");
        cmds.Should().Contain("configbackup create nightly");
        cmds[^1].Should().Be("end");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildConfigBackupCreate_rejects_blank_name(string name)
    {
        var act = () => ScalanceCliCommands.BuildConfigBackupCreate(name);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildConfigBackupCreate_rejects_name_over_64_chars()
    {
        var act = () => ScalanceCliCommands.BuildConfigBackupCreate(new string('x', 65));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildConfigBackupCreate_rejects_name_with_space()
    {
        var act = () => ScalanceCliCommands.BuildConfigBackupCreate("has space");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildConfigBackupRestore_and_Delete_use_correct_verb()
    {
        ScalanceCliCommands.BuildConfigBackupRestore("nightly")
            .Should().Contain("configbackup restore nightly");
        ScalanceCliCommands.BuildConfigBackupDelete("nightly")
            .Should().Contain("configbackup delete nightly");
    }

    [Fact]
    public void ParseConfigBackupNames_extracts_first_column_skipping_headers()
    {
        var output = @"Available memory: 2.4 MB
Name        Size
----        ----
nightly     12 KB
weekly      15 KB
";
        var names = ScalanceCliCommands.ParseConfigBackupNames(output);
        names.Should().BeEquivalentTo(new[] { "nightly", "weekly" });
    }

    // ---- traceroute (manual sec 5.1.10 p. 88) ----

    [Fact]
    public void FormatTraceRouteCommand_ipv4()
    {
        ScalanceCliCommands.FormatTraceRouteCommand("10.0.0.1")
            .Should().Be("traceroute ip 10.0.0.1");
    }

    [Fact]
    public void FormatTraceRouteCommand_ipv6()
    {
        ScalanceCliCommands.FormatTraceRouteCommand("2001:db8::1")
            .Should().Be("traceroute ipv6 2001:db8::1");
    }

    [Fact]
    public void FormatTraceRouteCommand_rejects_fqdn()
    {
        // Unlike ping (p. 86), manual p. 88 only lists ip/ipv6 keywords —
        // no fqdn-name branch. Explicit rejection avoids sending a line the
        // device would reject.
        var act = () => ScalanceCliCommands.FormatTraceRouteCommand("host.example.com");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(1, "192.168.1.1",  "ntp server id 1 ipv4 192.168.1.1")]
    [InlineData(2, "pool.ntp.org", "ntp server id 2 fqdn-name pool.ntp.org")]
    public void FormatNtpServerLine_branches_ipv4_and_fqdn(int id, string host, string expected)
    {
        ScalanceCliCommands.FormatNtpServerLine(id, host).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void FormatNtpServerLine_rejects_out_of_range_id(int id)
    {
        var act = () => ScalanceCliCommands.FormatNtpServerLine(id, "1.2.3.4");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FormatNtpServerLine_rejects_fqdn_over_100_chars()
    {
        var host = new string('a', 101);
        var act = () => ScalanceCliCommands.FormatNtpServerLine(1, host);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has\nnewline")]
    public void FormatNtpServerLine_rejects_ssh_unsafe_fqdn(string host)
    {
        var act = () => ScalanceCliCommands.FormatNtpServerLine(1, host);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FormatNtpServerLine_rejects_bsd_short_ipv4_as_fqdn()
    {
        // "1.2.3" used to parse as 1.2.0.3 via IPAddress.TryParse and be sent
        // verbatim on the ipv4 branch. Now the strict dotted-quad check pushes
        // it to the FQDN branch — which is itself the correct behaviour.
        ScalanceCliCommands.FormatNtpServerLine(1, "1.2.3")
            .Should().Be("ntp server id 1 fqdn-name 1.2.3");
    }

    [Fact]
    public void BuildSetSystemName_emits_system_name_not_hostname()
    {
        // Manual p. 98-99: SCALANCE uses `system name <name>`, NOT the
        // Cisco-IOS `hostname`. Max 255 characters.
        var cmds = ScalanceCliCommands.BuildSetSystemName("edge-router-01");
        cmds.Should().Contain("system name edge-router-01");
        cmds.Should().NotContain(c => c.StartsWith("hostname "));
        cmds[0].Should().Be("configure terminal");
        cmds[^1].Should().Be("write startup-config");
    }

    [Fact]
    public void BuildSetSystemName_rejects_over_255_chars()
    {
        var act = () => ScalanceCliCommands.BuildSetSystemName(new string('x', 256));
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("has\nnewline")]
    [InlineData("has\rcr")]
    [InlineData("has\"quote")]
    public void BuildSetSystemName_rejects_ssh_unsafe_chars(string name)
    {
        // Transport-layer defence — these would break the batched command stream.
        var act = () => ScalanceCliCommands.BuildSetSystemName(name);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FirewallRule_default_uses_space_separated_iftype()
    {
        // Manual p. 627: `from <iftype> [<ifstring>]` — iftype and ifstring
        // are separate tokens. Examples p. 65, 430 use `int vlan 1` with space.
        // `vlan1` (no space) would be parsed as an unknown iftype.
        var rule = new FirewallRule { Action = FirewallAction.Accept };
        rule.From.Should().Be("vlan 1");
        rule.To.Should().Be("vlan 2");

        var cmds = ScalanceCliCommands.BuildCreateFirewallRule(rule);
        cmds.Should().Contain(c => c.StartsWith("ipv4rule from vlan 1 to vlan 2 "));
    }

    [Theory]
    [InlineData("wan 1")]          // invalid iftype
    [InlineData("vlan abc")]       // non-numeric ifstring
    [InlineData("vlan 1 extra")]   // too many tokens
    [InlineData("VLAN 1")]         // case-sensitive: manual uses lowercase 'vlan'
    public void BuildCreateFirewallRule_rejects_invalid_iftype_shape(string fromValue)
    {
        var rule = new FirewallRule { From = fromValue, To = "Device", Action = FirewallAction.Accept };
        var act = () => ScalanceCliCommands.BuildCreateFirewallRule(rule);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("vlan 1")]
    [InlineData("Device")]
    [InlineData("IPsec 3")]
    [InlineData("IPsecall")]
    [InlineData("OpenVPNall")]
    public void BuildCreateFirewallRule_accepts_manual_listed_iftypes(string fromValue)
    {
        var rule = new FirewallRule { From = fromValue, To = "Device", Action = FirewallAction.Accept };
        var cmds = ScalanceCliCommands.BuildCreateFirewallRule(rule);
        cmds.Should().Contain(c => c.StartsWith($"ipv4rule from {fromValue} to Device "));
    }

    [Fact]
    public void BuildCreateFirewallRule_rejects_service_with_space()
    {
        var rule = new FirewallRule
        {
            From = "vlan 1", To = "Device",
            Action = FirewallAction.Accept, Service = "has space",
        };
        var act = () => ScalanceCliCommands.BuildCreateFirewallRule(rule);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildCreateFirewallRule_rejects_ifstring_out_of_range()
    {
        // Manual p. 627-628: ifstring range 0..4094.
        var rule = new FirewallRule { From = "vlan 5000", To = "Device", Action = FirewallAction.Accept };
        var act = () => ScalanceCliCommands.BuildCreateFirewallRule(rule);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetInterface_rejects_more_than_three_dns_servers()
    {
        // Manual p. 414: maximum of three DNS servers.
        var cfg = new InterfaceIpConfig
        {
            InterfaceName = "vlan 1", DhcpEnabled = true,
            DnsServers = { "1.1.1.1", "2.2.2.2", "3.3.3.3", "4.4.4.4" }
        };
        var act = () => ScalanceCliCommands.BuildSetInterface(cfg);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildCreateFirewallRule_rejects_srcip_with_newline()
    {
        var rule = new FirewallRule
        {
            From = "vlan 1", To = "Device",
            SourceCidr = "10.0.0.0/24\ninject",
            Action = FirewallAction.Accept,
        };
        var act = () => ScalanceCliCommands.BuildCreateFirewallRule(rule);
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
    public void BuildSetVpnTunnel_rejects_psk_over_255_chars()
    {
        // Manual sec 12.4.6.2 p. 729: auth psk <string(255)>.
        var t = new VpnTunnel
        {
            Name = "t", Enabled = true, RemoteEndpoint = "1.2.3.4",
            AuthMode = VpnAuthMode.Psk, PreSharedKey = new string('x', 256),
        };
        var act = () => ScalanceCliCommands.BuildSetVpnTunnel(t);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetVpnTunnel_rejects_psk_with_newline()
    {
        // Transport-layer defence — newline would break the SSH command stream.
        var t = new VpnTunnel
        {
            Name = "t", Enabled = true, RemoteEndpoint = "1.2.3.4",
            AuthMode = VpnAuthMode.Psk, PreSharedKey = "has\ninjection",
        };
        var act = () => ScalanceCliCommands.BuildSetVpnTunnel(t);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetVpnTunnel_rejects_cert_name_over_255_chars()
    {
        // Manual sec 12.4.6.1 p. 728: auth cacert <string(255)> localcert <string(255)>.
        var t = new VpnTunnel
        {
            Name = "t", Enabled = true, RemoteEndpoint = "1.2.3.4",
            AuthMode = VpnAuthMode.Certificate, LocalCertificateName = new string('c', 256),
        };
        var act = () => ScalanceCliCommands.BuildSetVpnTunnel(t);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("name with space")]
    [InlineData("name\ninject")]
    [InlineData("name\"quote")]
    public void BuildSetVpnTunnel_rejects_name_with_non_token_chars(string name)
    {
        var t = new VpnTunnel
        {
            Name = name, Enabled = true, RemoteEndpoint = "1.2.3.4",
            AuthMode = VpnAuthMode.Psk, PreSharedKey = "k"
        };
        var act = () => ScalanceCliCommands.BuildSetVpnTunnel(t);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetVpnTunnel_rejects_subnet_with_newline()
    {
        var t = new VpnTunnel
        {
            Name = "t", Enabled = true, RemoteEndpoint = "1.2.3.4",
            LocalSubnet = "10.0.0.0/24\ninject",
            AuthMode = VpnAuthMode.Psk, PreSharedKey = "k"
        };
        var act = () => ScalanceCliCommands.BuildSetVpnTunnel(t);
        act.Should().Throw<ArgumentException>();
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
