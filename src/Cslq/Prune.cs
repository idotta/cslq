using System.Text.Json;

namespace Cslq;

/// <summary>
/// Every pin the bump loop has ever restored stays in the NuGet global packages folder,
/// ~300 MB each, and nothing in <c>dotnet tool</c> removes one. Once a restore has succeeded
/// the pinned version is the only one this binary can run, so every other version of the
/// server packages goes. A version another manifest on the machine still pins reads as
/// unrestored afterwards — <c>dotnet tool run</c> answers <c>Run "dotnet tool restore"</c>
/// for it (verified with the directory moved aside), the message
/// <see cref="LspClient.NotRestored"/> recognises — so an older <c>cslq</c> re-restores
/// itself instead of breaking.
/// </summary>
internal static class Prune
{
    public const string PackageId = "roslyn-language-server";

    /// <summary>
    /// <paramref name="Removed"/> and <paramref name="Kept"/> are <c>package/version</c>,
    /// relative to <paramref name="Packages"/>; <paramref name="Kept"/> pairs each with why.
    /// </summary>
    public sealed record Result(
        string Packages,
        IReadOnlyList<string> Removed,
        IReadOnlyList<(string Dir, string Why)> Kept);

    /// <summary>The pinned server version in a tool manifest, or null when it names none.</summary>
    public static string? PinnedVersion(string manifestJson)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        return doc.RootElement.TryGetProperty("tools", out var tools)
            && tools.TryGetProperty(PackageId, out var tool)
            && tool.TryGetProperty("version", out var version)
            ? version.GetString()
            : null;
    }

    /// <summary>
    /// Deletes every version of the server packages — the RID-less shim and every
    /// <c>roslyn-language-server.&lt;rid&gt;</c> beside it — that is not the pin. NuGet
    /// lower-cases ids and versions on disk, so the pin is compared ignoring case. The
    /// <c>.nupkg.sha512</c> marker goes first, so a directory a still-running old daemon holds
    /// half-locked already reads as absent to NuGet and the next restore rewrites it rather
    /// than trusting what is left. Nothing here throws for a locked, unreadable or
    /// concurrently removed directory — it is reported as kept: the restore that just
    /// succeeded is the result, and this is housekeeping after it.
    /// </summary>
    public static Result Run(string packages, string pin)
    {
        var removed = new List<string>();
        var kept = new List<(string, string)>();
        foreach (var dir in OtherVersions(packages, pin, kept))
        {
            var label = Label(packages, dir);
            try
            {
                foreach (var marker in Directory.EnumerateFiles(dir, "*.nupkg.sha512"))
                    File.Delete(marker);
                Directory.Delete(dir, recursive: true);
                removed.Add(label);
            }
            catch (Exception ex) when (Tolerated(ex))
            {
                kept.Add((label, ex.Message.Trim()));
            }
        }

        return new Result(packages, removed, kept);
    }

    private static List<string> OtherVersions(string packages, string pin, List<(string, string)> kept)
    {
        var found = new List<string>();
        if (!Directory.Exists(packages)) return found;

        string[] candidates;
        try
        {
            candidates = Directory.GetDirectories(packages, PackageId + "*");
        }
        catch (Exception ex) when (Tolerated(ex))
        {
            kept.Add((PackageId + "*", ex.Message.Trim()));
            return found;
        }

        foreach (var package in candidates)
        {
            var id = Path.GetFileName(package);
            if (!id.Equals(PackageId, StringComparison.OrdinalIgnoreCase)
                && !id.StartsWith(PackageId + ".", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                found.AddRange(Directory.GetDirectories(package)
                    .Where(v => !Path.GetFileName(v).Equals(pin, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (Tolerated(ex))
            {
                kept.Add((Label(packages, package), ex.Message.Trim()));
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    private static bool Tolerated(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private static string Label(string packages, string dir) =>
        Path.GetRelativePath(packages, dir).Replace(Path.DirectorySeparatorChar, '/');
}
