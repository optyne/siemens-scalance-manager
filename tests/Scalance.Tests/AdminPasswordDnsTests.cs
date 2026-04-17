using FluentAssertions;
using Scalance.Core.Models;
using Scalance.Drivers;

namespace Scalance.Tests;

public class AdminPasswordDnsTests
{
    [Fact]
    public void BuildChangeOwnPassword_emits_single_EXEC_mode_command()
    {
        // Verified S615 CLI manual sec 12.1.2 p. 567: `change password <pwd>`
        // runs in User/Privileged EXEC mode; no configure terminal, no write memory.
        var cmds = ScalanceCliCommands.BuildChangeOwnPassword("s3cret!");
        cmds.Should().ContainSingle();
        cmds[0].Should().Be("change password s3cret!");
    }

    [Fact]
    public void BuildSetUserAccount_wraps_config_and_requires_role()
    {
        // Verified S615 CLI manual sec 12.1.4.7 p. 575: global config,
        // `user-account <name> password <pwd> role <role>`.
        var cmds = ScalanceCliCommands.BuildSetUserAccount("operator", "Pa55w0rd!", "admin");
        cmds[0].Should().Be("configure terminal");
        cmds.Should().Contain("user-account operator password Pa55w0rd! role admin");
        cmds.Should().Contain("end");
        cmds[^1].Should().Be("write memory");
    }

    [Fact]
    public void PasswordValidation_allows_backtick()
    {
        // Backtick was previously (incorrectly) disallowed — a transcription
        // error. S615 manual p. 576 lists 'ß' (sharp-s), NOT '`' (backtick).
        var cmds = ScalanceCliCommands.BuildChangeOwnPassword("back`tick");
        cmds[0].Should().Be("change password back`tick");
    }

    [Fact]
    public void BuildSetUserAccount_rejects_missing_role()
    {
        var act = () => ScalanceCliCommands.BuildSetUserAccount("u", "Pwd1234!", "");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("name;withsemi")]   // manual p. 575 disallowed
    [InlineData("name with space")] // manual p. 575 disallowed
    [InlineData("name?q")]           // manual p. 575 disallowed
    [InlineData("name\"quoted")]    // manual p. 575 disallowed
    [InlineData("name\ninjection")] // CR/LF SSH defence
    public void BuildSetUserAccount_rejects_disallowed_username_chars(string username)
    {
        var act = () => ScalanceCliCommands.BuildSetUserAccount(username, "Pwd1234!", "admin");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetUserAccount_rejects_username_over_250_chars()
    {
        var act = () => ScalanceCliCommands.BuildSetUserAccount(
            new string('u', 251), "Pwd1234!", "admin");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetUserAccount_rejects_role_with_space_or_newline()
    {
        // Role reaches the device on the same line as the password; a space
        // would shift field positions and a newline would break the batch.
        var act = () => ScalanceCliCommands.BuildSetUserAccount("u", "Pwd1234!", "admin sneaky");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void PasswordValidation_rejects_blank(string pwd)
    {
        var act = () => ScalanceCliCommands.BuildChangeOwnPassword(pwd);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("line1\nline2")]      // SSH line break — transport-layer defence
    [InlineData("has\rcr")]           // CR — transport-layer defence
    [InlineData("quoted \"thing\"")]  // quote — transport-layer defence
    [InlineData("semi;colon")]        // S615 manual p. 576 disallowed
    [InlineData("back\\slash")]       // S615 manual p. 576 disallowed
    [InlineData("has space")]         // S615 manual p. 576 disallowed
    [InlineData("qu?mark")]           // S615 manual p. 576 disallowed
    [InlineData("schlo\u00dfword")]   // ß (U+00DF) — S615 manual p. 576 disallowed
    public void PasswordValidation_rejects_disallowed_chars(string pwd)
    {
        var act = () => ScalanceCliCommands.BuildChangeOwnPassword(pwd);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetDns_rejects_more_than_three_servers()
    {
        // Manual p. 414: maximum of three DNS servers.
        var cfg = new DnsConfig { Servers = { "1.1.1.1", "2.2.2.2", "3.3.3.3", "4.4.4.4" } };
        var act = () => ScalanceCliCommands.BuildSetDns(cfg);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetDns_rejects_non_ipv4_server()
    {
        // Manual p. 414: `manual srv <ip_addr>` requires a valid IP.
        var cfg = new DnsConfig { Servers = { "not-an-ip" } };
        var act = () => ScalanceCliCommands.BuildSetDns(cfg);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSetDns_rejects_domain_name_with_space()
    {
        var cfg = new DnsConfig { Servers = { "8.8.8.8" }, DomainName = "foo bar.com" };
        var act = () => ScalanceCliCommands.BuildSetDns(cfg);
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
        // Verified: `ip domain name <name>` (space, not hyphen) — manual p. 10741.
        cmds.Should().Contain("ip domain name example.com");
        // Verified: `no manual all` clears previous — manual sec 9.7.3.2 p. 415.
        cmds.Should().Contain("no manual all");
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
        driver.LastPlannedCommands.Should().Contain("system name scalance-lab");
        driver.LastPlannedCommands.Should().Contain("manual srv 8.8.8.8");
        // Driver not connected → _credential is null → falls back to user-account path.
        driver.LastPlannedCommands.Should().Contain(c => c.StartsWith("user-account admin password new-p@ss role"));
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
