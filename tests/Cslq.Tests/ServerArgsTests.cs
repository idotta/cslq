namespace Cslq.Tests;

/// <summary>
/// The thin client forwards options it does not recognise straight through to the underlying
/// server, so a renamed flag produces no error at all — the run just behaves differently.
/// These assert the whole argument list rather than probing for one flag, so any edit has to
/// be deliberate enough to update the expectation.
/// </summary>
public class ServerArgsTests
{
    [Fact]
    public void The_dedicated_server_is_started_over_stdio_with_projects_auto_loaded()
    {
        Assert.Equal(
            ["tool", "run", "roslyn-language-server", "--stdio", "--autoLoadProjects", "--logLevel", "Warning"],
            ServerArgs.Stdio("Warning"));
    }

    /// <summary>
    /// <c>--daemon-mode</c> is the thin client's flag; the server's own equivalent is
    /// <c>--daemon</c>, which the thin client passes to a detached double launch of its own.
    /// Deliberately no <c>--clientProcessId</c>: the server would exit when that process does,
    /// which is the whole point of a daemon.
    /// </summary>
    [Fact]
    public void The_daemon_is_asked_for_by_the_thin_clients_flag_and_never_pinned_to_this_process()
    {
        var args = ServerArgs.Daemon("Information");

        Assert.Equal(
            ["tool", "run", "roslyn-language-server", "--daemon-mode", "--stdio", "--autoLoadProjects",
             "--logLevel", "Information"],
            args);
        Assert.DoesNotContain("--clientProcessId", args);
    }

    /// <summary>
    /// The server does not advertise <c>positionEncoding</c>, which per LSP 3.17 means utf-16
    /// — the same unit as a .NET string index, so naive indexing is correct and counting runes
    /// or UTF-8 bytes is the bug. <c>InitializeAsync</c> asserts this rather than adapting.
    /// </summary>
    [Fact]
    public void The_expected_position_encoding_is_utf16()
    {
        Assert.Equal("utf-16", ServerArgs.ExpectedPositionEncoding);
    }
}
