using Renci.SshNet;
using Renci.SshNet.Common;

namespace Scalance.Protocols.Ssh;

public sealed class SshSession : IAsyncDisposable
{
    private readonly Renci.SshNet.SshClient _client;

    /// <summary>
    /// True if the device requires a password change on first login.
    /// When set, the session is not usable for normal commands until the
    /// password is changed via WBM or console.
    /// </summary>
    public bool PasswordChangeRequired { get; private set; }

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

    public async Task<string> RunAsync(string command, CancellationToken ct = default)
    {
        using var cmd = _client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(5);
        return await Task.Run(() => cmd.Execute(), ct);
    }

    /// <summary>
    /// Runs a command with a custom timeout. Returns null on timeout instead of throwing.
    /// Useful for best-effort reads that should not block the UI.
    /// </summary>
    public async Task<string?> TryRunAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var cmd = _client.CreateCommand(command);
            cmd.CommandTimeout = timeout;
            return await Task.Run(() => cmd.Execute(), ct);
        }
        catch { return null; }
    }

    public async Task<IReadOnlyList<string>> RunBatchAsync(IEnumerable<string> commands, CancellationToken ct = default)
    {
        var outputs = new List<string>();
        foreach (var c in commands)
        {
            ct.ThrowIfCancellationRequested();
            outputs.Add(await RunAsync(c, ct));
        }
        return outputs;
    }

    public ValueTask DisposeAsync()
    {
        try { if (_client.IsConnected) _client.Disconnect(); } catch { }
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
