using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Scalance.Protocols.Ssh;

public sealed class SshSession : IAsyncDisposable
{
    private readonly Renci.SshNet.SshClient _client;
    private ShellStream? _shell;

    /// <summary>
    /// True if the device requires a password change on first login.
    /// When set, the session is not usable for normal commands until the
    /// password is changed via WBM or console.
    /// </summary>
    public bool PasswordChangeRequired { get; private set; }

    /// <summary>
    /// Default per-command timeout used when a caller does not pass one.
    /// We drive the SCALANCE CLI through an interactive shell stream and
    /// read until the next CLI prompt, so a command's wall-clock cost is
    /// bounded by this value. 15 s is a compromise: long enough to cover
    /// `show running-config` / `configbackup create` / `ping` on a real
    /// S615 (most replies arrive in ~50 ms, ping with count=1 takes ~2 s)
    /// without making typo-induced "no prompt" stalls feel like a hang.
    /// </summary>
    public TimeSpan DefaultCommandTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Matches the SCALANCE CLI prompt at the END of an accumulated buffer.
    ///
    /// IMPORTANT: SCALANCE's prompt is NOT a fixed string. It echoes the
    /// device's <c>system name</c>. A fresh device shows <c>CLI#</c>;
    /// setting <c>system name Foo</c> immediately changes the prompt to
    /// <c>Foo#</c>; entering IPsec phase-1 config then gives
    /// <c>Foo(config-ipsec-conn-phase1)#</c>. PH_SCALANCE-S615-CLI_76
    /// documents the lowercase examples (<c>cli(config-…)#</c>) but the
    /// firmware on the unit at 192.168.0.230 (V08.00.00, 2026-05-03)
    /// emits <c>CLI#</c> uppercase out of the box. So we don't anchor on
    /// the literal "cli" — we anchor on the *shape*:
    ///
    ///   &lt;hostname-token&gt; ('(' &lt;mode-label&gt; ')')? ('#' | '&gt;') &lt;eol&gt;
    ///
    /// Examples that all match:
    ///   CLI#
    ///   CLI&gt;
    ///   cli(config)#
    ///   cli(config-vlan-1)#
    ///   cli(config-if-vlan-1)#
    ///   cli(config-ntp)#
    ///   cli(config-ipsec)#
    ///   cli(config-ipsec-conn-phase1)#
    ///   PROBE_1777811373#                       (after `system name PROBE_…`)
    ///   my-device(config-events)#
    ///
    /// The hostname token allows ASCII letters / digits / underscore /
    /// hyphen, which covers everything S615's `system name` accepts after
    /// SSH-safety filtering. We don't try to parse which mode we're in —
    /// the call sites in ScalanceCliCommands already track that. This
    /// regex just needs to recognise that the device is *waiting for
    /// input* again.
    /// </summary>
    private static readonly Regex PromptPattern = new(
        @"[A-Za-z_][\w\-]*(?:\([^)\r\n]+\))?[#>]\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Strips ANSI / VT escape sequences so prompt detection isn't fooled
    /// by terminal control codes that some firmware revisions emit even on
    /// a vt100 terminal (cursor moves, colour resets).
    /// </summary>
    private static readonly Regex AnsiRegex = new(
        @"\x1B\[[0-9;]*[A-Za-z]|\x1B\][^\x07]*\x07|\x1B[=>]",
        RegexOptions.Compiled);

    private SshSession(Renci.SshNet.SshClient client)
    {
        _client = client;
    }

    public static async Task<SshSession> ConnectAsync(
        string host,
        int port,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var info = new ConnectionInfo(host, port, username,
            new PasswordAuthenticationMethod(username, password))
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
        var client = new Renci.SshNet.SshClient(info);
        try
        {
            await Task.Run(() => client.Connect(), ct);
        }
        catch (SshAuthenticationException ex) when (
            ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            var session = new SshSession(client) { PasswordChangeRequired = true };
            throw new InvalidOperationException(
                $"設備要求變更預設密碼。請透過 WBM (https://{host}) 或 console 登入並變更密碼後重試。", ex);
        }
        return new SshSession(client);
    }

    public static async Task<SshSession> ConnectWithKeyAsync(
        string host,
        int port,
        string username,
        string privateKeyPath,
        string? passphrase,
        CancellationToken ct = default)
    {
        var keyFile = string.IsNullOrEmpty(passphrase)
            ? new PrivateKeyFile(privateKeyPath)
            : new PrivateKeyFile(privateKeyPath, passphrase);
        var info = new ConnectionInfo(host, port, username,
            new PrivateKeyAuthenticationMethod(username, keyFile))
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
        var client = new Renci.SshNet.SshClient(info);
        await Task.Run(() => client.Connect(), ct);
        return new SshSession(client);
    }

    /// <summary>
    /// Attempts to change the device password through the SSH session.
    /// This is a placeholder — the actual prompt sequence needs real device testing.
    /// </summary>
    public async Task<string> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var cmds = new[] { currentPassword, newPassword, newPassword };
        var results = await RunBatchAsync(cmds, ct);
        return string.Join("\n", results);
    }

