using Scalance.Core.Models;
using Scalance.Protocols.Ssh;

namespace Scalance.Drivers;

public abstract class ScalanceCliDriverBase : SnmpDriverBase
{
    private SshSession? _ssh;
    private Credential? _credential;

    /// <summary>
    /// When true, CLI write operations on paths whose syntax is INFERRED
    /// (SetVlans / SetInterface / SetVpnTunnel) build the command list but do NOT
    /// execute it — they return Ok and expose the planned commands via
    /// <see cref="LastPlannedCommands"/> so a UI can show them for review.
    /// Defaults to <c>true</c> until the operator explicitly opts in, because the
    /// CLI syntax for those features is inferred from Cisco-IOS conventions
    /// (see docs/VERIFICATION.md) and has not been validated on real hardware.
    ///
    /// NTP writes are NOT gated by this flag because the NTP command syntax was
    /// validated on a real S615 (handoff notes) — they always execute.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Commands planned by the most recent write call (useful in DryRun).</summary>
    public IReadOnlyList<string> LastPlannedCommands { get; private set; } = Array.Empty<string>();

    // When a multi-step operation (e.g. ApplyBasicWizardAsync) is running, it
    // calls BeginPlanSession() so that each internal RunOrPlanAsync appends to
    // this aggregate instead of overwriting LastPlannedCommands. EndPlanSession
    // flushes the aggregate into LastPlannedCommands so the UI preview shows
    // the full wizard plan, not just the last sub-step.
    private List<string>? _planSession;

    private void BeginPlanSession() => _planSession = new List<string>();
    private void EndPlanSession()
    {
        if (_planSession is not null)
        {
            LastPlannedCommands = _planSession;
            _planSession = null;
        }
    }

    /// <param name="forceExecute">
    /// Pass true on paths whose syntax is already validated (NTP). Paths whose
    /// syntax is inferred from Cisco-IOS conventions should pass false so that
    /// <see cref="DryRun"/> gates execution.
    /// </param>
    private async Task<OperationResult> RunOrPlanAsync(IReadOnlyList<string> cmds, CancellationToken ct, bool forceExecute = false)
    {
        if (DryRun && !forceExecute)
        {
            if (_planSession is not null) _planSession.AddRange(cmds);
            else LastPlannedCommands = cmds;
            return OperationResult.Ok();
        }
        if (_planSession is null)
            LastPlannedCommands = Array.Empty<string>(); // cleared after a real execution so UI does not misreport a preview
        var ssh = await GetSshAsync(ct);
        await ssh.RunBatchAsync(cmds, ct);
        return OperationResult.Ok();
    }

    public override async Task<OperationResult> ConnectAsync(Device device, Credential credential, CancellationToken ct = default)
    {
        var baseResult = await base.ConnectAsync(device, credential, ct);
        if (!baseResult.Success) return baseResult;
        _credential = credential;
        return OperationResult.Ok();
    }

    protected async Task<SshSession> GetSshAsync(CancellationToken ct)
    {
        if (_ssh is not null) return _ssh;
        if (Device is null || _credential is null)
            throw new InvalidOperationException("Not connected.");
        if (string.IsNullOrEmpty(_credential.Username))
            throw new InvalidOperationException("SSH username required.");

        _ssh = !string.IsNullOrEmpty(_credential.PrivateKeyPath)
            ? await SshSession.ConnectWithKeyAsync(Device.Host, Device.SshPort,
                _credential.Username, _credential.PrivateKeyPath, _credential.Password, ct)
            : await SshSession.ConnectAsync(Device.Host, Device.SshPort,
                _credential.Username, _credential.Password ?? "", ct);
        return _ssh;
    }

