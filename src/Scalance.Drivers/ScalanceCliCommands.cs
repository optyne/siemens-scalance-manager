using Scalance.Core.Models;

namespace Scalance.Drivers;

/// <summary>
/// Static CLI command builders for SCALANCE devices.
///
/// VERIFICATION STATUS (see docs/VERIFICATION.md):
///   - All CLI commands have been updated to match the official Siemens S615
///     CLI manual: PH_SCALANCE-S615-CLI_76_en-US.pdf.
///   - VLAN: uses vlan/name/ports commands in VLAN config mode (pp. 249-267),
///     prefixed with `base bridge-mode dot1q-vlan` (p. 242) so the device is
///     guaranteed to be in dot1q-vlan mode before VLAN commands are accepted.
///   - IPv4: uses ip address in VLAN interface mode, ip route for default
///     gateway in global config (pp. 331-340).
///   - DNS: uses dnsclient mode with manual srv (pp. 408-417), NOT the
///     Cisco-style ip name-server.
///   - NTP: uses ntp mode with ntp server id syntax (pp. 215-221).
///   - IPsec: uses ipsec/connection name/remote-end name model (pp. 693-732),
///     phase 1 sub-commands ike-encryption / ike-auth / ike-keyderivation /
///     ike-lifetime (pp. 734-744), phase 2 sub-commands esp-encryption /
///     esp-auth / esp-keyderivation / lifetime (pp. 745-754). Both phases
///     emit `no default-ciphers` so user-supplied values take effect.
///   - Port identifier format "0.N" (module.port): verified from WBM manuals.
///
/// Drivers use DryRun mode by default (ScalanceCliDriverBase.DryRun=true) so
/// VLAN/IPv4/VPN writes never hit a real device until an operator confirms them.
/// NTP writes bypass DryRun as the syntax is validated against the CLI manual.
///
/// These builders are pure (no IO) so they can be unit-tested in isolation and
/// reused by S615Driver and XSeriesSwitchDriver (both inherit ScalanceCliDriverBase).
///
/// References:
///   - docs/PH_SCALANCE-S615-CLI_76_en-US.pdf (primary CLI reference)
///   - SIEMENS_PH_SCALANCE-S615-WBM_76.pdf (feature semantics + port naming)
///   - SIEMENS_PH_SCALANCE-XB-200-...-WBM_76.pdf (port naming M.P, no IPsec)
///   - docs/VERIFICATION.md
/// </summary>
public static class ScalanceCliCommands
{
    /// <summary>
    /// VLAN write. Creates VLANs and assigns port membership using real Siemens
    /// S615 CLI syntax from PH_SCALANCE-S615-CLI_76 pp. 249-267.
    ///
    /// Mode hierarchy:
    ///   cli(config)# vlan &lt;id&gt;          -> cli(config-vlan-$$$)#
    ///     name &lt;string&gt;                  (set VLAN name, max 32 chars)
    ///     ports (&lt;port-list&gt;)            (tagged member ports)
    ///       [untagged &lt;port-list&gt;]       (untagged member ports)
    ///       [forbidden &lt;port-list&gt;]      (forbidden ports)
    ///       [name &lt;vlan-name&gt;]           (optional name in ports cmd)
    ///     exit                            -> back to cli(config)#
    /// </summary>
    public static IReadOnlyList<string> BuildSetVlans(IReadOnlyList<Vlan> vlans)
    {
        if (vlans is null) throw new ArgumentNullException(nameof(vlans));

        var cmds = new List<string>
        {
            "configure terminal",
            // S615 CLI manual p. 242: VLAN commands require dot1q-vlan mode.
            // Idempotent — sending it when already in dot1q-vlan mode is a no-op.
            "base bridge-mode dot1q-vlan",
        };

        foreach (var v in vlans.OrderBy(x => x.Id))
        {
            // Enter VLAN configuration mode (creates VLAN if it doesn't exist).
            // S615 CLI manual p. 249-250: vlan <vlan-id(1-4094)>. The device
            // will reject anything outside this range, so fail fast here.
            if (v.Id < 1 || v.Id > 4094)
                throw new ArgumentOutOfRangeException(nameof(vlans),
                    $"VLAN id {v.Id} 超出範圍 1-4094（S615 manual p. 250）。");
            cmds.Add($"vlan {v.Id}");

            // Set VLAN name (p. 265): name <vlan-name> (max 32 chars).
            if (!string.IsNullOrWhiteSpace(v.Name))
            {
                if (v.Name.Length > 32)
                    throw new ArgumentException(
                        $"VLAN name '{v.Name}' 超過 32 字元（S615 manual p. 265）。",
                        nameof(vlans));
                cmds.Add($"name {v.Name}");
            }

            // Build the ports command (S615 CLI manual sec 8.1.4.5 p. 266-267).
            // Syntax: ports (<interface-type> <port-list>) [untagged (<interface-type> <port-list>)]
            //         [forbidden (<interface-type> <port-list>)] [name <vlan-name>]
            //
            // Per manual sec 3.7.5 p. 57, S615 physical ports are Fast Ethernet
            // addressed as `fa 0/N` (interface-type `fa` + module/port `0/N`).
            // Example: `interface fa 0/1`. NOT the dotted `0.1` form (that's
            // WBM display format only — wrong for CLI).
            var taggedPorts = new List<string>();
            var untaggedPorts = new List<string>();

            foreach (var p in v.Ports)
            {
                if (p.Mode == VlanMemberMode.Tagged)
                    taggedPorts.Add(FormatCliPortId(p.PortIndex));
                else if (p.Mode == VlanMemberMode.Untagged)
                    untaggedPorts.Add(FormatCliPortId(p.PortIndex));
                // VlanMemberMode.Excluded => not included in ports command
            }

            if (taggedPorts.Count > 0 || untaggedPorts.Count > 0)
            {
                var portsCmd = "ports";
                // Tagged list: "fa 0/1,0/2" inside the outer parens.
                portsCmd += taggedPorts.Count > 0
                    ? $" (fa {string.Join(",", taggedPorts)})"
                    : " ()";

                if (untaggedPorts.Count > 0)
                    portsCmd += $" untagged (fa {string.Join(",", untaggedPorts)})";

                cmds.Add(portsCmd);
            }

            cmds.Add("exit"); // back to cli(config)#
        }

        cmds.Add("end");
        cmds.Add("write memory");
        return cmds;
    }

