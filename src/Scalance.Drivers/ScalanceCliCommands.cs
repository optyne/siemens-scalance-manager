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
        // Interface name sits on the SSH command line alongside no user data,
        // but still defend the batched stream against CR/LF/quote.
        foreach (var c in cfg.InterfaceName)
            if (c == '\r' || c == '\n' || c == '"')
                throw new ArgumentException(
                    "InterfaceName 含非法控制字元 (CR/LF/\").", nameof(cfg));

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
            // p. 340: ip address dhcp (in interface config mode of VLAN).
            // Clear any static assignment first so re-applying a DHCP config
            // over a previously-static interface is deterministic.
            cmds.Add("no ip address");
            cmds.Add("ip address dhcp");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cfg.IpAddress))
                throw new ArgumentException("IpAddress required when DHCP disabled.", nameof(cfg));
            // Manual sec 9.1.3.2 p. 339 lists as a Requirement: "DHCP was
            // disabled with the no ip address command." Without this, a
            // transition from DHCP → static can leave the device in an
            // ambiguous state or reject the new static address. Emitting
            // `no ip address` unconditionally is idempotent and matches the
            // manual's prescribed order.
            cmds.Add("no ip address");
            // p. 338-339: ip address <ip-address> {<subnet-mask> | / <prefix-length(1-32)>}
            // Manual requires a valid IPv4 address and mask. Fail fast here so
            // the device doesn't reject the batch mid-way.
            RequireIpv4(cfg.IpAddress, "IpAddress");
            var mask = cfg.SubnetMask ?? PrefixToMask(cfg.PrefixLength ?? 24);
            RequireIpv4(mask, "SubnetMask");
            cmds.Add($"ip address {cfg.IpAddress} {mask}");
        }

        cmds.Add("exit"); // back to cli(config)#

        // Default gateway: S615 uses "ip route" in global config mode (p. 331-332)
        // ip route <prefix> <mask> <next-hop>
        // For default route: ip route 0.0.0.0 0.0.0.0 <gateway>
        if (!string.IsNullOrWhiteSpace(cfg.DefaultGateway))
        {
            RequireIpv4(cfg.DefaultGateway, "DefaultGateway");
            cmds.Add($"ip route 0.0.0.0 0.0.0.0 {cfg.DefaultGateway}");
        }

        // DNS: S615 uses a dedicated dnsclient mode (CLI manual sec 9.7, pp. 408-417),
        // NOT the Cisco-style "ip name-server". Mode hierarchy:
        //   cli(config)# dnsclient                -> cli(config-dnsclient)#
        //     no shutdown                          (enable client; p. 417)
        //     server type manual                   (use manually configured servers; p. 415)
        //     manual srv <ip>                      (add server; p. 414)
        //     exit                                 -> cli(config)#
        // Manual p. 414 requires the DNS server parameter to be a valid IP
        // and caps the list at three entries.
        var dnsServers = cfg.DnsServers
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList();
        if (dnsServers.Count > 3)
            throw new ArgumentException(
                $"DnsServers 共 {dnsServers.Count} 台超過上限 3 台（S615 manual p. 414）。",
                nameof(cfg));
        foreach (var s in dnsServers) RequireIpv4(s, "DnsServers entry");
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
    /// Require a string that can sit on a CLI line as a single whitespace-
    /// delimited token. Rejects CR/LF/NUL (batched SSH stream safety),
    /// double-quote (breaks quoted args), and inner space (would split
    /// the token into two arguments on the device).
    /// </summary>
    internal static void RequireCliToken(string value, string paramName)
    {
        if (value is null) throw new ArgumentNullException(paramName);
        foreach (var c in value)
            if (c == '\r' || c == '\n' || c == '\0' || c == '"' || c == ' ')
                throw new ArgumentException(
                    $"{paramName} 含非法字元 '{c}' — 須為單一 CLI token（不可含空白、引號、換行）。",
                    paramName);
    }

    /// <summary>
    /// Strict dotted-quad IPv4 check. Rejects the BSD short forms that
    /// <see cref="System.Net.IPAddress.TryParse"/> otherwise accepts
    /// (e.g. "1.2.3" → 1.2.0.3), which the SCALANCE CLI does not match.
    /// </summary>
    internal static void RequireIpv4(string value, string paramName)
    {
        // Require strict dotted-quad: System.Net.IPAddress.TryParse on .NET
        // will happily accept "1.2.3" as 1.2.0.3 (legacy BSD form), which the
        // SCALANCE CLI does not match. Enforce four octets before TryParse.
        var parts = value?.Split('.') ?? Array.Empty<string>();
        if (parts.Length != 4
            || !System.Net.IPAddress.TryParse(value, out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException(
                $"{paramName} '{value}' 不是合法的 IPv4 位址（需為 a.b.c.d 四段 0-255）。",
                paramName);
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
        // `connection name <name>` / `remote-end name <name>` use the rest of
        // the line as a single token. A space/CR/LF/quote would split or break
        // the batched command stream; reject early with a clear message.
        RequireCliToken(t.Name, "Tunnel name");

        var cmds = new List<string>
        {
            "configure terminal",

            // Enter IPsec configuration mode (p. 697)
            "ipsec",
        };

        // Create/enter remote end (p. 705)
        var remoteEndName = $"{t.Name}-remote";
        cmds.Add($"remote-end name {remoteEndName}");
        // Set remote endpoint address (p. 711): addr <subnet|dns>.
        // Manual allows a CIDR (incl. 0.0.0.0/0) or DNS name — we don't parse
        // the form but must prevent CR/LF/space/quote from breaking the batch.
        if (!string.IsNullOrEmpty(t.RemoteEndpoint))
        {
            RequireCliToken(t.RemoteEndpoint, "RemoteEndpoint");
            cmds.Add($"addr {t.RemoteEndpoint}");
        }
        // Set connection mode (p. 713-714): conn-mode {roadwarrior|standard}
        cmds.Add("conn-mode standard");
        // Set remote subnet (p. 715): subnet <subnet>
        if (!string.IsNullOrEmpty(t.RemoteSubnet))
        {
            RequireCliToken(t.RemoteSubnet, "RemoteSubnet");
            cmds.Add($"subnet {t.RemoteSubnet}");
        }
        cmds.Add("exit"); // back to cli(config-ipsec)#

        // Create/enter connection (p. 699)
        cmds.Add($"connection name {t.Name}");

        // Assign remote end to connection (p. 722): rmend name <name>
        cmds.Add($"rmend name {remoteEndName}");

        // Set local subnet (p. 719): loc-subnet <subnet>
        if (!string.IsNullOrEmpty(t.LocalSubnet))
        {
            RequireCliToken(t.LocalSubnet, "LocalSubnet");
            cmds.Add($"loc-subnet {t.LocalSubnet}");
        }

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

        // Ensure the IPsec subsystem is globally enabled so this tunnel can
        // run. Manual sec 12.4.3.18-19 pp. 709-710: `shutdown` / `no shutdown`
        // affects the ENTIRE IPsec method — not a single connection — so we
        // must never emit `shutdown` in a per-tunnel flow (that would kill
        // every other tunnel). Per-tunnel enable/disable is already handled
        // by the `operation start` / `operation disabled` lines above.
        // `no shutdown` is idempotent when IPsec is already enabled.
        cmds.Add("no shutdown");

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
        ValidateFirewallInterface(rule.From, "From");
        ValidateFirewallInterface(rule.To, "To");

        var actionStr = rule.Action switch
        {
            FirewallAction.Accept => "acc",
            FirewallAction.Drop => "drop",
            FirewallAction.Reject => "rej",
            _ => "acc",
        };

        var src = string.IsNullOrWhiteSpace(rule.SourceCidr) ? "0.0.0.0/0" : rule.SourceCidr;
        var dst = string.IsNullOrWhiteSpace(rule.DestinationCidr) ? "0.0.0.0/0" : rule.DestinationCidr;
        // srcip/dstip may hold CIDR, single IP, or a range "A - B". Reject CR/LF/
        // quote so a crafted value cannot break the batched SSH command stream.
        RejectLineControlChars(src, "SourceCidr");
        RejectLineControlChars(dst, "DestinationCidr");

        var svc = string.IsNullOrWhiteSpace(rule.Service) ||
                  rule.Service.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? "all" : rule.Service;
        if (svc.Length > 32)
            throw new ArgumentException($"service name '{svc}' 超過 32 字元（manual p. 628）。", nameof(rule));
        // Service name sits on the same CLI line as srcip/dstip; a space/CR/LF
        // would shift subsequent positional args or break the stream.
        RequireCliToken(svc, "Service");

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

    // Re-verified against PH_SCALANCE-S615-CLI_76 pp. 647-664 (sec 12.3.4.52
    // through 12.3.4.67). These are the ONLY user-selectable `prerule <svc>`
    // keywords. The earlier list contained `dcp`, `syslog`, `openvpn`, and
    // `sinemarc` — none of which exist as prerules in the manual; the device
    // would reject them. `cloudconnector` (p. 648) and `vxlan` (p. 664) were
    // missing. `all` and `show-int` are management keywords, not service
    // keywords, so they are intentionally not exposed here.
    // S615 CLI manual p. 598-599 enumerates the only iftype keywords accepted
    // by `ipv4rule from` / `to`. VLAN and PPP come with an integer ifstring;
    // IPsec/OpenVPN accept either a numeric suffix or the `-all` form; Device
    // and SinemaRC stand alone. Matching here up front means a typo like
    // "wan1" fails in the builder rather than causing a cryptic device reject.
    private static readonly HashSet<string> FirewallIfTypeFirstToken = new(StringComparer.Ordinal)
    {
        "vlan", "ppp",
        "IPsec", "IPsecall",
        "OpenVPN", "OpenVPNall",
        "SinemaRC", "Device",
    };

    private static void ValidateFirewallInterface(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                $"FirewallRule.{paramName} 必須指定介面（如 'vlan 1'）— manual p. 627。",
                paramName);
        RejectLineControlChars(value, paramName);
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !FirewallIfTypeFirstToken.Contains(parts[0]))
            throw new ArgumentException(
                $"FirewallRule.{paramName} '{value}' 首個 token 非手冊列出的 iftype（p. 598-599：vlan/ppp/IPsec[all]/OpenVPN[all]/SinemaRC/Device）。",
                paramName);
        // Second token — when present — must be a non-negative decimal (ifnum).
        // The manual range (p. 627-628) is 0..4094.
        if (parts.Length > 2)
            throw new ArgumentException(
                $"FirewallRule.{paramName} '{value}' 有多餘的 token — 格式為 '<iftype> [<ifstring>]'。",
                paramName);
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], out var ifNum) || ifNum < 0 || ifNum > 4094)
                throw new ArgumentException(
                    $"FirewallRule.{paramName} '{value}' 的 ifstring 必須是 0-4094 的整數（manual p. 627）。",
                    paramName);
        }
    }

    private static void RejectLineControlChars(string value, string paramName)
    {
        if (value is null) return;
        foreach (var c in value)
            if (c == '\r' || c == '\n' || c == '\0' || c == '"')
                throw new ArgumentException(
                    $"{paramName} 含非法控制字元（CR/LF/NUL/\"）。", paramName);
    }

    private static readonly HashSet<string> PredefinedRuleNames = new(StringComparer.Ordinal)
    {
        "cloudconnector","dhcp","dns","http","https","ipsec","ping",
        "snmp","ssh","systime","tcpevent","telnet","vrrp","vxlan"
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
    // Disallowed password characters per manual p. 576: § ? " ; : ß \ Space Delete.
    // Disallowed user-name  characters per manual p. 575: § ? " ; :       Space Delete.
    //   (note: user-name does NOT forbid backslash or ß — only the password does.)

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
        ValidateUserName(username);
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("role required — e.g. 'admin'", nameof(role));
        ValidateRoleOrToken(role, nameof(role));
        ValidatePassword(newPassword);
        return new List<string>
        {
            "configure terminal",
            $"user-account {username} password {newPassword} role {role}",
            "end",
            "write memory"
        };
    }

    private static void ValidateUserName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("username required", nameof(name));
        // Manual p. 575: 1-250 chars; disallowed set § ? " ; : + Space + Delete.
        if (name.Length < 1 || name.Length > 250)
            throw new ArgumentException(
                $"username 長度 {name.Length} 不在 1-250 範圍（S615 manual p. 575）。", nameof(name));
        foreach (var c in name)
        {
            if (c == '\r' || c == '\n')
                throw new ArgumentException("username 含換行字元（SSH 注入防護）。", nameof(name));
            if (c == '§' || c == '?' || c == '"' || c == ';' || c == ':' || c == ' ' || c == '\x7f')
                throw new ArgumentException(
                    $"username 含禁用字元 '{c}'（S615 manual p. 575）。", nameof(name));
        }
    }

    private static void ValidateRoleOrToken(string token, string paramName)
    {
        // Role names reach the device verbatim on the same line as the password,
        // so the minimum bar is no CR/LF/double-quote that could break the stream.
        foreach (var c in token)
            if (c == '\r' || c == '\n' || c == '"' || c == ' ')
                throw new ArgumentException(
                    $"{paramName} 含非法字元 '{c}' — 只允許單一 token，不可含空白或換行。",
                    paramName);
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

    // ---------- Configuration backup (manual sec 5.4.3 pp. 140-142) ----------

    /// <summary>
    /// Build CLI to create a named on-device configuration backup.
    /// Verified against PH_SCALANCE-S615-CLI_76 sec 5.4.3.3 p. 140:
    ///   configbackup create &lt;configbackup-name&gt;   (max 64 characters)
    /// Entered from Global configuration mode.
    /// </summary>
    public static IReadOnlyList<string> BuildConfigBackupCreate(string name)
    {
        ValidateConfigBackupName(name);
        return new List<string>
        {
            "configure terminal",
            $"configbackup create {name}",
            "end",
        };
    }

    /// <summary>
    /// Build CLI to restore a named on-device backup.
    /// Verified against PH_SCALANCE-S615-CLI_76 sec 5.4.3.4 p. 140-141:
    ///   configbackup restore &lt;configbackup-name&gt;
    /// </summary>
    public static IReadOnlyList<string> BuildConfigBackupRestore(string name)
    {
        ValidateConfigBackupName(name);
        return new List<string>
        {
            "configure terminal",
            $"configbackup restore {name}",
            "end",
        };
    }

    /// <summary>
    /// Build CLI to delete a named on-device backup.
    /// Verified against PH_SCALANCE-S615-CLI_76 sec 5.4.3.5 p. 141-142:
    ///   configbackup delete &lt;configbackup-name&gt;
    /// </summary>
    public static IReadOnlyList<string> BuildConfigBackupDelete(string name)
    {
        ValidateConfigBackupName(name);
        return new List<string>
        {
            "configure terminal",
            $"configbackup delete {name}",
            "end",
        };
    }

    private static void ValidateConfigBackupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("configbackup name required.", nameof(name));
        if (name.Length > 64)
            throw new ArgumentException(
                $"configbackup name 長度 {name.Length} 超過 64 字元（S615 manual p. 140）。",
                nameof(name));
        // Single CLI token — any inner whitespace or quote breaks the argument.
        RequireCliToken(name, "configbackup name");
    }

    /// <summary>
    /// Parse the tabular output of `show configbackup` (manual sec 5.4.1.2 p. 136)
    /// into a list of backup entry names. Output is best-effort since the manual
    /// does not provide a sample; the first row is "Available memory" and other
    /// rows list backup name + size. This extractor pulls the first whitespace-
    /// delimited token of each non-header line that isn't "Available".
    /// </summary>
    public static IReadOnlyList<string> ParseConfigBackupNames(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<string>();
        var names = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Available", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("---")) continue;
            // Skip a likely header row like "Name   Size".
            if (line.StartsWith("Name", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            names.Add(parts[0]);
        }
        return names;
    }

    // ---------- Diagnostics: ping ----------

    /// <summary>
    /// Build the CLI line for an IPv4 / FQDN ping. Verified against
    /// PH_SCALANCE-S615-CLI_76 sec 5.1.8 p. 86:
    ///   ping { &lt;destination-address&gt; | fqdn-name &lt;FQDN&gt; }
    ///        [size &lt;byte(0-2080)&gt;] [count &lt;packet_count(1-10)&gt;]
    ///        [timeout &lt;seconds(1-100)&gt;]
    /// Executed in User EXEC / Privileged EXEC — no `configure terminal`
    /// wrapper. Returns a single command string for the caller to run.
    /// </summary>
    public static string FormatPingCommand(string host, PingOptions? opts = null)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("ping host required", nameof(host));

        // Distinguish IPv4 vs FQDN — same rules as NTP (strict dotted-quad,
        // FQDN ≤ 100 chars, SSH-safe).
        string hostClause;
        var parts = host.Split('.');
        if (parts.Length == 4
            && System.Net.IPAddress.TryParse(host, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            hostClause = host;
        }
        else
        {
            if (host.Length > 100)
                throw new ArgumentException(
                    $"FQDN 長度 {host.Length} 超過 100 字元（S615 manual p. 86）。", nameof(host));
            foreach (var c in host)
                if (c == '\r' || c == '\n' || c == '"' || c == ' ' || c == '\0')
                    throw new ArgumentException(
                        "FQDN 含非法字元（CR/LF/NUL/space/\"）。", nameof(host));
            hostClause = $"fqdn-name {host}";
        }

        var line = $"ping {hostClause}";
        if (opts is not null)
        {
            if (opts.SizeBytes is int sz)
            {
                if (sz < 0 || sz > 2080)
                    throw new ArgumentOutOfRangeException(nameof(opts),
                        $"ping size {sz} 超出範圍 0-2080（manual p. 86）。");
                line += $" size {sz}";
            }
            if (opts.Count is int cnt)
            {
                if (cnt < 1 || cnt > 10)
                    throw new ArgumentOutOfRangeException(nameof(opts),
                        $"ping count {cnt} 超出範圍 1-10（manual p. 86）。");
                line += $" count {cnt}";
            }
            if (opts.TimeoutSeconds is int to)
            {
                if (to < 1 || to > 100)
                    throw new ArgumentOutOfRangeException(nameof(opts),
                        $"ping timeout {to} 超出範圍 1-100（manual p. 86）。");
                line += $" timeout {to}";
            }
        }
        return line;
    }

    /// <summary>
    /// Build the CLI line for a traceroute. Verified against
    /// PH_SCALANCE-S615-CLI_76 sec 5.1.10 p. 88:
    ///   traceroute {ip &lt;ip-address&gt; | ipv6 &lt;ip6-address&gt;}
    /// Executed in Privileged EXEC mode.
    /// </summary>
    public static string FormatTraceRouteCommand(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("traceroute host required", nameof(host));

        // Strict IPv4 dotted-quad branch.
        var parts = host.Split('.');
        if (parts.Length == 4
            && System.Net.IPAddress.TryParse(host, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return $"traceroute ip {host}";
        }

        // IPv6 literal branch — manual p. 88 only supports these two forms.
        if (System.Net.IPAddress.TryParse(host, out var ip6)
            && ip6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return $"traceroute ipv6 {host}";
        }

        throw new ArgumentException(
            $"traceroute host '{host}' 必須是合法的 IPv4 或 IPv6 字面值（S615 manual p. 88 不接受 FQDN）。",
            nameof(host));
    }

    // ---------- Syslog client ----------

    /// <summary>
    /// Build CLI commands to add a Syslog server. Verified against
    /// PH_SCALANCE-S615-CLI_76 sec 13.2.2.1 p. 824:
    ///   syslogserver { ipv4 &lt;ucast_addr&gt; | fqdn-name &lt;FQDN&gt; | ipv6 &lt;ip6_addr&gt; }
    ///                [&lt;port(1-65535)&gt;] [tls]
    /// Emitted inside EVENTS config mode (entered via `events` from global).
    /// </summary>
    public static IReadOnlyList<string> BuildAddSyslogServer(SyslogServer s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));
        var hostClause = FormatSyslogHostClause(s.Host);

        var line = "syslogserver " + hostClause;
        if (s.Port.HasValue)
        {
            if (s.Port.Value < 1 || s.Port.Value > 65535)
                throw new ArgumentOutOfRangeException(nameof(s),
                    $"Syslog port {s.Port.Value} 超出範圍 1-65535（manual p. 824）。");
            line += $" {s.Port.Value}";
        }
        if (s.UseTls) line += " tls";

        return new List<string>
        {
            "configure terminal",
            "events",  // enter EVENTS config mode (manual sec 13.1.9.1 p. 811)
            line,
            "end",
            "write memory",
        };
    }

    /// <summary>
    /// Build CLI commands to delete a Syslog server. Verified against
    /// PH_SCALANCE-S615-CLI_76 sec 13.2.2.2 p. 825:
    ///   no syslogserver { ipv4 &lt;ucast_addr&gt; | fqdn-name &lt;FQDN&gt; | ipv6 &lt;ip6_addr&gt; }
    /// </summary>
    public static IReadOnlyList<string> BuildRemoveSyslogServer(SyslogServer s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));
        return new List<string>
        {
            "configure terminal",
            "events",
            "no syslogserver " + FormatSyslogHostClause(s.Host),
            "end",
            "write memory",
        };
    }

    private static string FormatSyslogHostClause(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Syslog server host required.", nameof(host));

        // Strict IPv4 dotted-quad branch.
        var parts = host.Split('.');
        if (parts.Length == 4
            && System.Net.IPAddress.TryParse(host, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return $"ipv4 {host}";

        // IPv6 literal branch — IPAddress.TryParse accepts all RFC 4291 forms.
        if (System.Net.IPAddress.TryParse(host, out var ip6)
            && ip6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return $"ipv6 {host}";

        // FQDN branch — manual p. 824: max 100 chars; must be a single CLI token.
        if (host.Length > 100)
            throw new ArgumentException(
                $"FQDN 長度 {host.Length} 超過 100 字元（S615 manual p. 824）。", nameof(host));
        foreach (var c in host)
            if (c == '\r' || c == '\n' || c == '"' || c == ' ' || c == '\0')
                throw new ArgumentException(
                    "FQDN 含非法字元（CR/LF/NUL/space/\"）。", nameof(host));
        return $"fqdn-name {host}";
    }

    // ---------- NTP server line ----------

    /// <summary>
    /// Build a single `ntp server id &lt;1-3&gt; { ipv4 &lt;ip&gt; | fqdn-name &lt;fqdn&gt; }`
    /// command line. Verified against PH_SCALANCE-S615-CLI_76 sec 7.2.3.1 p. 217:
    ///   - id must be 1..3 (device limit).
    ///   - ipv4 value must be a valid dotted-quad; short BSD forms are rejected.
    ///   - fqdn max 100 characters; CR/LF/space/quote rejected for SSH safety.
    /// The caller is responsible for wrapping with `configure terminal` / `ntp`
    /// / `end` / `write memory`.
    /// </summary>
    public static string FormatNtpServerLine(int id, string host)
    {
        if (id < 1 || id > 3)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"ntp server id {id} 超出 1-3 範圍（S615 manual p. 217）。");
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("ntp server host required", nameof(host));

        // Distinguish IPv4 vs FQDN. IPv4 path uses the strict dotted-quad form.
        var parts = host.Split('.');
        if (parts.Length == 4
            && System.Net.IPAddress.TryParse(host, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return $"ntp server id {id} ipv4 {host}";
        }

        // Fall through to FQDN validation.
        if (host.Length > 100)
            throw new ArgumentException(
                $"FQDN 長度 {host.Length} 超過 100 字元（S615 manual p. 217）。", nameof(host));
        foreach (var c in host)
            if (c == '\r' || c == '\n' || c == '"' || c == ' ' || c == '\0')
                throw new ArgumentException(
                    "FQDN 含非法字元（CR/LF/NUL/space/\"）。", nameof(host));
        return $"ntp server id {id} fqdn-name {host}";
    }

    // ---------- System name (hostname) ----------

    /// <summary>
    /// Build CLI commands to set the device system name (hostname).
    /// Verified against PH_SCALANCE-S615-CLI_76 sec 5.1.11.12 p. 98-99:
    ///   system name &lt;system name&gt;    (max 255 characters)
    /// Note: SCALANCE uses `system name`, NOT the Cisco-IOS `hostname`.
    /// </summary>
    public static IReadOnlyList<string> BuildSetSystemName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("system name required", nameof(name));
        // Manual p. 99: max 255 characters.
        if (name.Length > 255)
            throw new ArgumentException(
                $"system name 長度 {name.Length} 超過 255 字元（S615 manual p. 99）。",
                nameof(name));
        // Defend the SSH batched stream: CR/LF would break command boundaries;
        // a double-quote mid-name could break `system name "..."` style quoting.
        foreach (var c in name)
            if (c == '\r' || c == '\n' || c == '"' || c == '\0')
                throw new ArgumentException(
                    "system name 含非法控制字元 (CR/LF/NUL/\").", nameof(name));
        return new List<string>
        {
            "configure terminal",
            $"system name {name}",
            "end",
            "write memory",
        };
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
        // Manual p. 414: "A maximum of three DNS servers can be configured."
        if (servers.Count > 3)
            throw new ArgumentException(
                $"DNS servers 共 {servers.Count} 台超過上限 3 台（S615 manual p. 414）。",
                nameof(cfg));
        // Manual p. 414: `manual srv <ip_addr>` requires a valid IP address.
        foreach (var s in servers) RequireIpv4(s, "DNS server");
        // Manual p. 98-99-adjacent refs: domain name must be one token.
        if (!string.IsNullOrWhiteSpace(cfg.DomainName))
            foreach (var c in cfg.DomainName)
                if (c == '\r' || c == '\n' || c == '"' || c == ' ')
                    throw new ArgumentException(
                        "DomainName 含空白或控制字元 (CR/LF/space/\")。", nameof(cfg));

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
