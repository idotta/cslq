using System.Reflection;

namespace Cslq;

/// <summary>
/// The one version string. <c>Version</c> in Cslq.csproj becomes the assembly's
/// informational version, which is what <c>cslq --version</c> prints and what
/// <c>initialize</c> sends as the client version, so the package, the release tag and the
/// wire can never disagree.
/// </summary>
internal static class Build
{
    public static string Version { get; } = Clean(typeof(Build).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// The SDK appends <c>+&lt;commit&gt;</c> to the informational version once source link
    /// is on. That is build metadata, not the version a release is tagged with.
    /// </summary>
    internal static string Clean(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return "0.0.0";
        var plus = informational.IndexOf('+');
        return (plus < 0 ? informational : informational[..plus]).Trim();
    }
}
