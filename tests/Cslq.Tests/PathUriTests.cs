namespace Cslq.Tests;

/// <summary>
/// Every path helper in .NET lies about a source-generated URI — <c>new Uri(u).LocalPath</c>
/// does not throw for one, it returns "/BuildInfo.g.cs", which renders as a confident wrong
/// answer at exit 0. These pin the two schemes that must never reach a path helper unchecked.
/// </summary>
public class PathUriTests
{
    /// <summary>
    /// The authority guid, the documentId and the assemblyPath in a generated URI are all
    /// regenerated per run and per machine, so the display label is built only from hintName
    /// and assemblyName. This URI carries the volatile fields precisely so a regression that
    /// starts using them shows up here.
    /// </summary>
    private const string Generated =
        "roslyn-source-generated://8d1e6a04-06c5-4f6d-9f1d-8b0e2a7c1234/BuildInfo.g.cs" +
        "?documentId=7f2c9b13-4a55-4c8e-9d3f-1e5b6a2d9c77" +
        "&assemblyPath=C%3A%5Cdev%5Cfixture%5CApp%5Cbin%5CDebug%5Cnet10.0%5CFixture.App.dll" +
        "&assemblyName=Fixture.App&assemblyVersion=1.0.0.0&typeName=BuildInfo&hintName=BuildInfo.g.cs";

    [Fact]
    public void A_generated_uri_displays_as_its_assembly_and_hint_name()
    {
        Assert.True(PathUri.IsGenerated(Generated));
        Assert.Equal("<generated>/Fixture.App/BuildInfo.g.cs", PathUri.Display("/anywhere", Generated));
    }

    /// <summary>
    /// <c>IsDecompiled</c> calls <c>ToPath</c>, so it has to rule out the generated scheme
    /// first or it asks a path question of something that has no path.
    /// </summary>
    [Fact]
    public void A_generated_uri_is_not_decompiled()
    {
        Assert.False(PathUri.IsDecompiled(Generated));
    }

    /// <summary>
    /// Roslyn's decompiled stand-in. For a framework or package type that document is the
    /// definition; for one whose source is in the workspace it means a ProjectReference is
    /// still bound to a built assembly. Both wear this fingerprint.
    /// </summary>
    [Fact]
    public void A_metadata_as_source_path_is_decompiled()
    {
        Assert.True(PathUri.IsDecompiled(Decompiled("Greeter.cs")));
    }

