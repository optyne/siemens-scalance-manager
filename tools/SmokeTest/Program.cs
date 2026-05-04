// SmokeTest — direct driver smoke against a real S615 (no UI, no DB).
//
// Usage: from repo root,
//   dotnet run --project tools/SmokeTest/SmokeTest.csproj
// Output: stdout + tools/SmokeTest/smoke.log
//
// What it covers:
//   1. SNMP path:  GetStatus, GetVlans (Dot1qVlanReader)
//   2. SSH path:   GetNtp, GetInterfaces (SSH-then-SNMP fallback),
//                  GetDns, ReadFirewallRules, GetVpnTunnels,
//                  Ping, ListConfigBackups
//   3. Write path: SetVlans dryrun, SetNtp (forceExecute=true) — only if
//                  --enable-write is passed and DryRun is set to false.
//                  By default we do NOT touch the device's running-config
//                  here; this is a read-mostly smoke.

using System.Diagnostics;
using Scalance.Core.Models;
using Scalance.Drivers;

const string Host = "192.168.1.1";
const string User = "admin";
const string Pass = "Industry4.0";

var logPath = Path.Combine(AppContext.BaseDirectory, "smoke.log");
// Walk up from bin/Debug/... back to the SmokeTest project dir for a
// stable log location next to the .csproj.
var projDir = AppContext.BaseDirectory;
for (int i = 0; i < 6 && projDir != Path.GetPathRoot(projDir); i++)
{
    if (File.Exists(Path.Combine(projDir, "SmokeTest.csproj"))) break;
    projDir = Path.GetFullPath(Path.Combine(projDir, ".."));
}
if (File.Exists(Path.Combine(projDir, "SmokeTest.csproj")))
    logPath = Path.Combine(projDir, "smoke.log");

using var logFile = new StreamWriter(logPath, append: false) { AutoFlush = true };
void Log(string msg)
{
    Console.WriteLine(msg);
    logFile.WriteLine(msg);
}

void Banner(string s)
{
    Log("");
    Log(new string('=', 72));
    Log(s);
    Log(new string('=', 72));
}

bool enableWrite = args.Any(a => a == "--enable-write");

Banner($"SmokeTest — {Host} ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss})");
Log($"Log file: {logPath}");
Log($"Write probes: {(enableWrite ? "ENABLED" : "disabled (pass --enable-write to turn on)")}");

var device = new Device
{
    Name = "smoke-S615",
    Model = DeviceModelKind.S615,
    Host = Host,
    SnmpPort = 161,
    SshPort = 22,
    SnmpVersion = SnmpVersion.V2c,
    PreferredProtocol = ProtocolKind.Ssh,
};
var credential = new Credential(User, Pass, null, "public", null, null);

var driver = new S615Driver();
// Keep DryRun on so any accidental write is caught.
driver.DryRun = !enableWrite;

