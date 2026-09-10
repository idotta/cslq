using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Cslq;

internal static partial class Program
{
    internal const string Usage = """
        cslq - semantic C# queries over the official roslyn-language-server

        usage:
          cslq ready   [--sentinel <symbol>]
          cslq refs    <symbol | file:line:col> [--max N] [--context N] [--tfm T]
          cslq def     <symbol | file:line:col> [--max N] [--context N] [--tfm T]
          cslq impl    <symbol | file:line:col> [--max N] [--context N] [--tfm T]
          cslq hover   <symbol | file:line:col> [--max N] [--tfm T]
          cslq sym     <query> [--max N]
          cslq outline <file | symbol> [--max N] [--tfm T]
          cslq diag    [path] [--errors-only] [--max N] [--context N] [--tfm T]
          cslq project <file> [--tfm T]
          cslq restore

        options:
          --root <dir>      workspace root (default: current directory)
          --sentinel <sym>  readiness probe symbol (default: inferred from the workspace)
          --max N           cap results (default: 50)
          --context N       source lines either side of a hit (default: 1; unused by outline, sym, hover)
          --timeout N       seconds to wait for workspace load (default: 180)
          --log-level L     server log level (default: Warning)
          --tfm T           answer in this target framework's context only
          --errors-only     diag: drop warnings and below
          --json            machine-readable output
          --no-daemon       start a dedicated server instead of the shared daemon
          --version         print the cslq version
          -h, --help        this message
        """;

    internal static readonly string[] Commands =
        ["ready", "refs", "def", "impl", "hover", "sym", "outline", "diag", "project", "restore"];

    // `outline` is here because it accepts a position too — see OutlineTargetAsync. `sym`
    // takes a free-text query and `diag` and `project` a path, none of which is
    // position-shaped.
    private static readonly string[] TakesPosition = ["refs", "def", "impl", "hover", "outline"];

    private static async Task<int> Main(string[] argv)
    {
        try
        {
            return await RunAsync(argv);
        }
        catch (CslqException ex)
        {
            Console.Error.WriteLine("cslq: " + ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C. Without this the cancellation escapes as an unhandled exception and the
            // interrupt is answered with a stack trace and exit 134.
            Console.Error.WriteLine("cslq: interrupted.");
            return 130;
        }
    }

    /// <summary>
    /// <c>projects</c> counts the projects readiness actually probed, and the two classes it
    /// could not probe are named rather than counted as if they had been. Both are listed for
    /// the same reason: <c>projects + skipped + unprobed</c> equalling the discovered project
    /// count is what lets a caller see that readiness was partial, and a project missing from
    /// all three would read as "loaded". The stderr line carries the same fact for text mode,
    /// which prints only <c>ready</c> by design — behind <c>--log-level Information</c>
    /// because the ordinary workspace has neither class, and a line on every run would train
    /// a caller to ignore it.
    /// </summary>
    private static int Ready(Options opts, IReadOnlyList<Sentinel> sentinels)
    {
        // The probe an explicit `--sentinel` adds is not a project, so none of the three
        // numbers may see it: counted, it would put the total one above the project count a
        // caller can check it against.
        var projects = sentinels.Where(s => !s.Explicit).ToList();
        var skipped = RelativeDirectories(opts.Root, projects.Where(s => s.Skipped));
        var unprobed = RelativeDirectories(
            opts.Root, projects.Where(s => s.Candidates.Count == 0 && !s.Skipped));

        var clauses = new List<string>();
        if (skipped.Count > 0)
        {
            clauses.Add("no sources of their own: " + string.Join(", ", skipped));
        }

        if (unprobed.Count > 0)
        {
            clauses.Add("no type declaration found: " + string.Join(", ", unprobed));
        }

        if (clauses.Count > 0 && opts.Verbose)
        {
            Console.Error.WriteLine("cslq: not probed — " + string.Join("; ", clauses));
        }

        Output.WriteReady(
            projects.Count(s => s.Candidates.Count > 0), skipped, unprobed, opts.Json);
        return 0;
    }

    private static IReadOnlyList<string> RelativeDirectories(
        string root, IEnumerable<Sentinel> sentinels) =>
        [.. sentinels.Select(s => PathUri.Relative(root, s.Directory))];

    private static async Task<int> RunAsync(string[] argv)
    {
        switch (Preflight(argv))
        {
            case Immediate.Usage: Console.WriteLine(Usage); return 2;
            case Immediate.Help: Console.WriteLine(Usage); return 0;
            case Immediate.Version: Console.WriteLine(Build.Version); return 0;
        }

        var opts = Options.Parse(argv);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Above both the workspace scan and the server launch, which is the whole point of
        // the command: a Dockerfile or a CI job pre-warming the ~300 MB pin has no solution
        // to point at, and project discovery would fail it for the absence.
        if (opts.Command == "restore") return await RestoreAsync(opts, cts.Token);

        // Before the server starts, like the argument checks in Options.Parse: this is a
        // filesystem scan, and a root with no project in it should say so instantly rather
        // than after a cold load.
        var sentinels = Sentinels(opts);

        await using var client = await LspClient.StartAsync(opts.Root, opts.LogLevel, opts.Daemon, cts.Token);

        try
        {
            return await DispatchAsync(client, opts, sentinels, cts.Token);
        }
        finally
        {
            // Only reached with the daemon asked for, so this says the daemon was unreachable,
            // not that it was declined.
            if (opts.Daemon && client.DaemonFallback)
            {
                Console.Error.WriteLine(
                    "cslq: daemon unreachable; this run used its own cold server");
            }
        }
    }

    internal enum Immediate { None, Usage, Help, Version }

    /// <summary>
    /// Asking for help or the version is answered wherever it appears, not only in the first
    /// position: `cslq refs Foo --help` used to be `unknown option`, which is the moment a
    /// caller most needs the usage text. Nothing here reaches a server, so a query argument
    /// that happens to be `--help` is a cost worth paying.
    /// </summary>
    internal static Immediate Preflight(string[] argv)
    {
        if (argv.Length == 0) return Immediate.Usage;
        if (argv.Any(a => a is "-h" or "--help")) return Immediate.Help;
        if (argv.Any(a => a is "--version")) return Immediate.Version;
        return Immediate.None;
    }

    private static async Task<int> DispatchAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        switch (opts.Command)
        {
            case "ready":
                await client.WaitReadyAsync(sentinels, opts.Timeout, ct);
                return Ready(opts, sentinels);

            case "refs":
                return await RefsAsync(client, opts, sentinels, ct);

            case "def":
                return await DefAsync(client, opts, sentinels, ct);

            case "impl":
                return await ImplAsync(client, opts, sentinels, ct);

            case "sym":
                return await SymAsync(client, opts, sentinels, ct);

            case "outline":
                return await OutlineAsync(client, opts, sentinels, ct);

            case "diag":
                return await DiagAsync(client, opts, sentinels, ct);

            case "hover":
                return await HoverAsync(client, opts, sentinels, ct);

            case "project":
                return await ProjectAsync(client, opts, sentinels, ct);

            // `restore` never reaches here: RunAsync answers it before a client exists.

            default:
                throw new System.Diagnostics.UnreachableException(
                    $"Options.Parse admitted '{opts.Command}'");
        }
    }

