using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cslq;

/// <summary>
/// The document lookups every renderer needs, bundled because they travel together and
/// none of them can be done from a URI alone: text for context lines (off disk for a real
/// file, from the server for a generated one), the consuming project for a generated
/// document's label, and the originating assembly for a decompiled one's. Passed as lambdas
/// rather than an <see cref="LspClient"/> so the renderers and <see cref="PathUri"/> stay
/// testable without a server.
/// </summary>
internal sealed record Documents(
    Func<string, Task<string[]>> Lines,
    Func<string, Task<string?>> Project,
    Func<string, Task<string?>> Assembly)
{
    public static Documents Of(LspClient client, CancellationToken ct) => new(
        u => client.LinesAsync(u, ct),
        u => client.ProjectOfAsync(u, ct),
        u => client.AssemblyOfAsync(u, ct));
}

/// <summary>
/// A symbol as a row: the hit itself and the kind to render it as. The two are separate
/// because <c>workspace/symbol</c> reports a constructor as a method and only a declaration
/// chain says otherwise, so a caller that has already computed the chain can hand the truth
/// in without the renderer asking for one.
/// </summary>
internal sealed record SymbolRow(SymbolInformation Symbol, int Kind);

/// <summary>
/// One diagnostic as the contexts collectively reported it: the finding itself, the contexts
/// that reported it, and how many were asked. The last is what lets a renderer tell "every
/// context has this" from "only net9.0 has this" without knowing what a context is.
/// </summary>
internal sealed record Report(
    string Uri, Diagnostic Diagnostic, IReadOnlyList<string> In, int Contexts);

/// <summary>
/// Raw LSP hands back URIs and zero-based line/character ranges, which is close to
/// useless to a model. Everything an agent sees goes through here instead: repo-relative
/// path, one-based line, the matched source line and a line of context either side.
/// </summary>
internal static class Output
{
    public const int DefaultMax = 50;

    // Source lines are full of quotes and angle brackets; the default encoder turns them
    // into " noise that a model then has to decode.
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// <paramref name="documents"/> carries the lookups a label and a context line need. Text
    /// is not read here because a generated document has no file behind it — only the server
    /// can supply it — and it is fetched lazily so a capped result set costs no requests for
    /// hits it drops. The label lookups run before the cap, because the label they produce is
    /// what the rows are sorted on.
    /// </summary>
    public static async Task WriteLocationsAsync(
        string root,
        IReadOnlyList<Location> locations,
        int max,
        int context,
        bool json,
        Documents documents,
        ContextNote? note = null)
    {
        var labelled = new List<Hit>(locations.Count);
        foreach (var location in locations)
        {
            labelled.Add(new Hit(
                location.Uri, await PathUri.DisplayAsync(root, location.Uri, documents), location.Range));
        }

        // Folded on what a row renders as, not on the URI. Roslyn answers the same place
        // twice for a declaration with an implicit or primary constructor and for a namespace,
        // and answers a generated document once per target framework under URIs whose authority
        // guid and documentId differ while the label does not -- both arrive here as rows a
        // caller cannot tell apart. The first occurrence is kept so Lines(hit.Uri) still has a
        // URI the server answers for.
        var hits = labelled
            .DistinctBy(h => (h.Display,
                h.Range.Start.Line, h.Range.Start.Character,
                h.Range.End.Line, h.Range.End.Character))
            // Source before generated before metadata: a multi-targeted project can produce
            // enough generated hits to push every source row past the cap, and the source rows
            // are the ones a caller came for.
            .OrderBy(h => Rank(h.Uri))
            .ThenBy(h => h.Display, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Range.Start.Line)
            .ThenBy(h => h.Range.Start.Character)
            .ToList();

        var shown = hits.Take(max).ToList();

        if (json)
        {
            var payload = new List<object>(shown.Count);
            foreach (var hit in shown)
            {
                payload.Add(new
                {
                    path = hit.Display,
                    line = hit.Range.Start.Line + 1,
                    column = hit.Range.Start.Character + 1,
                    endLine = hit.Range.End.Line + 1,
                    endColumn = hit.Range.End.Character + 1,
                    generated = PathUri.IsGenerated(hit.Uri),
                    metadata = PathUri.IsDecompiled(hit.Uri),
                    external = PathUri.IsExternal(root, hit.Uri),
                    text = At(await documents.Lines(hit.Uri), hit.Range.Start.Line)?.TrimEnd(),
                });
            }

            Console.WriteLine(JsonSerializer.Serialize(
                Envelope(hits.Count, hits.Count > shown.Count, payload, note),
                JsonOut));
            return;
        }

        if (shown.Count == 0)
        {
            WriteMissing("no results", note);
            return;
        }

        var first = true;
        foreach (var hit in shown)
        {
            if (!first) Console.WriteLine();
            first = false;

            var line = hit.Range.Start.Line;
            Console.WriteLine($"{hit.Display}:{line + 1}:{hit.Range.Start.Character + 1}");

            var lines = await documents.Lines(hit.Uri);
            var width = (line + 1 + context).ToString().Length;
            for (var i = Math.Max(0, line - context); i <= Math.Min(lines.Length - 1, line + context); i++)
            {
                var marker = i == line ? ">" : " ";
                var column = i == line ? hit.Range.Start.Character + 1 : (int?)null;
                Console.WriteLine(
                    $"{marker} {(i + 1).ToString().PadLeft(width)} | {Body(lines[i], column)}");
            }
        }

        if (hits.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"... {hits.Count - shown.Count} more (use --max {hits.Count} to see all)");
        }

