using FluentAssertions;
using Scalance.Core.Models;
using Scalance.Drivers;

namespace Scalance.Tests;

/// <summary>
/// Coverage gate: every command verb emitted by ScalanceCliCommands.* must be
/// in the allow-list below. The allow-list is the executable mirror of the
/// "Verified from primary sources" table in docs/VERIFICATION.md.
///
/// If a builder starts emitting a new verb (e.g. "ip domain", "qos", ...) this
/// test fails and forces the developer to either:
///   (a) cite a primary source for the new verb and add it here, OR
///   (b) revert the builder change.
///
/// This is the test that the verify.ps1 / build hook relies on to catch
/// drift between code and the verification document.
/// </summary>
public class CliCommandCoverageTests
{
    // Verbs that have a citation in docs/VERIFICATION.md ("Verified from primary
    // sources" table). Keep alphabetised. Sub-mode keywords (e.g. "ipv4", "fqdn-name")
    // appear as positional operands of the verbs above and are not separately listed.
    private static readonly HashSet<string> AllowedFirstTokens = new(StringComparer.Ordinal)
    {
        // mode navigation
        "configure", "end", "exit", "no", "write",

        // VLAN / bridge (S615 CLI manual pp. 242, 249-267)
        "base", "vlan", "name", "ports",

        // L3 interface / static route (pp. 331-340)
        "interface", "ip",

        // DNS (sec 9.7, pp. 408-417)
        "dnsclient", "server", "manual",

        // NTP (pp. 215-221) and clock
        "ntp", "clock",

        // IPsec (pp. 697-754)
        "ipsec", "remote-end", "addr", "conn-mode", "subnet",
        "connection", "rmend", "loc-subnet", "k-proto", "operation",
        "authentication", "auth", "phase", "shutdown",
        "default-ciphers",
        "ike-encryption", "ike-auth", "ike-keyderivation", "ike-lifetime",
        "esp-encryption", "esp-auth", "esp-keyderivation", "lifetime",

        // Firewall (pp. 591-646)
        "firewall", "ipv4rule", "prerule",

        // Events / syslog (pp. 811, 822-825)
        "events", "syslogserver",

        // System (sec 5.1.11.12 p. 98-99)
        "system",

        // Config save/restore (sec 5.4.3 pp. 140-142)
        "configbackup",

        // SNMP agent controls (sec 9.8 pp. 437-452)
        "snmpagent", "snmp",

        // Device restart (sec 5.3.1 p. 130-131)
        "restart",

        // Event severity (sec 13.1.10.11 pp. 820-821)
        "severity",
    };

    // "no <verb> ..." — when the first token is "no", validate the second token.
    private static string FirstSignificantToken(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        if (parts[0] == "no" && parts.Length >= 2) return parts[1];
        return parts[0];
    }

