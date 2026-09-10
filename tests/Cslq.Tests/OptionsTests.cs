namespace Cslq.Tests;

/// <summary>
/// Argument checking happens before the server starts, deliberately: everything after it
/// costs a cold load, so a typo answered by whatever failed first is answered minutes late
/// and about the wrong thing. <c>cslq bogus --root &lt;dir with no .csproj&gt;</c> once
/// reported the missing project.
/// </summary>
public class OptionsTests
{
    [Fact]
    public void Defaults_match_the_documented_usage()
    {
        var opts = Program.Options.Parse(["sym", "Greeter"]);

        Assert.Equal("sym", opts.Command);
        Assert.Equal("Greeter", opts.Argument);
        Assert.Equal(Output.DefaultMax, opts.Max);
        Assert.Equal(1, opts.Context);
        Assert.Equal(TimeSpan.FromSeconds(180), opts.Timeout);
        Assert.Equal("Warning", opts.LogLevel);
        Assert.False(opts.ErrorsOnly);
        Assert.False(opts.Json);
        Assert.True(opts.Daemon);
    }

    [Fact]
    public void The_daemon_is_on_unless_it_is_opted_out_of()
    {
        Assert.False(Program.Options.Parse(["ready", "--no-daemon"]).Daemon);
    }

    /// <summary>
    /// <c>--tfm</c> is off unless asked for, and the value is taken as written: matching it
    /// against the document's contexts happens where the contexts are known, not here.
    /// </summary>
    [Fact]
    public void A_target_framework_is_absent_unless_asked_for()
    {
        Assert.Null(Program.Options.Parse(["hover", "Only9"]).Tfm);
        Assert.Equal("net9.0", Program.Options.Parse(["hover", "Only9", "--tfm", "net9.0"]).Tfm);
    }

