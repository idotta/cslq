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

    /// <summary>
    /// The post-load grace, when it — and not <c>--timeout</c> — is what ended the wait, with
    /// how long the run had actually spent by then. Present only on that branch, because it is
    /// the only one where the headline's <c>--timeout</c> was a lie: the wait stops at
    /// <c>projectInitializationComplete + grace</c>, so a 60s <c>--timeout</c> reported "within
    /// 60s" after 22s and sent every reader to the one lever that cannot help.
    /// </summary>
    internal readonly record struct GraceBound(TimeSpan Grace, TimeSpan Waited);

    /// <param name="Stale">
    /// The subjects whose candidate sentinel no longer matches what the disk says, rendered
    /// the way <paramref name="Pending"/>'s are. A session infers its sentinels once per
    /// attach and a <c>.cs</c> edit does not re-attach it, so an edit that removes a
    /// not-yet-proved project's only candidate fails every later call in that session with a
    /// message naming a type that no longer exists anywhere — the reader's first move, a grep
    /// for it, then explains nothing. Computed on the failure path alone, where the run has
    /// already spent its whole timeout, exactly as <see cref="Diagnosis.Cause"/> is: the
    /// answer is a directory walk, and #42 took that off the request path deliberately.
    /// </param>
    internal sealed record Failure(
        TimeSpan Timeout,
        bool Fired,
        IReadOnlyList<Unresolved> Pending,
        IReadOnlyList<string> Unprobed,
        IReadOnlyList<string> Linked,
        string? Cause = null,
        string? StderrTail = null,
        IReadOnlyList<string>? Stale = null,
        string? Root = null,
        GraceBound? Grace = null,
        string? LogTail = null);

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

        // Said after the sentinels it is about, because it explains a name the reader has just
        // been handed and cannot find anywhere on disk.
        if (f.Stale is { Count: > 0 } stale)
        {
            lines.Add(
                $"  the sentinel for {string.Join(", ", stale)} was inferred when this session "
                + "attached and the source has changed since, so the name above may no longer "
                + $"exist; 'cslq session stop --root {f.Root}' re-infers it");
        }

        if (f.Unprobed.Count > 0)
        {
            lines.Add($"  not probed, for want of a type declaration: {string.Join(", ", f.Unprobed)}");
        }

        if (f.Linked.Count > 0)
        {
            lines.Add($"  not probed, having no sources of their own: {string.Join(", ", f.Linked)}");
        }

        // The server's own log before its stderr: window/logMessage is where a design-time
        // build failure is actually reported, and stderr is the process-level noise under it.
        return string.Join('\n', lines) + f.LogTail + f.StderrTail;
    }

    private static string Headline(Failure f)
    {
        if (!f.Fired)
        {
            // A short --timeout used to be reported as "projectInitializationComplete
            // never fired", which reads as a fault and is the ordinary state of every attach
            // until the reload ends. What the caller needs is the deadline, not the protocol.
            // Reaching here means the wait ran to the deadline: nothing has fired, so there is
            // no grace to bound from and --timeout really is the lever.
            return $"Workspace did not become ready within {Secs(f.Timeout)}: the workspace is "
                + "still loading — projectInitializationComplete has not fired, so the solution "
                + "load this run asked for has not finished. Raise --timeout.";
        }

        var notes = new List<string>();
        string lead;

        if (f.Grace is { } grace)
        {
            lead = $"Workspace did not become ready {Secs(grace.Grace)} after "
                + "projectInitializationComplete";
            notes.Add($"the wait ended there rather than at --timeout, {Secs(grace.Waited)} into "
                + $"the {Secs(f.Timeout)} it was given, so raising --timeout is not the lever");
        }
        else
        {
            lead = $"Workspace did not become ready within {Secs(f.Timeout)}";
            notes.Add("projectInitializationComplete fired");
        }

        // A cause is only ever computed in the every-project-empty state, so it is also what
        // says the headline may claim it — see LspClient.WaitReadyAsync's call.
        if (f.Cause is not null)
        {
            notes.Add("every probed project answered empty — the shape of a design-time build "
                + "that failed");
        }

        return $"{lead} ({string.Join("; ", notes)}):";
    }

    private static string Secs(TimeSpan span) => $"{span.TotalSeconds:0}s";

    private static string Quote(IReadOnlyList<string> candidates) =>
        $"'{string.Join("' / '", candidates)}'";
}
