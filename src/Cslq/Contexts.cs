namespace Cslq;

/// <summary>
/// One project context a document is compiled in: the <c>_vs_id</c> that names it on the
/// wire, and the two halves of that id worth reading. A multi-targeted project contributes
/// one of these per target framework, all naming the same <c>.csproj</c>; a file linked into
/// two projects contributes one per project.
/// <para>
/// <see cref="Id"/> travels back to the server verbatim in a
/// <c>_vs_projectContext</c>, so it is the only field that must not be reshaped. The guid
/// half of it is regenerated on every attach — see <c>LspClient.ContextsAsync</c>.
/// </para>
/// </summary>
internal sealed record DocumentContext(string Id, string File, string? Tfm, string? Label)
{
    /// <summary>
    /// The framework this context compiles for, as a caller types it into <c>--tfm</c>. A
    /// field the server did not send renders as <c>?</c>, like a missing assembly in a
    /// metadata label.
    /// </summary>
    public string Name => Tfm ?? "?";

    /// <summary>
    /// What to send as <c>_vs_projectContext</c>. <c>_vs_label</c> is display text the
    /// server never reads back — Roslyn matches the id alone — but it is part of the shape
    /// the request was measured against, and a hand-rolled payload with the wrong shape does
    /// not fail cleanly here (see CLAUDE.md).
    /// </summary>
    public VsProjectContext Wire => new(Id, Label ?? Name);
}

/// <summary>
/// The pure half of choosing a project context: the order, the <c>--tfm</c> filter and the
/// names an answer is labelled with. The request that produces the contexts lives in
/// <see cref="LspClient"/>.
/// </summary>
internal static class Contexts
{
    /// <summary>
    /// The document's project contexts, read off the wire: parsed, filtered and ordered.
    /// <para>
    /// A context whose <c>_vs_id</c> does not name a <c>.csproj</c> is dropped, and that is
    /// not defensive — it is the answer for a file no project compiles. Roslyn hands such a
    /// document a miscellaneous-files context,
    /// <c>&lt;guid&gt;|LanguageServerWorkspace Files Project for &lt;path&gt;</c>, labelled
    /// <c>Arquivos Diversos</c> on this machine (measured 2026-09-10 — the label is localised,
    /// like every other <c>_vs_label</c>). Keeping it would make <c>cslq project</c> answer
    /// <c>?</c> at exit 0 for `fixture/Ambient/Stray.cs`, where <c>no project</c> at exit 1 is
    /// both true and the thing that explains why <c>sym</c> cannot see its types.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<DocumentContext> Read(IEnumerable<ProjectContext> raw) =>
        Order(raw
            .Select(c =>
            {
                var (file, tfm) = Parse(c.Id);
                return file is null ? null : new DocumentContext(c.Id, file, tfm, c.Label);
            })
            .OfType<DocumentContext>());

    /// <summary>
    /// The order every context-bound request asks in: <c>.csproj</c> path, then framework,
    /// both ordinal. <b>Never the order the server sent and never <c>_vs_defaultIndex</c>.</b>
    /// Measured 2026-09-10 on <c>fixture2/Multi</c>: <c>_vs_defaultIndex</c> was 0 in 6 of 6
    /// runs while the array order itself varied per attach, and the unqualified answer
    /// followed the array — which is the whole of T-26 and T-27. So the index carries no
    /// information, and an order of our own is the only thing that makes an answer repeatable.
    /// </summary>
    internal static List<DocumentContext> Order(IEnumerable<DocumentContext> contexts) =>
    [
        .. contexts
            .OrderBy(c => c.File, StringComparer.Ordinal)
            .ThenBy(c => c.Tfm ?? string.Empty, StringComparer.Ordinal),
    ];

    /// <summary>
    /// The contexts a request may ask in. Without <c>--tfm</c> that is all of them; with it,
    /// the matching ones — plural, because a file linked into two projects can be compiled
    /// for one framework twice. No match is an error naming what the document does have: the
    /// alternative is a silently unfiltered answer, which is the bug this option exists to
    /// fix.
    /// </summary>
    internal static IReadOnlyList<DocumentContext> Select(
        IReadOnlyList<DocumentContext> ordered, string? tfm, string display)
    {
        if (tfm is null) return ordered;

        var kept = ordered
            .Where(c => string.Equals(c.Tfm, tfm, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return kept.Count > 0
            ? kept
            : throw new CslqException(ordered.Count == 0
                ? $"no context for --tfm {tfm}: {display} has no project context"
                : $"no context for --tfm {tfm}: {display} has {Names(ordered)}");
    }

    /// <summary>
    /// The contexts as a caller reads them, disambiguated by project only when the document
    /// really is compiled by more than one: a linked file's two <c>net10.0</c> contexts are
    /// two different answers, and printing the framework twice would say otherwise.
    /// </summary>
    internal static string Names(IReadOnlyList<DocumentContext> contexts) =>
        string.Join(", ", contexts.Select(c => Label(contexts, c)));

    internal static string Label(IReadOnlyList<DocumentContext> all, DocumentContext context) =>
        all.Select(c => c.File).Distinct(PathUri.PathComparer).Count() > 1
            ? $"{Path.GetFileNameWithoutExtension(context.File)} ({context.Name})"
            : context.Name;

    /// <summary>
    /// The path half of a <c>_vs_id</c> and the framework it carries for a multi-targeted
    /// project. Anything shaped unexpectedly yields nothing rather than a guess: a wrong
    /// project in a label is worse than no project.
    /// </summary>
    internal static (string? File, string? Tfm) Parse(string id)
    {
        var bar = id.IndexOf('|');
        if (bar < 0) return (null, null);

        var path = id[(bar + 1)..].Trim();
        string? tfm = null;
        var suffix = path.LastIndexOf(" ($", StringComparison.Ordinal);
        if (suffix > 0 && path.EndsWith(')'))
        {
            tfm = path[(suffix + 3)..^1];
            path = path[..suffix];
        }

        return path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? (path, tfm) : (null, null);
    }
}

/// <summary>
/// An answer and the context it came from — null when the document had none to choose from,
/// which is every document no project compiles.
/// </summary>
internal sealed record Answer<T>(T Value, DocumentContext? Context);

/// <summary>
/// What a renderer needs to say which project context answered: every context the document
/// has, the ones the request was allowed to ask in (all of them without <c>--tfm</c>), and
/// the one that answered. Absent for the commands that do not choose a context yet.
/// </summary>
internal sealed record ContextNote(
    IReadOnlyList<DocumentContext> All,
    IReadOnlyList<DocumentContext> Asked,
    DocumentContext? Answered);
