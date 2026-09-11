namespace Cslq.Tests;

/// <summary>
/// Which projects readiness waits for. Over-inclusion here is fatal rather than wasteful: a
/// project Roslyn never loaded has a sentinel that can never resolve, so `cslq ready` burns
/// its whole timeout and exits 1 on a workspace that was fine. Reading the root's solution is
/// what stops the scan from inventing those projects.
/// </summary>
public class ProjectDiscoveryTests
{
    /// <summary>
    /// The OrchardCore shape: template content sitting under the root as real <c>.csproj</c>
    /// files that the solution excludes. Before the solution was read, `cslq ready` on
    /// OrchardCore v3.0.1 failed after 900 s on exactly this.
    /// </summary>
    [Fact]
    public void A_solution_narrows_discovery_to_the_projects_it_lists()
    {
        using var ws = new Workspace(solution: false);
        var included = ws.Project("Included");
        ws.Project("Templates/Content/Excluded");
        ws.Write("Included/Real.cs", "internal class Real;");
        ws.Write("Templates/Content/Excluded/Template.cs", "internal class Template;");
        ws.Write("Only.slnx", """
            <Solution>
              <Project Path="Included/Included.csproj" />
            </Solution>
            """);

        var sentinels = Program.InferSentinels(ws.Root);

        Assert.Equal(included, Assert.Single(sentinels).Directory);
    }

    [Fact]
    public void A_solution_folder_does_not_hide_the_projects_inside_it()
    {
        using var ws = new Workspace(solution: false);
        var app = ws.Project("src/App");
        ws.Project("samples/Sample");
        ws.Write("src/App/Real.cs", "internal class Real;");
        ws.Write("samples/Sample/Sample.cs", "internal class Sample;");
        ws.Write("Only.slnx", """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/App/App.csproj" />
              </Folder>
            </Solution>
            """);

        Assert.Equal(app, Assert.Single(Program.InferSentinels(ws.Root)).Directory);
    }

