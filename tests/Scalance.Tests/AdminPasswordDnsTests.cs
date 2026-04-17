using FluentAssertions;
using Scalance.Core.Models;
using Scalance.Drivers;

namespace Scalance.Tests;

public class AdminPasswordDnsTests
{
    [Fact]
    public void BuildSetAdminPassword_wraps_with_configure_and_write()
    {
        var cmds = ScalanceCliCommands.BuildSetAdminPassword("admin", "s3cret!");

        cmds[0].Should().Be("configure terminal");
        cmds.Should().Contain("username admin password s3cret!");
        cmds.Should().Contain("end");
        cmds[^1].Should().Be("write memory");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BuildSetAdminPassword_rejects_blank_password(string pwd)
    {
        var act = () => ScalanceCliCommands.BuildSetAdminPassword("admin", pwd);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("has\rcr")]
    [InlineData("quoted \"thing\"")]
    public void BuildSetAdminPassword_rejects_injection_chars(string pwd)
    {
        var act = () => ScalanceCliCommands.BuildSetAdminPassword("admin", pwd);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetDns_enters_dnsclient_mode_and_adds_servers()
    {
        var cfg = new DnsConfig { Servers = { "8.8.8.8", "1.1.1.1" }, DomainName = "example.com" };
        var cmds = ScalanceCliCommands.BuildSetDns(cfg);

        cmds[0].Should().Be("configure terminal");
        cmds.Should().Contain("dnsclient");
        cmds.Should().Contain("server type manual");
        cmds.Should().Contain("manual srv 8.8.8.8");
        cmds.Should().Contain("manual srv 1.1.1.1");
        cmds.Should().Contain("no shutdown");
        cmds.Should().Contain("ip domain-name example.com");
        cmds[^1].Should().Be("write memory");
    }

    [Fact]
    public void BuildSetDns_with_no_servers_shuts_down_client()
    {
        var cmds = ScalanceCliCommands.BuildSetDns(new DnsConfig());
        cmds.Should().Contain("shutdown");
        cmds.Should().NotContain(c => c.StartsWith("manual srv "));
    }

    [Fact]
    public async Task ApplyBasicWizard_DryRun_aggregates_all_step_commands_into_preview()
    {
        var driver = new S615Driver { DryRun = true };
        var cfg = new BasicWizardConfig
        {
            Hostname = "scalance-lab",
            // Omit Ntp — SetNtpAsync uses forceExecute=true which bypasses DryRun
            // and would try to open an SSH session.
            Dns = new DnsConfig { Servers = { "8.8.8.8" } },
            NewAdminPassword = "new-p@ss"
        };

        var r = await driver.ApplyBasicWizardAsync(cfg);

        r.Success.Should().BeTrue();
        // The aggregated preview should include commands from ALL three steps,
        // not just the last one (password).
        driver.LastPlannedCommands.Should().Contain("hostname scalance-lab");
        driver.LastPlannedCommands.Should().Contain("manual srv 8.8.8.8");
        driver.LastPlannedCommands.Should().Contain("username admin password new-p@ss");
    }

    [Fact]
    public void ParseDnsClient_extracts_manual_servers()
    {
        var output = """
            DNS client:
                Status         : enabled
                manual srv 8.8.8.8
                manual srv 1.1.1.1
                Domain Name: example.com
            """;
        var cfg = ScalanceCliCommands.ParseDnsClient(output);

        cfg.Servers.Should().BeEquivalentTo(new[] { "8.8.8.8", "1.1.1.1" });
        cfg.DomainName.Should().Be("example.com");
    }
}
