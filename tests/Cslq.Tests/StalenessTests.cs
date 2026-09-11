namespace Cslq.Tests;

/// <summary>
/// The predicate that decides whether an open document has to be re-sent. A one-shot
/// <c>cslq</c> could not get this wrong — it opened every document from scratch — so
/// everything here is about the session, which holds <c>didOpen</c> across an edit.
/// </summary>
public class StalenessTests
{
    private const string Generated =
        "roslyn-source-generated://8d1e6a04-06c5-4f6d-9f1d-8b0e2a7c1234/BuildInfo.g.cs" +
        "?assemblyName=Fixture.App&typeName=Fixture.Gen.BuildInfoGenerator&hintName=BuildInfo.g.cs";

    [Fact]
    public void An_untouched_file_is_unchanged()
    {
        using var workspace = new Workspace();
        var uri = Written(workspace, "Core/Greeter.cs", "class Greeter { }");

        Assert.Equal(DocumentState.Unchanged, Staleness.Check(uri, Staleness.Of(uri)));
    }

    /// <summary>
    /// The case length alone cannot see. The timestamp is advanced explicitly rather than
    /// trusted to the write: filesystem timestamp granularity is host-dependent, and the
    /// predicate's contract is over the stamp, not over how fast two writes can be.
    /// </summary>
    [Fact]
    public void A_file_edited_in_place_to_the_same_length_is_changed()
    {
        using var workspace = new Workspace();
        var uri = Written(workspace, "Core/Greeter.cs", "class Greeter { }");
        var opened = Staleness.Of(uri);

        var path = PathUri.ToPath(uri);
        File.WriteAllText(path, "class Greetes { }");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));

        Assert.Equal(17, new FileInfo(path).Length);
        Assert.Equal(DocumentState.Changed, Staleness.Check(uri, opened));
    }

    [Fact]
    public void A_file_that_grew_is_changed()
    {
        using var workspace = new Workspace();
        var uri = Written(workspace, "Core/Greeter.cs", "class Greeter { }");
        var opened = Staleness.Of(uri);

        File.AppendAllText(PathUri.ToPath(uri), "\n// and a comment\n");

        Assert.Equal(DocumentState.Changed, Staleness.Check(uri, opened));
    }

    [Fact]
    public void A_deleted_file_is_deleted()
    {
        using var workspace = new Workspace();
        var uri = Written(workspace, "Core/Greeter.cs", "class Greeter { }");
        var opened = Staleness.Of(uri);

        File.Delete(PathUri.ToPath(uri));

        Assert.Equal(DocumentState.Deleted, Staleness.Check(uri, opened));
    }

    /// <summary>
    /// Both of these are URIs no edit can reach: the generated one has no file at all and
    /// <c>PathUri.ToPath</c> answers a confident <c>/BuildInfo.g.cs</c> for it, and the
    /// decompiled one is a temp file Roslyn wrote and will not rewrite. A stat is what the
    /// assertion is really about — neither path exists here, so a predicate that stat'ed
    /// either would answer <see cref="DocumentState.Deleted"/>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_uri_with_no_file_behind_it_is_never_stated(bool generated)
    {
        var uri = generated
            ? Generated
            : PathUri.FromPath(Path.Combine(
                Path.GetTempPath(), "MetadataAsSource", "abc123",
                "DecompilationMetadataAsSourceFileProvider", "def456", "Console.cs"));

        Assert.False(Staleness.HasFile(uri));
        Assert.Null(Staleness.Of(uri));
        Assert.Equal(DocumentState.Unchanged, Staleness.Check(uri, null));
        Assert.Equal(
            DocumentState.Unchanged,
            Staleness.Check(uri, new Staleness.Stamp(DateTime.UtcNow, 42)));
    }

    /// <summary>
    /// The signature a session compares on each request, and the reason it carries the path:
    /// a solution renamed keeps its bytes and its timestamp, so a stamp alone would call the
    /// project graph unchanged.
    /// </summary>
    [Fact]
    public void A_renamed_solution_changes_the_solution_signature()
    {
        using var workspace = new Workspace(solution: false);
        workspace.Write("Workspace.slnx", "<Solution />");
        var before = Program.SolutionSignature(workspace.Root);

        File.Move(
            Path.Combine(workspace.Root, "Workspace.slnx"),
            Path.Combine(workspace.Root, "Renamed.slnx"));

        Assert.NotEqual("", before);
        Assert.NotEqual(before, Program.SolutionSignature(workspace.Root));
    }

    [Fact]
    public void A_second_solution_appearing_changes_the_solution_signature()
    {
        using var workspace = new Workspace(solution: false);
        workspace.Write("Workspace.slnx", "<Solution />");
        var before = Program.SolutionSignature(workspace.Root);

        workspace.Write("Other.slnx", "<Solution />");

        Assert.NotEqual(before, Program.SolutionSignature(workspace.Root));
    }

    private static string Written(Workspace workspace, string relativePath, string text)
    {
        workspace.Write(relativePath, text);
        return PathUri.FromPath(Path.Combine(workspace.Root, relativePath));
    }
}