await using (driver)
{
    Banner("ConnectAsync");
    var sw = Stopwatch.StartNew();
    var connect = await driver.ConnectAsync(device, credential);
    sw.Stop();
    Log($"  Success={connect.Success}  Elapsed={sw.ElapsedMilliseconds} ms  Message={connect.Message}");
    if (!connect.Success)
    {
        Log("  ! Cannot continue without an SSH+SNMP connection.");
        return 1;
    }

    async Task RunRead<T>(string name, Func<CancellationToken, Task<OperationResult<T>>> call,
                          Func<T?, string>? summarize = null)
    {
        Log("");
        Log($">>> {name}");
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await call(default);
            sw.Stop();
            var summary = r.Success
                ? (summarize is null ? "ok" : summarize(r.Value))
                : $"FAIL: {r.Message}";
            Log($"    success={r.Success}  elapsed={sw.ElapsedMilliseconds} ms  {summary}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"    EXCEPTION elapsed={sw.ElapsedMilliseconds} ms  {ex.GetType().Name}: {ex.Message}");
        }
    }

    Banner("Read paths");

    await RunRead("GetStatusAsync (SNMP MIB-II)", driver.GetStatusAsync,
        v => v is null ? "null" : $"sysName='{v.SystemName}'  fw='{v.Firmware}'  uptime={v.Uptime}  ports={v.Ports.Count}");

    await RunRead("GetVlansAsync (SNMP Dot1qVlanReader)", driver.GetVlansAsync,
        v => v is null ? "null" : $"count={v.Count}; " + string.Join(" | ",
            v.Take(5).Select(vl =>
            {
                var untagged = string.Join(",", vl.Ports
                    .Where(p => p.Mode == VlanMemberMode.Untagged)
                    .Select(p => p.PortIndex));
                return $"{vl.Id}:{vl.Name} untagged=[{untagged}]";
            })));

    await RunRead("GetInterfacesAsync (SSH→SNMP)", driver.GetInterfacesAsync,
        v => v is null ? "null" : $"count={v.Count}; " + string.Join(" | ",
            v.Take(5).Select(i => $"{i.InterfaceName}={i.IpAddress}/{i.SubnetMask}")));

    await RunRead("GetNtpAsync (SSH `show ntp info`)", driver.GetNtpAsync,
        v => v is null ? "null" : $"enabled={v.Enabled}  servers={v.Servers.Count}  tz={v.Timezone}");

    await RunRead("GetDnsAsync (SSH `show dnsclient information`)", driver.GetDnsAsync,
        v => v is null ? "null" : $"servers=[{string.Join(",", v.Servers)}]  domain={v.DomainName}");

    await RunRead("ReadFirewallRulesAsync (SSH `show firewall ip-rules ipv4`)",
        driver.ReadFirewallRulesAsync,
        v => v is null ? "null" : $"count={v.Count}");

    await RunRead("ReadPredefinedRulesAsync (SSH `show firewall pre-rules ipv4`)",
        driver.ReadPredefinedRulesAsync,
        v => v is null ? "null" : $"count={v.Count}");

    await RunRead("GetVpnTunnelsAsync (SSH)", driver.GetVpnTunnelsAsync,
        v => v is null ? "null" : $"count={v.Count}");

    await RunRead("ListConfigBackupsAsync (SSH `show configbackup`)",
        driver.ListConfigBackupsAsync,
        v => v is null ? "null" : $"count={v.Count}; names=[{string.Join(",", v.Take(5))}]");

    Banner("Diagnostic");
    await RunRead("PingAsync(192.168.1.99, count=1, timeout=1)",
        ct => driver.PingAsync("192.168.1.99", new PingOptions { Count = 1, TimeoutSeconds = 1 }, ct),
        v => v is null ? "null" : $"output_len={v.Length}; tail={v.Replace("\r", " ").Replace("\n", " ").TrimEnd()[^Math.Min(120, v.Length)..]}");

    Banner("Write probes (DryRun preview only — nothing sent unless --enable-write)");
    Log($"  driver.DryRun = {driver.DryRun}");

    Log("");
    Log(">>> SetVlansAsync — preview only (re-send existing VLANs, should generate plan, not mutate)");
    var vlanRead = await driver.GetVlansAsync();
    if (vlanRead.Success && vlanRead.Value is not null)
    {
        var sw2 = Stopwatch.StartNew();
        var w = await driver.SetVlansAsync(vlanRead.Value);
        sw2.Stop();
        Log($"    success={w.Success}  elapsed={sw2.ElapsedMilliseconds} ms");
        Log($"    LastPlannedCommands ({driver.LastPlannedCommands.Count} lines):");
        foreach (var line in driver.LastPlannedCommands.Take(20))
            Log($"        {line}");
    }
    else
    {
        Log($"    skipped (couldn't read current VLANs first: {vlanRead.Message})");
    }

    Log("");
    Log(">>> SetNtpAsync — `forceExecute=true` so this DOES go to the device even with DryRun=true");
    Log("    Sending: empty config (NTP disabled, no servers) — equivalent to clearing the slot");
    var sw3 = Stopwatch.StartNew();
    var ntpW = await driver.SetNtpAsync(new NtpConfig
    {
        Enabled = false,
        Servers = new List<NtpServer>(),
        Timezone = "+00:00",
    });
    sw3.Stop();
    Log($"    success={ntpW.Success}  elapsed={sw3.ElapsedMilliseconds} ms  message={ntpW.Message}");

    Banner("Done");
    Log($"Total elapsed: see individual rows. Wrote log to {logPath}");
}

return 0;
