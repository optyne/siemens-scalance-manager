namespace Scalance.Core.Models;

public sealed class NtpConfig
{
    public bool Enabled { get; set; }
    public List<NtpServer> Servers { get; set; } = new();
    public string? Timezone { get; set; }
    public int PollIntervalSeconds { get; set; } = 64;
}

public sealed record NtpServer(string Host, int Port = 123, bool Preferred = false);