    /// <summary>
    /// Lazily opens a single interactive shell channel for this session and
    /// drains the welcome banner / first prompt before returning. We can't
    /// use <c>SshClient.CreateCommand(...).Execute()</c> against SCALANCE
    /// because SCALANCE's SSH server doesn't EOF an <c>exec</c> channel
    /// after a <c>show …</c> completes — it expects a long-lived shell
    /// with a prompt. Field-tested on a real S615 (192.168.0.230,
    /// FW V08.00.00, 2026-05-03): exec channel timed out on every CLI read
    /// (`show ntp info`, `show vlan`, `ping …`) even though SSH connect /
    /// auth completed in &lt; 1 s. ShellStream + read-until-prompt returned
    /// the same commands in 50 ms – 2 s.
    /// </summary>
    private async Task<ShellStream> EnsureShellAsync(CancellationToken ct)
    {
        if (_shell is not null) return _shell;
        // Generous window so most "show …" output fits without triggering
        // --More-- paging. SCALANCE doesn't have a portable
        // `terminal length 0` knob, so we rely on the window dimensions
        // negotiated at channel open time.
        var shell = _client.CreateShellStream(
            terminalName: "vt100",
            columns: 200u,
            rows: 200u,
            width: 2048u,
            height: 1024u,
            bufferSize: 65536);
        // Drain banner + first prompt. If this read times out the rest of
        // the session is unusable, so let the exception bubble up.
        await ReadUntilPromptAsync(shell, DefaultCommandTimeout, ct);
        _shell = shell;
        return shell;
    }

    /// <summary>
    /// Reads from <paramref name="shell"/> until the SCALANCE CLI prompt
    /// is observed at the end of the accumulated output, or
    /// <paramref name="timeout"/> elapses. Returns the raw bytes
    /// (including the echoed command line and the prompt itself); call
    /// sites are responsible for parsing them out as needed.
    /// </summary>
    private static async Task<string> ReadUntilPromptAsync(
        ShellStream shell, TimeSpan timeout, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (shell.DataAvailable)
            {
                int n = shell.Read(buf, 0, buf.Length);
                if (n > 0)
                {
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                    // Strip ANSI before testing the prompt regex so a
                    // colour-reset code right before `CLI#` doesn't break
                    // detection. We don't store the stripped form because
                    // some callers may want the raw bytes.
                    if (PromptPattern.IsMatch(StripAnsi(sb.ToString())))
                        return sb.ToString();
                }
            }
            else
            {
                await Task.Delay(50, ct);
            }
        }
        throw new TimeoutException(
            $"Timed out waiting for SCALANCE CLI prompt after {timeout.TotalSeconds:F1} s. " +
            $"Received {sb.Length} bytes; tail = '{Tail(sb.ToString(), 80)}'.");
    }

    private static string Tail(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = StripAnsi(s).Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length <= n ? s : "…" + s[^n..];
    }

    private static string StripAnsi(string s) => AnsiRegex.Replace(s, "");

    public Task<string> RunAsync(string command, CancellationToken ct = default)
        => RunAsync(command, DefaultCommandTimeout, ct);

    public async Task<string> RunAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var shell = await EnsureShellAsync(ct);
        shell.WriteLine(command);
        return await ReadUntilPromptAsync(shell, timeout, ct);
    }

    /// <summary>
    /// Runs a command with a custom timeout. Returns null on timeout instead
    /// of throwing. Useful for best-effort reads that should not block the UI.
    /// </summary>
    public async Task<string?> TryRunAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            return await RunAsync(command, timeout, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Run a sequence of CLI lines through the SAME interactive shell
    /// channel so that mode-changing commands (<c>configure terminal</c>,
    /// <c>ntp</c>, <c>events</c>, <c>ipsec</c>, <c>firewall</c>,
    /// <c>interface …</c>, …) carry state to the commands that follow.
    ///
    /// Each line is sent independently and we wait for the next prompt
    /// before issuing the following one. The previous newline-joined
    /// implementation worked around the exec-channel limitation by sending
    /// the whole batch as one input buffer; with a real shell stream we
    /// can wait for per-command prompts so a typo on line N still leaves
    /// us in a known state for line N+1.
    ///
    /// Returns a single-element list containing the combined output so the
    /// call-site contract (IReadOnlyList&lt;string&gt;) is preserved.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunBatchAsync(IEnumerable<string> commands, CancellationToken ct = default)
    {
        var lines = commands?.Where(l => l is not null).ToArray() ?? Array.Empty<string>();
        if (lines.Length == 0) return Array.Empty<string>();
        ct.ThrowIfCancellationRequested();
        var shell = await EnsureShellAsync(ct);
        var combined = new StringBuilder();
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            shell.WriteLine(line);
            combined.AppendLine(await ReadUntilPromptAsync(shell, DefaultCommandTimeout, ct));
        }
        return new[] { combined.ToString() };
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _shell?.Dispose();
            if (_client.IsConnected) _client.Disconnect();
        }
        catch { }
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
