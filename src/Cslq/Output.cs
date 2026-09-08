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
        Documents documents)
    {
        var labelled = new List<Hit>(locations.Count);
        foreach (var location in locations)
        {
            labelled.Add(new Hit(
                location.Uri, await PathUri.DisplayAsync(root, location.Uri, documents), location.Range));
        }

        var hits = labelled
            .OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase)
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
                    text = At(await documents.Lines(hit.Uri), hit.Range.Start.Line)?.TrimEnd(),
                });
            }

            Console.WriteLine(JsonSerializer.Serialize(
                new { count = hits.Count, truncated = hits.Count > shown.Count, results = payload },
                JsonOut));
            return;
        }

        if (shown.Count == 0)
        {
            Console.WriteLine("no results");
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
                Console.WriteLine($"{marker} {(i + 1).ToString().PadLeft(width)} | {lines[i].TrimEnd()}");
            }
        }

        if (hits.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"... {hits.Count - shown.Count} more (use --max {hits.Count} to see all)");
        }
    }

    /// <summary>
    /// Diagnostics follow the same rules as locations: repo-relative path, one-based
    /// line and column, the offending line and a line of context either side.
    /// </summary>
    public static async Task WriteDiagnosticsAsync(
        string root,
        IReadOnlyList<(string Uri, Diagnostic Diagnostic)> findings,
        int max,
        int context,
        bool json,
        Documents documents)
    {
        var labelled = new List<Finding>(findings.Count);
        foreach (var finding in findings)
        {
            labelled.Add(new Finding(
                finding.Uri, await PathUri.DisplayAsync(root, finding.Uri, documents), finding.Diagnostic));
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
                    source = hit.Diagnostic.Source,
                    message = hit.Diagnostic.Message,
                    generated = PathUri.IsGenerated(hit.Uri),
                    metadata = PathUri.IsDecompiled(hit.Uri),
                    text = At(await documents.Lines(hit.Uri), range.Start.Line)?.TrimEnd(),
                });
            }

            Console.WriteLine(JsonSerializer.Serialize(
                new { count = hits.Count, truncated = hits.Count > shown.Count, results = payload },
                JsonOut));
            return;
        }

        if (shown.Count == 0)
        {
            Console.WriteLine("no diagnostics");
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
            Console.WriteLine($"{hit.Display}:{start.Line + 1}:{start.Character + 1} {label}: {hit.Diagnostic.Message}");

            var lines = await documents.Lines(hit.Uri);
            var width = (start.Line + 1 + context).ToString().Length;
            for (var i = Math.Max(0, start.Line - context); i <= Math.Min(lines.Length - 1, start.Line + context); i++)
            {
                var marker = i == start.Line ? ">" : " ";
                Console.WriteLine($"{marker} {(i + 1).ToString().PadLeft(width)} | {lines[i].TrimEnd()}");
            }
        }

        if (hits.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"... {hits.Count - shown.Count} more (use --max {hits.Count} to see all)");
        }
    }

    /// <summary>
    /// A search result set is a list of places to go, not a place to read, so this is the
    /// renderer that prints no source line and no <c>&gt;</c> marker: <c>--context</c> is
    /// inert for it. Everything else in DESIGN.md's output rules still holds — root-relative
    /// path, one-based line and column, capped by <paramref name="max"/>, the same JSON
    /// envelope. No <c>|</c> appears in a row, unlike an outline's gutter, so a probe case
    /// can quote one whole. containerName is Roslyn's localised display text ("in Greeter
    /// (project Core (net10.0))"), not a namespace path; it is rendered because it is the
    /// only thing separating two symbols that share a name, and never asserted on, because
    /// DOTNET_CLI_UI_LANGUAGE pins its language but nothing pins its shape. The cap applies
    /// to the server's relevance order and the display sort is cosmetic -- see below.
    /// </summary>
    public static async Task WriteSymbolsAsync(
        string root,
        IReadOnlyList<SymbolInformation> symbols,
        int max,
        bool json,
        Documents documents)
    {
        // Truncate first, then sort: Roslyn answers workspace/symbol in relevance order --
        // exact, then prefix, then substring, across every project -- and Distinct's DistinctBy
        // keeps first-seen order, so that ranking arrives here intact. Sorting before the cap
        // would keep an alphabetical prefix of the hits rather than the best matches. Taking
        // before the projection is also what keeps PathUri.Display off the hits that are
        // dropped, which on a broad query is most of them. WriteLocationsAsync sorts first,
        // deliberately: textDocument/references has no ranking to preserve.
        var labelled = new List<Match>(Math.Min(max, symbols.Count));
        foreach (var symbol in symbols.Take(max))
        {
            labelled.Add(new Match(
                await PathUri.DisplayAsync(root, symbol.Location.Uri, documents), symbol));
        }

        var shown = labelled
            .OrderBy(h => h.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Display, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Symbol.Location.Range.Start.Line)
            .ThenBy(h => h.Symbol.Location.Range.Start.Character)
            .ToList();

        if (json)
        {
            var payload = shown.Select(h => new
            {
                name = h.Symbol.Name,
                kind = Kind(h.Symbol.Kind),
                container = h.Symbol.ContainerName,
                path = h.Display,
                line = h.Symbol.Location.Range.Start.Line + 1,
                column = h.Symbol.Location.Range.Start.Character + 1,
                generated = PathUri.IsGenerated(h.Symbol.Location.Uri),
                metadata = PathUri.IsDecompiled(h.Symbol.Location.Uri),
            });

            Console.WriteLine(JsonSerializer.Serialize(
                new { count = symbols.Count, truncated = symbols.Count > shown.Count, results = payload },
                JsonOut));
            return;
        }

        if (shown.Count == 0)
        {
            Console.WriteLine("no results");
            return;
        }

        var kindWidth = shown.Max(h => Kind(h.Symbol.Kind).Length);
        var nameWidth = shown.Max(h => h.Symbol.Name.Length);
        var containerWidth = shown.Max(h => (h.Symbol.ContainerName ?? string.Empty).Length);
        foreach (var hit in shown)
        {
            var start = hit.Symbol.Location.Range.Start;
            var row =
                $"{Kind(hit.Symbol.Kind).PadRight(kindWidth)}  " +
                $"{hit.Symbol.Name.PadRight(nameWidth)}  " +
                $"{(hit.Symbol.ContainerName ?? string.Empty).PadRight(containerWidth)}  " +
                $"{hit.Display}:{start.Line + 1}:{start.Character + 1}";
            Console.WriteLine(row);
        }

        if (symbols.Count > shown.Count)
        {
            Console.WriteLine();
            Console.WriteLine($"... {symbols.Count - shown.Count} more (use --max {symbols.Count} to see all)");
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
        IReadOnlyList<DocumentSymbol> symbols,
        int max,
        bool json,
        Documents documents)
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
                    results = Nodes(symbols, lines, ref budget),
                },
                JsonOut));
            return;
        }

        Console.WriteLine(display);
        if (total == 0)
        {
            Console.WriteLine("no symbols");
            return;
        }

        var rows = Flatten(symbols).Take(kept).ToList();
        var width = rows.Max(r => r.Symbol.SelectionRange.Start.Line + 1).ToString().Length;
        foreach (var row in rows)
        {
            var line = row.Symbol.SelectionRange.Start.Line;
            var text = At(lines, line)?.Trim() ?? row.Symbol.Name;
            Console.WriteLine($"  {(line + 1).ToString().PadLeft(width)} | {new string(' ', row.Depth * 2)}{text}");
        }

        if (total > kept)
        {
            Console.WriteLine();
            Console.WriteLine($"... {total - kept} more (use --max {total} to see all)");
        }
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
        Documents documents)
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
                    },
                ];

            Console.WriteLine(JsonSerializer.Serialize(
                new { count = results.Count, truncated = kept.Count < lines.Length, results },
                JsonOut));
            return;
        }

        if (value is null)
        {
            Console.WriteLine("no results");
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
    /// </summary>
    public static void WriteReady(int projects, bool json)
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
                results = new[] { new { ready = true, projects } },
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
    public static void WriteProject(string root, string path, string? project, string? tfm, bool json)
    {
        var display = project is null ? null : PathUri.Display(root, PathUri.FromPath(project));

        if (json)
        {
            List<object> results = display is null
                ? []
                :
                [
                    new
                    {
                        path = PathUri.Display(root, PathUri.FromPath(path)),
                        project = display,
                        tfm,
                        generated = false,
                        metadata = false,
                    },
                ];
            Console.WriteLine(JsonSerializer.Serialize(
                new { count = results.Count, truncated = false, results },
                JsonOut));
            return;
        }

        Console.WriteLine(display is null
            ? "no project"
            : tfm is null ? display : $"{display}  {tfm}");
    }

    private static int Count(IReadOnlyList<DocumentSymbol> symbols) =>
        symbols.Sum(s => 1 + Count(s.Children ?? []));

    private static IEnumerable<(DocumentSymbol Symbol, int Depth)> Flatten(
        IReadOnlyList<DocumentSymbol> symbols, int depth = 0)
    {
        foreach (var symbol in symbols)
        {
            yield return (symbol, depth);
            foreach (var child in Flatten(symbol.Children ?? [], depth + 1)) yield return child;
        }
    }

    // Pruned against the same pre-order budget the text form uses, so --max means the same
    // thing in both and a JSON case and a text case stay cases about the same output.
    private static List<object> Nodes(IReadOnlyList<DocumentSymbol> symbols, string[] lines, ref int budget)
    {
        var nodes = new List<object>();
        foreach (var symbol in symbols)
        {
            if (budget <= 0) break;
            budget--;

            var start = symbol.SelectionRange.Start;
            var end = symbol.SelectionRange.End;
            nodes.Add(new
            {
                name = symbol.Name,
                kind = Kind(symbol.Kind),
                detail = symbol.Detail,
                line = start.Line + 1,
                column = start.Character + 1,
                endLine = end.Line + 1,
                endColumn = end.Character + 1,
                text = At(lines, start.Line)?.TrimEnd(),
                children = Nodes(symbol.Children ?? [], lines, ref budget),
            });
        }

        return nodes;
    }

    // LSP SymbolKind. An unmapped value renders as its integer rather than "unknown": the
    // server is free to add kinds, and dropping the one fact we have helps nobody.
    private static string Kind(int kind) => kind switch
    {
        1 => "file",
        2 => "module",
        3 => "namespace",
        4 => "package",
        5 => "class",
        6 => "method",
        7 => "property",
        8 => "field",
        9 => "constructor",
        10 => "enum",
        11 => "interface",
        12 => "function",
        13 => "variable",
        14 => "constant",
        15 => "string",
        16 => "number",
        17 => "boolean",
        18 => "array",
        19 => "object",
        20 => "key",
        21 => "null",
        22 => "enumMember",
        23 => "struct",
        24 => "event",
        25 => "operator",
        26 => "typeParameter",
        _ => kind.ToString(),
    };

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
    private static string? Code(JsonElement code) => code.ValueKind switch
    {
        JsonValueKind.String => code.GetString(),
        JsonValueKind.Number => code.ToString(),
        _ => null,
    };

    private static string? At(string[] lines, int zeroBased) =>
        zeroBased >= 0 && zeroBased < lines.Length ? lines[zeroBased] : null;

    private sealed record Hit(string Uri, string Display, Range Range);

    private sealed record Finding(string Uri, string Display, Diagnostic Diagnostic);

    private sealed record Match(string Display, SymbolInformation Symbol);
}
