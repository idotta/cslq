namespace Cslq;

internal static class PathUri
{
    /// <summary>
    /// Roslyn reports source-generated documents under its own scheme. Nothing is on disk,
    /// and <c>new Uri(u).LocalPath</c> does not throw for one — it returns a path-shaped
    /// string ("/BuildInfo.g.cs") that then renders as a confident wrong answer, so every
    /// URI-to-path conversion has to check this first.
    /// </summary>
    public const string GeneratedScheme = "roslyn-source-generated";

    /// <summary>
    /// Whether the host resolves file paths case-insensitively: Windows and macOS do,
    /// everything else is ordinal. Named rather than repeated, so the two properties below and
    /// the tests that pin them cannot drift apart. Not <c>!IsLinux()</c>, which would make
    /// every other Unix — FreeBSD, for one — case-insensitive by accident.
    /// </summary>
    public static bool PathsAreCaseInsensitive { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>
    /// How two file paths are compared for identity. Windows and macOS resolve paths
    /// case-insensitively, Linux does not — and Linux is the platform CI has always run, so
    /// comparing <c>OrdinalIgnoreCase</c> everywhere was a latent bug on the only host nobody
    /// was watching: two genuinely different files differing only in case would be treated as
    /// one, silently. Path <em>identity</em> only: an extension or scheme check
    /// (<c>.cs</c>, <c>.csproj</c>, <c>.slnx</c>, <c>roslyn-source-generated:</c>) stays
    /// case-insensitive on every platform, because that is a spelling question rather than a
    /// question about which file this is, and <see cref="Output"/>'s display sort stays
    /// case-insensitive because it is presentation.
    /// </summary>
    public static StringComparison PathComparison { get; } =
        PathsAreCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary><see cref="PathComparison"/> as a comparer, for dictionary keys and sorts.</summary>
    public static StringComparer PathComparer { get; } =
        PathsAreCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string FromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string ToPath(string uri) => new Uri(uri).LocalPath;

    public static bool IsGenerated(string uri) =>
        uri.StartsWith(GeneratedScheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a location is Roslyn's decompiled stand-in for a symbol it only has an assembly
    /// for. Two things wear this fingerprint and they are not the same answer: for a framework
    /// or NuGet type the decompiled document <em>is</em> the definition, while for a type whose
    /// source is in the workspace it means a <c>ProjectReference</c> is still bound to the
    /// referenced project's built assembly and the answer is about to change. What tells them
    /// apart is whether the workspace also declares the type — see
    /// <see cref="LspClient.SettleAsync"/>.
    /// </summary>
    public static bool IsDecompiled(string uri) =>
        !IsGenerated(uri) &&
        ToPath(uri).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains("MetadataAsSource", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The type a decompiled document stands for, which is its file name: Roslyn writes one
    /// file per type, named after it, under two layers of run-specific guid directories that
    /// carry no other information. That name is the only thing in the URI worth reading, and
    /// it is what <see cref="LspClient.SettleAsync"/> asks the workspace about.
    /// </summary>
    public static string MetadataTypeName(string uri) =>
        Path.GetFileNameWithoutExtension(ToPath(uri));

    /// <summary>
    /// The assembly a decompiled document was produced from, read off the header Roslyn writes
    /// at the top of it (<c>#region Assembly System.Console, Version=10.0.0.0, ...</c>).
    /// The URI cannot supply it — the temp path is two guids and a file name — so the label
    /// comes from the document's own text, which for a decompiled document is a real file on
    /// disk. Null when the header is not there, which renders as <c>?</c> rather than a guess.
    /// The leading byte-order mark is stripped: Roslyn writes one.
    /// </summary>
    public static string? MetadataAssembly(string? firstLine)
    {
        var line = firstLine?.TrimStart('\uFEFF').Trim();
        const string prefix = "#region Assembly ";
        if (line is null || !line.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var name = line[prefix.Length..];
        var comma = name.IndexOf(',');
        if (comma >= 0) name = name[..comma];
        name = name.Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Whether any of <paramref name="uris"/> is an ordinary file inside
    /// <paramref name="root"/>. That is the discriminator between the two decompiled cases: a
    /// type Roslyn answered for out of an assembly <em>and</em> declares in a workspace file is
    /// a stale <c>ProjectReference</c> binding, and one it only has the assembly for is a
    /// framework or package type whose decompiled document is the real answer.
    /// </summary>
    public static bool AnyUnder(string root, IEnumerable<string> uris)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return uris.Any(u =>
            !IsGenerated(u) && !IsDecompiled(u) &&
            Path.GetFullPath(ToPath(u)).StartsWith(prefix, PathComparison));
    }

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
    public static string Display(
        string root, string uri, string? projectFile = null, string? assembly = null)
    {
        // Before the file branch: a decompiled document is a file URI, and its path is a
        // machine-absolute temp path that Relative would hand straight to the caller.
        if (IsDecompiled(uri))
        {
            return $"<metadata>/{assembly ?? "?"}/{MetadataTypeName(uri)}.cs";
        }

        if (!IsGenerated(uri)) return Relative(root, ToPath(uri));

        var query = Query(uri);
        var generator = query.GetValueOrDefault("assemblyName", "?");
        var hint = query.GetValueOrDefault("hintName") ?? ToPath(uri).TrimStart('/');
        var project = projectFile is null
            ? string.Empty
            : Relative(root, Path.GetDirectoryName(Path.GetFullPath(projectFile))!) + "/";
        return $"<generated>/{project}{generator}/{hint}";
    }

    /// <summary>
    /// <see cref="Display(string, string, string?, string?)"/> with the two lookups the label
    /// needs attached. Both cost a request or a file read, so each is made only for the kind of
    /// URI that needs it: a generated document needs its consuming project, a decompiled one
    /// needs its assembly, and an ordinary file URI carries its own path and needs nothing
    /// asked.
    /// </summary>
    public static async Task<string> DisplayAsync(string root, string uri, Documents documents)
    {
        if (IsDecompiled(uri)) return Display(root, uri, assembly: await documents.Assembly(uri));
        if (IsGenerated(uri)) return Display(root, uri, await documents.Project(uri));
        return Display(root, uri);
    }

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
