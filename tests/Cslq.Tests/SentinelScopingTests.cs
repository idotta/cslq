namespace Cslq.Tests;

/// <summary>
/// The other half of <c>Web/</c> + <c>Web/Tests/</c>. <see cref="SentinelInferenceTests"/>
/// pins what <c>Program.InferSentinels</c> puts in <see cref="Sentinel.Nested"/>; this pins
/// what readiness does with it — that a hit under a nested project is discarded rather than
/// counted, which is the difference between "every project loaded" and an incomplete answer
/// at exit 0. Nothing in <c>probes/</c> reaches it: all three fixture projects are siblings,
/// so every <c>Nested</c> list there is empty.
/// </summary>
public class SentinelScopingTests
{
    /// <summary>
    /// Tests declaring its own <c>Program</c> is the ordinary shape, not a contrived one, and
    /// <c>workspace/symbol</c> answers both. Accepting the Tests hit marks Web ready while Web
    /// is still loading, and every command then answers with Web's hits simply missing.
    /// </summary>
    [Fact]
    public void A_hit_under_a_nested_project_does_not_mark_the_parent_ready()
    {
        using var ws = Nested();
        var web = Sentinels(ws).Single(s => Name(s) == "Web");

        Assert.True(web.Accepts(Uri(ws, "Web/Program.cs")));
        Assert.False(web.Accepts(Uri(ws, "Web/Tests/Program.cs")));
    }

    /// <summary>
    /// The nested project scopes to itself the same way: its parent's files are above it, not
    /// under it, so <c>Under</c> alone settles this one and <c>Nested</c> is empty.
    /// </summary>
    [Fact]
    public void The_nested_project_is_marked_ready_only_by_its_own_hit()
    {
        using var ws = Nested();
        var tests = Sentinels(ws).Single(s => Name(s) == "Tests");

        Assert.True(tests.Accepts(Uri(ws, "Web/Tests/Program.cs")));
        Assert.False(tests.Accepts(Uri(ws, "Web/Program.cs")));
        Assert.Empty(tests.Nested);
    }

    [Fact]
    public void A_hit_in_a_sibling_project_marks_neither_ready()
    {
        using var ws = Nested();
        ws.Project("Api");
        ws.Write("Api/Program.cs", "namespace Api; internal class Program;");
        var sentinels = Sentinels(ws);

        Assert.False(sentinels.Single(s => Name(s) == "Web").Accepts(Uri(ws, "Api/Program.cs")));
        Assert.False(sentinels.Single(s => Name(s) == "Tests").Accepts(Uri(ws, "Api/Program.cs")));
    }

    /// <summary>
    /// A generated document has no on-disk path to scope, and <c>PathUri.ToPath</c> answers a
    /// path-shaped lie for one rather than throwing — <c>/Program.g.cs</c>, which resolves
    /// against the current directory and could land anywhere. It can never prove a project
    /// loaded, so it is rejected before any path arithmetic happens.
    /// </summary>
    [Fact]
    public void A_generated_hit_never_marks_a_project_ready()
    {
        using var ws = Nested();
        var web = Sentinels(ws).Single(s => Name(s) == "Web");

        Assert.False(web.Accepts(
            "roslyn-source-generated://b1f6b80c-83c3-4bbd-b2e0-12be26636667/Program.g.cs" +
            "?hintName=Program.g.cs&assemblyName=Gen"));
    }

    /// <summary>
    /// <c>Sentinel.Accepts</c> is the other path-identity site, and readiness is what it
    /// decides: on Linux a hit under <c>WEB/</c> is a different directory from <c>Web/</c> and
    /// proves nothing, while on Windows and macOS it is the same one. Asserted both ways
    /// rather than skipped, so a comparer that stopped varying goes red rather than quiet.
    /// </summary>
    [Fact]
    public void A_case_differing_hit_follows_the_platform()
    {
        using var ws = Nested();
        var web = Sentinels(ws).Single(s => Name(s) == "Web");

        Assert.Equal(PathUri.PathsAreCaseInsensitive, web.Accepts(Uri(ws, "WEB/Program.cs")));
    }

    private static Workspace Nested()
    {
        var ws = new Workspace();
        ws.Project("Web");
        ws.Write("Web/Program.cs", "namespace Web; internal class Program;");
        ws.Project("Web/Tests");
        ws.Write("Web/Tests/Program.cs", "namespace Web.Tests; internal class Program;");
        return ws;
    }

    private static IReadOnlyList<Sentinel> Sentinels(Workspace ws) => Program.InferSentinels(ws.Root);

    private static string Name(Sentinel sentinel) => Path.GetFileName(sentinel.Directory);

    private static string Uri(Workspace ws, string relativePath) =>
        PathUri.FromPath(Path.Combine(ws.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
