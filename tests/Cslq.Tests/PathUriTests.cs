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
    /// Roslyn's decompiled stand-in, which a query fired before the referenced project loaded
    /// answers with instead of the repo. Legitimate for a NuGet symbol, a bug's fingerprint
    /// for one whose source is in the workspace.
    /// </summary>
    [Fact]
    public void A_metadata_as_source_path_is_decompiled()
    {
        var uri = PathUri.FromPath(
            Path.Combine(Path.GetTempPath(), "MetadataAsSource", "abc123", "Greeter.cs"));

        Assert.True(PathUri.IsDecompiled(uri));
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
        var display = await PathUri.DisplayAsync(
            "/anywhere", Generated, _ => Task.FromResult<string?>(null));

        Assert.Equal("<generated>/Fixture.App/BuildInfo.g.cs", display);
    }

    /// <summary>
    /// A file URI carries its own path, so the lookup — a round trip to the server — is never
    /// made for one.
    /// </summary>
    [Fact]
    public async Task A_file_uri_is_never_looked_up()
    {
        var asked = false;

        await PathUri.DisplayAsync(
            Path.GetTempPath(),
            PathUri.FromPath(Path.Combine(Path.GetTempPath(), "Greeter.cs")),
            _ =>
            {
                asked = true;
                return Task.FromResult<string?>(null);
            });

        Assert.False(asked);
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

    [Fact]
    public void A_round_trip_through_a_file_uri_preserves_the_path()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo", "Core", "Greeter.cs"));

        Assert.Equal(path, PathUri.ToPath(PathUri.FromPath(path)));
    }
}
