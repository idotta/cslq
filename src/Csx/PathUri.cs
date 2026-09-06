namespace Csx;

internal static class PathUri
{
    /// <summary>
    /// Roslyn reports source-generated documents under its own scheme. Nothing is on disk,
    /// and <c>new Uri(u).LocalPath</c> does not throw for one — it returns a path-shaped
    /// string ("/BuildInfo.g.cs") that then renders as a confident wrong answer, so every
    /// URI-to-path conversion has to check this first.
    /// </summary>
    public const string GeneratedScheme = "roslyn-source-generated";

    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string ToPath(string uri) => new Uri(uri).LocalPath;

    public static bool IsGenerated(string uri) =>
        uri.StartsWith(GeneratedScheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a location is Roslyn's decompiled stand-in for a symbol it only has an assembly
    /// for. Legitimate for a symbol from a NuGet package, and a bug's fingerprint for one whose
    /// source is in the workspace: a <c>ProjectReference</c> binds to the referenced project's
    /// *built assembly* until that project is itself loaded, so a query fired too early answers
    /// with a temp file under <c>MetadataAsSource</c> instead of the repo.
    /// </summary>
    public static bool IsDecompiled(string uri) =>
        !IsGenerated(uri) &&
        ToPath(uri).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains("MetadataAsSource", StringComparer.OrdinalIgnoreCase);

    /// <summary>Agents want repo-relative forward-slash paths, not absolute paths or URIs.</summary>
    public static string Relative(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel.StartsWith("..", StringComparison.Ordinal) ? path.Replace('\\', '/') : rel.Replace('\\', '/');
    }

    /// <summary>
    /// The display form for any location. A generated URI carries an authority guid and a
    /// documentId that are both regenerated on every workspace load, plus a machine-absolute
    /// assemblyPath, so the label is built only from the fields that are stable across runs
    /// and machines. The angle brackets keep it from being mistaken for a readable file.
    /// <para>
    /// Those stable fields name the <em>generator</em>, never the project consuming it, so
    /// one generator applied to several projects renders several distinct documents
    /// identically. <paramref name="projectFile"/> — the <c>.csproj</c> from
    /// <c>LspClient.ProjectOfAsync</c> — is what separates them, and its directory is used
    /// rather than its file name so two same-named projects in different directories stay
    /// distinct. Null when the server would not say, which restores the older ambiguous form
    /// rather than inventing a project.
    /// </para>
    /// </summary>
    public static string Display(string root, string uri, string? projectFile = null)
    {
        if (!IsGenerated(uri)) return Relative(root, ToPath(uri));

        var query = Query(uri);
        var assembly = query.GetValueOrDefault("assemblyName", "?");
        var hint = query.GetValueOrDefault("hintName") ?? ToPath(uri).TrimStart('/');
        var project = projectFile is null
            ? string.Empty
            : Relative(root, Path.GetDirectoryName(Path.GetFullPath(projectFile))!) + "/";
        return $"<generated>/{project}{assembly}/{hint}";
    }

    /// <summary>
    /// <see cref="Display(string, string, string?)"/> with the project lookup attached. The
    /// lookup is a request, so it is made only for a generated URI — a file URI carries its
    /// own path and needs nothing asked.
    /// </summary>
    public static async Task<string> DisplayAsync(
        string root, string uri, Func<string, Task<string?>> projectOf) =>
        IsGenerated(uri) ? Display(root, uri, await projectOf(uri)) : Display(root, uri);

    private static Dictionary<string, string> Query(string uri)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in new Uri(uri).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) pairs[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return pairs;
    }
}