    [Fact]
    public void A_target_framework_needs_a_value()
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(["hover", "X", "--tfm"]));

        Assert.Equal("option '--tfm' needs a value", ex.Message);
    }

    [Fact]
    public void An_unknown_command_is_rejected_before_anything_starts()
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(["bogus"]));

        Assert.Contains("unknown command 'bogus'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_option_is_rejected()
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(["refs", "--nope"]));

        Assert.Contains("unknown option '--nope'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_positional_argument_is_rejected()
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(["refs", "A", "B"]));

        Assert.Contains("unexpected argument 'B'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_without_a_value_is_rejected()
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(["sym", "A", "--max"]));

        Assert.Contains("needs a value", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blank value is a misquoted shell variable, never a request. <c>--root ""</c> reached
    /// <c>Path.GetFullPath</c>, whose <c>ArgumentException</c> is not a <c>CslqException</c>,
    /// so the run ended in a stack trace and exit 127.
    /// </summary>
    [Theory]
    [InlineData("--root", "")]
    [InlineData("--root", "  ")]
    [InlineData("--sentinel", "")]
    [InlineData("--log-level", "")]
    [InlineData("--max", "")]
    public void An_option_given_a_blank_value_is_rejected(string option, string value)
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(["sym", "A", option, value]));

        Assert.Equal($"option '{option}' needs a value", ex.Message);
    }

    /// <summary>
    /// Overflow is not garbage: <c>--max 99999999999</c> is a number, just not one that fits,
    /// and "needs an integer" reads as a lie about the input.
    /// </summary>
    [Theory]
    [InlineData("99999999999", "out of range")]
    [InlineData("-99999999999", "out of range")]
    [InlineData("x", "needs an integer")]
    public void A_numeric_option_distinguishes_overflow_from_garbage(string value, string expected)
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(["sym", "A", "--max", value]));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_max_below_one_is_rejected_but_a_zero_timeout_is_not()
    {
        Assert.Throws<UsageException>(() => Program.Options.Parse(["sym", "A", "--max", "0"]));

        // A zero timeout is how `premature-query-fails-loudly` proves that a query fired
        // before load fails loudly rather than answering empty.
        Assert.Equal(TimeSpan.Zero, Program.Options.Parse(["ready", "--timeout", "0"]).Timeout);
    }

    [Fact]
    public void A_context_of_zero_is_allowed()
    {
        Assert.Equal(0, Program.Options.Parse(["refs", "A", "--context", "0"]).Context);
    }

    [Fact]
    public void A_nonexistent_root_is_rejected()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cslq-tests", Path.GetRandomFileName());

        var ex = Assert.Throws<CslqException>(() => Program.Options.Parse(["ready", "--root", missing]));

        Assert.Contains("no such directory", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Positions are one-based everywhere in <c>cslq</c>. Zero would hand Roslyn a negative
    /// position after the <c>line - 1</c>, which it throws out of as an unhandled RPC fault.
    /// </summary>
    [Theory]
    [InlineData("Core/Greeter.cs:0:1", "one-based")]
    [InlineData("Core/Greeter.cs:1:0", "one-based")]
    [InlineData("Core/Greeter.cs:+1:2", "not a position")]
    [InlineData("Core/Greeter.cs:1:", "not a position")]
    public void A_file_shaped_argument_that_is_not_a_position_is_rejected(string spec, string expected)
    {
        var ex = Assert.Throws<CslqException>(() => Program.Options.Parse(["refs", spec]));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_windows_drive_letter_does_not_read_as_a_line_number()
    {
        var opts = Program.Options.Parse(["refs", @"C:\dev\repo\Core\Greeter.cs:9:35"]);

        Assert.Equal(@"C:\dev\repo\Core\Greeter.cs:9:35", opts.Argument);
    }

    /// <summary>
    /// Only the commands that accept a position are validated: <c>sym Foo:1</c> is a
    /// legitimate query and <c>diag nope:x</c> a path, and validating those rejected both.
    /// </summary>
    [Theory]
    [InlineData("sym")]
    [InlineData("diag")]
    public void A_colon_in_a_non_position_argument_is_left_alone(string command)
    {
        Assert.Equal("Foo:1", Program.Options.Parse([command, "Foo:1"]).Argument);
    }

    /// <summary>
    /// The pre-warm. It reaches no workspace, so the only thing the parser has to get right
    /// is that it is a command at all and that <c>--json</c> still applies to it.
    /// </summary>
    [Fact]
    public void Restore_parses_with_no_argument()
    {
        var opts = Program.Options.Parse(["restore"]);

        Assert.Equal("restore", opts.Command);
        Assert.Null(opts.Argument);
        Assert.False(opts.Json);
        Assert.True(Program.Options.Parse(["restore", "--json"]).Json);
    }

    /// <summary>
    /// The manifest restored is the one packed beside the running binary, found by walking up
    /// from it. A path argument would read as if it could be pointed somewhere else.
    /// </summary>
    [Fact]
    public void Restore_rejects_an_argument()
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(["restore", "somewhere"]));

        Assert.Contains("restore takes no argument", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An option is an option wherever it sits: `cslq --root . --timeout 600 def ContentItem`
    /// was `unknown command '--root'` plus a usage block that never said the command had to
    /// come first. The option set is closed and each member is a flag or takes exactly one
    /// value, so what is left after the options are lifted out is the positionals, in order.
    /// </summary>
    [Fact]
    public void Options_are_accepted_before_the_command()
    {
        var opts = Program.Options.Parse(["--root", ".", "--timeout", "600", "def", "ContentItem"]);

        Assert.Equal("def", opts.Command);
        Assert.Equal("ContentItem", opts.Argument);
        Assert.Equal(TimeSpan.FromSeconds(600), opts.Timeout);
    }

    /// <summary>
    /// Between the command and its argument, and after both: the same parse either way, and
    /// the same one as the all-trailing form the usage text shows.
    /// </summary>
    [Fact]
    public void Options_are_accepted_between_and_after_the_positionals()
    {
        var between = Program.Options.Parse(["refs", "--max", "5", "Greet", "--json"]);
        var after = Program.Options.Parse(["refs", "Greet", "--max", "5", "--json"]);

        Assert.Equal(after, between);
        Assert.Equal("Greet", between.Argument);
        Assert.Equal(5, between.Max);
        Assert.True(between.Json);
    }

    /// <summary>
    /// A value is consumed by the option that asked for it, so a value that happens to spell
    /// a command is a value: `--sentinel ready refs Greet` asks `refs`, not `ready`.
    /// </summary>
    [Fact]
    public void An_option_value_is_never_read_as_a_positional()
    {
        var opts = Program.Options.Parse(["--sentinel", "ready", "refs", "Greet"]);

        Assert.Equal("refs", opts.Command);
        Assert.Equal("Greet", opts.Argument);
        Assert.Equal("ready", opts.Sentinel);
    }

    [Fact]
    public void No_command_at_all_is_a_usage_error()
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(["--json"]));

        Assert.Equal("no command given", ex.Message);
    }

    /// <summary>
    /// Exit 2 is the invocation the parser could not understand, and the type is what carries
    /// it: `Main` answers a <c>UsageException</c> with the line, the usage block and 2, and
    /// everything else with the line and 1. The usage text is no longer folded into the
    /// message, so it is not asserted here.
    /// </summary>
    [Theory]
    [InlineData(new[] { "bogus" }, "unknown command 'bogus'")]
    [InlineData(new[] { "refs", "--nope" }, "unknown option '--nope'")]
    [InlineData(new[] { "refs", "A", "B" }, "unexpected argument 'B'")]
    [InlineData(new[] { "sym", "A", "--max" }, "option '--max' needs a value")]
    [InlineData(new[] { "sym", "A", "--max", "x" }, "--max needs an integer")]
    [InlineData(new[] { "sym", "A", "--max", "0" }, "--max needs to be 1 or more")]
    public void A_malformed_invocation_is_a_usage_error(string[] argv, string expected)
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(argv));

        Assert.Equal(expected, ex.Message);
    }

    /// <summary>
    /// A workspace that is not there is not a usage mistake: the command line was understood
    /// and the query failed, which is exit 1. The same for a malformed position — it is the
    /// target, not the grammar.
    /// </summary>
    [Fact]
    public void A_failure_the_parser_understood_is_not_a_usage_error()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cslq-tests", Path.GetRandomFileName());

        Assert.IsNotType<UsageException>(
            Assert.Throws<CslqException>(() => Program.Options.Parse(["ready", "--root", missing])));
        Assert.IsNotType<UsageException>(
            Assert.Throws<CslqException>(() => Program.Options.Parse(["refs", "Core/Greeter.cs:0:1"])));
    }

    /// <summary>
    /// `ready Greet --root fixture` printed `ready` and exited 0, so an agent that meant
    /// `--sentinel Greet` was told the workspace was ready for a symbol nothing had probed
    /// for. The message names the option it was probably reaching for.
    /// </summary>
    [Fact]
    public void Ready_rejects_a_stray_positional_and_points_at_the_sentinel()
    {
        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(["ready", "Greet"]));

        Assert.Equal(
            "ready takes no argument; got 'Greet' (did you mean --sentinel Greet?)", ex.Message);
    }

    /// <summary>
    /// The commands that do take one still do, `diag`'s optional path included.
    /// </summary>
    [Theory]
    [InlineData("sym")]
    [InlineData("diag")]
    [InlineData("outline")]
    [InlineData("project")]
    public void A_command_that_takes_an_argument_still_takes_one(string command)
    {
        Assert.Equal("Foo", Program.Options.Parse([command, "Foo"]).Argument);
    }

    /// <summary>
    /// The seven names the server's own <c>--logLevel</c> parses, checked here rather than
    /// forwarded: against a running daemon a bad one was accepted and did nothing at all.
    /// </summary>
    [Theory]
    [InlineData("Trace")]
    [InlineData("Information")]
    [InlineData("information")]
    [InlineData("NONE")]
    public void A_log_level_the_server_knows_is_forwarded_as_written(string level)
    {
        Assert.Equal(level, Program.Options.Parse(["ready", "--log-level", level]).LogLevel);
    }

    [Fact]
    public void A_log_level_the_server_does_not_know_is_a_usage_error()
    {
        var ex = Assert.Throws<UsageException>(
            () => Program.Options.Parse(["ready", "--log-level", "bogus"]));

        Assert.Equal(
            "unknown --log-level 'bogus'; expected one of " +
            "Trace, Debug, Information, Warning, Error, Critical, None",
            ex.Message);
    }

    /// <summary>
    /// Only `Trace`, `Debug` and `Information` ask for more than warnings, and the check is
    /// case-insensitive because the level is taken as the caller wrote it.
    /// </summary>
    [Theory]
    [InlineData("Information", true)]
    [InlineData("debug", true)]
    [InlineData("Warning", false)]
    [InlineData("None", false)]
    public void Verbose_is_the_levels_below_warning(string level, bool verbose)
    {
        Assert.Equal(verbose, Program.Options.Parse(["ready", "--log-level", level]).Verbose);
    }

    /// <summary>
    /// `sym Only10 --root fixture2 --tfm net9.0` printed the net10.0 hit too, at exit 0:
    /// `workspace/symbol` is context-independent, and `ready` and `restore` name no document,
    /// so there is no context for the option to choose and it filtered nothing.
    /// </summary>
    [Theory]
    [InlineData("sym")]
    [InlineData("ready")]
    [InlineData("restore")]
    public void A_command_with_no_project_context_rejects_a_target_framework(string command)
    {
        string[] argv = command == "sym" ? [command, "Only10", "--tfm", "net9.0"] : [command, "--tfm", "net9.0"];

        var ex = Assert.Throws<UsageException>(() => Program.Options.Parse(argv));

        Assert.Equal(
            $"--tfm does not apply to {command}; it is honoured by " +
            "refs, def, impl, hover, outline, diag, project",
            ex.Message);
    }

    /// <summary>
    /// The rejection list and the honouring list are complements: every command is in exactly
    /// one of them, so a command added to `Commands` and to neither list would be caught here
    /// rather than by silently accepting an option it ignores.
    /// </summary>
    [Fact]
    public void Every_command_either_honours_the_target_framework_or_rejects_it()
    {
        foreach (var command in Program.Commands)
        {
            if (Program.TakesTfm.Contains(command)) continue;

            string[] argv = command == "sym" ? [command, "Q", "--tfm", "net9.0"] : [command, "--tfm", "net9.0"];
            Assert.Throws<UsageException>(() => Program.Options.Parse(argv));
        }
    }
}
