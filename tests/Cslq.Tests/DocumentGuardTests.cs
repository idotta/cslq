namespace Cslq.Tests;

/// <summary>
/// The guard every file-taking command runs before any <c>didOpen</c>. Both halves of it were
/// answered by the server before it existed: a <c>.csproj</c> handed to <c>diag</c> came back
/// as a wall of parse errors at exit 0, because <c>OpenAsync</c> declares
/// <c>languageId: csharp</c> for whatever it is given, and a path above the root came back as
/// <c>no results</c>.
/// </summary>
public class DocumentGuardTests
{
    [Theory]
    [InlineData("Core/Greeter.cs")]
    [InlineData("Core/Counter.razor")]
    [InlineData("Core/Index.cshtml")]
    [InlineData("Core/Greeter.CS")]
    public void A_csharp_document_under_the_root_is_accepted(string target)
    {
        using var workspace = new Workspace();
        workspace.Project("Core");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.Root, target)),
            Program.CheckDocument(workspace.Root, target));
    }

    /// <summary>Razor answers in file mode, which is why it is not `.cs` alone — see T-50.</summary>
    [Theory]
    [InlineData("Core/Core.csproj")]
    [InlineData("Core/Legacy.vb")]
    [InlineData("appsettings.json")]
    [InlineData("README.md")]
    public void Anything_else_is_not_a_csharp_document(string target)
    {
        using var workspace = new Workspace();
        workspace.Project("Core");

        var ex = Assert.Throws<CslqException>(() => Program.CheckDocument(workspace.Root, target));

        Assert.Equal($"{target} is not a C# document", ex.Message);
    }

    /// <summary>The message spells the path the caller did, like <c>no such file</c> does.</summary>
    [Fact]
    public void A_path_above_the_root_is_outside_it()
    {
        using var workspace = new Workspace();
        var project = workspace.Project("Core");

        var ex = Assert.Throws<CslqException>(
            () => Program.CheckDocument(project, "../Elsewhere.cs"));

        Assert.Equal("../Elsewhere.cs is outside --root", ex.Message);
    }

    /// <summary>
    /// Outside-ness is checked first: a caller who pointed at another repository is told that,
    /// not that the file they named there has the wrong extension.
    /// </summary>
    [Fact]
    public void Outside_the_root_beats_the_extension()
    {
        using var workspace = new Workspace();
        var project = workspace.Project("Core");

        var ex = Assert.Throws<CslqException>(
            () => Program.CheckDocument(project, "../Elsewhere.csproj"));

        Assert.Equal("../Elsewhere.csproj is outside --root", ex.Message);
    }

    /// <summary>
    /// A directory is a legitimate <c>diag</c> target, so the root check stands on its own —
    /// including for the root itself, which is what <c>diag .</c> is.
    /// </summary>
    [Fact]
    public void The_root_itself_and_a_directory_under_it_are_under_the_root()
    {
        using var workspace = new Workspace();
        workspace.Project("Core");

        Assert.Equal(Path.GetFullPath(workspace.Root), Program.CheckUnderRoot(workspace.Root, "."));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.Root, "Core")),
            Program.CheckUnderRoot(workspace.Root, "Core"));
        Assert.Throws<CslqException>(() => Program.CheckUnderRoot(workspace.Root, ".."));
    }

    /// <summary>
    /// Windows and macOS resolve paths case-insensitively and Linux does not, so the root
    /// comparison follows <see cref="PathUri.PathComparison"/> rather than being ignore-case
    /// everywhere: on Linux two roots differing only in case are two different directories.
    /// </summary>
    [Fact]
    public void The_root_comparison_follows_the_host()
    {
        using var workspace = new Workspace();
        workspace.Project("Core");

        var shouted = workspace.Root.ToUpperInvariant();
        var target = Path.Combine(workspace.Root, "Core", "Greeter.cs");

        Assert.Equal(PathUri.PathsAreCaseInsensitive, PathUri.IsUnder(shouted, target));
    }
}