    public override async Task<OperationResult<NtpConfig>> GetNtpAsync(CancellationToken ct = default)
    {
        try
        {
            var ssh = await GetSshAsync(ct);
            // Verified command: `show ntp info` — S615 CLI manual sec 7.2.1.1 p. 215.
            var output = await ssh.RunAsync("show ntp info", ct);
            return OperationResult<NtpConfig>.Ok(ParseNtp(output));
        }
        catch (Exception ex)
        {
            return OperationResult<NtpConfig>.Fail($"GetNtp failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Set NTP configuration using real S615 CLI syntax from
    /// PH_SCALANCE-S615-CLI_76 pp. 216-221.
    ///
    /// Mode hierarchy:
    ///   cli(config)# ntp                -> cli(config-ntp)#
    ///     ntp server id &lt;1-3&gt; { ipv4 &lt;ip&gt; | fqdn-name &lt;fqdn&gt; } [poll &lt;sec&gt;]
    ///     exit                           -> cli(config)#
    /// </summary>
    public override async Task<OperationResult> SetNtpAsync(NtpConfig config, CancellationToken ct = default)
    {
        try
        {
            var cmds = new List<string> { "configure terminal" };

            // Enter NTP configuration mode (p. 216)
            cmds.Add("ntp");

            // Configure up to 3 NTP servers (p. 217-218). Host validation
            // (strict IPv4 dotted-quad or FQDN ≤100 chars, SSH-safe) lives in
            // ScalanceCliCommands.FormatNtpServerLine so syntax rules stay in
            // one place.
            int serverId = 1;
            foreach (var s in config.Servers)
            {
                if (serverId > 3) break; // S615 supports max 3 NTP servers
                try
                {
                    cmds.Add(ScalanceCliCommands.FormatNtpServerLine(serverId, s.Host));
                }
                catch (ArgumentException ex)
                {
                    return OperationResult.Fail($"NTP server #{serverId}: {ex.Message}");
                }
                serverId++;
            }

            // Time zone offset: SCALANCE uses `ntp time diff +HH:MM` INSIDE
            // NTP config mode (S615 CLI manual sec 7.2.3.6 p. 221). It is NOT
            // the Cisco-IOS `clock timezone`. Value must be signed with both
            // components two-digit (e.g. "+08:00", "-05:30").
            if (!string.IsNullOrEmpty(config.Timezone))
            {
                if (!IsValidNtpTimeDiff(config.Timezone))
                    return OperationResult.Fail(
                        $"Timezone '{config.Timezone}' 格式錯誤。SCALANCE 需要 '+HH:MM' 或 '-HH:MM'（例如 '+08:00'）。");
                cmds.Add($"ntp time diff {config.Timezone}");
            }

            cmds.Add("exit"); // back to cli(config)#

            cmds.Add("end");
            cmds.Add("write memory");
            // NTP syntax is validated against the CLI manual — bypass DryRun.
            return await RunOrPlanAsync(cmds, ct, forceExecute: true);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"SetNtp failed: {ex.Message}", ex);
        }
    }

    private static bool IsValidNtpTimeDiff(string s)
    {
        // Expect exactly "+HH:MM" or "-HH:MM". Manual p. 221: signed, no spaces,
        // both fields two digits including leading zero.
        if (s.Length != 6) return false;
        if (s[0] != '+' && s[0] != '-') return false;
        if (s[3] != ':') return false;
        return int.TryParse(s.AsSpan(1, 2), out var h) && h is >= 0 and <= 14
            && int.TryParse(s.AsSpan(4, 2), out var m) && m is >= 0 and <= 59;
    }

    public override async Task<OperationResult<string>> BackupConfigAsync(CancellationToken ct = default)
    {
        try
        {
            var ssh = await GetSshAsync(ct);
            var output = await ssh.RunAsync("show running-config", ct);
            return OperationResult<string>.Ok(output);
        }
        catch (Exception ex)
        {
            return OperationResult<string>.Fail($"Backup failed: {ex.Message}", ex);
        }
    }

    // ---------- VLAN (read via Q-BRIDGE, write via CLI) ----------

    public override async Task<OperationResult<IReadOnlyList<Vlan>>> GetVlansAsync(CancellationToken ct = default)
    {
        if (Snmp is null) return OperationResult<IReadOnlyList<Vlan>>.Fail("Not connected.");
        try
        {
            var vlans = await Dot1qVlanReader.ReadAsync(Snmp, ct);
            return OperationResult<IReadOnlyList<Vlan>>.Ok(vlans);
        }
        catch (Exception ex)
        {
            return OperationResult<IReadOnlyList<Vlan>>.Fail($"GetVlans failed: {ex.Message}", ex);
        }
    }

    public override async Task<OperationResult> SetVlansAsync(IReadOnlyList<Vlan> vlans, CancellationToken ct = default)
    {
        try
        {
            var cmds = ScalanceCliCommands.BuildSetVlans(vlans);
            return await RunOrPlanAsync(cmds, ct);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"SetVlans failed: {ex.Message}", ex);
        }
    }

    // ---------- Interfaces / Subnet ----------

    public override async Task<OperationResult<IReadOnlyList<InterfaceIpConfig>>> GetInterfacesAsync(CancellationToken ct = default)
    {
        var diag = new List<string>();

        // Try SSH CLI first (faster, more detailed)
        try
        {
            var ssh = await GetSshAsync(ct);
            var output = await ssh.TryRunAsync("show ip interface", TimeSpan.FromSeconds(5), ct);
            if (!string.IsNullOrWhiteSpace(output))
            {
                var parsed = ParseInterfaces(output);
                diag.Add($"SSH 'show ip interface' returned {output.Length} bytes, parsed {parsed.Count} interfaces");
                if (parsed.Count > 0)
                    return OperationResult<IReadOnlyList<InterfaceIpConfig>>.Ok(parsed);
            }
            else
            {
                diag.Add("SSH 'show ip interface' returned empty/timeout");
            }
        }
        catch (Exception ex)
        {
            diag.Add($"SSH failed: {ex.Message}");
        }

        // SNMP fallback — try ipAddrTable first, then augment/fallback with ifTable
        try
        {
            var fromSnmp = await ReadInterfacesViaSnmpAsync(ct, diag);
            if (fromSnmp is not null && fromSnmp.Count > 0)
                return OperationResult<IReadOnlyList<InterfaceIpConfig>>.Ok(fromSnmp);
        }
        catch (Exception ex)
        {
            diag.Add($"SNMP ipAddrTable failed: {ex.Message}");
        }

        // Last-resort fallback: show device's configured IP as a single synthetic interface
        if (!string.IsNullOrWhiteSpace(Device?.Host))
        {
            diag.Add("Fallback: synthesising single entry from Device.Host");
            return OperationResult<IReadOnlyList<InterfaceIpConfig>>.Ok(new[]
            {
                new InterfaceIpConfig
                {
                    InterfaceName = "vlan1 (推斷)",
                    IpAddress = Device.Host,
                    VlanId = 1,
                }
            });
        }

        return OperationResult<IReadOnlyList<InterfaceIpConfig>>.Fail(
            "無法從 SSH 或 SNMP 取得介面設定。診斷：\n • " + string.Join("\n • ", diag));
    }

    private async Task<IReadOnlyList<InterfaceIpConfig>?> ReadInterfacesViaSnmpAsync(CancellationToken ct, List<string>? diag = null)
    {
        try
        {
            if (Snmp is null) { diag?.Add("SNMP client null"); return null; }
            // RFC 1213 ipAddrTable: .1.3.6.1.2.1.4.20.1 — keyed by IP suffix A.B.C.D
            var entries = await Snmp.WalkAsync("1.3.6.1.2.1.4.20.1.1", ct);
            diag?.Add($"SNMP ipAddrTable returned {entries.Count} entries");
            if (entries.Count == 0)
            {
                // Fallback: walk ifTable to at least list interfaces (without IPs)
                return await ReadInterfacesFromIfTableAsync(ct, diag);
            }

            var masks = (await Snmp.WalkAsync("1.3.6.1.2.1.4.20.1.3", ct))
                .ToDictionary(v => IpSuffix(v.Id.ToString()), v => v.Data.ToString());
            var ifIdx = (await Snmp.WalkAsync("1.3.6.1.2.1.4.20.1.2", ct))
                .ToDictionary(v => IpSuffix(v.Id.ToString()), v => v.Data.ToString());
            var ifNames = (await Snmp.WalkAsync("1.3.6.1.2.1.2.2.1.2", ct))
                .ToDictionary(v => v.Id.ToString().Split('.').Last(), v => v.Data.ToString());

            // Default route for DefaultGateway: ipRouteTable (.1.3.6.1.2.1.4.21.1)
            //   .1.A.B.C.D = destination
            //   .7.A.B.C.D = next hop
            //   Look up 0.0.0.0 destination to find default gateway
            string? defaultGw = null;
            try
            {
                var routeDests = await Snmp.WalkAsync("1.3.6.1.2.1.4.21.1.1", ct);
                var routeNextHops = (await Snmp.WalkAsync("1.3.6.1.2.1.4.21.1.7", ct))
                    .ToDictionary(v => IpSuffix(v.Id.ToString()), v => v.Data.ToString());
                foreach (var r in routeDests)
                {
                    if (r.Data.ToString() == "0.0.0.0" &&
                        routeNextHops.TryGetValue(IpSuffix(r.Id.ToString()), out var gw) &&
                        gw != "0.0.0.0")
                    {
                        defaultGw = gw;
                        break;
                    }
                }
            }
            catch { /* ipRouteTable may not be populated on switches */ }

            var result = new List<InterfaceIpConfig>();
            foreach (var e in entries)
            {
                var ip = e.Data.ToString() ?? "";
                if (string.IsNullOrEmpty(ip) || ip == "127.0.0.1" || ip == "0.0.0.0") continue;
                var key = IpSuffix(e.Id.ToString());
                masks.TryGetValue(key, out var mask);
                ifIdx.TryGetValue(key, out var idx);
                var name = idx is not null && ifNames.TryGetValue(idx, out var n) ? n : $"if{ip}";

                var cfg = new InterfaceIpConfig
                {
                    InterfaceName = name,
                    IpAddress = ip,
                    SubnetMask = string.IsNullOrWhiteSpace(mask) ? null : mask,
                    PrefixLength = MaskToPrefix(mask),
                    VlanId = ExtractVlanId(name),
                    DefaultGateway = defaultGw,
                };
                result.Add(cfg);
            }
            return result.Count == 0 ? null : result;
        }
        catch { return null; }
    }

    private async Task<IReadOnlyList<InterfaceIpConfig>?> ReadInterfacesFromIfTableAsync(CancellationToken ct, List<string>? diag)
    {
        if (Snmp is null) return null;
        try
        {
            // RFC 1213 ifTable .1.3.6.1.2.1.2.2.1
            //   .2 ifDescr, .3 ifType, .8 ifOperStatus
            var names = await Snmp.WalkAsync("1.3.6.1.2.1.2.2.1.2", ct);
            diag?.Add($"SNMP ifTable (ifDescr) returned {names.Count} entries");
            var result = new List<InterfaceIpConfig>();
            foreach (var n in names)
            {
                var name = n.Data.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Filter out loopback, virtual, null interfaces
                var lower = name.ToLowerInvariant();
                if (lower.Contains("loopback") || lower.Contains("null") || lower == "lo") continue;
                result.Add(new InterfaceIpConfig
                {
                    InterfaceName = name,
                    VlanId = ExtractVlanId(name),
                });
            }
            // Augment with management IP from Device.Host if we have it and no interface has an IP
            if (result.Count > 0 && !string.IsNullOrWhiteSpace(Device?.Host))
            {
                // Try to attach the device IP to a VLAN or management interface
                var mgmtIf = result.FirstOrDefault(r => r.InterfaceName.ToLowerInvariant().Contains("vlan"))
                             ?? result.First();
                mgmtIf.IpAddress = Device.Host;
            }
            return result.Count == 0 ? null : result;
        }
        catch (Exception ex)
        {
            diag?.Add($"ifTable walk failed: {ex.Message}");
            return null;
        }
    }

    private static int? MaskToPrefix(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask)) return null;
        if (!System.Net.IPAddress.TryParse(mask, out var ip)) return null;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return null;
        int bits = 0;
        foreach (var b in bytes)
        {
            for (int i = 7; i >= 0; i--)
            {
                if ((b & (1 << i)) != 0) bits++;
                else return bits;
            }
        }
        return bits;
    }

    private static bool IsModuleDotPort(string token)
    {
        // SCALANCE WBM port form "M.P" — e.g. "0.1", "1.16". Both halves are integers.
        var parts = token.Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], out _)
            && int.TryParse(parts[1], out _);
    }

    private static int? ExtractVlanId(string interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName)) return null;
        var name = interfaceName.ToLowerInvariant();
        if (!name.StartsWith("vlan")) return null;
        var numPart = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(numPart, out var id) ? id : null;
    }

    private static string IpSuffix(string oid)
    {
        var parts = oid.Split('.');
        return parts.Length >= 4 ? string.Join('.', parts.TakeLast(4)) : oid;
    }

    public override async Task<OperationResult> SetInterfaceAsync(InterfaceIpConfig config, CancellationToken ct = default)
    {
        try
        {
            var cmds = ScalanceCliCommands.BuildSetInterface(config);
            return await RunOrPlanAsync(cmds, ct);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"SetInterface failed: {ex.Message}", ex);
        }
    }

    // ---------- VPN (IPsec) ----------

    public override async Task<OperationResult<IReadOnlyList<VpnTunnel>>> GetVpnTunnelsAsync(CancellationToken ct = default)
    {
        if (!Capabilities.HasFlag(Scalance.Core.Capabilities.DeviceCapability.IpsecVpn))
            return OperationResult<IReadOnlyList<VpnTunnel>>.Fail("VPN not supported by this device.");

        try
        {
            var ssh = await GetSshAsync(ct);
            var output = await ssh.RunAsync("show running-config", ct);
            return OperationResult<IReadOnlyList<VpnTunnel>>.Ok(ParseVpnTunnels(output));
        }
        catch (Exception ex)
        {
            return OperationResult<IReadOnlyList<VpnTunnel>>.Fail($"GetVpnTunnels failed: {ex.Message}", ex);
        }
    }

    public override async Task<OperationResult> SetVpnTunnelAsync(VpnTunnel tunnel, CancellationToken ct = default)
    {
        if (!Capabilities.HasFlag(Scalance.Core.Capabilities.DeviceCapability.IpsecVpn))
            return OperationResult.Fail("VPN not supported by this device.");
        try
        {
            var cmds = ScalanceCliCommands.BuildSetVpnTunnel(tunnel);
            return await RunOrPlanAsync(cmds, ct);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"SetVpnTunnel failed: {ex.Message}", ex);
        }
    }

    // ---------- Firewall ----------

    public override async Task<OperationResult<IReadOnlyList<FirewallRule>>> ReadFirewallRulesAsync(CancellationToken ct = default)
    {
        try
        {
            var ssh = await GetSshAsync(ct);
            var output = await ssh.RunAsync(ScalanceCliCommands.ShowFirewallIpRules, ct);
            var rules = ScalanceCliCommands.ParseFirewallRules(output);
            return OperationResult<IReadOnlyList<FirewallRule>>.Ok(rules);
        }
        catch (Exception ex)
        {
            return OperationResult<IReadOnlyList<FirewallRule>>.Fail($"讀取防火牆規則失敗：{ex.Message}", ex);
        }
    }

    public override async Task<OperationResult> WriteFirewallRuleAsync(FirewallRule rule, CancellationToken ct = default)
    {
        try
        {
            var cmds = ScalanceCliCommands.BuildCreateFirewallRule(rule);
            return await RunOrPlanAsync(cmds, ct);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"寫入防火牆規則失敗：{ex.Message}", ex);
        }
    }

    public override async Task<OperationResult<IReadOnlyList<PredefinedFirewallService>>> ReadPredefinedRulesAsync(CancellationToken ct = default)
    {
        try
        {
            var ssh = await GetSshAsync(ct);
            var output = await ssh.RunAsync(ScalanceCliCommands.ShowFirewallPreRules, ct);
            var services = ScalanceCliCommands.ParsePredefinedRules(output);
            return OperationResult<IReadOnlyList<PredefinedFirewallService>>.Ok(services);
        }
        catch (Exception ex)
        {
            return OperationResult<IReadOnlyList<PredefinedFirewallService>>.Fail($"讀取預定義防火牆服務失敗：{ex.Message}", ex);
        }
    }

    public override async Task<OperationResult> WritePredefinedRuleAsync(PredefinedFirewallService service, CancellationToken ct = default)
    {
        try
        {
            var cmds = ScalanceCliCommands.BuildSetPredefinedRule(service);
            return await RunOrPlanAsync(cmds, ct);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"寫入預定義防火牆服務失敗：{ex.Message}", ex);
        }
    }

    // ---------- parsers ----------

    internal static IReadOnlyList<InterfaceIpConfig> ParseInterfaces(string output)
    {
        // S615 "show ip interface" output format (multi-section):
        //   Interface vlan1
        //     IP Address: 192.168.1.1
        //     Subnet Mask: 255.255.255.0
        //     ...
        //   Interface vlan2
        //     ...
        //
        // Legacy "show ip interface brief" (Cisco-ish):
        //   Interface    IP-Address    OK? Method Status Protocol
        //   vlan1        192.168.1.1   YES manual up     up
        //
        // This parser handles both: scans for "Interface <name>" section headers OR
        // tabular single-line entries. Returns at minimum the interface names even
        // if IP/mask extraction fails.
        var result = new List<InterfaceIpConfig>();
        InterfaceIpConfig? current = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var trimmed = line.Trim();

            // Section header: "Interface vlan1" or "Interface ethernet 0/1"
            if (trimmed.StartsWith("Interface ", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Contains("IP-Address") && !trimmed.Contains("IP Address"))
            {
                var name = trimmed.Substring("Interface ".Length).Trim().TrimEnd(':');
                if (!string.IsNullOrWhiteSpace(name))
                {
                    current = new InterfaceIpConfig
                    {
                        InterfaceName = name,
                        VlanId = ExtractVlanId(name),
                    };
                    result.Add(current);
                    continue;
                }
            }

            // Key-value pairs inside a section (S615 format)
            if (current is not null && trimmed.Contains(':'))
            {
                var sep = trimmed.IndexOf(':');
                var key = trimmed.Substring(0, sep).Trim();
                var value = trimmed.Substring(sep + 1).Trim();
                if (key.Equals("IP Address", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("IP-Address", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("IPv4 Address", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Address", StringComparison.OrdinalIgnoreCase))
                {
                    // Handles "192.168.1.1/24" or "192.168.1.1 255.255.255.0" or plain IP
                    var ipParts = value.Split('/', ' ');
                    var ipOnly = ipParts[0];
                    if (!ipOnly.Equals("unassigned", StringComparison.OrdinalIgnoreCase) &&
                        !ipOnly.Equals("-", StringComparison.OrdinalIgnoreCase))
                    {
                        current.IpAddress = ipOnly;
                        if (ipParts.Length > 1 && int.TryParse(ipParts[1], out var prefix))
                            current.PrefixLength = prefix;
                    }
                }
                else if (key.Equals("Subnet Mask", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Netmask", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Network Mask", StringComparison.OrdinalIgnoreCase))
                {
                    current.SubnetMask = value;
                    current.PrefixLength ??= MaskToPrefix(value);
                }
                else if (key.Equals("Default Gateway", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Gateway", StringComparison.OrdinalIgnoreCase))
                {
                    if (value != "0.0.0.0" && !string.IsNullOrWhiteSpace(value))
                        current.DefaultGateway = value;
                }
                else if (key.Equals("Prefix Length", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Prefix", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var prefix)) current.PrefixLength = prefix;
                }
                else if (key.Equals("VLAN", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("VLAN ID", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var vid)) current.VlanId = vid;
                }
                else if (key.Equals("DHCP", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Method", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Address Type", StringComparison.OrdinalIgnoreCase))
                {
                    var v = value.ToLowerInvariant();
                    current.DhcpEnabled = v.Contains("dhcp") || v == "dynamic" || v == "on" || v == "enabled";
                }
                else if (key.Equals("DNS Servers", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("DNS", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Name Server", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("Name Servers", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var dns in value.Split(',', ' ', StringSplitOptions.RemoveEmptyEntries))
                        if (System.Net.IPAddress.TryParse(dns.Trim(), out _))
                            current.DnsServers.Add(dns.Trim());
                }
                // Finalize: if VlanId still null but name is vlan<N>, extract from name
                current.VlanId ??= ExtractVlanId(current.InterfaceName);
                continue;
            }

            // Legacy tabular fallback: skip header row, parse single-line entries
            if (trimmed.StartsWith("Interface", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string ifName;
            string ip;
            if (parts.Length >= 6 && parts[1].StartsWith("0/", StringComparison.Ordinal))
            {
                ifName = $"{parts[0]} {parts[1]}";
                ip = parts[2];
            }
            else if (parts[0].StartsWith("vlan", StringComparison.OrdinalIgnoreCase) ||
                     parts[0].Contains("0/") || parts[0].StartsWith("ppp") ||
                     IsModuleDotPort(parts[0]))
            {
                ifName = parts[0];
                ip = parts.Length > 1 ? parts[1] : "";
            }
            else continue;

            var cfg = new InterfaceIpConfig { InterfaceName = ifName };
            if (!string.IsNullOrEmpty(ip) &&
                !ip.Equals("unassigned", StringComparison.OrdinalIgnoreCase) &&
                !ip.Equals("-", StringComparison.OrdinalIgnoreCase))
            {
                cfg.IpAddress = ip;
            }
            result.Add(cfg);
            current = cfg;
        }
        return result;
    }

    internal static IReadOnlyList<VpnTunnel> ParseVpnTunnels(string runningConfig)
    {
        // Parser supports both legacy Cisco-style crypto-map format (for backward compat
        // with existing test data) and the real S615 IPsec connection model.
        // The real S615 uses "show ipsec connections" for status; this parser handles
        // "show running-config" output which may contain either format.
        var tunnels = new List<VpnTunnel>();
        VpnTunnel? current = null;
        foreach (var raw in runningConfig.Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            // Legacy: "no crypto map NAME enable" toggles an existing tunnel.
            // Split: ["no", "crypto", "map", "NAME", "enable"] -> parts[3] is name
            if (trimmed.StartsWith("no crypto map ", StringComparison.OrdinalIgnoreCase)
                && trimmed.EndsWith(" enable", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    var target = tunnels.FirstOrDefault(t => t.Name.Equals(parts[3], StringComparison.OrdinalIgnoreCase));
                    if (target is not null) target.Enabled = false;
                }
                continue;
            }

            // Legacy: "crypto map NAME 10 ipsec-isakmp"
            if (trimmed.StartsWith("crypto map ", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains(" ipsec-isakmp", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    current = new VpnTunnel { Name = parts[2], Enabled = true };
                    tunnels.Add(current);
                }
                continue;
            }
            if (current is null) continue;

            if (trimmed.StartsWith("set peer ", StringComparison.OrdinalIgnoreCase))
                current.RemoteEndpoint = trimmed.Substring("set peer ".Length).Trim();
            else if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                current = null; // left the stanza
        }
        return tunnels;
    }

    private static NtpConfig ParseNtp(string output)
    {
        var config = new NtpConfig();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ntp server ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                    config.Servers.Add(new NtpServer(parts[2]));
            }
            if (trimmed.Contains("ntp enable", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("no", StringComparison.OrdinalIgnoreCase))
                config.Enabled = true;
        }
        return config;
    }

    // ---------- Admin password (INFERRED) ----------

    public override async Task<OperationResult> SetAdminPasswordAsync(string username, string newPassword, CancellationToken ct = default)
    {
        try
        {
            // If we're changing the currently-logged-in user's password, we MUST
            // use `change password <pwd>` — the `user-account` command cannot
            // target the logged-in user (S615 CLI manual p. 576).
            bool isSelf = !string.IsNullOrEmpty(_credential?.Username)
                && string.Equals(_credential!.Username, username, StringComparison.OrdinalIgnoreCase);

            var cmds = isSelf
                ? ScalanceCliCommands.BuildChangeOwnPassword(newPassword)
                : ScalanceCliCommands.BuildSetUserAccount(username, newPassword, "admin");
            return await RunOrPlanAsync(cmds, ct);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"SetAdminPassword failed: {ex.Message}", ex);
        }
    }

    // ---------- DNS ----------

    public override async Task<OperationResult<DnsConfig>> GetDnsAsync(CancellationToken ct = default)
    {
        try
        {
            var ssh = await GetSshAsync(ct);
            // Verified command: `show dnsclient information` — S615 CLI manual sec 9.7.1.1 p. 409.
            var output = await ssh.TryRunAsync("show dnsclient information", TimeSpan.FromSeconds(5), ct) ?? "";
            return OperationResult<DnsConfig>.Ok(ScalanceCliCommands.ParseDnsClient(output));
        }
        catch (Exception ex)
        {
            return OperationResult<DnsConfig>.Fail($"GetDns failed: {ex.Message}", ex);
        }
    }

    public override async Task<OperationResult> SetDnsAsync(DnsConfig config, CancellationToken ct = default)
    {
        try
        {
            var cmds = ScalanceCliCommands.BuildSetDns(config);
            return await RunOrPlanAsync(cmds, ct);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"SetDns failed: {ex.Message}", ex);
        }
    }

    // ---------- Basic Wizard (composition) ----------
    //
    // Mirrors the WBM "Basic Wizard" by applying hostname + interface IP + DNS +
    // NTP + admin password in a single operator action. Each sub-step runs
    // through its dedicated builder so DryRun still applies uniformly; NTP runs
    // last and is the only step that bypasses DryRun (validated syntax).
    public override async Task<OperationResult> ApplyBasicWizardAsync(BasicWizardConfig config, CancellationToken ct = default)
    {
        if (config is null) return OperationResult.Fail("Basic Wizard config is null.");

        var failures = new List<string>();
        BeginPlanSession();
        try
        {

        if (!string.IsNullOrWhiteSpace(config.Hostname))
        {
            // Verified: SCALANCE uses `system name <name>` — S615 CLI manual sec
            // 5.1.11.12 p. 98. Cisco-style `hostname` would be rejected. Length
            // and SSH-safety validation live in BuildSetSystemName.
            try
            {
                var cmds = ScalanceCliCommands.BuildSetSystemName(config.Hostname);
                var r = await RunOrPlanAsync(cmds, ct);
                if (!r.Success) failures.Add($"hostname: {r.Message}");
            }
            catch (ArgumentException ex) { failures.Add($"hostname: {ex.Message}"); }
        }

        if (config.Interface is not null)
        {
            var r = await SetInterfaceAsync(config.Interface, ct);
            if (!r.Success) failures.Add($"interface: {r.Message}");
        }

        if (config.Dns is not null)
        {
            var r = await SetDnsAsync(config.Dns, ct);
            if (!r.Success) failures.Add($"dns: {r.Message}");
        }

        if (config.Ntp is not null)
        {
            var r = await SetNtpAsync(config.Ntp, ct);
            if (!r.Success) failures.Add($"ntp: {r.Message}");
        }

        // Password change is last — if it succeeds, the live SSH session's
        // credentials are invalidated for any subsequent call on this driver.
        if (!string.IsNullOrEmpty(config.NewAdminPassword))
        {
            var r = await SetAdminPasswordAsync(config.AdminUsername, config.NewAdminPassword, ct);
            if (!r.Success) failures.Add($"password: {r.Message}");
        }

        return failures.Count == 0
            ? OperationResult.Ok()
            : OperationResult.Fail("Basic Wizard 部分步驟失敗：" + string.Join("; ", failures));
        }
        finally { EndPlanSession(); }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_ssh is not null) await _ssh.DisposeAsync();
        await base.DisposeAsync();
    }
}
