namespace Cslq.Tests;

/// <summary>
/// The readiness failure text. Two findings live here and neither can be staged live:
/// the 1,050-character single line a 26-project repository produced, and the short
/// <c>--timeout</c> reported as <c>projectInitializationComplete never fired</c> — a state
/// that is ordinary on every attach until the reload ends, and so reads as a fault that is
/// not one. Both are layout and wording over inputs a temp tree and a string can supply.
/// </summary>
public class ReadinessMessageTests
{
    [Fact]
    public void Every_item_is_on_its_own_line()
    {
        var message = Readiness.Message(new Readiness.Failure(
            TimeSpan.FromSeconds(180),
            Fired: true,
            [Pending("Core", "Greeter"), Pending("App", "Program", "Square"), Pending("Gen", "Gen")],
            ["Resources"],
            ["Linked.Shared"]));

        var lines = message.Split('\n');

        // Headline, three projects, the two not-probed classes.
        Assert.Equal(6, lines.Length);
        Assert.All(lines[1..], l => Assert.StartsWith("  ", l));
        Assert.All(lines, l => Assert.True(l.Length < 200, l));
    }

    /// <summary>
    /// The substring two probe legs match on, kept deliberately: the layout moved, the claim
    /// did not.
    /// </summary>
    [Fact]
    public void A_pending_project_is_named_with_what_was_asked_for_it()
    {
        var message = Readiness.Message(new Readiness.Failure(
            TimeSpan.FromSeconds(150), Fired: true, [Pending("B", "Ghost")], [], []));

        Assert.Contains("Workspace did not become ready within 150s", message);
        Assert.Contains("projectInitializationComplete fired", message);
        Assert.Contains("sentinel query 'Ghost' returned no symbols for project B", message);
    }

    /// <summary>
    /// The notification not having fired means the load this client asked for is still
    /// running — which is a sentence, not a protocol detail — and the lever is the timeout.
    /// </summary>
    [Fact]
    public void A_load_that_has_not_finished_says_so_and_names_the_lever()
    {
        var message = Readiness.Message(new Readiness.Failure(
            TimeSpan.FromSeconds(0), Fired: false, [Pending("Core", "Greeter")], [], []));

        Assert.Contains("the workspace is still loading", message);
        Assert.Contains("Raise --timeout", message);
        Assert.DoesNotContain("never fired", message);
    }

    /// <summary>
    /// The headline half: the interpretation is stated where the reader is, not left to be
    /// inferred from a notification name and an empty list.
    /// </summary>
    [Fact]
    public void A_cause_is_the_first_item_and_changes_the_headline()
    {
        var message = Readiness.Message(new Readiness.Failure(
            TimeSpan.FromSeconds(45),
            Fired: true,
            [Pending("Lib", "LibType")],
            [],
            [],
            Cause: "the .NET SDK cannot run in this root"));

        var lines = message.Split('\n');
        Assert.Contains("every probed project answered empty", lines[0]);
        Assert.Equal("  cause: the .NET SDK cannot run in this root", lines[1]);
    }

    /// <summary>
    /// Without a cause the headline may not claim the diagnosis: one project of twenty empty
    /// is an ordinary unresolvable candidate, and <c>exhausted-candidate-fails-after-load</c>
    /// is exactly that shape.
    /// </summary>
    [Fact]
    public void Without_a_cause_the_headline_claims_nothing()
    {
        var message = Readiness.Message(new Readiness.Failure(
            TimeSpan.FromSeconds(45), Fired: true, [Pending("B", "Ghost")], [], []));

        Assert.DoesNotContain("answered empty", message);
        Assert.DoesNotContain("cause:", message);
    }

    [Fact]
    public void The_server_stderr_tail_still_rides_at_the_end()
    {
        var message = Readiness.Message(new Readiness.Failure(
            TimeSpan.FromSeconds(45),
            Fired: true,
            [Pending("B", "Ghost")],
            [],
            [],
            StderrTail: "\n--- server stderr ---\nboom"));

        Assert.EndsWith("\n--- server stderr ---\nboom", message);
    }

    /// <summary>
    /// An explicit <c>--sentinel</c> is not a project — <c>ready --json</c>'s three numbers
    /// exclude it for the same reason — so the failure text may not call it one. The subject
    /// is rendered by the caller and printed verbatim here.
    /// </summary>
    [Fact]
    public void The_explicit_sentinel_is_not_printed_as_a_project()
    {
        var message = Readiness.Message(new Readiness.Failure(
            TimeSpan.FromSeconds(45),
            Fired: true,
            [new Readiness.Unresolved("explicit sentinel 'Greet'", ["Greet"])],
            [],
            []));

        Assert.Contains("returned no symbols for explicit sentinel 'Greet'", message);
        Assert.DoesNotContain("for project explicit", message);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 3, false)]
    [InlineData(3, 3, true)]
    public void Every_project_empty_is_all_of_them_and_at_least_one(int pending, int probed, bool expected)
    {
        Assert.Equal(expected, Readiness.EveryProjectEmpty(pending, probed));
    }

    private static Readiness.Unresolved Pending(string project, params string[] candidates) =>
        new($"project {project}", candidates);
}