    /// <summary>
    /// The older line format. Its paths carry a backslash, which off Windows is a filename
    /// character rather than a separator, and its project entries also cover solution folders
    /// — the second entry here — which have no project file to load.
    /// </summary>
    [Fact]
    public void The_older_sln_format_is_read_the_same_way_including_its_backslashes()
    {
        using var ws = new Workspace(solution: false);
        var included = ws.Project("Included");
        ws.Project("Excluded");
        ws.Write("Included/Real.cs", "internal class Real;");
        ws.Write("Excluded/Other.cs", "internal class Other;");
        ws.Write("Only.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Included", "Included\Included.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
            EndGlobal
            """);

        Assert.Equal(included, Assert.Single(Program.InferSentinels(ws.Root)).Directory);
    }

    /// <summary>
    /// Waiting on a project that is not on disk is the same unresolvable sentinel the solution
    /// read exists to prevent, just arrived at by another route.
    /// </summary>
    [Fact]
    public void A_listed_project_that_is_not_on_disk_is_dropped()
    {
        using var ws = new Workspace(solution: false);
        var real = ws.Project("Real");
        ws.Project("Excluded");
        ws.Write("Real/Thing.cs", "internal class Thing;");
        ws.Write("Excluded/Other.cs", "internal class Other;");
        ws.Write("Only.slnx", """
            <Solution>
              <Project Path="Real/Real.csproj" />
              <Project Path="Ghost/Ghost.csproj" />
            </Solution>
            """);

        Assert.Equal(real, Assert.Single(Program.InferSentinels(ws.Root)).Directory);
    }

    [Fact]
    public void A_project_that_is_not_csharp_is_dropped()
    {
        using var ws = new Workspace(solution: false);
        var app = ws.Project("App");
        ws.Project("Excluded");
        ws.Write("App/Real.cs", "internal class Real;");
        ws.Write("Excluded/Other.cs", "internal class Other;");
        ws.Write("Legacy/Legacy.vbproj", "<Project />");
        ws.Write("Only.slnx", """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Legacy/Legacy.vbproj" />
            </Solution>
            """);

        Assert.Equal(app, Assert.Single(Program.InferSentinels(ws.Root)).Directory);
    }

    /// <summary>
    /// Two solutions give no basis for choosing between them, and the <c>.csproj</c> scan that
    /// used to answer instead is over-inclusive: a project neither solution loads gets a
    /// sentinel that can never resolve, so readiness burned its whole timeout — three runs out
    /// of three on a real repository. Both files are named, since the fix is to point
    /// <c>--root</c> at one of them.
    /// </summary>
    [Fact]
    public void Two_solutions_at_the_root_fail_before_the_server_starts()
    {
        using var ws = new Workspace(solution: false);
        ws.Project("One");
        ws.Project("Two");
        ws.Write("One/A.cs", "internal class A;");
        ws.Write("Two/B.cs", "internal class B;");
        ws.Write("First.slnx", """
            <Solution>
              <Project Path="One/One.csproj" />
            </Solution>
            """);
        ws.Write("Second.slnx", """
            <Solution>
              <Project Path="Two/Two.csproj" />
            </Solution>
            """);

        var ex = Assert.Throws<CslqException>(() => Program.InferSentinels(ws.Root));

        Assert.Contains("two solutions at", ex.Message, StringComparison.Ordinal);
        Assert.Contains("First.slnx", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Second.slnx", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A solution in a subdirectory describes that subtree, not this root — so a root holding
    /// only that is a root with no solution, and says so rather than scanning the projects up
    /// to it.
    /// </summary>
    [Fact]
    public void A_solution_below_the_root_is_not_the_roots_solution()
    {
        using var ws = new Workspace(solution: false);
        ws.Project("Sub/One");
        ws.Project("Loose");
        ws.Write("Sub/One/A.cs", "internal class A;");
        ws.Write("Loose/B.cs", "internal class B;");
        ws.Write("Sub/Sub.slnx", """
            <Solution>
              <Project Path="One/One.csproj" />
            </Solution>
            """);

        var ex = Assert.Throws<CslqException>(() => Program.InferSentinels(ws.Root));

        Assert.Equal(Program.NoSolution(ws.Root), ex.Message);
    }

    /// <summary>
    /// The failure this replaced: two bare <c>.csproj</c> under the root loaded the scan's
    /// projects, then burned the whole timeout, because <c>--autoLoadProjects</c> does not
    /// discover a bare project. It is knowable before the server starts.
    /// </summary>
    [Fact]
    public void A_root_with_no_solution_fails_before_the_server_starts()
    {
        using var ws = new Workspace(solution: false);
        ws.Project("One");
        ws.Project("Two");
        ws.Write("One/A.cs", "internal class A;");
        ws.Write("Two/B.cs", "internal class B;");

        var ex = Assert.Throws<CslqException>(() => Program.InferSentinels(ws.Root));

        Assert.Equal(Program.NoSolution(ws.Root), ex.Message);
    }

    /// <summary>
    /// The message has to name the fix, not just the symptom: the root is in the wrong place,
    /// and the reader has to be told where it belongs. It must not offer <c>--sentinel</c>,
    /// which does bypass the check but leaves Roslyn loading nothing — the reader would follow
    /// the hint into the full timeout this error exists to remove.
    /// </summary>
    [Fact]
    public void The_no_solution_message_names_the_root_and_the_fix()
    {
        var message = Program.NoSolution("/some/where");

        Assert.Contains("no .sln or .slnx at /some/where", message, StringComparison.Ordinal);
        Assert.Contains("--root must be the directory holding the .sln/.slnx", message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--sentinel", message, StringComparison.Ordinal);
        Assert.Single(message.Split('\n'));
    }

    /// <summary>
    /// A hand-edited solution that no longer parses has to arrive as a CLI error. `Main` catches
    /// `CslqException` and nothing else, so an escaping `XmlException` answered a bad solution
    /// file with an unhandled stack trace and exit 127.
    /// </summary>
    [Fact]
    public void A_solution_that_is_not_valid_xml_fails_as_a_cli_error()
    {
        using var ws = new Workspace(solution: false);
        ws.Project("App");
        ws.Write("App/Real.cs", "internal class Real;");
        ws.Write("Broken.slnx", """
            <Solution>
              <Project Path="App/App.csproj"
            </Solution>
            """);

        var ex = Assert.Throws<CslqException>(() => Program.InferSentinels(ws.Root));

        Assert.Contains("Broken.slnx is not valid XML", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An extended-length root (<c>\\?\C:\...</c>) is a real path that <c>Path.GetFullPath</c>
    /// preserves and every filesystem API accepts, and nothing above them does:
    /// <c>XDocument.Load(string)</c> resolves its argument as a URI and threw
    /// <c>UriFormatException</c> — not <c>XmlException</c>, so it escaped as a stack trace and
    /// exit 127 — and <c>PathUri.FromPath</c> would have thrown the same on the next hit. The
    /// prefix is therefore stripped once, in <c>Options.Parse</c>, and discovery sees an
    /// ordinary root. Windows-only: the prefix is a Win32 spelling.
    /// </summary>
    [Fact]
    public void An_extended_length_root_is_read_like_any_other()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("\\\\?\\ is a Win32 path prefix.");

        using var ws = new Workspace();
        var app = ws.Project("App");
        ws.Write("App/Real.cs", "internal class Real;");

        var opts = Program.Options.Parse(["ready", "--root", @"\\?\" + ws.Root]);

        Assert.Equal(ws.Root, opts.Root);
        Assert.Equal(app, Assert.Single(Program.InferSentinels(opts.Root)).Directory);
    }

    /// <summary>
    /// "no .csproj under &lt;root&gt;" would be a lie about a root whose solution simply lists
    /// no C# project, and would send the reader looking for files that are sitting right there.
    /// </summary>
    [Fact]
    public void A_solution_listing_no_csharp_project_is_named_in_the_error()
    {
        using var ws = new Workspace(solution: false);
        ws.Project("App");
        ws.Write("App/Real.cs", "internal class Real;");
        ws.Write("Only.slnx", """
            <Solution>
              <Project Path="Legacy/Legacy.vbproj" />
            </Solution>
            """);

        var ex = Assert.Throws<CslqException>(() => Program.InferSentinels(ws.Root));

        Assert.Contains("Only.slnx lists no C# project", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The server is a C# one, so a VB or F# project in the solution is not loaded and not
    /// searched — and a reference to a C# symbol from its source is then missing from
    /// <c>refs</c> at exit 0, which is the part no caller can see. The solution lists them, so
    /// naming them costs a read discovery is making anyway.
    /// </summary>
    [Fact]
    public void The_non_csharp_projects_a_solution_lists_are_named()
    {
        using var ws = new Workspace(solution: false);
        ws.Project("App");
        ws.Write("App/Real.cs", "internal class Real;");
        ws.Write("Legacy/Legacy.vbproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        ws.Write("Calc/Calc.fsproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        ws.Write("Mixed.slnx", """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Legacy/Legacy.vbproj" />
              <Project Path="Calc/Calc.fsproj" />
            </Solution>
            """);

        Assert.Equal(["Calc.fsproj", "Legacy.vbproj"], Program.NonCsharpProjects(ws.Root));

        // And the C# project is still the only one waited on.
        Assert.Equal("App", Path.GetFileName(Assert.Single(Program.InferSentinels(ws.Root)).Directory));
    }

    /// <summary>
    /// A project file the solution lists but the disk does not hold is dropped for C# already,
    /// so reporting it as an unsearched VB project would name a file the reader cannot open.
    /// A <c>.sln</c> solution folder has no project file at all and falls out the same way.
    /// </summary>
    [Fact]
    public void A_listed_project_absent_from_disk_is_not_named()
    {
        using var ws = new Workspace(solution: false);
        ws.Project("App");
        ws.Write("App/Real.cs", "internal class Real;");
        ws.Write("Gone.slnx", """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Legacy/Legacy.vbproj" />
            </Solution>
            """);

        Assert.Empty(Program.NonCsharpProjects(ws.Root));
    }

    /// <summary>
    /// The advisory may not pre-empt the error the caller is about to be given properly: a root
    /// with no solution, or with two, is a failure that names itself, and this is silent there.
    /// </summary>
    [Fact]
    public void A_root_that_is_not_one_solution_is_not_advised_about()
    {
        using var ws = new Workspace(solution: false);
        ws.Write("Legacy/Legacy.vbproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var listing = """
            <Solution>
              <Project Path="Legacy/Legacy.vbproj" />
            </Solution>
            """;

        Assert.Empty(Program.NonCsharpProjects(ws.Root));

        ws.Write("One.slnx", listing);
        Assert.Equal(["Legacy.vbproj"], Program.NonCsharpProjects(ws.Root));

        ws.Write("Two.slnx", listing);
        Assert.Empty(Program.NonCsharpProjects(ws.Root));
    }

    [Fact]
    public void The_notice_counts_every_project_and_names_the_first_five()
    {
        string[] many = ["a.vbproj", "b.vbproj", "c.vbproj", "d.fsproj", "e.fsproj", "f.fsproj"];

        Assert.Equal(
            "6 non-C# project(s) not searched: a.vbproj, b.vbproj, c.vbproj, d.fsproj, e.fsproj, and 1 more",
            Program.NonCsharpNotice(many));
        Assert.Equal(
            "1 non-C# project(s) not searched: a.vbproj",
            Program.NonCsharpNotice(["a.vbproj"]));
    }

    /// <summary>
    /// <c>dotnet new console</c>'s default shape. Inference cannot read a type out of it and
    /// refuses in 90 ms, which is right — but the obvious repair is a trap: the <c>Program</c>
    /// class such a file compiles to is compiler-generated and <c>workspace/symbol</c> does not
    /// index it, so <c>--sentinel Program</c> reads as correct and waits out the whole timeout,
    /// where a method or local function in the same file resolves in about two seconds. The
    /// message is what has to say so.
    /// </summary>
    [Fact]
    public void A_workspace_of_only_top_level_statements_says_what_does_resolve()
    {
        using var ws = new Workspace();
        ws.Project("Tls");
        ws.Write("Tls/Program.cs", """
            Console.WriteLine(Greet("world"));

            static string Greet(string name) => $"Hello, {name}";
            """);

        var ex = Assert.Throws<CslqException>(() => Program.InferSentinels(ws.Root));

        Assert.Contains("no project declares a type", ex.Message, StringComparison.Ordinal);
        Assert.Contains("a type, a method, or a local function", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--sentinel Program waits out the whole timeout", ex.Message, StringComparison.Ordinal);
    }
}
