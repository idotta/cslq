namespace Cslq.Tests;

/// <summary>
/// <see cref="Program.InferSentinels"/> decides what <c>cslq</c> waits for before it answers,
/// so a bad candidate does not fail loudly — it burns the whole timeout, or worse, marks a
/// project ready that has not loaded and lets an incomplete answer out at exit 0. The cases
/// here are the ones that cost measured time: prose read as a declaration, and a nested
/// project answering for its parent.
/// </summary>
public class SentinelInferenceTests
{
    /// <summary>
    /// The OrchardCore failure: <c>TypeDeclaration</c> matches `class` followed by a word, so
    /// "identifying the class and assembly context" in a doc comment yields the candidate
    /// `and`, which no <c>workspace/symbol</c> query can resolve. One sentence can fill all
    /// three slots, so capping the list is no defence — the comment has to be stripped first.
    /// This is the shape CLAUDE.md records as unreproducible in <c>fixture/</c>: it needs
    /// prose above the only declaration in a single-file project.
    /// </summary>
    [Fact]
    public void Prose_in_a_doc_comment_is_not_a_candidate()
    {
        using var ws = new Workspace();
        ws.Project("Only");
        ws.Write("Only/BuildInfo.cs", """
            namespace Only;

            /// <summary>
            /// A record identifying the class and assembly context of the build, for
            /// the interface and enum of the report.
            /// </summary>
            internal static class BuildInfo
            {
            }
            """);

        var candidates = Program.InferSentinels(ws.Root).Single().Candidates;

        Assert.Equal(["BuildInfo"], candidates);
    }

    [Fact]
    public void A_declaration_inside_a_string_literal_is_not_a_candidate()
    {
        using var ws = new Workspace();
        ws.Project("Only");
        ws.Write("Only/Real.cs", """"
            namespace Only;

            internal static class Real
            {
                private const string Raw = """
                    class RawGhost
                    """;

                private const string Verbatim = @"class VerbatimGhost";
                private const string Ordinary = "class OrdinaryGhost";
            }
            """");

        var candidates = Program.InferSentinels(ws.Root).Single().Candidates;

        Assert.Equal(["Real"], candidates);
    }

    /// <summary>
    /// Candidates are every match in a file, not the first. Taking only the first is what let
    /// one bad line mask the real type below it, and it is a one-character difference
    /// (<c>.Match</c> against <c>.Matches</c>) that nothing else here would catch.
    /// </summary>
    [Fact]
    public void Every_declaration_in_a_file_contributes_a_candidate()
    {
        using var ws = new Workspace();
        ws.Project("Only");
        ws.Write("Only/Pair.cs", """
            namespace Only;

            internal interface IFirst;

            internal sealed record Second : IFirst;
            """);

        var candidates = Program.InferSentinels(ws.Root).Single().Candidates;

        Assert.Equal(["IFirst", "Second"], candidates);
    }

    [Fact]
    public void The_candidate_list_is_capped_at_three()
    {
        using var ws = new Workspace();
        ws.Project("Only");
        ws.Write("Only/Many.cs", """
            namespace Only;

            internal class A;
            internal class B;
            internal class C;
            internal class D;
            internal class E;
            """);

        var candidates = Program.InferSentinels(ws.Root).Single().Candidates;

        Assert.Equal(["A", "B", "C"], candidates);
    }

    /// <summary>
    /// <c>Web/</c> and <c>Web/Tests/</c> both declaring <c>Program</c> is the ordinary shape,
    /// and it is the one that brought back incomplete-answers-at-exit-0: Tests loading marked
    /// Web ready. Only the inference half is asserted here — Web takes no candidate from Tests,
    /// and Web carries Tests in its <c>Nested</c> list so a hit can be scoped away. That
    /// <see cref="LspClient"/> actually discards such a hit when deciding Web is ready is the
    /// other half, and it is still exercised by nothing; ROADMAP.md records why.
    /// </summary>
    [Fact]
    public void A_nested_project_neither_lends_its_types_nor_goes_unscoped()
    {
        using var ws = new Workspace();
        var web = ws.Project("Web");
        var tests = ws.Project("Web/Tests");
        ws.Write("Web/Startup.cs", "internal class Startup;");
        ws.Write("Web/Tests/StartupTests.cs", "internal class StartupTests;");

        var sentinels = Program.InferSentinels(ws.Root);

        var outer = sentinels.Single(s => s.Directory == web);
        Assert.Equal(["Startup"], outer.Candidates);
        Assert.Equal([tests], outer.Nested);

        var inner = sentinels.Single(s => s.Directory == tests);
        Assert.Equal(["StartupTests"], inner.Candidates);
        Assert.Empty(inner.Nested);
    }

    /// <summary>
    /// A project of only top-level statements declares nothing to probe. It is still returned,
    /// so readiness can name it as unprobed rather than leave it silently absent — which would
    /// read as "loaded".
    /// </summary>
    [Fact]
    public void A_project_declaring_no_type_is_returned_without_candidates()
    {
        using var ws = new Workspace();
        ws.Project("Lib");
        var app = ws.Project("App");
        ws.Write("Lib/Thing.cs", "internal class Thing;");
        ws.Write("App/Program.cs", "System.Console.WriteLine(\"hi\");");

        var sentinels = Program.InferSentinels(ws.Root);

        Assert.Empty(sentinels.Single(s => s.Directory == app).Candidates);
    }

    /// <summary>
    /// Two solutions on purpose: that is the only route to the <c>.csproj</c> scan now, and the
    /// scan is the only thing the <c>bin</c>/<c>obj</c> filter protects. Read off a solution,
    /// <c>Only/obj/Nested/Nested.csproj</c> would be excluded by not being listed, which pins
    /// nothing.
    /// </summary>
    [Fact]
    public void Build_output_is_not_scanned()
    {
        using var ws = new Workspace(solution: false);
        ws.Project("Only");
        ws.Write("Only/Real.cs", "internal class Real;");
        ws.Write("Only/obj/Debug/Generated.cs", "internal class ObjGhost;");
        ws.Write("Only/bin/Debug/Copied.cs", "internal class BinGhost;");
        ws.Write("Only/obj/Nested/Nested.csproj", "<Project />");
        ws.Write("First.slnx", "<Solution />");
        ws.Write("Second.slnx", "<Solution />");

        var sentinels = Program.InferSentinels(ws.Root);

        Assert.Equal(["Real"], Assert.Single(sentinels).Candidates);
    }

    /// <summary>
    /// Roslyn loads nothing for a root with no project in it, so every sentinel would be
    /// unresolvable and every query would answer empty. Waiting out the full timeout only
    /// delays the same conclusion, so this fails before the server starts.
    /// </summary>
    [Fact]
    public void A_root_with_no_project_fails_immediately()
    {
        // Two solutions again: with one, the message names it instead — see
        // ProjectDiscoveryTests — and with none, the root is rejected before the scan runs.
        using var ws = new Workspace(solution: false);
        ws.Write("Loose.cs", "internal class Loose;");
        ws.Write("First.slnx", "<Solution />");
        ws.Write("Second.slnx", "<Solution />");

        var ex = Assert.Throws<CslqException>(() => Program.InferSentinels(ws.Root));

        Assert.Contains("no .csproj", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_root_whose_projects_declare_nothing_fails_immediately()
    {
        using var ws = new Workspace();
        ws.Project("App");
        ws.Write("App/Program.cs", "System.Console.WriteLine(\"hi\");");

        var ex = Assert.Throws<CslqException>(() => Program.InferSentinels(ws.Root));

        Assert.Contains("--sentinel", ex.Message, StringComparison.Ordinal);
    }
}
