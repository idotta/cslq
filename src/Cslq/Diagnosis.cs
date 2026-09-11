namespace Cslq;

/// <summary>
/// Why a workspace that finished loading answered nothing. Reached only when readiness has
/// already failed <em>and</em> every probed project came back empty, which is the signature of
/// a design-time build that failed rather than of one unresolvable sentinel: the server loaded
/// the solution, compiled nothing and answered every query with an honest empty list.
/// <para>
/// On the failure path by design, not as a pre-flight. The run has already spent its whole
/// <c>--timeout</c> by the time this is asked, so a <c>dotnet</c> launch costs nothing that
/// matters, while on the happy path it would be one process start on every invocation — the
/// same reason the restore and the prune are kept off the start path.
/// </para>
/// <para>
/// Measured on CleanArchitecture with a <c>global.json</c> pinning an SDK that is not
/// installed: 181.7 s and a message naming neither <c>global.json</c>, the SDK nor MSBuild.
/// <c>dotnet --version</c> run with the working directory set to <c>--root</c> exits 155
/// there, and that is the whole diagnosis.
/// </para>
/// </summary>
internal static class Diagnosis
{
    /// <summary>What <c>dotnet --version</c> said in the root, or null when it could not run.</summary>
    internal readonly record struct Sdk(int ExitCode, string Output);

    /// <summary>
    /// The sentence for the failure message, or null when nothing was found to say. Pure, so
    /// the interpretation is testable without an SDK that is not installed: the two inputs are
    /// what the probes below measured.
    /// </summary>
    internal static string? Cause(Sdk? sdk, IReadOnlyList<string> unrestored)
    {
        if (sdk is { ExitCode: not 0 } bad)
        {
            // The SDK's own text is what names global.json and the version it wanted, and it
            // is pinned to English on the child the way it is on the server. The exit code is
            // deliberately not printed: Windows reports the 155 a shell shows as the raw
            // -2147450725, and a number nothing on the machine agrees on reads as noise.
            return $"the .NET SDK cannot run in this root — 'dotnet --version' fails there: "
                + $"{bad.Output} Until that is fixed the server's design-time build fails for "
                + "every project and the workspace loads empty.";
        }

        if (unrestored.Count > 0)
        {
            return $"no restore output under {Names(unrestored)} (obj/project.assets.json is "
                + "missing), so the design-time build had no resolved references; run "
                + "'dotnet restore' and check that it succeeds.";
        }

        return "the solution loaded and every project compiled to nothing; check that "
            + "'dotnet build' succeeds in this root — a design-time build failure is reported "
            + "to the server and never to us.";
    }

    /// <summary>
    /// The two or three lines of <c>dotnet --version</c>'s output that say something. Its
    /// first line is <c>The command could not be loaded, possibly because:</c> — boilerplate
    /// that is the same for every failure — while the sentence naming the cause sits four
    /// lines down and the version and the <c>global.json</c> path two below that. Measured
    /// 2026-09-10 against a root pinning 9.9.900. Falls back to the first non-empty line for
    /// any failure whose shape is not this one.
    /// </summary>
    internal static string Summary(string output)
    {
        string[] telling =
            ["compatible .NET SDK was not found", "Requested SDK version", "global.json file"];

        var lines = output
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var told = lines.Where(l => telling.Any(t => l.Contains(t, StringComparison.Ordinal))).ToList();
        var text = told.Count > 0 ? string.Join(" ", told) : lines.FirstOrDefault() ?? "no output";
        return text.Length > 300 ? text[..300] : text;
    }

    private static string Names(IReadOnlyList<string> projects) =>
        projects.Count <= 4
            ? string.Join(", ", projects)
            : string.Join(", ", projects.Take(4)) + $" and {projects.Count - 4} more";

    /// <summary>
    /// Projects with no <c>obj/project.assets.json</c>. A file check, so it costs nothing and
    /// cannot fail the run; a restore that never happened is the other half of T-35's class,
    /// and it is the half <c>dotnet --version</c> cannot see. Note this is a *failed* restore
    /// talking: the server does restore on its own, measured, so the absence of the file after
    /// a full load is evidence that the restore it ran did not succeed.
    /// </summary>
    internal static IReadOnlyList<string> Unrestored(string root, IEnumerable<string> projectDirectories) =>
    [
        .. projectDirectories
            .Where(d => !File.Exists(Path.Combine(d, "obj", "project.assets.json")))
            .Select(d => PathUri.Relative(root, d)),
    ];
}