    /// <summary>
    /// Pays the one-time server download on demand, so the first real query does not.
    /// <see cref="LspClient.StartAsync"/> already restores on the failure it recognises;
    /// this is the same restore asked for deliberately, with no workspace, no server and
    /// nothing to be ready for. A failure is a <c>CslqException</c> like any other.
    /// </summary>
    private static async Task<int> RestoreAsync(Options opts, CancellationToken ct)
    {
        var manifestRoot = ServerArgs.ToolManifestRoot();
        var pruned = await LspClient.RestoreAsync(manifestRoot, ct);
        Output.WriteRestored(manifestRoot, pruned, opts.Json);
        return 0;
    }

    private static async Task<int> RefsAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CslqException("refs needs a symbol or file:line:col");

        CheckTarget(opts.Root, target);

        // Gate on a sentinel that must exist, never on the symbol being asked about:
        // otherwise a genuinely absent symbol is indistinguishable from a workspace that
        // has not finished loading, and the caller waits out the whole timeout for it.
        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, opts.Max, ct);
        var (all, asked) = await ContextsAsync(client, opts, uri, ct);
        var locations = await client.ReferencesAsync(uri, position, asked, ct);
        await Output.WriteLocationsAsync(
            opts.Root, locations, opts.Max, opts.Context, opts.Json, Documents.Of(client, ct),
            Union(all, asked));
        return locations.Count == 0 ? 1 : 0;
    }

    private static async Task<int> DefAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CslqException("def needs a symbol or file:line:col");

        CheckTarget(opts.Root, target);
        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, opts.Max, ct);
        var (all, asked) = await ContextsAsync(client, opts, uri, ct);
        var answer = await client.DefinitionAsync(uri, position, asked, ct);
        await Output.WriteLocationsAsync(
            opts.Root, answer.Value, opts.Max, opts.Context, opts.Json, Documents.Of(client, ct),
            new ContextNote(all, asked, answer.Context));
        return answer.Value.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// Roslyn does not answer empty for a member that has no implementations — it falls
    /// through to the declaration, exactly as <c>definition</c> does when fired at one. So
    /// <c>impl</c> on an ordinary method is <c>def</c>, and the empty result this exits 1 on
    /// means the position resolved to no symbol at all, not that nothing implements the
    /// symbol. Verified on the wire against 5.12.0-1.26426.8.
    /// </summary>
    private static async Task<int> ImplAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CslqException("impl needs a symbol or file:line:col");

        CheckTarget(opts.Root, target);
        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, opts.Max, ct);
        var (all, asked) = await ContextsAsync(client, opts, uri, ct);
        var locations = await client.ImplementationsAsync(uri, position, asked, ct);
        await Output.WriteLocationsAsync(
            opts.Root, locations, opts.Max, opts.Context, opts.Json, Documents.Of(client, ct),
            Union(all, asked));
        return locations.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// The answer to "what is this": type, full signature, and the doc-comment summary, from
    /// one <c>textDocument/hover</c>. Empty exits 1, like <c>def</c> — a position that
    /// resolves to no symbol is a lookup that failed, not a symbol with nothing to say.
    /// </summary>
    private static async Task<int> HoverAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CslqException("hover needs a symbol or file:line:col");

        CheckTarget(opts.Root, target);
        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, opts.Max, ct);
        var (all, asked) = await ContextsAsync(client, opts, uri, ct);
        var answer = await client.HoverAsync(uri, position, asked, ct);
        await Output.WriteHoverAsync(
            opts.Root, uri, position, answer.Value, opts.Max, opts.Json, Documents.Of(client, ct),
            new ContextNote(all, asked, answer.Context));
        return answer.Value?.Contents?.Value is null ? 1 : 0;
    }

    /// <summary>
    /// The note for an answer that is a <em>set</em>: every context asked contributed to it,
    /// so no single one "answered" and the note says what was tried instead. Null
    /// <c>Answered</c> is what tells <c>Output</c> which of the two sentences to print.
    /// </summary>
    private static ContextNote Union(
        IReadOnlyList<DocumentContext> all, IReadOnlyList<DocumentContext> asked) =>
        new(all, asked, Answered: null);

    /// <summary>
    /// What each asked context renders as, positionally: the label <c>Contexts.Names</c>
    /// would print, disambiguated by project only where the document really is compiled by
    /// more than one.
    /// </summary>
    private static IReadOnlyList<string> Names(
        IReadOnlyList<DocumentContext> all, IReadOnlyList<DocumentContext> asked) =>
        [.. asked.Select(c => Contexts.Label(all, c))];

    /// <summary>
    /// The contexts a context-bound command asks in: every context the document has, and the
    /// subset <c>--tfm</c> allows. One request per document per command, cached on the client,
    /// and the same request the generated-document label path already makes.
    /// </summary>
    private static async Task<(IReadOnlyList<DocumentContext> All, IReadOnlyList<DocumentContext> Asked)>
        ContextsAsync(LspClient client, Options opts, string uri, CancellationToken ct)
    {
        var all = await client.ContextsAsync(uri, ct);
        return (all, Contexts.Select(all, opts.Tfm, PathUri.Display(opts.Root, uri)));
    }

    /// <summary>
    /// Which project compiles a file, and for which target framework — <b>every</b> context,
    /// one row each, because a multi-targeted document has several and printing one of them
    /// said the file had a single home. A file, never a symbol: the question is about a
    /// document, and <c>textDocument/_vs_getProjectContexts</c> answers it for an ordinary file
    /// as readily as for the generated ones it was wired up for. A file no project compiles is
    /// not an error — <c>no project</c> is the answer, and the reason <c>sym</c> cannot see the
    /// types in it. This is the command an agent runs to find out whether it has to reason
    /// about <c>#if</c> branches at all, so a truthful count is the whole of its value.
    /// </summary>
    private static async Task<int> ProjectAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CslqException("project needs a file");
        if (Directory.Exists(Path.GetFullPath(Path.Combine(opts.Root, target))))
        {
            throw new CslqException($"project needs a file, not a directory: {target}");
        }

        var full = CheckFile(opts.Root, target);

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var uri = PathUri.FromPath(full);
        await client.OpenAsync(uri, ct);
        var (_, asked) = await ContextsAsync(client, opts, uri, ct);
        Output.WriteProject(opts.Root, full, asked, opts.Max, opts.Json);
        return asked.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// A search, so the query goes to the server as written and every answer is a result:
    /// no dotted narrowing, no ambiguity error, no candidate dump — several matches are
    /// the point rather than a problem. Empty exits 1, like <c>refs</c>: a search that found
    /// nothing is a lookup that failed.
    /// </summary>
    private static async Task<int> SymAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var query = opts.Argument ?? throw new CslqException("sym needs a query");

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var matches = Distinct(await client.SymbolsAsync(query, ct));
        await Output.WriteSymbolsAsync(
            opts.Root, query, matches, opts.Max, opts.Json, Documents.Of(client, ct));
        return matches.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// Exits 0 for a document with no symbols, like <c>diag</c> and unlike <c>refs</c>: an
    /// empty file is a query that was answered. A target that fails to resolve still exits 1,
    /// by throwing out of the resolver.
    /// </summary>
    /// <summary>
    /// Every declaration in one document, unioned over the contexts that compile it. A file
    /// whose whole body sits inside one <c>#if</c> answered <c>no symbols</c> at exit 0 in the
    /// other context — T-29, and a wrong answer rather than a partial one, since the class is
    /// right there in the file.
    /// </summary>
    private static async Task<int> OutlineAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CslqException("outline needs a file or symbol");
        var file = OutlineFile(opts.Root, target, out var line);

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        string uri;
        if (file is null)
        {
            uri = await OutlineSymbolAsync(client, opts.Root, target, opts.Max, ct);
        }
        else
        {
            uri = PathUri.FromPath(file);
            if (line > 0) await CheckLineAsync(client, opts.Root, uri, line, ct);
        }

        var (all, asked) = await ContextsAsync(client, opts, uri, ct);
        var views = await client.DocumentSymbolsAsync(uri, asked, Names(all, asked), ct);
        await Output.WriteOutlineAsync(
            opts.Root, uri, Outline.Merge(views), views.Count, opts.Max, opts.Json,
            Documents.Of(client, ct), Union(all, asked));
        return 0;
    }

    /// <summary>
    /// The file half of an <c>outline</c> target: a file path, or a <c>file:line:col</c> spec
    /// whose document is outlined, so a position copied from a <c>def</c> result works. Null
    /// for a target that is not a file at all, which goes to <see cref="OutlineSymbolAsync"/> —
    /// the only route to a source-generated document, which has no path. Anything file-shaped
    /// is resolved as a file and nothing else: letting a mistyped path fall through to the
    /// symbol resolver produces "no symbol matched 'Core/Missing.cs'" and a candidate dump,
    /// when the answer is that the file is not there.
    /// <para>
    /// Nothing here needs the server, so <c>outline</c> runs it before readiness and answers a
    /// bad path without waiting out a cold load. <paramref name="line"/> is 0 unless the target
    /// carried a position.
    /// </para>
    /// </summary>
    private static string? OutlineFile(string root, string target, out int line)
    {
        var spec = TryParsePosition(target, out var file, out line, out _);
        var path = spec ? file : target;
        var full = Path.GetFullPath(Path.Combine(root, path));

        if (File.Exists(full)) return CheckDocument(root, path);

        if (Directory.Exists(full)) throw new CslqException($"outline needs a file, not a directory: {path}");
        if (LooksLikePath(path)) throw new CslqException($"no such file: {path}");
        return null;
    }

    /// <summary>The document declaring <paramref name="target"/>, for <c>outline</c>.</summary>
    private static async Task<string> OutlineSymbolAsync(
        LspClient client, string root, string target, int max, CancellationToken ct)
    {
        // Overloads are not ambiguity here: several matches that share a document all outline
        // to the same thing, so collapse by document and only complain if they really differ.
        var matches = await MatchSymbolsAsync(client, root, target, max, ct);
        var uris = matches.Select(m => m.Symbol.Location.Uri).Distinct(StringComparer.Ordinal).ToList();
        if (uris.Count > 1)
        {
            // Labelled before the cap, unlike a symbol listing's rows: this one is a row per
            // document, the label is all a row carries, and so it is what the order is on.
            var labelled = new List<(string Uri, string Label)>(uris.Count);
            foreach (var u in uris)
            {
                labelled.Add((u, await PathUri.DisplayAsync(root, u, Documents.Of(client, ct))));
            }

            var rows = labelled
                .OrderBy(l => Output.Rank(l.Uri))
                .ThenBy(l => l.Label, StringComparer.Ordinal)
                .Take(max)
                .Select(l => "  " + l.Label)
                .ToList();

            throw new CslqException(
                $"'{target}' is declared in several documents; pick one:\n"
                + string.Join('\n', rows) + More(uris.Count, rows.Count));
        }

        return uris[0];
    }

    /// <summary>
    /// The truncation footer the output rules require, for the two candidate listings that are
    /// carried in an exception message rather than printed by <see cref="Output"/>. A broad
    /// ambiguous target on a real repository dumps hundreds of rows otherwise, which is the
    /// exact cost <c>--max</c> exists to prevent — and it lands in the caller's context before
    /// the caller can react to it.
    /// </summary>
    private static string More(int total, int shown) => total > shown
        ? $"\n... {total - shown} more (use --max {total} to see all)"
        : string.Empty;

    private static bool LooksLikePath(string target) =>
        target.Contains('/') || target.Contains('\\') ||
        target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The three extensions Roslyn answers document questions for. Razor is here because it
    /// works in file mode — <c>outline</c>, <c>refs</c>, <c>def</c> and a per-file <c>diag</c>
    /// all answer for a <c>.razor</c> — even though the directory walk does not reach one.
    /// </summary>
    private static readonly string[] DocumentExtensions = [".cs", ".razor", ".cshtml"];

    /// <summary>
    /// The one gate every file-taking command puts its argument through, before any
    /// <c>didOpen</c>. <see cref="LspClient.OpenAsync"/> declares <c>languageId: csharp</c>
    /// for whatever it is handed and the daemon then holds the document open for its whole
    /// lifetime, so a <c>.csproj</c> given to <c>diag</c> answered with a hundred parse errors
    /// at exit 0, and a <c>.json</c> did the same. A path above <c>--root</c> is not in the
    /// workspace at all. Both are argument errors, and both are named here rather than left to
    /// the server to answer with <c>no results</c>.
    /// <para>
    /// The message spells the path the caller did, like <c>no such file</c>; the full path is
    /// returned for the caller to use. Existence is not checked — <c>diag</c> takes a directory
    /// too, so each caller says what it means by a missing target.
    /// </para>
    /// </summary>
    internal static string CheckDocument(string root, string target)
    {
        var full = CheckUnderRoot(root, target);
        var extension = Path.GetExtension(full);
        return DocumentExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? full
            : throw new CslqException($"{target} is not a C# document");
    }

    /// <summary>
    /// The first half of <see cref="CheckDocument"/>, on its own for <c>diag</c>'s directory
    /// walk: a directory is a legitimate target there, but one above the root is not.
    /// </summary>
    internal static string CheckUnderRoot(string root, string target)
    {
        var full = Path.GetFullPath(Path.Combine(root, target));
        return PathUri.IsUnder(root, full)
            ? full
            : throw new CslqException($"{target} is outside --root");
    }

    /// <summary><see cref="CheckDocument"/> for a command that needs the file to be there.</summary>
    private static string CheckFile(string root, string target)
    {
        var full = CheckDocument(root, target);
        return File.Exists(full) ? full : throw new CslqException($"no such file: {target}");
    }

    /// <summary>
    /// The guard for a <c>symbol | file:line:col</c> target, which only has a file to check
    /// half the time. Run by <c>refs</c>, <c>def</c>, <c>impl</c> and <c>hover</c> before
    /// readiness, so a bad path is answered in milliseconds rather than after a cold load;
    /// <see cref="LocateAsync"/> repeats the check because it is the one that needs the path.
    /// </summary>
    private static void CheckTarget(string root, string target)
    {
        if (TryParsePosition(target, out var file, out _, out _)) CheckFile(root, file);
    }

    /// <summary>
    /// Exit code reports whether the query was answered, not whether the workspace is clean:
    /// a repo with no diagnostics is a successful `diag`, unlike an empty `refs`, which means
    /// the lookup failed.
    /// </summary>
    private static async Task<int> DiagAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var files = DiagFiles(opts);

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var findings = new List<Report>();
        // The note names one document's contexts, so only a single-file diag can carry one; a
        // walk spans documents with different context sets and says it per row instead.
        ContextNote? note = null;
        foreach (var uri in files.Select(PathUri.FromPath))
        {
            var (all, asked) = await ContextsAsync(client, opts, uri, ct);
            if (files.Count == 1) note = Union(all, asked);

            var views = await client.DiagnosticsAsync(uri, asked, Names(all, asked), ct);
            findings.AddRange(Reports(uri, views));
        }

        if (opts.ErrorsOnly)
        {
            findings.RemoveAll(f => Output.Severity(f.Diagnostic.Severity) != "error");
        }

        await Output.WriteDiagnosticsAsync(
            opts.Root, findings, opts.Max, opts.Context, opts.Json, Documents.Of(client, ct), note);
        return 0;
    }

    /// <summary>
    /// One document's diagnostics from every context it is compiled in, folded: a row every
    /// context reports is one row, and a row only some of them report keeps the list of which.
    /// The fold key is what a reader sees — position, severity, code and message — because two
    /// contexts reporting the same finding are one finding, and the per-framework duplication
    /// that would otherwise appear is exactly what testers confirmed <c>diag</c> did not have.
    /// </summary>
    internal static IEnumerable<Report> Reports(
        string uri, IReadOnlyList<(string Name, IReadOnlyList<Diagnostic> Items)> views)
    {
        var order = new List<(int, int, int?, string?, string)>();
        var folded = new Dictionary<(int, int, int?, string?, string), (Diagnostic First, List<string> In)>();

        foreach (var (name, items) in views)
        {
            foreach (var item in items)
            {
                var key = (item.Range.Start.Line, item.Range.Start.Character, item.Severity,
                    Output.Code(item.Code), item.Message);
                if (!folded.TryGetValue(key, out var entry))
                {
                    entry = (item, []);
                    folded[key] = entry;
                    order.Add(key);
                }

                entry.In.Add(name);
            }
        }

        return order.Select(k => new Report(uri, folded[k].First, folded[k].In, views.Count));
    }

    /// <summary>
    /// The documents one <c>diag</c> pulls, resolved before the server is waited on so a bad
    /// path is an instant argument error. A directory keeps walking — that is <c>diag</c>'s
    /// contract, and the walk is <c>.cs</c>-only — while a file passes the document guard, so
    /// a <c>.csproj</c> is refused rather than opened as C# and answered with a wall of parse
    /// errors at exit 0. Per file, not <c>workspace/diagnostic</c>: that endpoint answers but
    /// returns zero reports, which is what <c>workspaceDiagnostics: false</c> in its dynamic
    /// registration means. Verified against 5.12.0-1.26426.8.
    /// </summary>
    private static IReadOnlyList<string> DiagFiles(Options opts)
    {
        if (opts.Argument is not { } target) return SourceFiles(opts.Root).ToList();

        var full = CheckUnderRoot(opts.Root, target);
        if (Directory.Exists(full)) return SourceFiles(full).ToList();
        if (!File.Exists(full)) throw new CslqException($"no such file or directory: {target}");
        return [CheckDocument(opts.Root, target)];
    }

    /// <summary>
    /// Returns a URI, not a path: a source-generated declaration has no path, and converting
    /// one to a path and back silently yields a different, nonexistent document.
    /// </summary>
    private static async Task<(string Uri, Position Position)> LocateAsync(
        LspClient client, string root, string target, int max, CancellationToken ct)
    {
        if (TryParsePosition(target, out var file, out var line, out var column))
        {
            var uri = PathUri.FromPath(CheckFile(root, file));
            await CheckLineAsync(client, root, uri, line, ct);
            return (uri, new Position(line - 1, column - 1));
        }

        var matches = await MatchSymbolsAsync(client, root, target, max, ct);
        if (matches.Count > 1)
        {
            throw new CslqException(
                $"'{target}' is ambiguous; qualify it further:\n"
                + await Output.SymbolListingAsync(
                    root, LastSegment(target), matches, max, Documents.Of(client, ct)));
        }

        var match = matches[0].Symbol;
        return (match.Location.Uri, match.Location.Range.Start);
    }

    /// <summary>
    /// Every symbol matching <paramref name="target"/>, deduplicated by location. Callers
    /// decide what more than one means: for <c>refs</c> and <c>def</c> it is ambiguity, for
    /// <c>outline</c> it is only ambiguity when the documents differ.
    /// <para>
    /// One query, no retry. This used to re-ask for 10s while the selection came back empty,
    /// because a sentinel proved only that <em>some</em> project had loaded and a miss was
    /// indistinguishable from a project still loading. Readiness now waits for every project,
    /// so an empty answer means the symbol is absent — and the retry only made every genuine
    /// miss cost 40 requests and 10s.
    /// </para>
    /// </summary>
    private static async Task<List<SymbolRow>> MatchSymbolsAsync(
        LspClient client, string root, string target, int max, CancellationToken ct)
    {
        var name = LastSegment(target);
        var candidates = await client.SymbolsAsync(name, ct);
        var named = Distinct(candidates.Where(
            s => string.Equals(s.Name, name, StringComparison.Ordinal)));
        var matches = await SelectAsync(client, named, target, ct);

        if (matches.Count == 0)
        {
            var seen = candidates.Count == 0
                ? string.Empty
                : "\ncandidates:\n" + await Output.SymbolListingAsync(
                    root, name, [.. Distinct(candidates).Select(c => new SymbolRow(c, c.Kind))],
                    max, Documents.Of(client, ct));
            throw new CslqException($"no symbol matched '{target}'{seen}");
        }

        return matches;
    }

    /// <summary>
    /// The two decisions of "Targeting a symbol by name" in <c>DESIGN.md</c>, applied to the
    /// candidates whose name already equals the target's last segment. Both read a candidate's
    /// declaration chain off the syntax tree, which costs one
    /// <c>textDocument/documentSymbol</c> per distinct document, so both are gated: a dotted
    /// target needs chains to verify its leading segments, a bare name only when more than one
    /// candidate survived. A bare unambiguous name -- the common case -- costs no extra
    /// request at all.
    /// </summary>
    private static async Task<List<SymbolRow>> SelectAsync(
        LspClient client, List<SymbolInformation> named, string target, CancellationToken ct)
    {
        var dotted = target.Contains('.');
        if (!dotted && named.Count < 2) return [.. named.Select(s => new SymbolRow(s, s.Kind))];

        var trees = new Dictionary<string, IReadOnlyList<DocumentSymbol>>(StringComparer.Ordinal);
        var chains = new List<Targets.Candidate>(named.Count);
        foreach (var symbol in named)
        {
            var uri = symbol.Location.Uri;
            if (!trees.TryGetValue(uri, out var tree))
            {
                // The union of every context, and deliberately not the `--tfm` subset: this is
                // "where in the file is this declared", and a chain read from one context's
                // tree could not see a declaration in the other branch at all. That is why
                // `def Fixture2.Multi.Only9` answered `no symbol matched` in 2 of 6 runs
                // while the bare `def Only9` was already deterministic. `--tfm` still
                // constrains the answer; it has no business constraining the lookup.
                var contexts = await client.ContextsAsync(uri, ct);
                var views = await client.DocumentSymbolsAsync(
                    uri, contexts, [.. contexts.Select(c => c.Name)], ct);
                tree = Outline.Tree(Outline.Merge(views));
                trees[uri] = tree;
            }

            chains.Add(new Targets.Candidate(
                symbol.Kind, Targets.Chain(tree, symbol.Location.Range.Start)));
        }

        // The chain is the only thing that knows a constructor is one, and the ambiguity
        // listing is the one place these rows are shown, so the kind travels out with them
        // rather than being recomputed from a request the renderer would have to make.
        var rows = named
            .Select((s, i) => new SymbolRow(s, Targets.IsConstructor(chains[i]) ? Targets.Constructor : s.Kind))
            .ToList();

        if (dotted)
        {
            return [.. rows.Where((_, i) => Targets.ChainMatches(chains[i].Chain, target))];
        }

        var type = Targets.TypeOverConstructors(chains);
        return type < 0 ? rows : [rows[type]];
    }

    // Roslyn reports a symbol once per project that sees it, so a symbol in a multi-targeted
    // or referenced project arrives several times at one location.
    private static List<SymbolInformation> Distinct(IEnumerable<SymbolInformation> symbols) => symbols
        .DistinctBy(s => (s.Location.Uri, s.Location.Range.Start.Line, s.Location.Range.Start.Character))
        .ToList();

    private static string LastSegment(string target)
    {
        var dot = target.LastIndexOf('.');
        return dot < 0 ? target : target[(dot + 1)..];
    }

    // Anchored on the last two colons so a Windows drive letter does not parse as a line.
    [GeneratedRegex(@"^(?<file>.+):(?<line>\d+):(?<col>\d+)$")]
    private static partial Regex PositionSpec();

    /// <summary>
    /// Throws rather than returning <c>false</c> once the <c>file:line:col</c> shape has
    /// matched but the numbers are unusable. Falling through to the symbol resolver instead
    /// would answer "no symbol matched 'Core/Greeter.cs:0:1'" and dump candidates, when the
    /// real answer is that the position is not one. Zero is rejected with overflow: positions
    /// are one-based everywhere in <c>cslq</c>, and <c>line - 1</c> would otherwise hand Roslyn
    /// a negative position, which it throws out of as an unhandled RPC fault.
    /// </summary>
    private static bool TryParsePosition(string spec, out string file, out int line, out int column)
    {
        var m = PositionSpec().Match(spec);
        if (m.Success)
        {
            file = m.Groups["file"].Value;
            line = Coordinate(m.Groups["line"].Value, "line");
            column = Coordinate(m.Groups["col"].Value, "column");
            return true;
        }

        (file, line, column) = (string.Empty, 0, 0);
        return false;
    }

    /// <summary>
    /// A line past the end of the document, rejected before the position reaches the server.
    /// It is the server that refuses it — <c>The requested line number 98 must be less than
    /// the number of lines 14. (Parameter 'Line')</c> — and that arrives as a
    /// <c>RemoteInvocationException</c>, which <c>Main</c> does not catch: a stack trace and
    /// exit 127. Said here instead, where the file name and the count are known.
    /// <para>
    /// Roslyn's count includes the empty line after the final newline, so its limit for a
    /// 13-line file is 14. The count reported here is the one <c>cat -n</c> shows the caller,
    /// so line 14 of that file is rejected too — a position nothing can be at. The column
    /// stays unchecked: it is inside a line that exists, and the server answers `no results`.
    /// </para>
    /// <para>
    /// Only a caller-spelled <c>file:line:col</c> gets here, which is always an ordinary file
    /// on disk — a source-generated document is reachable by symbol alone — so the read is
    /// the one <see cref="LspClient.LinesAsync"/> caches for the context lines anyway. An
    /// empty file counts as one line, since 1:1 is a position in it, but the message reports
    /// the real count rather than claiming a line that is not there.
    /// </para>
    /// </summary>
    private static async Task CheckLineAsync(
        LspClient client, string root, string uri, int line, CancellationToken ct)
    {
        var lines = await client.LinesAsync(uri, ct);
        if (line <= Math.Max(1, lines.Length)) return;

        var count = lines.Length == 1 ? "1 line" : $"{lines.Length} lines";
        throw new CslqException(
            $"line out of range: {line} ({PathUri.Display(root, uri)} has {count})");
    }

    private static int Coordinate(string text, string name) => int.TryParse(text, out var value)
        ? value > 0 ? value : throw new CslqException($"{name} is one-based: {text}")
        : throw new CslqException($"{name} out of range: {text}");

    /// <summary>
    /// Rejects a file-shaped argument that is not a usable position, so it fails at parse time
    /// rather than reaching the symbol resolver. <see cref="TryParsePosition"/> already throws
    /// once <see cref="PositionSpec"/> matched; what is left is a spec it could not match at
    /// all — a `+`, a sign, an empty coordinate — which would otherwise start a server, wait
    /// out readiness, and answer "no symbol matched 'Core/Greeter.cs:+1:2'".
    /// </summary>
    private static void ValidatePosition(string argument)
    {
        if (TryParsePosition(argument, out _, out _, out _)) return;

        // Only the last segment: a Windows drive letter puts a colon in the first one.
        var segment = argument[(argument.LastIndexOfAny(['/', '\\']) + 1)..];
        if (segment.Contains(':'))
        {
            throw new CslqException($"'{argument}' is not a position: expected file:line:col");
        }
    }

    [GeneratedRegex(@"\b(?:class|struct|record|interface|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclaration();

    // Raw strings first, then verbatim, then ordinary: a shorter alternative would otherwise
    // close a longer literal early. Replaced with a space, not nothing, so `class/*x*/Foo`
    // does not become the declaration `classFoo`.
    [GeneratedRegex("\"\"\"[\\s\\S]*?\"\"\"|@\"(?:[^\"]|\"\")*\"|\"(?:\\\\.|[^\"\\\\\n])*\"|'(?:\\\\.|[^'\\\\\n])*'|//[^\n]*|/\\*[\\s\\S]*?\\*/")]
    private static partial Regex NonCode();

    /// <summary>
    /// One readiness probe per project, because a single one only ever proved that
    /// <em>some</em> project had loaded — see <see cref="LspClient.WaitReadyAsync"/> for the
    /// incomplete answers that produced. An explicit <c>--sentinel</c> <em>adds</em> a
    /// root-scoped probe to that set rather than replacing it: replacing it reopened the
    /// incomplete-answer bug through the escape hatch — on OrchardCore a <c>--sentinel</c> run
    /// answered <c>impl StartupBase</c> with 321 hits against 331, at exit 0. It stands alone
    /// only when inference finds nothing at all, which is the layout it is the escape hatch
    /// for.
    /// </summary>
    internal static IReadOnlyList<Sentinel> Sentinels(Options opts)
    {
        if (opts.Sentinel is not { } explicitSentinel) return InferSentinels(opts.Root);

        var probe = new Sentinel(
            Path.GetFullPath(opts.Root), [explicitSentinel], [], Explicit: true);

        try
        {
            return [.. InferSentinels(opts.Root), probe];
        }
        catch (CslqException)
        {
            return [probe];
        }
    }

    /// <summary>
    /// A project per <c>.csproj</c>, and per project the type names declared in its own files,
    /// most-shallow-file-first. Several candidates rather than one because
    /// <see cref="TypeDeclaration"/> is a regex, not a parser: even with comments and string
    /// literals stripped it reads a type out of a file no project compiles, out of an
    /// excluded <c>#if</c> branch, or out of a <c>using</c> alias, and one such guess would
    /// otherwise block readiness for the whole run. Capped because the list is a fallback
    /// chain, not an index.
    /// <para>
    /// A project that declares no type at all — one that is only top-level statements —
    /// contributes no candidate. It is still returned, so that
    /// <see cref="LspClient.WaitReadyAsync"/> can name it as unprobed on the failure path
    /// rather than leaving it silently absent from readiness.
    /// </para>
    /// <para>
    /// A root with no solution, with two of them, or with none holding a <c>.csproj</c>, fails
    /// immediately. Roslyn loads nothing useful for such a root, so every sentinel is
    /// unresolvable and every query answers empty: waiting out the full timeout only delays the
    /// same conclusion. An explicit <c>--sentinel</c> falls back to its own probe alone when
    /// this throws, which is the escape hatch for a layout this cannot read.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<Sentinel> InferSentinels(string root)
    {
        var solutions = Solutions(root);
        if (solutions.Count == 0) throw new CslqException(NoSolution(root));

        if (solutions.Count > 1) throw new CslqException(TwoSolutions(root, solutions));

        var projects = ProjectDirectories(solutions[0]);
        if (projects.Count == 0)
        {
            // The solution is named rather than the root: "no .csproj under <root>" would be a
            // lie about a root whose solution simply lists no C# project, and would send the
            // reader looking for files that are sitting right there.
            throw new CslqException(
                $"{Path.GetFileName(solutions[0])} lists no C# project; point --root at a "
                + "workspace or pass --sentinel");
        }

        // Nesting boundaries come off the disk, not off the discovered list: a `.csproj` the
        // solution does not list is still a directory Roslyn compiles separately, and reading
        // types out of one is what burned 900s on OrchardCore's
        // `src/Templates/OrchardCore.ProjectTemplates`. The discovered list is unioned in
        // because a solution may list a project outside the root, which a scan under the root
        // cannot see.
        var boundaries = Directories(ScannedProjects(root))
            .Union(projects, PathUri.PathComparer)
            .ToList();
        var sentinels = projects.Select(d => Probe(d, boundaries)).ToList();

        return sentinels.Any(s => s.Candidates.Count > 0)
            ? sentinels
            : throw new CslqException($"could not infer a readiness sentinel under {root}; pass --sentinel");
    }

    /// <summary>
    /// The type names declared in a project's own files, most-shallow-file-first. A project
    /// left with no candidate is reported as unprobed rather than assumed loaded.
    /// <para>
    /// <see cref="NonCode"/> first, because <see cref="TypeDeclaration"/> matches English
    /// prose: "identifying the class and assembly context" in a doc comment yields the
    /// candidate <c>and</c>, a word no <c>workspace/symbol</c> query can resolve. Measured
    /// 2026-09-06, before the strip: <c>cslq ready</c> on OrchardCore v3.0.1 failed after 900s
    /// on fifteen projects, six of whose candidate lists were <c>'and' / 'and' / 'and'</c> —
    /// one doc comment can fill all three slots, so capping at three is no defence and taking
    /// every match per file rather than the first is not one either.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> Candidates(IEnumerable<string> ownSourceFiles) =>
        ownSourceFiles
            .SelectMany(f => TypeDeclaration().Matches(NonCode().Replace(File.ReadAllText(f), " ")))
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

    /// <summary>
    /// One project's probe: what to query for, the boundaries that scope a hit, and whether
    /// the project can be probed at all. A project whose <c>.csproj</c> says it compiles
    /// nothing of its own gets no candidates however many <c>.cs</c> files sit under it —
    /// MSBuild settles that and a source scan cannot override it — and one whose sources are
    /// linked in from elsewhere is skipped, because a hit for a linked document lands under
    /// the source directory and <see cref="Sentinel.Accepts"/> can never accept it.
    /// </summary>
    private static Sentinel Probe(string directory, IReadOnlyList<string> boundaries)
    {
        var nested = NestedProjects(directory, boundaries);
        var sources = ProjectKind(directory);
        // Materialised, not lazy: `Candidates` and the emptiness test would otherwise walk
        // the directory tree twice for every project, 226 times over on OrchardCore.
        List<string> own = sources.None ? [] : [.. OwnSourceFiles(directory, nested)];

        return new Sentinel(
            directory, Candidates(own), nested, Skipped: sources.Elsewhere && own.Count == 0);
    }

    /// <summary>
    /// A project's <em>own</em> C# files: everything under its directory that is not under a
    /// project nested inside it. That exclusion is only half of the nesting fix — the hit has
    /// to be scoped too, which is what <see cref="Sentinel.Nested"/> carries, since
    /// <c>Web/</c> and <c>Web/Tests/</c> both declaring <c>Program</c> is the ordinary shape
    /// and a scan of A's own files alone does not stop B's <c>Program</c> from answering A's
    /// query.
    /// </summary>
    private static IEnumerable<string> OwnSourceFiles(
        string directory, IReadOnlyList<string> nested) =>
        SourceFiles(directory).Where(f => !nested.Any(n => IsUnder(f, n)));

    /// <summary>
    /// What the project's own <c>.csproj</c> text claims about where its sources live. Every
    /// <c>.csproj</c> in the directory is read and a claim is taken only when all of them
    /// agree: two project files in one directory are indistinguishable to the rest of this
    /// inference — a documented limit — and the conservative reading leaves such a pair
    /// probed. An unreadable file is an ordinary project, for the reason
    /// <see cref="ProjectSources.Read"/> treats an unparseable one as one.
    /// </summary>
    private static ProjectSources.Kind ProjectKind(string directory)
    {
        var kinds = Directory
            .EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
            .Select(ReadKind)
            .ToList();

        return kinds.Count == 0
            ? new ProjectSources.Kind(false, false)
            : new ProjectSources.Kind(kinds.All(k => k.Elsewhere), kinds.All(k => k.None));

        static ProjectSources.Kind ReadKind(string file)
        {
            try
            {
                return ProjectSources.Read(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ProjectSources.Kind(false, false);
            }
        }
    }

    /// <summary>
    /// The nesting boundaries inside a project directory. Over the <em>on-disk</em>
    /// <c>.csproj</c> set rather than the solution's list: a template or sample project the
    /// solution excludes is still a directory whose sources belong to it and not to the
    /// project above it.
    /// </summary>
    private static IReadOnlyList<string> NestedProjects(
        string directory, IReadOnlyList<string> boundaries) =>
        [.. boundaries.Where(p => p != directory && IsUnder(p, directory))];

    private static bool IsUnder(string path, string directory) => path.StartsWith(
        directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar,
        PathUri.PathComparison);

    /// <summary>
    /// Every project directory the root's solution lists. An approximation of what Roslyn
    /// actually loaded, because <b>the server cannot be asked</b> —
    /// <c>workspace/_roslyn_restorableProjects</c> is a server-to-client request and carries no
    /// project list.
    /// <para>
    /// The solution is read rather than ignored because <b>over-inclusion is fatal, not merely
    /// wasteful</b>: a <c>.csproj</c> the solution excludes is never loaded, so its types are
    /// never indexed, its sentinel can never resolve, and readiness burns the whole timeout and
    /// exits 1. Measured 2026-09-06, before this read the solution: <c>cslq ready</c> on
    /// OrchardCore v3.0.1 failed on <c>src/Templates/OrchardCore.ProjectTemplates/content/*</c>,
    /// which is <c>dotnet new</c> template content the solution excludes, and the only way past
    /// it was to point <c>--root</c> below the templates.
    /// </para>
    /// <para>
    /// Exactly one solution reaches this: neither no solution nor two of them is a case handled
    /// here, because <see cref="InferSentinels"/> rejects both roots before it gets this far.
    /// A project the solution lists but that is not on disk is dropped, since waiting on one
    /// would be the same unresolvable sentinel by another route. A <c>.slnf</c> solution filter
    /// is not read: a root holding only one is a root with no solution.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ProjectDirectories(string solution) =>
        Directories(SolutionProjects(solution));

    private static IReadOnlyList<string> Directories(IEnumerable<string> projectFiles) => projectFiles
        .Select(f => Path.GetFullPath(Path.GetDirectoryName(f)!))
        .Distinct(PathUri.PathComparer)
        .Order(PathUri.PathComparer)
        .ToList();

    /// <summary>
    /// The root's own solutions. Top-level only: a solution in a subdirectory describes that
    /// subtree rather than this root, and Roslyn would not open it for this root either.
    /// Capped at two, which is all the caller distinguishes: one is read, and two or more is
    /// an error that names the two it found.
    /// </summary>
    private static IReadOnlyList<string> Solutions(string root) => Directory
        .EnumerateFiles(root, "*.sln*", SearchOption.TopDirectoryOnly)
        .Where(f => Path.GetExtension(f) is ".sln" or ".slnx")
        .Take(2)
        .ToList();

    /// <summary>
    /// A root with no solution never becomes ready: <c>--autoLoadProjects</c> does not discover
    /// a bare <c>.csproj</c>, so <c>projectInitializationComplete</c> never fires and every
    /// query answers empty for the whole timeout. Knowable before the server starts, so it is
    /// said instantly instead.
    /// <para>
    /// Deliberately no "or pass <c>--sentinel</c>" here, though <see cref="Sentinels"/> does
    /// fall back to the explicit probe when this throws: Roslyn still loads nothing for such a
    /// root, so the hint would send the reader straight into the timeout this exists to remove.
    /// The other failures in <see cref="InferSentinels"/> keep it, because there the workspace
    /// does load.
    /// </para>
    /// </summary>
    internal static string NoSolution(string root) =>
        $"no .sln or .slnx at {root}; cslq loads the projects the root's solution lists, so "
        + "--root must be the directory holding the .sln/.slnx";

    /// <summary>
    /// Two solutions give no basis for choosing between them, and guessing is not free: the
    /// <c>.csproj</c> scan this replaced is over-inclusive, so a project neither solution loads
    /// gets a sentinel that can never resolve and readiness burns its whole timeout — three
    /// runs out of three on a real repository. Knowable before the server starts, like
    /// <see cref="NoSolution"/>, and both files are named so the reader can see which two.
    /// </summary>
    internal static string TwoSolutions(string root, IEnumerable<string> solutions) =>
        $"two solutions at {root}: {string.Join(", ", solutions.Select(Path.GetFileName))}; "
        + "cslq reads exactly one, so point --root at a directory holding one solution";

    /// <summary>
    /// The C# projects a solution lists. <c>.slnx</c> is XML that nests projects under folder
    /// elements, so every descendant is taken rather than the direct children; <c>.sln</c> is
    /// the older line format, whose project entries also cover solution folders and non-C#
    /// projects, which the extension filter drops. Paths are solution-relative, and <c>.sln</c>
    /// writes them with a backslash, which off Windows is a filename character rather than a
    /// separator.
    /// </summary>
    private static IEnumerable<string> SolutionProjects(string solution)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(solution))!;
        var listed = Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? SolutionXml(solution).Descendants("Project").Select(e => (string?)e.Attribute("Path"))
            : SolutionEntry().Matches(File.ReadAllText(solution)).Select(m => m.Groups["path"].Value);

        return listed
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(
                Path.Combine(directory, p!.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
    }

    /// <summary>
    /// A hand-edited <c>.slnx</c> that no longer parses is a workspace mistake, not a defect,
    /// and has to arrive as one: <c>Main</c> catches <see cref="CslqException"/> and nothing
    /// else, so an escaping <see cref="XmlException"/> answers a bad solution file with an
    /// unhandled stack trace and exit 127.
    /// </summary>
    private static XDocument SolutionXml(string solution)
    {
        try
        {
            // From a stream, not the path: the string overload resolves its argument as a
            // URI, and an extended-length path (`\\?\C:\...`) is not one — it threw
            // UriFormatException, which is not an XmlException and so escaped as a stack
            // trace. `PathUri.Plain` strips the prefix off `--root` before it ever gets here,
            // which is what actually fixes that path; this stays because the string overload
            // buys nothing in exchange for resolving a filename the caller already has.
            using var file = File.OpenRead(solution);
            return XDocument.Load(file);
        }
        catch (XmlException ex)
        {
            throw new CslqException($"{Path.GetFileName(solution)} is not valid XML: {ex.Message}");
        }
    }

    // `Project("{type guid}") = "Name", "Relative\Path.csproj", "{project guid}"`.
    [GeneratedRegex(@"^Project\(""\{[^}]*\}""\)\s*=\s*""[^""]*"",\s*""(?<path>[^""]+)""",
        RegexOptions.Multiline)]
    private static partial Regex SolutionEntry();

    /// <summary>
    /// Every <c>.csproj</c> on disk under the root. Not a project list — the solution's is the
    /// only one of those — but the nesting boundaries, which are a property of the disk: a
    /// project file the solution excludes is still a directory whose sources belong to it and
    /// not to the project above it.
    /// </summary>
    private static IEnumerable<string> ScannedProjects(string root) => Directory
        .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// The workspace's own C# files. Sorted because
    /// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> order is
    /// filesystem-defined, and neither the inferred sentinel nor the order diagnostics are
    /// pulled in should depend on which file it happens to hit first.
    /// </summary>
    private static IEnumerable<string> SourceFiles(string root) => Directory
        .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Order(PathUri.PathComparer);

    internal sealed record Options(
        string Command,
        string? Argument,
        string Root,
        string? Sentinel,
        int Max,
        int Context,
        TimeSpan Timeout,
        string LogLevel,
        bool ErrorsOnly,
        bool Json,
        bool Daemon,
        string? Tfm)
    {
        /// <summary>
        /// Whether the caller asked for more than warnings. The level is otherwise handed
        /// straight to the server, so this is the only place <c>cslq</c> reads it: it is not
        /// validated here either — an unrecognised level counts as quiet, and the server is
        /// what rejects it — and the comparison is case-insensitive because the server's own
        /// enum parse is.
        /// </summary>
        public bool Verbose => LogLevel.ToLowerInvariant() is "trace" or "debug" or "information";

        public static Options Parse(string[] argv)
        {
            // Here rather than in DispatchAsync's default branch, for the reason the numeric
            // checks are here: everything between the two starts a server and scans the
            // workspace, so a typo would be answered by whatever failed first. It was —
            // `cslq bogus --root <dir with no .csproj>` reported the missing project.
            string command = argv[0] is var c && Commands.Contains(c)
                ? c
                : throw new CslqException($"unknown command '{argv[0]}'\n\n{Usage}");
            string? argument = null;
            var root = Directory.GetCurrentDirectory();
            string? sentinel = null;
            var max = Output.DefaultMax;
            var context = 1;
            var timeout = TimeSpan.FromSeconds(180);
            var logLevel = "Warning";
            var errorsOnly = false;
            var json = false;
            var daemon = true;
            string? tfm = null;

            for (var i = 1; i < argv.Length; i++)
            {
                switch (argv[i])
                {
                    case "--root": root = PathUri.Plain(Path.GetFullPath(Next(argv, ref i))); break;
                    case "--sentinel": sentinel = Next(argv, ref i); break;
                    case "--max": max = Int(argv, ref i, 1); break;
                    case "--context": context = Int(argv, ref i, 0); break;
                    // No floor of 1: a zero timeout is how `premature-query-fails-loudly`
                    // proves a query fired before load fails loudly rather than answering
                    // empty. Only a negative one is rejected.
                    case "--timeout": timeout = TimeSpan.FromSeconds(Int(argv, ref i, 0)); break;
                    case "--log-level": logLevel = Next(argv, ref i); break;
                    case "--tfm": tfm = Next(argv, ref i); break;
                    case "--errors-only": errorsOnly = true; break;
                    case "--json": json = true; break;
                    case "--no-daemon": daemon = false; break;
                    default:
                        if (argv[i].StartsWith('-')) throw new CslqException($"unknown option '{argv[i]}'");
                        if (argument is not null) throw new CslqException($"unexpected argument '{argv[i]}'");
                        argument = argv[i];
                        break;
                }
            }

            if (!Directory.Exists(root)) throw new CslqException($"no such directory: {root}");

            // Here rather than in LocateAsync: that runs after StartAsync and WaitReadyAsync,
            // so `file:0:1` would start a server and wait out readiness before printing an
            // argument error. Only for the commands that accept a position: `sym Foo:1` is a
            // legitimate query and `diag nope:x` a path, and validating those rejected both.
            if (argument is not null && TakesPosition.Contains(command)) ValidatePosition(argument);

            // `restore` names nothing: the manifest it restores is the one packed beside the
            // running binary, found by walking up from it, and a path here would read as if
            // it could be pointed somewhere else.
            if (command == "restore" && argument is not null)
                throw new CslqException($"restore takes no argument; got '{argument}'");

            return new Options(
                command, argument, root, sentinel, max, context, timeout, logLevel, errorsOnly, json,
                daemon, tfm);
        }

        /// <summary>
        /// The value after an option, which is never legitimately blank: every option here
        /// names a directory, a symbol, a number or a log level, and an empty one is a
        /// misquoted shell variable rather than a request. Without this
        /// <c>--root ""</c> reached <see cref="Path.GetFullPath(string)"/>, whose
        /// <c>ArgumentException</c> is not a <see cref="CslqException"/> and so answered a
        /// typo with a stack trace and exit 127.
        /// </summary>
        private static string Next(string[] argv, ref int i)
        {
            var name = argv[i];
            if (++i >= argv.Length || string.IsNullOrWhiteSpace(argv[i]))
                throw new CslqException($"option '{name}' needs a value");
            return argv[i];
        }

        private static int Int(string[] argv, ref int i, int floor)
        {
            var name = argv[i];
            var text = Next(argv, ref i);
            if (!int.TryParse(text, out var value))
            {
                // Overflow is not garbage: `--max 99999999999` is a number, just not one that
                // fits, and "needs an integer" reads as a lie about the input.
                var magnitude = text.StartsWith('-') ? text[1..] : text;
                throw new CslqException(magnitude.Length > 0 && magnitude.All(char.IsAsciiDigit)
                    ? $"{name} out of range: {text}"
                    : $"{name} needs an integer");
            }

            if (value < floor) throw new CslqException($"{name} needs to be {floor} or more");
            return value;
        }
    }
}
