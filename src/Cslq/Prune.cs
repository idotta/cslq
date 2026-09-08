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
    /// The version directories of the server packages — the RID-less shim and every
    /// <c>roslyn-language-server.&lt;rid&gt;</c> beside it — that are not the pin. NuGet
    /// lower-cases ids and versions on disk, so the pin is compared ignoring case.
    /// </summary>
    public static IReadOnlyList<string> OtherVersions(string packages, string pin)
    {
        if (!Directory.Exists(packages)) return [];

        var found = new List<string>();
        foreach (var package in Directory.EnumerateDirectories(packages, PackageId + "*"))
        {
            var id = Path.GetFileName(package);
            if (!id.Equals(PackageId, StringComparison.OrdinalIgnoreCase)
                && !id.StartsWith(PackageId + ".", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var version in Directory.EnumerateDirectories(package))
            {
                if (!Path.GetFileName(version).Equals(pin, StringComparison.OrdinalIgnoreCase))
                    found.Add(version);
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>
    /// Deletes every other version. The <c>.nupkg.sha512</c> marker goes first, so a
    /// directory a still-running old daemon holds half-locked already reads as absent to
    /// NuGet and the next restore rewrites it rather than trusting what is left. A locked or
    /// unwritable directory is reported, never thrown: the restore that just succeeded is the
    /// result, and this is housekeeping after it.
    /// </summary>
    public static Result Run(string packages, string pin)
    {
        var removed = new List<string>();
        var kept = new List<(string, string)>();
        foreach (var dir in OtherVersions(packages, pin))
        {
            var label = Path.GetRelativePath(packages, dir).Replace(Path.DirectorySeparatorChar, '/');
            try
            {
                foreach (var marker in Directory.EnumerateFiles(dir, "*.nupkg.sha512"))
                    File.Delete(marker);
                Directory.Delete(dir, recursive: true);
                removed.Add(label);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                kept.Add((label, ex.Message.Trim()));
            }
        }

        return new Result(packages, removed, kept);
    }
}
