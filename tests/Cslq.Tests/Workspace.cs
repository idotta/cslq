namespace Cslq.Tests;

/// <summary>
/// A throwaway directory tree. Sentinel inference is a filesystem scan, so the only way to
/// test it is to give it a filesystem; these trees are the shapes <c>fixture/</c> cannot
/// hold, either because they are broken on purpose or because they would collide with the
/// fixture's own pinned counts.
/// </summary>
internal sealed class Workspace : IDisposable
{
    private readonly bool _solution;
    private readonly List<string> _projects = [];

    /// <param name="solution">
    /// Whether <see cref="Project"/> keeps a root <c>Workspace.slnx</c> listing everything it
    /// has created. On by default because a root with no solution is now rejected outright;
    /// pass <c>false</c> when the test supplies its own solutions, or means to have none.
    /// </param>
    public Workspace(bool solution = true)
    {
        _solution = solution;
        Root = Path.Combine(Path.GetTempPath(), "cslq-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>A project directory holding an empty <c>.csproj</c>, returned full-path.</summary>
    public string Project(string relativeDirectory)
    {
        var dir = Path.Combine(Root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var project = Path.Combine(dir, Path.GetFileName(dir) + ".csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        _projects.Add(Path.GetRelativePath(Root, project).Replace(Path.DirectorySeparatorChar, '/'));
        if (_solution)
        {
            File.WriteAllText(
                Path.Combine(Root, "Workspace.slnx"),
                string.Join(Environment.NewLine,
                    ["<Solution>",
                     .. _projects.Select(p => $"  <Project Path=\"{p}\" />"),
                     "</Solution>"]));
        }

        return Path.GetFullPath(dir);
    }

    public void Write(string relativePath, string text)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp tree is not a test failure.
        }
    }
}