    /// <summary>
    /// The temp path is two run-specific guid directories and a file name, and it is
    /// machine-absolute, so it must never reach the caller — that was the confident wrong
    /// answer <c>def</c> at a framework member used to print. The assembly cannot come from
    /// the URI at all; only the type name can.
    /// </summary>
    [Fact]
    public void A_decompiled_uri_displays_as_its_assembly_and_type()
    {
        var uri = Decompiled("Console.cs");

        var display = PathUri.Display("/anywhere", uri, assembly: "System.Console");

        Assert.Equal("<metadata>/System.Console/Console.cs", display);
        Assert.DoesNotContain("MetadataAsSource", display, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unreadable header renders <c>?</c> rather than a guess, the same way an unanswered
    /// project lookup falls back rather than inventing one — and the temp path still does not
    /// leak.
    /// </summary>
    [Fact]
    public void An_unknown_assembly_renders_as_a_question_mark()
    {
        Assert.Equal(
            "<metadata>/?/Console.cs", PathUri.Display("/anywhere", Decompiled("Console.cs")));
    }

    [Fact]
    public void A_decompiled_uri_names_the_type_it_stands_for()
    {
        Assert.Equal("Console", PathUri.MetadataTypeName(Decompiled("Console.cs")));
    }

    /// <summary>
    /// The header Roslyn writes at the top of a decompiled document, byte-order mark and all.
    /// It is the only place the assembly name exists.
    /// </summary>
    [Theory]
    [InlineData(
        "\uFEFF#region Assembly System.Console, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b0",
        "System.Console")]
    [InlineData("#region Assembly Newtonsoft.Json, Version=13.0.0.0", "Newtonsoft.Json")]
    [InlineData("#region Assembly System.Runtime", "System.Runtime")]
    public void The_assembly_comes_off_the_decompilation_header(string line, string want)
    {
        Assert.Equal(want, PathUri.MetadataAssembly(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("using System;")]
    [InlineData("#region Assembly ")]
    public void A_document_with_no_such_header_names_no_assembly(string? line)
    {
        Assert.Null(PathUri.MetadataAssembly(line));
    }

    /// <summary>
    /// The discriminator between the two decompiled cases. A type Roslyn answered for out of
    /// an assembly and also declares in a workspace file is a stale ProjectReference binding
    /// worth re-asking about; one it only has the assembly for is the real answer, and
    /// re-asking cost ten seconds per framework <c>def</c> before this existed.
    /// </summary>
    [Fact]
    public void A_workspace_source_hit_marks_a_decompiled_answer_stale()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");

        Assert.True(PathUri.AnyUnder(root, [PathUri.FromPath(Path.Combine(root, "Core", "Greeter.cs"))]));
    }

    /// <summary>
    /// Neither a decompiled document nor a generated one counts as workspace source, and the
    /// decompiled one is the trap: its temp path is a real file path, so a plain prefix test
    /// against a root under the temp directory would call every framework answer stale.
    /// </summary>
    [Fact]
    public void A_metadata_or_generated_hit_does_not()
    {
        Assert.False(PathUri.AnyUnder(Path.GetTempPath(), [Decompiled("Console.cs"), Generated]));
    }

    /// <summary>
    /// Path identity is the platform's, not Windows'. Everything here compared
    /// <c>OrdinalIgnoreCase</c> until Milestone 5 item 5, which is wrong on Linux — the
    /// platform CI has always run, so nothing was watching. The expectation is asserted on
    /// both platforms rather than skipped on one: a comparer that stopped varying would then
    /// go red somewhere instead of quietly passing everywhere.
    /// </summary>
    [Fact]
    public void Path_comparison_is_case_sensitive_only_on_linux()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(StringComparison.Ordinal, PathUri.PathComparison);
            Assert.Same(StringComparer.Ordinal, PathUri.PathComparer);
        }
        else
        {
            Assert.Equal(StringComparison.OrdinalIgnoreCase, PathUri.PathComparison);
            Assert.Same(StringComparer.OrdinalIgnoreCase, PathUri.PathComparer);
        }
    }

    /// <summary>
    /// <c>temp/repo</c> and <c>temp/REPO</c> are one directory on Windows and two on Linux, so
    /// whether a hit in the second counts as workspace source under the first is the platform's
    /// answer. Getting this wrong on Linux would call a framework <c>def</c> stale on the
    /// strength of a same-name-different-case directory and burn the settle budget re-asking.
    /// </summary>
    [Fact]
    public void A_hit_under_a_case_differing_root_follows_the_platform()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var hit = PathUri.FromPath(Path.Combine(Path.GetTempPath(), "REPO", "Core", "Greeter.cs"));

        Assert.Equal(!OperatingSystem.IsLinux(), PathUri.AnyUnder(root, [hit]));
    }

    [Fact]
    public void A_hit_outside_the_root_does_not()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var outside = PathUri.FromPath(Path.Combine(Path.GetTempPath(), "elsewhere", "Greeter.cs"));