    private static IEnumerable<string> AllBuilderOutputs()
    {
        // VLAN: representative payload exercising name + tagged + untagged + multi-vlan
        var vlans = new List<Vlan>
        {
            new Vlan { Id = 10, Name = "office",
                Ports =
                {
                    new VlanPortMembership(1, VlanMemberMode.Untagged),
                    new VlanPortMembership(2, VlanMemberMode.Tagged),
                } },
            new Vlan { Id = 20, Name = "iot",
                Ports = { new VlanPortMembership(3, VlanMemberMode.Untagged) } },
        };
        foreach (var c in ScalanceCliCommands.BuildSetVlans(vlans)) yield return c;

        // Interface: static IP + default gateway + DNS
        var ifStatic = new InterfaceIpConfig
        {
            InterfaceName = "vlan1",
            DhcpEnabled = false,
            IpAddress = "192.168.1.1",
            PrefixLength = 24,
            DefaultGateway = "192.168.1.254",
            DnsServers = { "8.8.8.8", "1.1.1.1" },
        };
        foreach (var c in ScalanceCliCommands.BuildSetInterface(ifStatic)) yield return c;

        // Interface: DHCP variant (different code path)
        var ifDhcp = new InterfaceIpConfig { InterfaceName = "vlan2", DhcpEnabled = true };
        foreach (var c in ScalanceCliCommands.BuildSetInterface(ifDhcp)) yield return c;

        // VPN: PSK + full phase 1/2 settings
        var vpnPsk = new VpnTunnel
        {
            Name = "branch",
            Enabled = true,
            RemoteEndpoint = "203.0.113.10",
            LocalSubnet = "192.168.1.0/24",
            RemoteSubnet = "192.168.2.0/24",
            AuthMode = VpnAuthMode.Psk,
            PreSharedKey = "k",
            Ike = new IkeSettings { Encryption = "aes256cbc", Hash = "sha256",
                                    DhGroup = "14", LifetimeMinutes = 480 },
            Esp = new EspSettings { Encryption = "aes256cbc", Hash = "sha256",
                                    PfsGroup = "14", LifetimeMinutes = 60 },
        };
        foreach (var c in ScalanceCliCommands.BuildSetVpnTunnel(vpnPsk)) yield return c;

        // VPN: certificate auth + disabled (different code paths)
        var vpnCert = new VpnTunnel
        {
            Name = "off",
            Enabled = false,
            RemoteEndpoint = "198.51.100.1",
            AuthMode = VpnAuthMode.Certificate,
            LocalCertificateName = "site.crt",
            Esp = new EspSettings { PfsGroup = "none" },
        };
        foreach (var c in ScalanceCliCommands.BuildSetVpnTunnel(vpnCert)) yield return c;

        // Firewall: create + delete + predefined service
        var rule = new FirewallRule
        {
            From = "vlan 1", To = "vlan 2",
            SourceCidr = "10.0.0.0/24", DestinationCidr = "0.0.0.0/0",
            Action = FirewallAction.Accept, Service = "https",
            Log = true, Index = 1,
        };
        foreach (var c in ScalanceCliCommands.BuildCreateFirewallRule(rule)) yield return c;
        foreach (var c in ScalanceCliCommands.BuildDeleteFirewallRule(5)) yield return c;
        foreach (var c in ScalanceCliCommands.BuildSetPredefinedRule(
            new PredefinedFirewallService { ServiceName = "https",
                LocalAccess = true, ExternalAccess = false })) yield return c;

        // System name + Syslog (new 2026-04)
        foreach (var c in ScalanceCliCommands.BuildSetSystemName("edge-01")) yield return c;
        foreach (var c in ScalanceCliCommands.BuildAddSyslogServer(
            new SyslogServer { Host = "10.0.0.5", Port = 6514, UseTls = true })) yield return c;
        foreach (var c in ScalanceCliCommands.BuildRemoveSyslogServer(
            new SyslogServer { Host = "10.0.0.5" })) yield return c;

        // Config backup (sec 5.4.3 pp. 140-142)
        foreach (var c in ScalanceCliCommands.BuildConfigBackupCreate("nightly")) yield return c;
        foreach (var c in ScalanceCliCommands.BuildConfigBackupRestore("nightly")) yield return c;
        foreach (var c in ScalanceCliCommands.BuildConfigBackupDelete("nightly")) yield return c;

        // SNMP agent controls (sec 9.8 pp. 437-452)
        foreach (var c in ScalanceCliCommands.BuildSetSnmpAgentEnabled(true)) yield return c;
        foreach (var c in ScalanceCliCommands.BuildSetSnmpAgentEnabled(false)) yield return c;
        foreach (var c in ScalanceCliCommands.BuildSetSnmpAgentVersion(SnmpAgentVersionPolicy.V3Only)) yield return c;
        foreach (var c in ScalanceCliCommands.BuildSetSnmpAgentPort(8161)) yield return c;
        foreach (var c in ScalanceCliCommands.BuildResetSnmpAgentPort()) yield return c;

        // Device restart (sec 5.3.1 p. 130-131)
        yield return ScalanceCliCommands.FormatRestartCommand(RestartMode.Current);
        yield return ScalanceCliCommands.FormatRestartCommand(RestartMode.Memory);
        yield return ScalanceCliCommands.FormatRestartCommand(RestartMode.Factory);

        // Event severity (sec 13.1.10.11 pp. 820-821)
        foreach (var c in ScalanceCliCommands.BuildSetEventSeverity(EventSink.Syslog, EventSeverity.Warning))
            yield return c;
    }

    [Fact]
    public void Every_emitted_command_starts_with_a_whitelisted_verb()
    {
        var unknown = new List<string>();
        foreach (var cmd in AllBuilderOutputs())
        {
            var verb = FirstSignificantToken(cmd);
            if (verb.Length == 0) continue;
            if (!AllowedFirstTokens.Contains(verb))
                unknown.Add($"  '{cmd}'  (verb: '{verb}')");
        }

        unknown.Should().BeEmpty(
            "every CLI verb must be backed by a citation in docs/VERIFICATION.md " +
            "before being emitted to a real device. Unknown verbs:\n" +
            string.Join("\n", unknown) +
            "\nFix: cite the verb in VERIFICATION.md and add it to AllowedFirstTokens, " +
            "or revert the builder change.");
    }

    [Fact]
    public void Every_command_is_nonempty_single_line()
    {
        foreach (var cmd in AllBuilderOutputs())
        {
            cmd.Should().NotBeNullOrWhiteSpace();
            cmd.Should().NotContain("\n");
            cmd.Should().NotContain("\r");
        }
    }
}
