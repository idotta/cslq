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
    /// Build output is neither a source of candidates nor a nesting boundary. The
    /// <c>obj/Nested/Nested.csproj</c> is the second half: it is on disk, and every
    /// <c>.csproj</c> on disk is a boundary, so without the filter it would take
    /// <c>Only/obj</c>'s files away from <c>Only</c>.
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
        ws.Write("Only.slnx", """
            <Solution>
              <Project Path="Only/Only.csproj" />
            </Solution>
            """);

        var sentinels = Program.InferSentinels(ws.Root);

        var only = Assert.Single(sentinels);
        Assert.Equal(["Real"], only.Candidates);
        Assert.Empty(only.Nested);
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

    /// <summary>
    /// The OrchardCore failure the solution read did not fix. Its
    /// <c>src/Templates/OrchardCore.ProjectTemplates</c> <em>is</em> listed, and the five
    /// <c>content/*/*.csproj</c> template projects under it are not — so the wrapper's scan
    /// read types out of <c>dotnet new</c> template text Roslyn never binds, and
    /// <c>cslq ready</c> burned 900s on candidates that cannot resolve. A nesting boundary is
    /// therefore any <c>.csproj</c> on disk, listed or not.
    /// </summary>
    [Fact]
    public void A_csproj_the_solution_does_not_list_is_still_a_nesting_boundary()
    {
        using var ws = new Workspace();
        var wrapper = ws.Project("Templates");
        ws.Write("Templates/Wrapper.cs", "internal class Wrapper;");
        ws.Write("Templates/content/T/T.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        ws.Write("Templates/content/T/Program.cs", "internal class TemplateGhost;");

        var only = Assert.Single(Program.InferSentinels(ws.Root));

        Assert.Equal(["Wrapper"], only.Candidates);
        Assert.Equal([Path.Combine(wrapper, "content", "T")], only.Nested);
    }

    /// <summary>
    /// The other half of the same failure, and the one that holds even for template content
    /// carrying no <c>.csproj</c> of its own: <c>EnableDefaultItems=false</c> with no
    /// <c>&lt;Compile Include&gt;</c> is MSBuild for "this project compiles nothing", which no
    /// source scan may override.
    /// </summary>
    [Fact]
    public void A_project_that_compiles_nothing_of_its_own_gets_no_candidates()
    {
        using var ws = new Workspace();
        ws.Project("Lib");
        ws.Write("Lib/Thing.cs", "internal class Thing;");
        var templates = ws.Project("Templates");
        ws.Write("Templates/Templates.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultItems>False</EnableDefaultItems>
              </PropertyGroup>
            </Project>
            """);
        ws.Write("Templates/content/Program.cs", "internal class TemplateGhost;");

        var sentinel = Program.InferSentinels(ws.Root).Single(s => s.Directory == templates);

        Assert.Empty(sentinel.Candidates);
        Assert.True(sentinel.Skipped);
    }

    /// <summary>
    /// Turning the default glob off and then naming a file of its own is an ordinary project
    /// with an explicit item list, not one that compiles nothing.
    /// </summary>
    [Fact]
    public void An_explicit_compile_include_of_its_own_file_keeps_a_project_probed()
    {
        using var ws = new Workspace();
        ws.Project("Only");
        ws.Write("Only/Only.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultItems>false</EnableDefaultItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="src\Real.cs" />
              </ItemGroup>
            </Project>
            """);
        ws.Write("Only/src/Real.cs", "internal class Real;");

        var only = Assert.Single(Program.InferSentinels(ws.Root));

        Assert.Equal(["Real"], only.Candidates);
        Assert.False(only.Skipped);
    }

    /// <summary>
    /// The CommunityToolkit shape: fourteen of its twenty-six projects import a shared
    /// <c>.projitems</c> and own no <c>.cs</c> under their own directory. A hit for a linked
    /// document sits under the <em>source</em> directory, so <c>Sentinel.Accepts</c> can never
    /// accept it and no candidate could prove the project loaded. It is reported skipped
    /// rather than counted as probed.
    /// </summary>
    [Fact]
    public void A_project_whose_sources_are_linked_in_is_skipped()
    {
        using var ws = new Workspace();
        ws.Project("Shared");
        ws.Write("Shared/Thing.cs", "internal class Thing;");
        var linked = ws.Project("Linked");
        ws.Write("Linked/Linked.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\Shared\Shared.projitems" Label="Shared" />
            </Project>
            """);

        var sentinel = Program.InferSentinels(ws.Root).Single(s => s.Directory == linked);

        Assert.Empty(sentinel.Candidates);
        Assert.True(sentinel.Skipped);
    }

    /// <summary>
    /// The same import beside a file of its own is not skipped: the project owns a document
    /// whose location can be scoped, which is all the probe needs.
    /// </summary>
    [Fact]
    public void A_linked_project_with_a_file_of_its_own_is_still_probed()
    {
        using var ws = new Workspace();
        var mixed = ws.Project("Mixed");
        ws.Write("Mixed/Mixed.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\Shared\Shared.projitems" Label="Shared" />
            </Project>
            """);
        ws.Write("Mixed/Own.cs", "internal class Own;");

        var only = Assert.Single(Program.InferSentinels(ws.Root));

        Assert.Equal(mixed, only.Directory);
        Assert.Equal(["Own"], only.Candidates);
        Assert.False(only.Skipped);
    }

    /// <summary>
    /// An explicit <c>--sentinel</c> adds a root-scoped probe to the inferred set rather than
    /// replacing it. Replacing it put the incomplete-answer bug back through the escape hatch:
    /// on OrchardCore a <c>--sentinel</c> run answered <c>impl StartupBase</c> with 321 hits
    /// against 331, at exit 0, because only one project had to have loaded.
    /// </summary>
    [Fact]
    public void An_explicit_sentinel_is_added_to_the_inferred_set()
    {
        using var ws = new Workspace();
        var one = ws.Project("One");
        var two = ws.Project("Two");
        ws.Write("One/A.cs", "internal class A;");
        ws.Write("Two/B.cs", "internal class B;");

        var sentinels = Program.Sentinels(
            Program.Options.Parse(["ready", "--root", ws.Root, "--sentinel", "Chosen"]));

        Assert.Equal([one, two, Path.GetFullPath(ws.Root)], sentinels.Select(s => s.Directory));
        var probe = sentinels[^1];
        Assert.Equal(["Chosen"], probe.Candidates);
        Assert.Empty(probe.Nested);
        Assert.True(probe.Explicit);
        Assert.DoesNotContain(sentinels.Take(sentinels.Count - 1), s => s.Explicit);
    }

    /// <summary>
    /// It still stands alone where inference finds nothing, which is the layout it is the
    /// escape hatch for — a root with no solution is the one every probe row uses.
    /// </summary>
    [Fact]
    public void An_explicit_sentinel_stands_alone_where_inference_finds_nothing()
    {
        using var ws = new Workspace(solution: false);
        ws.Write("Loose.cs", "internal class Loose;");

        var sentinels = Program.Sentinels(
            Program.Options.Parse(["ready", "--root", ws.Root, "--sentinel", "Chosen"]));

        var only = Assert.Single(sentinels);
        Assert.Equal(Path.GetFullPath(ws.Root), only.Directory);
        Assert.Equal(["Chosen"], only.Candidates);
        Assert.True(only.Explicit);
    }
}
