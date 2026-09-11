namespace Cslq;

/// <summary>
/// The readiness failure message, assembled away from the wait that produces it so it can be
/// pinned by a unit test — the states it has to distinguish are exactly the ones a live probe
/// is worst at staging.
/// <para>
/// Two things it is not. It is not one line: on a 26-project repository the old single line
/// ran to 1,050 characters, and its content was right — it is what exposed the linked-project
/// class — so the fix is layout, one item per line. And it does not report "never fired" as
/// though that were a diagnosis: the notification ends the solution load <em>this client</em>
/// asked for, so not having fired means the load is still running, which is a sentence a
/// caller can act on and an implementation detail is not.
/// </para>
/// </summary>
internal static class Readiness
{
    /// <summary>
    /// One probe that never answered, and what was asked for it. <c>Subject</c> is rendered
    /// rather than a bare name because an explicit <c>--sentinel</c> is not a project and
    /// must not be printed as one: it reads <c>explicit sentinel 'X'</c>, where a project
    /// reads <c>project Core</c>.
    /// </summary>
    internal readonly record struct Unresolved(string Subject, IReadOnlyList<string> Candidates);

    internal sealed record Failure(
        TimeSpan Timeout,
        bool Fired,
        IReadOnlyList<Unresolved> Pending,
        IReadOnlyList<string> Unprobed,
        IReadOnlyList<string> Linked,
        string? Cause = null,
        string? StderrTail = null);

    /// <summary>
    /// Whether every project that could be probed came back empty. With the load finished,
    /// that is the signature of a design-time build that failed — the server loaded the
    /// solution, compiled nothing, and answered every query with an honest empty list — and it
    /// is the one state worth spending a <c>dotnet</c> launch to explain. One project empty
    /// out of twenty is an ordinary unresolvable candidate and explains itself.
    /// </summary>
    internal static bool EveryProjectEmpty(int pending, int probed) => probed > 0 && pending == probed;

    internal static string Message(Failure f)
    {
        var lines = new List<string> { Headline(f) };

        // The cause first: it is the only line that says why, and the per-project list below
        // is the evidence for it rather than the other way round.
        if (f.Cause is { } cause) lines.Add($"  cause: {cause}");

        foreach (var (subject, candidates) in f.Pending)
        {
            lines.Add($"  sentinel query {Quote(candidates)} returned no symbols for {subject}");
        }

        if (f.Unprobed.Count > 0)
        {
            lines.Add($"  not probed, for want of a type declaration: {string.Join(", ", f.Unprobed)}");
        }

        if (f.Linked.Count > 0)
        {
            lines.Add($"  not probed, having no sources of their own: {string.Join(", ", f.Linked)}");
        }

        return string.Join('\n', lines) + f.StderrTail;
    }

    private static string Headline(Failure f)
    {
        var within = $"Workspace did not become ready within {f.Timeout.TotalSeconds:0}s";

        if (!f.Fired)
        {
            // T-42: a short --timeout used to be reported as "projectInitializationComplete
            // never fired", which reads as a fault and is the ordinary state of every attach
            // until the reload ends. What the caller needs is the deadline, not the protocol.
            return $"{within}: the workspace is still loading — projectInitializationComplete "
                + "has not fired, so the solution load this run asked for has not finished. "
                + "Raise --timeout.";
        }

        // A cause is only ever computed in the every-project-empty state, so it is also what
        // says the headline may claim it — see LspClient.WaitReadyAsync's call.
        return f.Cause is not null
            ? $"{within} (projectInitializationComplete fired, and every probed project "
                + "answered empty — the shape of a design-time build that failed):"
            : $"{within} (projectInitializationComplete fired):";
    }

    private static string Quote(IReadOnlyList<string> candidates) =>
        $"'{string.Join("' / '", candidates)}'";
}
