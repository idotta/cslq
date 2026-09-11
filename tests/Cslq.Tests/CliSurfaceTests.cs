namespace Cslq.Tests;

/// <summary>
/// The surface a caller reaches before anything starts a server: asking for help or the
/// version, and the one startup failure a first run always hits.
/// </summary>
public class CliSurfaceTests
{
    [Fact]
    public void No_arguments_is_the_usage_text()
    {
        Assert.Equal(Program.Immediate.Usage, Program.Preflight([]));
    }

    /// <summary>`cslq refs Foo --help` was `unknown option '--help'` until this existed.</summary>
    [Fact]
    public void Help_is_answered_wherever_it_appears()
    {
        Assert.Equal(Program.Immediate.Help, Program.Preflight(["--help"]));
        Assert.Equal(Program.Immediate.Help, Program.Preflight(["-h"]));
        Assert.Equal(Program.Immediate.Help, Program.Preflight(["refs", "Foo", "--help"]));
        Assert.Equal(Program.Immediate.Help, Program.Preflight(["diag", "--root", "x", "-h"]));
    }

    [Fact]
    public void Version_is_answered_wherever_it_appears()
    {
        Assert.Equal(Program.Immediate.Version, Program.Preflight(["--version"]));
        Assert.Equal(Program.Immediate.Version, Program.Preflight(["sym", "Foo", "--version"]));
    }

    /// <summary>Help wins: it is the more useful answer to a caller who asked for both.</summary>
    [Fact]
    public void Help_beats_version()
    {
        Assert.Equal(Program.Immediate.Help, Program.Preflight(["--version", "--help"]));
    }

    [Fact]
    public void An_ordinary_command_reaches_the_parser()
    {
        Assert.Equal(Program.Immediate.None, Program.Preflight(["refs", "Greet", "--root", "fixture"]));
    }

    /// <summary>
    /// One version string: what `--version` prints is what `initialize` sends and what the
    /// package is built with, so `Version` in Cslq.csproj is the only place it is written.
    /// </summary>
    [Fact]
    public void The_version_is_the_assemblys_own_and_looks_like_one()
    {
        Assert.Equal(
            typeof(Program).Assembly.GetName().Version!.ToString(3),
            Build.Version);
    }

    [Theory]
    [InlineData("0.1.0+9a1b2c3", "0.1.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(null, "0.0.0")]
    [InlineData("", "0.0.0")]
    public void Build_metadata_is_not_part_of_the_version(string? informational, string want)
    {
        Assert.Equal(want, Build.Clean(informational));
    }

    /// <summary>
    /// The prose around it is localised — an unrestored tool answers in Portuguese on the
    /// dev machine without <c>DOTNET_CLI_UI_LANGUAGE</c> — but the quoted command is not.
    /// </summary>
    [Theory]
    [InlineData("Run \"dotnet tool restore\" to make the \"roslyn-language-server\" command available.")]
    [InlineData("Execute \"dotnet tool restore\" para tornar o comando disponível.")]
    public void The_not_restored_failure_is_recognised_by_the_command_it_names(string stderr)
    {
        Assert.True(LspClient.NotRestored(stderr));
    }

    [Fact]
    public void An_unrelated_startup_failure_is_not_a_missing_restore()
    {
        Assert.False(LspClient.NotRestored(
            "the language server closed the connection during initialize: connection lost"));
    }

    /// <summary>
    /// Restore runs in the manifest directory, which is why it takes no path argument.
    /// </summary>
    [Fact]
    public void The_restore_command_is_the_manifest_restore()
    {
        Assert.Equal(["tool", "restore"], ServerArgs.Restore());
    }

    /// <summary>
    /// The prune after a restore deletes from wherever NuGet says it extracts, so the folder
    /// is asked for, not assumed to be <c>~/.nuget/packages</c>.
    /// </summary>
    [Fact]
    public void The_packages_folder_is_asked_of_the_cli()
    {
        Assert.Equal(["nuget", "locals", "global-packages", "--list"], ServerArgs.GlobalPackages());
    }

    /// <summary>
    /// A command wired into the dispatch but never into the usage text is invisible to the
    /// caller who most needs it — the one reading <c>cslq --help</c> to find out what exists.
    /// </summary>
    [Fact]
    public void Every_command_is_named_in_the_usage_text()
    {
        foreach (var command in Program.Commands)
        {
            Assert.Contains("cslq " + command, Program.Usage, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>sym</c> is Roslyn's fuzzy matcher and looked like a lookup: measured on the fixture,
    /// <c>AV</c> answers <c>AudioVolume</c> and <c>Greter</c> answers <c>Greeter</c>, so a
    /// caller who reads a hit as "this name exists" is reading it wrong. The usage text is
    /// where that has to be said, because it is the one page every caller sees.
    /// </summary>
    [Fact]
    public void The_usage_text_says_sym_is_fuzzy()
    {
        Assert.Contains("sym is a fuzzy search", Program.Usage, StringComparison.Ordinal);
        Assert.Contains("AV finds AudioVolume", Program.Usage, StringComparison.Ordinal);
        Assert.Contains("ALL-CAPS", Program.Usage, StringComparison.Ordinal);
    }
}