    /// <summary>
    /// Apply a single L3 interface config (IPv4 + optional DHCP) using real Siemens
    /// S615 CLI syntax from PH_SCALANCE-S615-CLI_76 pp. 337-340.
    ///
    /// The S615 uses VLAN interfaces for L3 addressing. The interface config mode
    /// is entered via: interface vlan &lt;id&gt; -> cli(config-if-vlan-$$$)#
    ///
    /// IP commands in interface config mode (p. 338-340):
    ///   ip address &lt;ip&gt; {&lt;mask&gt; | / &lt;prefix&gt;} [secondary]
    ///   ip address dhcp
    ///   no ip address [&lt;addr&gt; | dhcp]
    ///
    /// Default gateway uses ip route in global config (p. 331-332):
    ///   ip route &lt;prefix&gt; &lt;mask&gt; &lt;next-hop&gt;
    ///
    /// DNS uses ip domain name in global config.
    /// </summary>
    public static IReadOnlyList<string> BuildSetInterface(InterfaceIpConfig cfg)
    {
        if (cfg is null) throw new ArgumentNullException(nameof(cfg));
        if (string.IsNullOrWhiteSpace(cfg.InterfaceName))
            throw new ArgumentException("InterfaceName is required.", nameof(cfg));

        var cmds = new List<string>
        {
            "configure terminal",
            // S615 interface config: interface <name>
            // For VLAN interfaces: interface vlan <id> -> cli(config-if-vlan-$$$)#
            // For physical ports: interface <type> <M.P> -> cli(config-if-$$$)#
            $"interface {cfg.InterfaceName}",
        };

        if (cfg.DhcpEnabled)
        {
            // p. 340: ip address dhcp (in interface config mode of VLAN)
            cmds.Add("ip address dhcp");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cfg.IpAddress))
                throw new ArgumentException("IpAddress required when DHCP disabled.", nameof(cfg));
            // p. 338-339: ip address <ip-address> {<subnet-mask> | / <prefix-length(1-32)>}
            var mask = cfg.SubnetMask ?? PrefixToMask(cfg.PrefixLength ?? 24);
            cmds.Add($"ip address {cfg.IpAddress} {mask}");
        }

        cmds.Add("exit"); // back to cli(config)#

        // Default gateway: S615 uses "ip route" in global config mode (p. 331-332)
        // ip route <prefix> <mask> <next-hop>
        // For default route: ip route 0.0.0.0 0.0.0.0 <gateway>
        if (!string.IsNullOrWhiteSpace(cfg.DefaultGateway))
            cmds.Add($"ip route 0.0.0.0 0.0.0.0 {cfg.DefaultGateway}");

        // DNS: S615 uses a dedicated dnsclient mode (CLI manual sec 9.7, pp. 408-417),
        // NOT the Cisco-style "ip name-server". Mode hierarchy:
        //   cli(config)# dnsclient                -> cli(config-dnsclient)#
        //     no shutdown                          (enable client; p. 417)
        //     server type manual                   (use manually configured servers; p. 415)
        //     manual srv <ip>                      (add server; p. 414)
        //     exit                                 -> cli(config)#
        var dnsServers = cfg.DnsServers
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList();
        if (dnsServers.Count > 0)
        {
            cmds.Add("dnsclient");
            cmds.Add("no shutdown");
            cmds.Add("server type manual");
            foreach (var dns in dnsServers)
                cmds.Add($"manual srv {dns}");
            cmds.Add("exit");
        }

        cmds.Add("end");
        cmds.Add("write memory");
        return cmds;
    }

    /// <summary>
    /// IPSec tunnel write using real Siemens S615 CLI syntax from
    /// PH_SCALANCE-S615-CLI_76 pp. 697-732.
    ///
    /// Mode hierarchy:
    ///   cli(config)# ipsec                 -> cli(config-ipsec)#
    ///     remote-end name &lt;name&gt;          -> cli(config-ipsec-rmend-X)#
    ///       addr &lt;subnet|dns&gt;
    ///       conn-mode {roadwarrior|standard}
    ///       subnet &lt;subnet|dns&gt;
    ///       exit                           -> cli(config-ipsec)#
    ///     connection name &lt;name&gt;          -> cli(config-conn-X)#
    ///       rmend name &lt;remote-end-name&gt;
    ///       loc-subnet &lt;cidr&gt;
    ///       operation {start|wait|disabled|...}
    ///       k-proto {ikev1|ikev2}
    ///       authentication                 -> cli(config-conn-auth)#
    ///         auth psk &lt;key&gt;
    ///         exit                         -> cli(config-conn-X)#
    ///       phase 1                        -> cli(config-conn-phsX)#
    ///         (phase 1 settings)
    ///         exit                         -> cli(config-conn-X)#
    ///       phase 2                        -> cli(config-conn-phsX)#
    ///         (phase 2 settings)
    ///         exit                         -> cli(config-conn-X)#
    ///       exit                           -> cli(config-ipsec)#
    ///     no shutdown | shutdown           (enable/disable IPsec globally)
    ///     exit                             -> cli(config)#
    /// </summary>
    public static IReadOnlyList<string> BuildSetVpnTunnel(VpnTunnel t)
    {
        if (t is null) throw new ArgumentNullException(nameof(t));
        if (string.IsNullOrWhiteSpace(t.Name))
            throw new ArgumentException("Tunnel name required.", nameof(t));
        // S615 CLI manual limits (sec 12.4.3.2 p. 699 / 12.4.3.4 p. 705):
        //   connection name: max 122 chars
        //   remote-end name: max 128 chars (we derive it as "<tunnel>-remote" = +7 chars)
        if (t.Name.Length > 122)
            throw new ArgumentException($"Tunnel name '{t.Name}' 超過 122 字元（S615 manual p. 699）。", nameof(t));
        if (t.Name.Length + 7 > 128)
            throw new ArgumentException($"Tunnel name too long — '{t.Name}-remote' 會超過 remote-end 128 字元限制。", nameof(t));

        var cmds = new List<string>
        {
            "configure terminal",

            // Enter IPsec configuration mode (p. 697)
            "ipsec",
        };

        // Create/enter remote end (p. 705)
        var remoteEndName = $"{t.Name}-remote";
        cmds.Add($"remote-end name {remoteEndName}");
        // Set remote endpoint address (p. 711): addr <subnet|dns>
        if (!string.IsNullOrEmpty(t.RemoteEndpoint))
            cmds.Add($"addr {t.RemoteEndpoint}");
        // Set connection mode (p. 713-714): conn-mode {roadwarrior|standard}
        cmds.Add("conn-mode standard");
        // Set remote subnet (p. 715): subnet <subnet>
        if (!string.IsNullOrEmpty(t.RemoteSubnet))
            cmds.Add($"subnet {t.RemoteSubnet}");
        cmds.Add("exit"); // back to cli(config-ipsec)#

        // Create/enter connection (p. 699)
        cmds.Add($"connection name {t.Name}");

        // Assign remote end to connection (p. 722): rmend name <name>
        cmds.Add($"rmend name {remoteEndName}");

        // Set local subnet (p. 719): loc-subnet <subnet>
        if (!string.IsNullOrEmpty(t.LocalSubnet))
            cmds.Add($"loc-subnet {t.LocalSubnet}");

        // Set IKE version (p. 718): k-proto {ikev1|ikev2}
        cmds.Add("k-proto ikev2");

        // Set operation mode (p. 719-721): operation {start|wait|disabled|...}
        cmds.Add(t.Enabled ? "operation start" : "operation disabled");

        // Authentication (p. 717, 727-729)
        cmds.Add("authentication");
        if (t.AuthMode == VpnAuthMode.Psk && !string.IsNullOrEmpty(t.PreSharedKey))
        {
            // auth psk <string(255)> — manual sec 12.4.6.2 p. 729.
            if (t.PreSharedKey.Length > 255)
                throw new ArgumentException(
                    $"PSK 長度 {t.PreSharedKey.Length} 超過 255 字元（S615 manual p. 729）。",
                    nameof(t));
            // Also defend the SSH transport: CR/LF/double-quote would break
            // the batched command stream.
            foreach (var c in t.PreSharedKey)
                if (c == '\r' || c == '\n' || c == '"')
                    throw new ArgumentException("PSK 含非法控制字元 (CR/LF/\").", nameof(t));
            cmds.Add($"auth psk {t.PreSharedKey}");
        }
        else if (t.AuthMode == VpnAuthMode.Certificate && !string.IsNullOrEmpty(t.LocalCertificateName))
        {
            // auth cacert <string(255)> localcert <string(255)> — manual p. 728.
            // VpnTunnel currently only carries one cert name; reuse it for both
            // operands until the model gains a separate CA field.
            if (t.LocalCertificateName.Length > 255)
                throw new ArgumentException(
                    $"certificate name 長度 {t.LocalCertificateName.Length} 超過 255 字元（S615 manual p. 728）。",
                    nameof(t));
            cmds.Add($"auth cacert {t.LocalCertificateName} localcert {t.LocalCertificateName}");
        }
        cmds.Add("exit"); // back to cli(config-conn-X)#

        // Phase 1 configuration (S615 CLI manual sec 12.4.7 pp. 734-744).
        // Verified parameter values:
        //   ike-encryption    {3des|aes128cbc|aes192cbc|aes256cbc|aes128ctr|aes192ctr|
        //                      aes256ctr|aes128ccm16|aes192ccm16|aes256ccm16|
        //                      aes128gcm16|aes192gcm16|aes256gcm16}          (p. 741)
        //   ike-auth          {md5|sha1|sha256|sha384|sha512}                 (p. 740)
        //   ike-keyderivation {dhgroup <1|2|5|14|15|16|17|18>}                (p. 742)
        //   ike-lifetime      <min(10-2500000)>   ← MINUTES, not seconds      (p. 744)
        // Custom values only take effect after "no default-ciphers" (p. 735-736).
        cmds.Add("phase 1");
        cmds.Add("no default-ciphers");
        if (!string.IsNullOrWhiteSpace(t.Ike.Encryption))
            cmds.Add($"ike-encryption {ValidateIkeEncryption(t.Ike.Encryption)}");
        if (!string.IsNullOrWhiteSpace(t.Ike.Hash))
            cmds.Add($"ike-auth {ValidateIkeEspHash(t.Ike.Hash)}");
        if (!string.IsNullOrWhiteSpace(t.Ike.DhGroup))
            cmds.Add($"ike-keyderivation dhgroup {ValidateDhGroup(t.Ike.DhGroup)}");
        if (t.Ike.LifetimeMinutes > 0)
            cmds.Add($"ike-lifetime {ClampRange(t.Ike.LifetimeMinutes, 10, 2500000, "ike-lifetime")}");
        cmds.Add("exit"); // back to cli(config-conn-X)#

        // Phase 2 configuration (S615 CLI manual sec 12.4.8 pp. 745-754).
        // Verified parameter values (same cipher/hash sets as phase 1):
        //   esp-encryption    {same list as ike-encryption}     (p. 749)
        //   esp-auth          {md5|sha1|sha256|sha384|sha512}   (p. 749)
        //   esp-keyderivation {none|dhgroup <1|2|5|14|15|16|17|18>} (p. 751)
        //   lifetime          <min(10-16666666)>  ← MINUTES      (p. 752)
        cmds.Add("phase 2");
        cmds.Add("no default-ciphers");
        if (!string.IsNullOrWhiteSpace(t.Esp.Encryption))
            cmds.Add($"esp-encryption {ValidateIkeEncryption(t.Esp.Encryption)}");
        if (!string.IsNullOrWhiteSpace(t.Esp.Hash))
            cmds.Add($"esp-auth {ValidateIkeEspHash(t.Esp.Hash)}");
        if (!string.IsNullOrWhiteSpace(t.Esp.PfsGroup))
        {
            if (t.Esp.PfsGroup.Equals("none", StringComparison.OrdinalIgnoreCase))
                cmds.Add("esp-keyderivation none");
            else
                cmds.Add($"esp-keyderivation dhgroup {ValidateDhGroup(t.Esp.PfsGroup)}");
        }
        if (t.Esp.LifetimeMinutes > 0)
            cmds.Add($"lifetime {ClampRange(t.Esp.LifetimeMinutes, 10, 16666666, "esp lifetime")}");
        cmds.Add("exit"); // back to cli(config-conn-X)#

        cmds.Add("exit"); // back to cli(config-ipsec)#

        // Enable/disable IPsec globally (p. 709-710)
        cmds.Add(t.Enabled ? "no shutdown" : "shutdown");

        cmds.Add("exit"); // back to cli(config)#
        cmds.Add("end");
        cmds.Add("write memory");
        return cmds;
    }

    // ---- firewall constants ----

    public const string ShowFirewallIpRules = "show firewall ip-rules ipv4";
    public const string ShowFirewallPreRules = "show firewall pre-rules ipv4";
    public const string ShowFirewallInfo = "show firewall information";
    public const string ShowFirewallServices = "show firewall ip-services";

    /// <summary>
    /// Build CLI commands to create a new IPv4 firewall rule.
    ///
    /// Verified against S615 CLI manual sec 12.3.4.31 p. 627-629:
    ///   ipv4rule from &lt;iftype&gt; [&lt;ifstring&gt;] to &lt;iftype&gt; [&lt;ifstring&gt;]
    ///             srcip &lt;ip|subnet|range&gt; dstip &lt;ip|subnet|range&gt;
    ///             action {drop|acc|rej}
    ///             [service &lt;all|name(32)&gt;] [log {no|info|war|cri}]
    ///             [prior &lt;0-127&gt;] [comment &lt;string(32)&gt;]
    ///
    /// Caller is responsible for using valid iftype keywords in From/To
    /// (e.g. "vlan 1", "Device", "IPsec 3"). Wildcard source/destination is
    /// expressed as "0.0.0.0/0" — the "*" form used previously is invalid
    /// per manual and rejected by the device.
    /// </summary>
    public static IReadOnlyList<string> BuildCreateFirewallRule(FirewallRule rule)
    {
        if (rule is null) throw new ArgumentNullException(nameof(rule));

        // Manual p. 627: `from` / `to` must name a valid iftype keyword
        // (e.g. "vlan 1", "Device", "IPsec 3"). Empty values produce
        // `ipv4rule from  to  srcip …` which the device rejects outright.
        if (string.IsNullOrWhiteSpace(rule.From))
            throw new ArgumentException("FirewallRule.From 必須指定介面（如 'vlan 1'）— manual p. 627。", nameof(rule));
        if (string.IsNullOrWhiteSpace(rule.To))
            throw new ArgumentException("FirewallRule.To 必須指定介面（如 'vlan 2'）— manual p. 627。", nameof(rule));

        var actionStr = rule.Action switch
        {
            FirewallAction.Accept => "acc",
            FirewallAction.Drop => "drop",
            FirewallAction.Reject => "rej",
            _ => "acc",
        };

        var src = string.IsNullOrWhiteSpace(rule.SourceCidr) ? "0.0.0.0/0" : rule.SourceCidr;
        var dst = string.IsNullOrWhiteSpace(rule.DestinationCidr) ? "0.0.0.0/0" : rule.DestinationCidr;

        var svc = string.IsNullOrWhiteSpace(rule.Service) ||
                  rule.Service.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? "all" : rule.Service;
        if (svc.Length > 32)
            throw new ArgumentException($"service name '{svc}' 超過 32 字元（manual p. 628）。", nameof(rule));

        var cmd = $"ipv4rule from {rule.From} to {rule.To} srcip {src} dstip {dst} action {actionStr} service {svc}";

        // log values per manual p. 628: {no|info|war|cri}. "info" is the most
        // commonly useful level; the Log bool maps to it.
        if (rule.Log)
            cmd += " log info";

        if (rule.Index > 0)
        {
            if (rule.Index > 127)
                throw new ArgumentOutOfRangeException(nameof(rule), $"prior {rule.Index} 超出範圍 0-127（manual p. 629）。");
            cmd += $" prior {rule.Index}";
        }

        return new List<string>
        {
            "configure terminal",
            "firewall",
            cmd,
            "end",
            "write memory",
        };
    }

    /// <summary>
    /// Build CLI commands to delete a firewall rule by index.
    /// Verified against S615 CLI manual sec 12.3.4.32 p. 630:
    ///   no ipv4rule {all | idx &lt;integer(1-128)&gt;}
    /// </summary>
    public static IReadOnlyList<string> BuildDeleteFirewallRule(int index)
    {
        if (index < 1 || index > 128)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"firewall rule idx {index} 超出範圍 1-128（manual sec 12.3.4.32 p. 630）。");
        return new List<string>
        {
            "configure terminal",
            "firewall",
            $"no ipv4rule idx {index}",
            "end",
            "write memory",
        };
    }

    /// <summary>
    /// Delete ALL ipv4 firewall rules. Manual sec 12.3.4.32 p. 630:
    ///   no ipv4rule all
    /// </summary>
    public static IReadOnlyList<string> BuildDeleteAllFirewallRules()
    {
        return new List<string>
        {
            "configure terminal",
            "firewall",
            "no ipv4rule all",
            "end",
            "write memory",
        };
    }

    /// <summary>Build CLI commands to toggle a predefined firewall service.</summary>
    public static IReadOnlyList<string> BuildSetPredefinedRule(PredefinedFirewallService svc)
    {
        if (svc is null) throw new ArgumentNullException(nameof(svc));
        if (string.IsNullOrWhiteSpace(svc.ServiceName))
            throw new ArgumentException("ServiceName required", nameof(svc));

        // Verified against S615 CLI manual sec 12.3.4.57-66 (pp. 653-663).
        // Canonical form for every prerule variant:
        //   prerule <svc> ipv4 {int <if-type> <if-index> | all-int} {enabled|disabled}
        //
        // Service keywords (lowercase, from manual TOC):
        //   dcp, dhcp, dns, http, https, ipsec, ping, snmp, ssh, syslog,
        //   systime, tcpevent, telnet, vrrp, openvpn, sinemarc.
        //
        // Interpretation of LocalAccess / ExternalAccess used here: by default
        // S615 places the management VLAN on vlan 1 (trusted) and the WAN on
        // vlan 2 (untrusted). These remain convention, not manual-mandated —
        // but the emitted command form is now manual-verified regardless.
        var name = svc.ServiceName.Trim().ToLowerInvariant();
        if (!PredefinedRuleNames.Contains(name))
            throw new ArgumentException(
                $"'{svc.ServiceName}' 不是 S615 支援的 prerule 關鍵字（manual sec 12.3.4.47-66）。",
                nameof(svc));

        var cmds = new List<string>
        {
            "configure terminal",
            "firewall",
            // Local = trusted side (vlan 1); toggle state based on LocalAccess.
            $"prerule {name} ipv4 int vlan 1 {(svc.LocalAccess ? "enabled" : "disabled")}",
            // External = untrusted side (vlan 2); toggle based on ExternalAccess.
            $"prerule {name} ipv4 int vlan 2 {(svc.ExternalAccess ? "enabled" : "disabled")}",
            "end",
            "write memory",
        };
        return cmds;
    }

    private static readonly HashSet<string> PredefinedRuleNames = new(StringComparer.Ordinal)
    {
        "dcp","dhcp","dns","http","https","ipsec","ping","snmp","ssh",
        "syslog","systime","tcpevent","telnet","vrrp","openvpn","sinemarc"
    };

    // ---- firewall parsers ----

    /// <summary>
    /// Best-effort parser for <c>show firewall ip-rules ipv4</c> tabular output.
    /// Returns an empty list if parsing fails (graceful degradation until validated
    /// on a real device).
    /// </summary>
    public static IReadOnlyList<FirewallRule> ParseFirewallRules(string showOutput)
    {
        if (string.IsNullOrWhiteSpace(showOutput))
            return Array.Empty<FirewallRule>();

        var rules = new List<FirewallRule>();
        var lines = showOutput.Split('\n');
        bool headerPassed = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // Skip header and separator lines (containing "---" or starting with column names)
            if (line.Contains("---") || line.Contains("==="))
            {
                headerPassed = true;
                continue;
            }
            if (!headerPassed && (line.StartsWith("idx", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Idx", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("IDX", StringComparison.OrdinalIgnoreCase)))
            {
                headerPassed = true;
                continue;
            }
            if (!headerPassed) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Expected columns: idx, enabled, from, to, srcip, dstip, action, service, log, prior
            if (parts.Length < 7) continue;

            try
            {
                var rule = new FirewallRule();

                if (int.TryParse(parts[0], out var idx))
                    rule.Index = idx;
                else
                    continue; // first column must be numeric index

                // parts[1] may be enabled flag (yes/no/on/off) or directly "from"
                int offset;
                if (parts[1].Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || parts[1].Equals("no", StringComparison.OrdinalIgnoreCase)
                    || parts[1].Equals("on", StringComparison.OrdinalIgnoreCase)
                    || parts[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Enabled = parts[1].Equals("yes", StringComparison.OrdinalIgnoreCase)
                                   || parts[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                    offset = 2;
                }
                else
                {
                    rule.Enabled = true;
                    offset = 1;
                }

                if (parts.Length <= offset + 4) continue;

                rule.From = parts[offset];
                rule.To = parts[offset + 1];
                rule.SourceCidr = parts[offset + 2] == "*" ? "" : parts[offset + 2];
                rule.DestinationCidr = parts[offset + 3] == "*" ? "" : parts[offset + 3];

                var actionStr = parts[offset + 4].ToLowerInvariant();
                rule.Action = actionStr switch
                {
                    "acc" or "accept" => FirewallAction.Accept,
                    "drop" => FirewallAction.Drop,
                    "rej" or "reject" => FirewallAction.Reject,
                    _ => FirewallAction.Accept,
                };

                if (parts.Length > offset + 5)
                    rule.Service = parts[offset + 5];

                if (parts.Length > offset + 6)
                    rule.Log = !parts[offset + 6].Equals("no", StringComparison.OrdinalIgnoreCase);

                rules.Add(rule);
            }
            catch
            {
                // Graceful degradation — skip unparseable lines
            }
        }
        return rules;
    }

    /// <summary>
    /// Best-effort parser for <c>show firewall pre-rules ipv4</c> output.
    /// Scans for known predefined service names and detects enabled/disabled state.
    /// Returns an empty list if parsing fails.
    /// </summary>
    public static IReadOnlyList<PredefinedFirewallService> ParsePredefinedRules(string showOutput)
    {
        if (string.IsNullOrWhiteSpace(showOutput))
            return Array.Empty<PredefinedFirewallService>();

        var knownServices = new[]
        {
            "http", "https", "ssh", "ping", "snmp", "dns",
            "dhcp", "telnet", "ipsec", "systime", "vrrp",
        };

        var services = new List<PredefinedFirewallService>();
        var lines = showOutput.Split('\n');

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var lower = line.ToLowerInvariant();
            foreach (var svcName in knownServices)
            {
                // Check if line starts with the service name or contains it as a distinct token
                var parts = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (!parts[0].Equals(svcName, StringComparison.OrdinalIgnoreCase)) continue;

                var svc = new PredefinedFirewallService { ServiceName = svcName };

                // Try to detect enabled/disabled from remaining tokens
                // Expected format: "servicename  vlan1:enabled  vlan2:disabled" or similar
                var rest = string.Join(" ", parts.Skip(1));
                svc.LocalAccess = rest.Contains("vlan1") && !rest.Contains("vlan1:dis")
                    && !rest.Contains("vlan 1:dis")
                    || (rest.Contains("vlan1:en") || rest.Contains("vlan 1:en"));
                svc.ExternalAccess = rest.Contains("vlan2") && !rest.Contains("vlan2:dis")
                    && !rest.Contains("vlan 2:dis")
                    || (rest.Contains("vlan2:en") || rest.Contains("vlan 2:en"));

                // Fallback: if tokens include "enabled"/"disabled" in positional order
                if (!svc.LocalAccess && !svc.ExternalAccess && parts.Length >= 3)
                {
                    svc.LocalAccess = parts[1].Contains("enable");
                    svc.ExternalAccess = parts.Length >= 3 && parts[2].Contains("enable");
                }

                services.Add(svc);
                break; // matched this line
            }
        }
        return services;
    }

    // ---- helpers ----

    /// <summary>
    /// Format a 1-based or absolute port index as a SCALANCE "module.port" string.
    /// Single-module devices always report module 0, so port 1 becomes "0.1".
    /// If the caller already encoded module+port (e.g. 101 meaning module 1 port 1),
    /// the helper also accepts that by treating values >=100 as already dot-formatted.
    /// </summary>
    /// <summary>
    /// WBM display format: "M.P" (e.g. 0.1). Used only by UI/WBM-style reporting.
    /// For CLI use <see cref="FormatCliPortId"/>.
    /// </summary>
    public static string FormatPortId(int port)
    {
        if (port < 0) throw new ArgumentOutOfRangeException(nameof(port));
        if (port >= 100) return $"{port / 100}.{port % 100}";
        return $"0.{port}";
    }

    /// <summary>
    /// CLI port id for ports/interface commands — returns just "0/N" (or "M/N"
    /// when port >= 100). The interface-type keyword (e.g. "fa") is prefixed
    /// once per parens group at the call site, per manual sec 8.1.4.5 syntax.
    /// </summary>
    public static string FormatCliPortId(int port)
    {
        if (port < 0) throw new ArgumentOutOfRangeException(nameof(port));
        if (port >= 100) return $"{port / 100}/{port % 100}";
        return $"0/{port}";
    }

    public static string PrefixToMask(int prefix)
    {
        if (prefix < 0 || prefix > 32) throw new ArgumentOutOfRangeException(nameof(prefix));
        uint bits = prefix == 0 ? 0 : 0xFFFFFFFFu << (32 - prefix);
        return $"{(bits >> 24) & 0xFF}.{(bits >> 16) & 0xFF}.{(bits >> 8) & 0xFF}.{bits & 0xFF}";
    }

    public static int MaskToPrefix(string mask)
    {
        var parts = mask.Split('.');
        if (parts.Length != 4) throw new ArgumentException("Invalid mask.", nameof(mask));
        uint bits = 0;
        for (int i = 0; i < 4; i++) bits = (bits << 8) | byte.Parse(parts[i]);
        int prefix = 0;
        for (int i = 31; i >= 0 && ((bits >> i) & 1) == 1; i--) prefix++;
        return prefix;
    }

    private static readonly HashSet<string> IkeEspEncryptionValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "3des",
        "aes128cbc","aes192cbc","aes256cbc",
        "aes128ctr","aes192ctr","aes256ctr",
        "aes128ccm16","aes192ccm16","aes256ccm16",
        "aes128gcm16","aes192gcm16","aes256gcm16"
    };

    private static readonly HashSet<string> IkeEspHashValues = new(StringComparer.OrdinalIgnoreCase)
    { "md5","sha1","sha256","sha384","sha512" };

    private static readonly HashSet<string> DhGroupValues = new(StringComparer.Ordinal)
    { "1","2","5","14","15","16","17","18" };

    internal static string ValidateIkeEncryption(string v)
    {
        var key = v.Trim().ToLowerInvariant();
        if (!IkeEspEncryptionValues.Contains(key))
            throw new ArgumentException(
                $"encryption '{v}' 不合法 — S615 CLI manual p. 741/749 僅接受 {string.Join("/", IkeEspEncryptionValues)}。");
        return key;
    }

    internal static string ValidateIkeEspHash(string v)
    {
        var key = v.Trim().ToLowerInvariant();
        if (!IkeEspHashValues.Contains(key))
            throw new ArgumentException(
                $"hash '{v}' 不合法 — S615 CLI manual p. 740/749 僅接受 md5/sha1/sha256/sha384/sha512。");
        return key;
    }

    internal static string ValidateDhGroup(string v)
    {
        var key = v.Trim();
        if (!DhGroupValues.Contains(key))
            throw new ArgumentException(
                $"DH group '{v}' 不合法 — S615 CLI manual p. 742/751 僅接受 1/2/5/14/15/16/17/18。");
        return key;
    }

    internal static int ClampRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(name,
                $"{name} {value} 超出 S615 可接受範圍 {min}-{max}（minutes）。");
        return value;
    }

    /// <summary>Accept "10.0.0.0/24" or "10.0.0.0 255.255.255.0" and return ACL-style "net mask".</summary>
    internal static string NetFromCidr(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return "any";
        if (spec.Contains('/'))
        {
            var p = spec.Split('/');
            return $"{p[0]} {PrefixToMask(int.Parse(p[1]))}";
        }
        return spec;
    }

    // ---------- Admin password (VERIFIED against PH_SCALANCE-S615-CLI_76) ----------
    //
    // Two distinct commands, different modes:
    //   - `change password <pwd>`  — User/Privileged EXEC (cli> / cli#).
    //       Changes the password of the currently logged-in user. No
    //       `configure terminal` needed; Trial mode saves immediately so no
    //       `write memory` either. Manual sec 12.1.2 p. 567.
    //   - `user-account <name> password <pwd> role <role>` — Global config.
    //       Creates OR updates another user. Role is REQUIRED. Cannot target
    //       the currently-logged-in user (the manual forbids it: "logged in
    //       users cannot be deleted or changed"). Manual sec 12.1.4.7 p. 575.
    //
    // Disallowed password characters per manual p. 576: § ? " ; : ` \ Space Delete.

    /// <summary>
    /// Change the password of the currently logged-in user. Use this when the
    /// SSH session's user is the account being updated (the typical "change
    /// admin password" flow).
    /// </summary>
    public static IReadOnlyList<string> BuildChangeOwnPassword(string newPassword)
    {
        ValidatePassword(newPassword);
        return new List<string> { $"change password {newPassword}" };
    }

    /// <summary>
    /// Set/overwrite another user's password and role in global config.
    /// Cannot target the currently-logged-in user — use <see cref="BuildChangeOwnPassword"/>
    /// for that case. Role must be a system-defined or previously-created role name.
    /// </summary>
    public static IReadOnlyList<string> BuildSetUserAccount(string username, string newPassword, string role)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("username required", nameof(username));
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("role required — e.g. 'admin'", nameof(role));
        ValidatePassword(newPassword);
        return new List<string>
        {
            "configure terminal",
            $"user-account {username} password {newPassword} role {role}",
            "end",
            "write memory"
        };
    }

    private static void ValidatePassword(string pwd)
    {
        if (string.IsNullOrWhiteSpace(pwd)) throw new ArgumentException("password required", nameof(pwd));
        // Defend the SSH transport line first (CR/LF/quote break batching).
        foreach (var c in pwd)
        {
            if (c == '\r' || c == '\n' || c == '"')
                throw new ArgumentException("password contains illegal control character.", nameof(pwd));
            // Siemens-disallowed charset per S615 CLI manual p. 576 user-password:
            //   § ? " ; : ß \ + Space + Delete. The sharp-s 'ß' (U+00DF) is
            //   the correct character — NOT backtick '`' (which was a prior
            //   transcription error from fonts where the two look similar).
            if (c == '§' || c == '?' || c == ';' || c == ':' || c == 'ß' || c == '\\' || c == ' ' || c == '\x7f')
                throw new ArgumentException($"password contains disallowed character '{c}' (S615 manual p. 576).", nameof(pwd));
        }
    }

    // ---------- DNS client (uses verified dnsclient mode — see BuildSetInterface header) ----------

    /// <summary>
    /// Configures the DNS client using the verified S615 "dnsclient" mode
    /// (CLI manual sec 9.7, pp. 408-417). Idempotent: any previous manual
    /// servers are cleared before the new list is applied.
    /// </summary>
    public static IReadOnlyList<string> BuildSetDns(DnsConfig cfg)
    {
        if (cfg is null) throw new ArgumentNullException(nameof(cfg));
        var servers = (cfg.Servers ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        var cmds = new List<string> { "configure terminal", "dnsclient" };

        // Clear all previously configured manual servers so replay is idempotent.
        // Verified syntax: `no manual {srv <ip_addr>|all}` — S615 CLI manual
        // sec 9.7.3.2 p. 415. `all` removes every manual DNS entry.
        cmds.Add("no manual all");

        if (servers.Count > 0)
        {
            cmds.Add("server type manual");
            foreach (var s in servers) cmds.Add($"manual srv {s}");
            cmds.Add("no shutdown");
        }
        else
        {
            cmds.Add("shutdown");
        }
        cmds.Add("exit");

        // Domain name: per manual p. 10741 the command is "ip domain name"
        // (space, not hyphen) or "domain name". Neither Cisco-style hyphenated.
        if (!string.IsNullOrWhiteSpace(cfg.DomainName))
            cmds.Add($"ip domain name {cfg.DomainName}");

        cmds.Add("end");
        cmds.Add("write memory");
        return cmds;
    }

    /// <summary>Parse output of "show dnsclient" into a DnsConfig.</summary>
    public static DnsConfig ParseDnsClient(string output)
    {
        var cfg = new DnsConfig();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // Expected lines look like:
            //   Manual Server 1: 8.8.8.8
            //   manual srv 8.8.8.8
            //   Domain Name: example.com
            if (line.StartsWith("manual srv ", StringComparison.OrdinalIgnoreCase))
            {
                var ip = line.Substring("manual srv ".Length).Trim();
                if (System.Net.IPAddress.TryParse(ip, out _)) cfg.Servers.Add(ip);
                continue;
            }
            if (line.Contains(':'))
            {
                var sep = line.IndexOf(':');
                var key = line.Substring(0, sep).Trim().ToLowerInvariant();
                var val = line.Substring(sep + 1).Trim();
                if (key.Contains("domain") && key.Contains("name"))
                    cfg.DomainName = val;
                else if (key.Contains("server") && System.Net.IPAddress.TryParse(val, out _))
                    cfg.Servers.Add(val);
            }
        }
        return cfg;
    }
}