        Assert.False(PathUri.AnyUnder(root, [outside]));
    }

    [Fact]
    public void An_ordinary_workspace_path_is_not_decompiled()
    {
        var uri = PathUri.FromPath(Path.Combine(Path.GetTempPath(), "repo", "Core", "Greeter.cs"));

        Assert.False(PathUri.IsDecompiled(uri));
    }

    [Fact]
    public void A_file_uri_displays_relative_to_the_root_with_forward_slashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var uri = PathUri.FromPath(Path.Combine(root, "Core", "Greeter.cs"));

        Assert.Equal("Core/Greeter.cs", PathUri.Display(root, uri));
    }

    /// <summary>
    /// The generator-only label is the same for every project consuming that generator, so
    /// two distinct documents render identically. The consuming project's <c>.csproj</c> —
    /// which only <c>textDocument/_vs_getProjectContexts</c> can supply — is what separates
    /// them, and its directory is used rather than its file name so two same-named projects
    /// in different directories stay distinct.
    /// </summary>
    [Fact]
    public void A_generated_uri_displays_the_project_that_consumed_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var alpha = Path.Combine(root, "Alpha", "Alpha.csproj");
        var beta = Path.Combine(root, "Beta", "Beta.csproj");

        Assert.Equal("<generated>/Alpha/Fixture.App/BuildInfo.g.cs", PathUri.Display(root, Generated, alpha));
        Assert.Equal("<generated>/Beta/Fixture.App/BuildInfo.g.cs", PathUri.Display(root, Generated, beta));
    }

    /// <summary>
    /// A server that will not name the project falls back to the older ambiguous label rather
    /// than inventing one: coarse is recoverable, wrong is not.
    /// </summary>
    [Fact]
    public async Task An_unanswered_project_lookup_falls_back_to_the_generator_only_label()
    {
        var display = await PathUri.DisplayAsync("/anywhere", Generated, Nothing());

        Assert.Equal("<generated>/Fixture.App/BuildInfo.g.cs", display);
    }

    [Fact]
    public async Task A_decompiled_uri_is_labelled_from_its_own_first_line()
    {
        var display = await PathUri.DisplayAsync(
            "/anywhere",
            Decompiled("Console.cs"),
            Nothing() with { Assembly = _ => Task.FromResult<string?>("System.Console") });

        Assert.Equal("<metadata>/System.Console/Console.cs", display);
    }

    /// <summary>
    /// A file URI carries its own path, so neither lookup — a round trip to the server and a
    /// file read — is made for one. Each kind of URI pays only for the lookup its own label
    /// needs, which is the whole reason they are separate.
    /// </summary>
    [Fact]
    public async Task A_file_uri_is_never_looked_up()
    {
        var asked = new List<string>();
        var documents = new Documents(
            _ => Task.FromResult<string[]>([]),
            _ => { asked.Add("project"); return Task.FromResult<string?>(null); },
            _ => { asked.Add("assembly"); return Task.FromResult<string?>(null); });

        await PathUri.DisplayAsync(
            Path.GetTempPath(),
            PathUri.FromPath(Path.Combine(Path.GetTempPath(), "Greeter.cs")),
            documents);

        Assert.Empty(asked);
    }

    /// <summary>
    /// And a generated URI does not pay for the assembly read, nor a decompiled one for the
    /// project request.
    /// </summary>
    [Fact]
    public async Task Each_label_asks_only_the_lookup_it_needs()
    {
        var asked = new List<string>();
        var documents = new Documents(
            _ => Task.FromResult<string[]>([]),
            _ => { asked.Add("project"); return Task.FromResult<string?>(null); },
            _ => { asked.Add("assembly"); return Task.FromResult<string?>(null); });

        await PathUri.DisplayAsync("/anywhere", Generated, documents);
        Assert.Equal(["project"], asked);

        asked.Clear();
        await PathUri.DisplayAsync("/anywhere", Decompiled("Console.cs"), documents);
        Assert.Equal(["assembly"], asked);
    }

    /// <summary>
    /// A hit outside the root — a linked file, or a symbol resolved from elsewhere on disk —
    /// stays absolute rather than becoming a `../../..` chain that reads as noise.
    /// </summary>
    [Fact]
    public void A_path_outside_the_root_stays_absolute()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "Stray.cs");

        var display = PathUri.Display(root, PathUri.FromPath(outside));

        Assert.Equal(outside.Replace(Path.DirectorySeparatorChar, '/'), display);
        Assert.DoesNotContain("..", display, StringComparison.Ordinal);
    }

    private static string Decompiled(string file) => PathUri.FromPath(Path.Combine(
        Path.GetTempPath(), "MetadataAsSource", "abc123",
        "DecompilationMetadataAsSourceFileProvider", "def456", file));

    private static Documents Nothing() => new(
        _ => Task.FromResult<string[]>([]),
        _ => Task.FromResult<string?>(null),
        _ => Task.FromResult<string?>(null));

    [Fact]
    public void A_round_trip_through_a_file_uri_preserves_the_path()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo", "Core", "Greeter.cs"));

        Assert.Equal(path, PathUri.ToPath(PathUri.FromPath(path)));
    }
}