        WriteContextNote(note, empty: false);
    }

    /// <summary>
    /// The <c>{ count, truncated, results }</c> envelope, plus the keys a context-bound answer
    /// adds: <c>contexts</c> is how many the document has, and <c>tfm</c> is the one that
    /// answered — <b>present only when there is one</b>. A union has no answering context, and
    /// emitting <c>tfm: null</c> there would hand a caller a field to interpret when the answer
    /// is that the question does not apply; that is the complaint against an always-null <c>source</c>, and
    /// the rule is the same. Both keys are absent for the commands that do not choose a
    /// context at all.
    /// </summary>
    private static object Envelope(int count, bool truncated, object results, ContextNote? note) =>
        (note, note?.Answered) switch
        {
            (null, _) => new { count, truncated, results },
            (not null, null) => new { count, truncated, contexts = note.All.Count, results },
            var (_, answered) => new
            {
                count,
                truncated,
                tfm = answered!.Tfm,
                contexts = note!.All.Count,
                results,
            },
        };

    /// <summary>
    /// The line that says which project context answered, last and after a blank line like the
    /// truncation footer. Only for a document with more than one context: a single-context
    /// answer is the ordinary case and a note on every one of them would train a caller to
    /// skip it. The <c>N of M</c> shape covers the three cases — answered in one, tried them
    /// all and found nothing, tried the <c>--tfm</c> subset and found nothing — and names
    /// every context either way, because the useful next move is asking a different one.
    /// <para>
    /// A null <see cref="ContextNote.Answered"/> means the answer is a <em>union</em> of every
    /// context asked — <c>refs</c>, <c>impl</c>, <c>outline</c> and <c>diag</c> — where no
    /// single context answered. It says <c>merged from</c>, not <c>tried</c>: an answer is
    /// sitting right above the line, and "tried" beside a correct answer reads as a failure a
    /// caller then has to rule out. <c>tried</c> is kept for the empty answer, where it is
    /// exactly right and the only thing there is to say.
    /// </para>
    /// </summary>
    private static void WriteContextNote(ContextNote? note, bool empty)
    {
        if (ContextSentence(note, empty) is not { } sentence) return;

        Console.WriteLine();
        Console.WriteLine(sentence);
    }

    /// <summary>
    /// The sentence itself, split off <see cref="WriteContextNote"/> because a non-answer no
    /// longer prints where an answer does: it is one <c>cslq:</c> line on stderr, and the note
    /// is part of that line rather than a paragraph under it.
    /// </summary>
    private static string? ContextSentence(ContextNote? note, bool empty)
    {
        if (note is null || note.All.Count < 2) return null;

        var all = Contexts.Names(note.All);
        var subset = note.Asked.Count == note.All.Count
            ? null
            : $"{Contexts.Names(note.Asked)} of ";

        return true switch
        {
            _ when empty => $"tried {subset ?? "all "}{note.All.Count} contexts: {all}",
            _ when note.Answered is null => $"merged from {subset}{note.All.Count} contexts: {all}",
            _ => $"answered in {Contexts.Label(note.All, note.Answered)} of {note.All.Count} contexts: {all}",
        };
    }

    /// <summary>
    /// An empty answer that exits non-zero — <c>no results</c>, <c>no project</c>. In text mode
    /// stdout carries the answer and nothing else, so this is one <c>cslq:</c>-prefixed line on
    /// stderr like any other failure: a caller that treats stdout as data used to store
    /// <c>no results</c> as a hit. The context note, when there is one, rides on the same line.
    /// <c>--json</c> is the other half of the rule and does not come here: an empty answer
    /// keeps the ordinary <c>{ count, truncated, results }</c> envelope on stdout, where
    /// <c>count: 0</c> already says the same thing in a machine-readable way.
    /// </summary>
    private static void WriteMissing(string message, ContextNote? note = null)
    {
        var sentence = ContextSentence(note, empty: true);
        Console.Error.WriteLine(sentence is null ? $"cslq: {message}" : $"cslq: {message}; {sentence}");
    }

    /// <summary>
    /// A failure, in whichever mode was asked for. The human line goes to stderr always, so a
    /// log still reads; <c>--json</c> adds a single <c>{ "error": ... }</c> object on stdout
    /// carrying the same message. That object is the discriminator a caller needs: an answer
    /// envelope never has an <c>error</c> key and an error object never has <c>count</c>, so
    /// one field separates the two shapes without inspecting the exit code.
    /// </summary>
    public static void WriteError(string message, bool json)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { error = message }, JsonOut));
        Console.Error.WriteLine("cslq: " + message);
    }

    /// <summary>
    /// Diagnostics follow the same rules as locations: repo-relative path, one-based
    /// line and column, the offending line and a line of context either side.
    /// </summary>
    public static async Task WriteDiagnosticsAsync(
        string root,
        IReadOnlyList<Report> findings,
        int max,
        int context,
        bool json,
        Documents documents,
        ContextNote? note = null)
    {
        var labelled = new List<Finding>(findings.Count);
        foreach (var finding in findings)
        {
            labelled.Add(new Finding(
                finding.Uri,
                await PathUri.DisplayAsync(root, finding.Uri, documents),
                finding.Diagnostic,
                Only(finding)));
        }

        var hits = labelled
            .OrderBy(f => f.Display, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Diagnostic.Range.Start.Line)
            .ThenBy(f => f.Diagnostic.Range.Start.Character)
            .ToList();

        var shown = hits.Take(max).ToList();

        if (json)
        {
            var payload = new List<object>(shown.Count);
            foreach (var hit in shown)
            {
                var range = hit.Diagnostic.Range;
                payload.Add(new
                {
                    path = hit.Display,
                    line = range.Start.Line + 1,
                    column = range.Start.Character + 1,
                    endLine = range.End.Line + 1,
                    endColumn = range.End.Character + 1,
                    severity = Severity(hit.Diagnostic.Severity),
                    code = Code(hit.Diagnostic.Code),
                    message = hit.Diagnostic.Message,
                    generated = PathUri.IsGenerated(hit.Uri),
                    metadata = PathUri.IsDecompiled(hit.Uri),
                    external = PathUri.IsExternal(root, hit.Uri),
                    tfm = hit.Only,
                    text = At(await documents.Lines(hit.Uri), range.Start.Line)?.TrimEnd(),
                });
            }

            Console.WriteLine(JsonSerializer.Serialize(
                Envelope(hits.Count, hits.Count > shown.Count, payload, note),
                JsonOut));
            return;
        }

        if (shown.Count == 0)
        {
            Console.WriteLine("no diagnostics");
            WriteContextNote(note, empty: true);
            return;
        }

        var first = true;
        foreach (var hit in shown)
        {
            if (!first) Console.WriteLine();
            first = false;

            var start = hit.Diagnostic.Range.Start;
            var code = Code(hit.Diagnostic.Code);
            var label = code is null ? Severity(hit.Diagnostic.Severity) : $"{Severity(hit.Diagnostic.Severity)} {code}";
            var only = hit.Only is null ? string.Empty : $" [{hit.Only}]";
            Console.WriteLine(
                $"{hit.Display}:{start.Line + 1}:{start.Character + 1} {label}: {hit.Diagnostic.Message}{only}");

            var lines = await documents.Lines(hit.Uri);
            var width = (start.Line + 1 + context).ToString().Length;
            for (var i = Math.Max(0, start.Line - context); i <= Math.Min(lines.Length - 1, start.Line + context); i++)
            {
                var marker = i == start.Line ? ">" : " ";
                var column = i == start.Line ? start.Character + 1 : (int?)null;
                Console.WriteLine(
                    $"{marker} {(i + 1).ToString().PadLeft(width)} | {Body(lines[i], column)}");
            }
        }

        if (hits.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"... {hits.Count - shown.Count} more (use --max {hits.Count} to see all)");
        }

        WriteContextNote(note, empty: false);
    }

    /// <summary>
    /// The contexts a finding is <em>only</em> in, or null when every context asked reports it
    /// — which is every finding in a single-context document, so an ordinary <c>diag</c> row
    /// is unchanged. A row carrying a framework is what makes a conditional finding visible: a CS0029 that exists in
    /// <c>net9.0</c> and nowhere else used never to be reported at all.
    /// </summary>
    private static string? Only(Report report) =>
        report.Contexts < 2 || report.In.Count >= report.Contexts
            ? null
            : string.Join(", ", report.In);

    /// <summary>
    /// A search result set is a list of places to go, not a place to read, so this is the
    /// renderer that prints no source line and no <c>&gt;</c> marker: <c>--context</c> is
    /// inert for it. Everything else in DESIGN.md's output rules still holds — root-relative
    /// path, one-based line and column, capped by <paramref name="max"/>, the same JSON
    /// envelope. No <c>|</c> appears in a row, unlike an outline's gutter, so a probe case
    /// can quote one whole. containerName is Roslyn's localised display text ("in Greeter
    /// (project Core (net10.0))"), not a namespace path; it is rendered because it is the
    /// only thing separating two symbols that share a name, and never asserted on, because
    /// DOTNET_CLI_UI_LANGUAGE pins its language but nothing pins its shape. The cap is a
    /// relevance cut against <paramref name="query"/> — the text the caller typed — computed
    /// here rather than inherited from the server: see <see cref="ShownAsync"/>, which the
    /// candidate listings share so that a listed row and a <c>sym</c> row are the same row.
    /// </summary>
    public static async Task WriteSymbolsAsync(
        string root,
        string query,
        IReadOnlyList<SymbolInformation> symbols,
        int max,
        bool json,
        Documents documents)
    {
        var rows = symbols.Select(s => new SymbolRow(s, s.Kind)).ToList();
        var shown = await ShownAsync(root, query, rows, max, documents);

        if (json)
        {
            var payload = shown.Select(h => new
            {
                name = h.Row.Symbol.Name,
                kind = Kind(h.Row.Kind),
                container = h.Row.Symbol.ContainerName,
                path = h.Display,
                line = h.Row.Symbol.Location.Range.Start.Line + 1,
                column = h.Row.Symbol.Location.Range.Start.Character + 1,
                generated = PathUri.IsGenerated(h.Row.Symbol.Location.Uri),
                metadata = PathUri.IsDecompiled(h.Row.Symbol.Location.Uri),
                external = PathUri.IsExternal(root, h.Row.Symbol.Location.Uri),
            });

            Console.WriteLine(JsonSerializer.Serialize(
                new { count = symbols.Count, truncated = symbols.Count > shown.Count, results = payload },
                JsonOut));
            return;
        }

        if (shown.Count == 0)
        {
            WriteMissing("no results");
            return;
        }

        foreach (var row in Rows(shown)) Console.WriteLine(row);

        if (symbols.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"... {symbols.Count - shown.Count} more (use --max {symbols.Count} to see all)");
        }
    }

    /// <summary>
    /// A candidate listing, in <c>sym</c>'s row shape and its order, indented two spaces and
    /// followed by the same truncation footer. Returned rather than printed because the two
    /// listings that use it -- an ambiguous target and the <c>candidates:</c> dump of a target
    /// that matched nothing -- are carried in a <see cref="CslqException"/> message. Every row
    /// is therefore a <c>path:line:col</c> a caller can paste straight back as a target, which
    /// is the whole point of being shown the candidates.
    /// <para>
    /// <paramref name="query"/> is the bare name the caller typed -- the listings' hits are
    /// fuzzy, so relevance is what decides which survive the cap.
    /// <see cref="SymbolRow.Kind"/> is the kind to render, which is the symbol's own unless the
    /// caller already knows better: <c>workspace/symbol</c> reports a constructor as a method,
    /// and only a declaration chain says otherwise.
    /// </para>
    /// </summary>
    public static async Task<string> SymbolListingAsync(
        string root,
        string query,
        IReadOnlyList<SymbolRow> candidates,
        int max,
        Documents documents)
    {
        var shown = await ShownAsync(root, query, candidates, max, documents);
        var text = string.Join('\n', Rows(shown).Select(r => "  " + r));

        return candidates.Count > shown.Count
            ? text + $"\n... {candidates.Count - shown.Count} more (use --max {candidates.Count} to see all)"
            : text;
    }

    /// <summary>
    /// Rank against the query, then cut, then label, then sort for display. The cut used to be
    /// symbols.Take(max) on the premise that Roslyn answers workspace/symbol in relevance
    /// order; that holds on the fixture and was measured false on three real corpora, where
    /// the answer arrives grouped per project and per target framework with generated copies
    /// first, so a broad query's exact match fell outside --max 50. Ranking here makes the cut
    /// mean the same thing on every repository. The projection stays after the cut:
    /// PathUri.DisplayAsync costs a request per generated URI and a broad query drops most of
    /// its hits, so the tiebreak is on the raw URI rather than the label.
    /// WriteLocationsAsync labels first, deliberately: it folds rows on what they render as,
    /// and textDocument/references has no ranking to preserve.
    /// </summary>
    private static async Task<List<Match>> ShownAsync(
        string root, string query, IReadOnlyList<SymbolRow> rows, int max, Documents documents)
    {
        var ranked = rows
            .OrderBy(r => Relevance(r.Symbol.Name, query))
            .ThenBy(r => Rank(r.Symbol.Location.Uri))
            .ThenBy(r => r.Symbol.Location.Uri, StringComparer.Ordinal)
            .ThenBy(r => r.Symbol.Location.Range.Start.Line)
            .ThenBy(r => r.Symbol.Location.Range.Start.Character)
            .Take(max);

        var labelled = new List<Match>(Math.Min(max, rows.Count));
        foreach (var row in ranked)
        {
            labelled.Add(new Match(
                await PathUri.DisplayAsync(root, row.Symbol.Location.Uri, documents), row));
        }

        return labelled
            .OrderBy(h => Relevance(h.Row.Symbol.Name, query))
            .ThenBy(h => Rank(h.Row.Symbol.Location.Uri))
            .ThenBy(h => h.Display, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Row.Symbol.Location.Range.Start.Line)
            .ThenBy(h => h.Row.Symbol.Location.Range.Start.Character)
            .ToList();
    }

    // kind  name  container  path:line:col, each column padded to the widest row shown.
    private static IEnumerable<string> Rows(IReadOnlyList<Match> shown)
    {
        if (shown.Count == 0) yield break;

        var kindWidth = shown.Max(h => Kind(h.Row.Kind).Length);
        var nameWidth = shown.Max(h => h.Row.Symbol.Name.Length);
        var containerWidth = shown.Max(h => (h.Row.Symbol.ContainerName ?? string.Empty).Length);
        foreach (var hit in shown)
        {
            var start = hit.Row.Symbol.Location.Range.Start;
            yield return
                $"{Kind(hit.Row.Kind).PadRight(kindWidth)}  " +
                $"{hit.Row.Symbol.Name.PadRight(nameWidth)}  " +
                $"{(hit.Row.Symbol.ContainerName ?? string.Empty).PadRight(containerWidth)}  " +
                $"{hit.Display}:{start.Line + 1}:{start.Character + 1}";
        }
    }

    /// <summary>
    /// The one command that does not print path:line plus context per row — see DESIGN.md.
    /// An outline is already the summary, so it renders as a tree: the document path once as
    /// a header, then one row per symbol carrying that declaration's own source line,
    /// indented by nesting depth. <paramref name="max"/> caps rows over the pre-order
    /// flattening, so a truncated tree is always a prefix and no node outlives its parent.
    /// </summary>
    public static async Task WriteOutlineAsync(
        string root,
        string uri,
        IReadOnlyList<OutlineNode> symbols,
        int contexts,
        int max,
        bool json,
        Documents documents,
        ContextNote? note = null)
    {
        var display = await PathUri.DisplayAsync(root, uri, documents);
        var total = Count(symbols);
        var kept = Math.Min(max, total);
        var lines = await documents.Lines(uri);

        if (json)
        {
            var budget = kept;
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    count = total,
                    truncated = total > kept,
                    path = display,
                    generated = PathUri.IsGenerated(uri),
                    metadata = PathUri.IsDecompiled(uri),
                    external = PathUri.IsExternal(root, uri),
                    // No envelope-level tfm here, unlike the other context-bound commands:
                    // an outline is a union, and every row carries its own.
                    contexts = note?.All.Count,
                    results = Nodes(symbols, contexts, lines, ref budget),
                },
                JsonOut));
            return;
        }

        Console.WriteLine(display);
        if (total == 0)
        {
            Console.WriteLine("no symbols");
            WriteContextNote(note, empty: true);
            return;
        }

        var rows = Flatten(symbols).Take(kept).ToList();
        // A line several shown declarations start on: every one of them used to print that
        // whole line, so `public enum Colour { Red, Green, Blue }` came out four times over.
        var crowded = rows
            .GroupBy(r => r.Node.Symbol.SelectionRange.Start.Line)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();
        var gutters = rows.Select(r => Gutter(r.Node.Symbol, crowded)).ToList();
        var width = gutters.Max(g => g.Length);
        foreach (var (row, gutter) in rows.Zip(gutters))
        {
            var line = row.Node.Symbol.SelectionRange.Start.Line;
            var text = Declaration(lines, row.Node.Symbol, crowded.Contains(line));
            // The mark goes after the source line, so a declaration every context compiles
            // renders exactly as it did before contexts existed.
            var mark = Outline.Mark(row.Node, contexts) is { } tfms ? $"  [{tfms}]" : string.Empty;
            Console.WriteLine(
                $"  {gutter.PadLeft(width)} | {new string(' ', row.Depth * 2)}{text}{mark}");
        }

        if (total > kept)
        {
            Console.WriteLine();
            Console.WriteLine($"... {total - kept} more (use --max {total} to see all)");
        }

        WriteContextNote(note, empty: false);
    }

    /// <summary>
    /// The one command whose result is prose rather than a place, so it prints no context
    /// lines and no <c>&gt;</c> marker and <c>--context</c> is inert for it — a narrower
    /// exception than <c>sym</c>'s, which at least still renders rows. Everything else holds:
    /// the position the hover applies to as a root-relative one-based <c>path:line:col</c>
    /// header, and the same <c>{ count, truncated, results }</c> envelope, where
    /// <c>count</c> is 1 for a hover and 0 for none and <paramref name="max"/> caps the
    /// documentation's <em>lines</em> — the one result is never what a cap could usefully
    /// trim.
    /// </summary>
    public static async Task WriteHoverAsync(
        string root,
        string uri,
        Position position,
        Hover? hover,
        int max,
        bool json,
        Documents documents,
        ContextNote? note = null)
    {
        var value = hover?.Contents?.Value;
        var (signature, documentation) = HoverText(value);
        string[] lines = documentation.Length == 0 ? [] : documentation.Split('\n');
        var kept = lines.Take(max).ToList();
        // The header comes from the hover's own range when it has one: the server widens a
        // position to the whole identifier, which is a better answer than the column asked
        // about. A hover carries no range for a symbol it declined to describe.
        var start = hover?.Range?.Start ?? position;
        var display = await PathUri.DisplayAsync(root, uri, documents);

        if (json)
        {
            List<object> results = value is null
                ? []
                :
                [
                    new
                    {
                        path = display,
                        line = start.Line + 1,
                        column = start.Character + 1,
                        signature,
                        documentation = string.Join('\n', kept),
                        generated = PathUri.IsGenerated(uri),
                        metadata = PathUri.IsDecompiled(uri),
                        external = PathUri.IsExternal(root, uri),
                    },
                ];

            Console.WriteLine(JsonSerializer.Serialize(
                Envelope(results.Count, kept.Count < lines.Length, results, note),
                JsonOut));
            return;
        }

        if (value is null)
        {
            WriteMissing("no results", note);
            return;
        }

        Console.WriteLine($"{display}:{start.Line + 1}:{start.Character + 1}");
        Console.WriteLine(signature);
        foreach (var line in kept) Console.WriteLine(line);

        if (kept.Count < lines.Length)
        {
            Console.WriteLine();
            Console.WriteLine($"... {lines.Length - kept.Count} more (use --max {lines.Length} to see all)");
        }

        WriteContextNote(note, empty: false);
    }

    /// <summary>
    /// A hover's signature and documentation, split off the plaintext block Roslyn sends.
    /// The first non-empty line is the signature — <c>void Console.WriteLine(string? value)
    /// (+ 19 overloads)</c> — and everything after it is documentation, which for a framework
    /// member is the summary and any <c>Exceptions:</c> list. Line endings are normalised
    /// because the server sends CRLF regardless of platform, and the whole value is trimmed of
    /// the trailing newline it always carries. No markdown to strip: the client declares
    /// plaintext as its only <c>contentFormat</c>, which is what keeps fences and
    /// <c>&amp;nbsp;</c> runs out of this.
    /// </summary>
    internal static (string Signature, string Documentation) HoverText(string? value)
    {
        var text = (value ?? string.Empty).ReplaceLineEndings("\n").Trim('\n', ' ', '\t');
        if (text.Length == 0) return (string.Empty, string.Empty);

        var lines = text.Split('\n');
        var first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (first < 0) return (string.Empty, string.Empty);

        var documentation = string.Join('\n', lines.Skip(first + 1)).Trim('\n');
        return (lines[first].Trim(), documentation);
    }

    /// <summary>
    /// <c>ready</c> under <c>--json</c>. It used to print the literal <c>ready</c> either way,
    /// which broke the envelope on the one command every session runs first. Text mode still
    /// prints <c>ready</c> and nothing else — it is the cheapest possible thing for a shell to
    /// test — so this is the only command whose two modes carry different information.
    /// <para>
    /// <paramref name="projects"/> is the probed count, not the discovered one, and the two
    /// lists account for the difference: <c>projects + skipped + unprobed</c> is every project
    /// the root's solution yielded. A project absent from all three would read as loaded when
    /// nothing had checked it, which is the whole failure the per-project sentinel exists to
    /// close.
    /// </para>
    /// </summary>
    public static void WriteReady(
        int projects,
        IReadOnlyList<string> skipped,
        IReadOnlyList<string> unprobed,
        bool json)
    {
        if (!json)
        {
            Console.WriteLine("ready");
            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                count = 1,
                truncated = false,
                results = new[] { new { ready = true, projects, skipped, unprobed } },
            },
            JsonOut));
    }

    /// <summary>
    /// Where <c>cslq restore</c> restored to. The path is the tool's own manifest directory
    /// rather than anything under a workspace root, so it is absolute and not put through
    /// <see cref="PathUri"/>: there is no root for it to be relative to.
    /// </summary>
    public static void WriteRestored(string manifestRoot, Prune.Result? pruned, bool json)
    {
        if (!json)
        {
            Console.WriteLine("restored the pinned language server in " + manifestRoot);
            foreach (var line in PruneLines(pruned)) Console.WriteLine(line);
            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                count = 1,
                truncated = false,
                results = new[]
                {
                    new
                    {
                        restored = true,
                        manifest = manifestRoot,
                        packages = pruned?.Packages,
                        removed = pruned?.Removed ?? [],
                        kept = pruned?.Kept.Select(k => new { dir = k.Dir, why = k.Why }) ?? [],
                    },
                },
            },
            JsonOut));
    }

    /// <summary>
    /// What the post-restore prune did, one line per outcome, unprefixed: <c>cslq restore</c>
    /// prints them as they are and <see cref="LspClient.StartAsync"/> prefixes them for
    /// stderr. Nothing at all when nothing was there to remove.
    /// </summary>
    public static IEnumerable<string> PruneLines(Prune.Result? pruned)
    {
        if (pruned is null) yield break;
        if (pruned.Removed.Count > 0)
        {
            yield return $"removed {pruned.Removed.Count} other version(s) of the language server from "
                + $"{pruned.Packages}: {string.Join(", ", pruned.Removed)}";
        }

        foreach (var (dir, why) in pruned.Kept)
            yield return $"could not remove {dir} from {pruned.Packages}: {why} Run 'cslq restore' once nothing is using it.";
    }

    /// <summary>
    /// The project a file is compiled by, and the target framework it is compiled for. Exits
    /// through the same envelope as everything else; both paths are rendered through
    /// <see cref="PathUri.Display(string, string, string?, string?)"/> like every other
    /// location, so the decompiled and generated branches apply here too rather than being
    /// bypassed by a raw <c>Relative</c>. The row carries <c>generated</c> and
    /// <c>metadata</c> for the same reason every other row does: a caller never has to parse
    /// a label to know what kind of place it names.
    /// </summary>
    /// <summary>
    /// One row per project context, in <see cref="Contexts.Order"/>'s order, because a
    /// multi-targeted document is compiled several times and <c>count: 1</c> claimed
    /// otherwise. <c>--max</c> applies for the same reason the envelope carries
    /// <c>truncated</c>; a linked file in a 16-context solution is the shape that needs it.
    /// </summary>
    public static void WriteProject(
        string root, string path, IReadOnlyList<DocumentContext> contexts, int max, bool json)
    {
        var display = PathUri.Display(root, PathUri.FromPath(path));
        var external = PathUri.IsExternal(root, PathUri.FromPath(path));
        var rows = contexts
            .Select(c => (Project: PathUri.Display(root, PathUri.FromPath(c.File)), c.Tfm))
            .ToList();
        var shown = rows.Take(max).ToList();

        if (json)
        {
            var results = shown
                .Select(r => new
                {
                    path = display,
                    project = r.Project,
                    tfm = r.Tfm,
                    generated = false,
                    metadata = false,
                    external = external,
                })
                .ToList();
            Console.WriteLine(JsonSerializer.Serialize(
                new { count = rows.Count, truncated = rows.Count > shown.Count, results },
                JsonOut));
            return;
        }

        if (shown.Count == 0)
        {
            WriteMissing("no project");
            return;
        }

        foreach (var row in shown)
        {
            Console.WriteLine(row.Tfm is null ? row.Project : $"{row.Project}  {row.Tfm}");
        }

        if (rows.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"... {rows.Count - shown.Count} more (use --max {rows.Count} to see all)");
        }
    }

    /// <summary>
    /// An outline row's gutter: the declaration's line, and its column too when it shares
    /// that line with another shown declaration. The column is the identifier's, the same
    /// one <c>--json</c> reports and the same one every other command prints, so a crowded
    /// row is still a <c>line:col</c> a caller can paste back. An uncrowded document never
    /// grows the column and renders exactly as it always did, which is the whole value of an
    /// outline.
    /// </summary>
    internal static string Gutter(DocumentSymbol symbol, IReadOnlySet<int> crowded)
    {
        var start = symbol.SelectionRange.Start;
        return crowded.Contains(start.Line)
            ? $"{start.Line + 1}:{start.Character + 1}"
            : (start.Line + 1).ToString();
    }

    /// <summary>
    /// What an outline row prints for a declaration: its own source line when it has that
    /// line to itself, and its own <em>span</em> of the line when it does not.
    /// <para>
    /// The span is the declaration's <c>range</c> clipped to the identifier's line -- the
    /// whole extent where it fits, the rest of the line where the body runs on -- so
    /// <c>public enum Colour { Red, Green, Blue }</c> outlines as itself followed by
    /// <c>Red</c>, <c>Green</c> and <c>Blue</c> rather than as itself four times. A
    /// multi-declarator field is the same shape: <c>First</c> and <c>Second</c> print their
    /// own declarators.
    /// </para>
    /// <para>
    /// Elided the same way every other source line is, and around the identifier column,
    /// since a declaration can also be the long line -- see <see cref="Elide"/>.
    /// </para>
    /// </summary>
    internal static string Declaration(string[] lines, DocumentSymbol symbol, bool crowded)
    {
        var identifier = symbol.SelectionRange.Start;
        if (At(lines, identifier.Line) is not { } line) return symbol.Name;

        // The declaration's own extent, clipped to this line: it starts where the
        // declaration does when that is on this line and at the margin otherwise, and ends
        // where the declaration does when that is on this line and at the end of it
        // otherwise. An uncrowded row takes the whole line, as it always did.
        var start = crowded && symbol.Range.Start.Line == identifier.Line
            ? Math.Clamp(symbol.Range.Start.Character, 0, line.Length)
            : 0;
        var end = crowded && symbol.Range.End.Line == identifier.Line
            ? Math.Clamp(symbol.Range.End.Character, start, line.Length)
            : line.Length;

        var trimmed = line[start..end].TrimEnd();
        var lead = trimmed.Length - trimmed.TrimStart().Length;
        var body = trimmed.TrimStart();
        if (body.Length == 0) return symbol.Name;

        // The identifier's column inside the text actually printed, so the elision window
        // lands on it rather than on a column counted from a different origin.
        var column = identifier.Character + 1 - start - lead;
        return Elide(body, column > 0 ? column : null);
    }

    private static int Count(IReadOnlyList<OutlineNode> symbols) =>
        symbols.Sum(s => 1 + Count(s.Children));

    private static IEnumerable<(OutlineNode Node, int Depth)> Flatten(
        IReadOnlyList<OutlineNode> symbols, int depth = 0)
    {
        foreach (var symbol in symbols)
        {
            yield return (symbol, depth);
            foreach (var child in Flatten(symbol.Children, depth + 1)) yield return child;
        }
    }

    // Pruned against the same pre-order budget the text form uses, so --max means the same
    // thing in both and a JSON case and a text case stay cases about the same output.
    private static List<object> Nodes(
        IReadOnlyList<OutlineNode> symbols, int contexts, string[] lines, ref int budget,
        string? parent = null)
    {
        var nodes = new List<object>();
        foreach (var node in symbols)
        {
            if (budget <= 0) break;
            budget--;

            var symbol = node.Symbol;
            var start = symbol.SelectionRange.Start;
            var end = symbol.SelectionRange.End;
            nodes.Add(new
            {
                name = symbol.Name,
                kind = Kinds.Name(Kinds.Of(symbol.Kind, symbol.Name, parent)),
                detail = symbol.Detail,
                line = start.Line + 1,
                column = start.Character + 1,
                endLine = end.Line + 1,
                endColumn = end.Character + 1,
                tfm = Outline.Mark(node, contexts),
                text = At(lines, start.Line)?.TrimEnd(),
                children = Nodes(node.Children, contexts, lines, ref budget, symbol.Name),
            });
        }

        return nodes;
    }

    // One table for every command -- see Kinds, which is also where the two normalisations
    // that make sym and outline agree about a delegate and a constructor live.
    private static string Kind(int kind) => Kinds.Name(Kinds.Normalise(kind));

    public static string Severity(int? severity) => severity switch
    {
        1 => "error",
        2 => "warning",
        3 => "info",
        4 => "hint",
        // Absent means "as the client sees fit"; treat it as the worst case rather than
        // hiding it from --errors-only.
        _ => "error",
    };

    // string-or-int per the spec, and Roslyn's own analyzers are free to use either.
    internal static string? Code(JsonElement code) => code.ValueKind switch
    {
        JsonValueKind.String => code.GetString(),
        JsonValueKind.Number => code.ToString(),
        _ => null,
    };

    // How well a symbol's name answers what the caller typed: 0 exact, 1 prefix, 2 substring,
    // 3 anything else -- the server's own documented ranking, computed here because the answer
    // does not arrive in it. Case-insensitive, like the query itself.
    private static int Relevance(string name, string query) =>
        name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
        : name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
        : name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2
        : 3;

    // 0 = an ordinary file, 1 = source-generated, 2 = decompiled metadata.
    internal static int Rank(string uri) =>
        PathUri.IsGenerated(uri) ? 1 : PathUri.IsDecompiled(uri) ? 2 : 0;

    private static string? At(string[] lines, int zeroBased) =>
        zeroBased >= 0 && zeroBased < lines.Length ? lines[zeroBased] : null;

    /// <summary>
    /// How much of one source line text mode will print. A hit on a 20,079-character line put
    /// the whole line in the answer -- for one result -- which is the context window the
    /// output rules exist to protect, spent on a line no reader was going to read. 200 is
    /// wider than any hand-written C# line and narrow enough that a generated or minified one
    /// cannot dominate.
    /// </summary>
    internal const int LineBudget = 200;

    /// <summary>
    /// One source line, cut to <see cref="LineBudget"/> around the interesting column.
    /// <para>
    /// <paramref name="column"/> is the one-based column the row is *about* -- a hit, a
    /// diagnostic's start, a declaration's identifier -- and the window is centred on it, so
    /// the thing the caller asked about is always in view. A context line has no such column
    /// and keeps its head instead: the start of a statement is what says what it is.
    /// </para>
    /// <para>
    /// The elision is display only, and this is the part a caller has to know: the
    /// <c>path:line:col</c> above the row is untouched and still refers to the real line, so
    /// it pastes back as a target unchanged, while a column counted off the printed text is
    /// meaningless. <c>--json</c> carries the line whole for exactly that reason. Each cut
    /// end is marked with a horizontal ellipsis, so a row that was elided says so.
    /// </para>
    /// </summary>
    internal static string Elide(string text, int? column, int budget = LineBudget)
    {
        if (text.Length <= budget) return text;
        if (column is not { } col) return text[..budget] + "…";

        var hit = Math.Clamp(col - 1, 0, text.Length);
        var start = Math.Clamp(hit - (budget / 2), 0, text.Length - budget);
        var end = start + budget;
        return (start > 0 ? "…" : string.Empty)
            + text[start..end]
            + (end < text.Length ? "…" : string.Empty);
    }

    /// <summary>
    /// A source line as a location row prints it: trailing whitespace gone, then elided
    /// around <paramref name="column"/>. Trailing only -- trimming the indentation off would
    /// move every column and the window is placed by column.
    /// </summary>
    private static string Body(string text, int? column) => Elide(text.TrimEnd(), column);

    private sealed record Hit(string Uri, string Display, Range Range);

    private sealed record Finding(string Uri, string Display, Diagnostic Diagnostic, string? Only);

    private sealed record Match(string Display, SymbolRow Row);
}
