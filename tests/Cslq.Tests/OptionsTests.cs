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

    [Fact]
    public void An_unknown_command_is_rejected_before_anything_starts()
    {
        var ex = Assert.Throws<CslqException>(() => Program.Options.Parse(["bogus"]));

        Assert.Contains("unknown command 'bogus'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_option_is_rejected()
    {
        var ex = Assert.Throws<CslqException>(() => Program.Options.Parse(["refs", "--nope"]));

        Assert.Contains("unknown option '--nope'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_positional_argument_is_rejected()
    {
        var ex = Assert.Throws<CslqException>(() => Program.Options.Parse(["refs", "A", "B"]));

        Assert.Contains("unexpected argument 'B'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_without_a_value_is_rejected()
    {
        var ex = Assert.Throws<CslqException>(() => Program.Options.Parse(["sym", "A", "--max"]));

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
        var ex = Assert.Throws<CslqException>(() => Program.Options.Parse(["sym", "A", option, value]));

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
        var ex = Assert.Throws<CslqException>(() => Program.Options.Parse(["sym", "A", "--max", value]));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_max_below_one_is_rejected_but_a_zero_timeout_is_not()
    {
        Assert.Throws<CslqException>(() => Program.Options.Parse(["sym", "A", "--max", "0"]));

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
        var ex = Assert.Throws<CslqException>(() => Program.Options.Parse(["restore", "somewhere"]));

        Assert.Contains("restore takes no argument", ex.Message, StringComparison.Ordinal);
    }
}
