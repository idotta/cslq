namespace Cslq.Tests;

/// <summary>
/// The predicate behind the readiness message's stale-sentinel line. A session infers its
/// sentinels once per attach, and an ordinary <c>.cs</c> edit neither re-attaches it nor
/// changes <c>Program.SolutionSignature</c> — so after an edit that removes a not-yet-proved
/// project's only candidate, every call in that session fails naming a type that is no longer
/// anywhere on disk. A live leg can stage the failure but not the comparison; this is where
/// the comparison is pinned, the way <see cref="ReadyProofTests"/> pins <c>Unproved</c>.
/// </summary>
public class StaleSentinelTests
{
    /// <summary>
    /// The shape the line exists for: the candidate cached at attach names a type the edit
    /// renamed away, and a fresh scan of the same project says something else.
    /// </summary>
    [Fact]
    public void A_candidate_the_source_no_longer_declares_is_stale()
    {
        var held = Project("Lone", "LoneBeacon");
        var fresh = Project("Lone", "Zeta");

        Assert.True(LspClient.Stale(held, [fresh]));
    }

    /// <summary>An unedited project is not, which is every project on an ordinary failure.</summary>
    [Fact]
    public void An_unchanged_candidate_list_is_not_stale()
    {
        var held = Project("Core", "Greeter", "Party");

        Assert.False(LspClient.Stale(held, [Project("Core", "Greeter", "Party"), Project("App", "Square")]));
    }

    /// <summary>
    /// Order is part of the comparison: <c>ResolvesAsync</c> asks the candidates in the order
    /// the scan found them, so a first candidate that moved to another file is a different ask
    /// even though the set is the same.
    /// </summary>
    [Fact]
    public void The_candidate_order_is_part_of_the_comparison()
    {
        Assert.True(LspClient.Stale(Project("App", "Square", "Program"), [Project("App", "Program", "Square")]));
    }

    /// <summary>A project the scan no longer produces at all cannot be anything else.</summary>
    [Fact]
    public void A_project_that_is_gone_is_stale()
    {
        Assert.True(LspClient.Stale(Project("Lone", "LoneBeacon"), [Project("Core", "Greeter")]));
    }

    /// <summary>
    /// An explicit <c>--sentinel</c> never is. It came off the command line rather than off a
    /// scan, so no edit can invalidate it, and telling the caller their own argument went
    /// stale sends them looking for a change that is not there.
    /// </summary>
    [Fact]
    public void An_explicit_sentinel_is_never_stale()
    {
        var probe = new Sentinel(Root, ["Ghost"], [], Explicit: true);

        Assert.False(LspClient.Stale(probe, []));
    }

    /// <summary>
    /// The projects are matched by <c>ProofKey</c>, so the same directory spelled differently
    /// is the same project and not a vanished one.
    /// </summary>
    [Fact]
    public void The_same_directory_spelled_differently_is_the_same_project()
    {
        var again = new Sentinel(
            Path.Combine(Root, "Api", "..", "Web") + Path.DirectorySeparatorChar, ["Program"], []);

        Assert.False(LspClient.Stale(again, [Project("Web", "Program")]));
    }

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cslq-stale-sentinel");

    private static Sentinel Project(string name, params string[] candidates) =>
        new(Path.Combine(Root, name), candidates, []);
}
