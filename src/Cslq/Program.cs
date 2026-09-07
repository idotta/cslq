using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Cslq;

internal static partial class Program
{
    private const string Usage = """
        cslq - semantic C# queries over the official roslyn-language-server

        usage:
          cslq ready   [--sentinel <symbol>]
          cslq refs    <symbol | file:line:col> [--max N] [--context N]
          cslq def     <symbol | file:line:col> [--max N] [--context N]
          cslq impl    <symbol | file:line:col> [--max N] [--context N]
          cslq sym     <query> [--max N]
          cslq outline <file | symbol> [--max N]
          cslq diag    [path] [--errors-only] [--max N] [--context N]

        options:
          --root <dir>      workspace root (default: current directory)
          --sentinel <sym>  readiness probe symbol (default: inferred from the workspace)
          --max N           cap results (default: 50)
          --context N       source lines either side of a hit (default: 1; unused by outline, sym)
          --timeout N       seconds to wait for workspace load (default: 180)
          --log-level L     server log level (default: Warning)
          --errors-only     diag: drop warnings and below
          --json            machine-readable output
          --no-daemon       start a dedicated server instead of the shared daemon
          --version         print the cslq version
          -h, --help        this message
        """;

    private static readonly string[] Commands =
        ["ready", "refs", "def", "impl", "sym", "outline", "diag"];

    // `outline` is here because it accepts a position too — see OutlineTargetAsync. `sym`
    // takes a free-text query and `diag` a path, neither of which is position-shaped.
    private static readonly string[] TakesPosition = ["refs", "def", "impl", "outline"];

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

    private static async Task<int> RunAsync(string[] argv)
    {
        switch (Preflight(argv))
        {
            case Immediate.Usage: Console.WriteLine(Usage); return 2;
            case Immediate.Help: Console.WriteLine(Usage); return 0;
            case Immediate.Version: Console.WriteLine(Build.Version); return 0;
        }

        var opts = Options.Parse(argv);

        // Before the server starts, like the argument checks in Options.Parse: this is a
        // filesystem scan, and a root with no project in it should say so instantly rather
        // than after a cold load.
        var sentinels = Sentinels(opts);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

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
                Console.WriteLine("ready");
                return 0;

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

            default:
                throw new System.Diagnostics.UnreachableException(
                    $"Options.Parse admitted '{opts.Command}'");
        }
    }

    private static async Task<int> RefsAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CslqException("refs needs a symbol or file:line:col");

        // Gate on a sentinel that must exist, never on the symbol being asked about:
        // otherwise a genuinely absent symbol is indistinguishable from a workspace that
        // has not finished loading, and the caller waits out the whole timeout for it.
        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, ct);
        var locations = await client.ReferencesAsync(uri, position, ct);
        await Output.WriteLocationsAsync(
            opts.Root, locations, opts.Max, opts.Context, opts.Json,
            u => client.LinesAsync(u, ct), u => client.ProjectOfAsync(u, ct));
        return locations.Count == 0 ? 1 : 0;
    }

    private static async Task<int> DefAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CslqException("def needs a symbol or file:line:col");

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, ct);
        var locations = await client.DefinitionAsync(uri, position, ct);
        await Output.WriteLocationsAsync(
            opts.Root, locations, opts.Max, opts.Context, opts.Json,
            u => client.LinesAsync(u, ct), u => client.ProjectOfAsync(u, ct));
        return locations.Count == 0 ? 1 : 0;
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

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, ct);
        var locations = await client.ImplementationsAsync(uri, position, ct);
        await Output.WriteLocationsAsync(
            opts.Root, locations, opts.Max, opts.Context, opts.Json,
            u => client.LinesAsync(u, ct), u => client.ProjectOfAsync(u, ct));
        return locations.Count == 0 ? 1 : 0;
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
            opts.Root, matches, opts.Max, opts.Json, u => client.ProjectOfAsync(u, ct));
        return matches.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// Exits 0 for a document with no symbols, like <c>diag</c> and unlike <c>refs</c>: an
    /// empty file is a query that was answered. A target that fails to resolve still exits 1,
    /// by throwing out of the resolver.
    /// </summary>
    private static async Task<int> OutlineAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CslqException("outline needs a file or symbol");

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var uri = await OutlineTargetAsync(client, opts.Root, target, ct);
        var symbols = await client.DocumentSymbolsAsync(uri, ct);
        await Output.WriteOutlineAsync(
            opts.Root, uri, symbols, opts.Max, opts.Json,
            u => client.LinesAsync(u, ct), u => client.ProjectOfAsync(u, ct));
        return 0;
    }

    /// <summary>
    /// A file path, a <c>file:line:col</c> spec (whose document is outlined, so a position
    /// copied from a <c>def</c> result works), or a symbol whose declaring document is
    /// outlined — the last being the only route to a source-generated document, which has no
    /// path. Anything file-shaped is resolved as a file and nothing else: letting a mistyped
    /// path fall through to the symbol resolver produces "no symbol matched 'Core/Missing.cs'"
    /// and a candidate dump, when the answer is that the file is not there.
    /// </summary>
    private static async Task<string> OutlineTargetAsync(
        LspClient client, string root, string target, CancellationToken ct)
    {
        var path = TryParsePosition(target, out var file, out _, out _) ? file : target;
        var full = Path.GetFullPath(Path.Combine(root, path));

        if (File.Exists(full)) return PathUri.FromPath(full);
        if (Directory.Exists(full)) throw new CslqException($"outline needs a file, not a directory: {path}");
        if (LooksLikePath(path)) throw new CslqException($"no such file: {path}");

        // Overloads are not ambiguity here: several matches that share a document all outline
        // to the same thing, so collapse by document and only complain if they really differ.
        var matches = await MatchSymbolsAsync(client, target, ct);
        var uris = matches.Select(m => m.Location.Uri).Distinct(StringComparer.Ordinal).ToList();
        if (uris.Count > 1)
        {
            var rows = new List<string>(uris.Count);
            foreach (var u in uris)
            {
                rows.Add("  " + await PathUri.DisplayAsync(root, u, x => client.ProjectOfAsync(x, ct)));
            }

            throw new CslqException(
                $"'{target}' is declared in several documents; pick one:\n{string.Join('\n', rows)}");
        }

        return uris[0];
    }

    private static bool LooksLikePath(string target) =>
        target.Contains('/') || target.Contains('\\') ||
        target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Exit code reports whether the query was answered, not whether the workspace is clean:
    /// a repo with no diagnostics is a successful `diag`, unlike an empty `refs`, which means
    /// the lookup failed.
    /// </summary>
    private static async Task<int> DiagAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var findings = new List<(string Uri, Diagnostic Diagnostic)>();
        if (opts.Argument is { } target)
        {
            var full = Path.GetFullPath(Path.Combine(opts.Root, target));
            if (Directory.Exists(full))
            {
                foreach (var uri in SourceFiles(full).Select(PathUri.FromPath))
                {
                    findings.AddRange((await client.DiagnosticsAsync(uri, ct)).Select(d => (uri, d)));
                }
            }
            else if (File.Exists(full))
            {
                var uri = PathUri.FromPath(full);
                findings.AddRange((await client.DiagnosticsAsync(uri, ct)).Select(d => (uri, d)));
            }
            else
            {
                throw new CslqException($"no such file or directory: {target}");
            }
        }
        else
        {
            // Per file, not workspace/diagnostic: that endpoint answers but returns zero
            // reports, which is what workspaceDiagnostics: false in its dynamic registration
            // means. Verified against 5.12.0-1.26426.8.
            foreach (var uri in SourceFiles(opts.Root).Select(PathUri.FromPath))
            {
                findings.AddRange((await client.DiagnosticsAsync(uri, ct)).Select(d => (uri, d)));
            }
        }

        if (opts.ErrorsOnly)
        {
            findings.RemoveAll(f => Output.Severity(f.Diagnostic.Severity) != "error");
        }

        await Output.WriteDiagnosticsAsync(
            opts.Root, findings, opts.Max, opts.Context, opts.Json,
            u => client.LinesAsync(u, ct), u => client.ProjectOfAsync(u, ct));
        return 0;
    }

    /// <summary>
    /// Returns a URI, not a path: a source-generated declaration has no path, and converting
    /// one to a path and back silently yields a different, nonexistent document.
    /// </summary>
    private static async Task<(string Uri, Position Position)> LocateAsync(
        LspClient client, string root, string target, CancellationToken ct)
    {
        if (TryParsePosition(target, out var file, out var line, out var column))
        {
            var full = Path.GetFullPath(Path.Combine(root, file));
            if (!File.Exists(full)) throw new CslqException($"no such file: {file}");
            return (PathUri.FromPath(full), new Position(line - 1, column - 1));
        }

        var matches = await MatchSymbolsAsync(client, target, ct);
        if (matches.Count > 1)
        {
            var rows = new List<string>(matches.Count);
            foreach (var m in matches)
            {
                var display = await PathUri.DisplayAsync(root, m.Location.Uri, x => client.ProjectOfAsync(x, ct));
                rows.Add($"  {FullName(m)}  {display}:{m.Location.Range.Start.Line + 1}");
            }

            throw new CslqException(
                $"'{target}' is ambiguous; qualify it further:\n{string.Join('\n', rows)}");
        }

        var match = matches[0];
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
    private static async Task<List<SymbolInformation>> MatchSymbolsAsync(
        LspClient client, string target, CancellationToken ct)
    {
        var candidates = await client.SymbolsAsync(LastSegment(target), ct);
        var matches = Distinct(candidates.Where(s => Matches(s, target)));

        if (matches.Count == 0)
        {
            var seen = candidates.Count == 0
                ? string.Empty
                : "\ncandidates:\n" + string.Join('\n', candidates.Select(c => "  " + FullName(c)));
            throw new CslqException($"no symbol matched '{target}'{seen}");
        }

        return matches;
    }

    // Roslyn reports a symbol once per project that sees it, so a symbol in a multi-targeted
    // or referenced project arrives several times at one location.
    private static List<SymbolInformation> Distinct(IEnumerable<SymbolInformation> symbols) => symbols
        .DistinctBy(s => (s.Location.Uri, s.Location.Range.Start.Line, s.Location.Range.Start.Character))
        .ToList();

    /// <summary>
    /// Roslyn returns containerName as a localised display string ("in Greeter (project
    /// Core (net10.0))"), not a namespace path, so a dotted target can only narrow by the
    /// identifiers that appear in it — in practice the enclosing type. A target that stays
    /// ambiguous is reported with its candidates so the caller can fall back to file:line:col.
    /// </summary>
    private static bool Matches(SymbolInformation symbol, string target)
    {
        var segments = target.Split('.');
        if (!string.Equals(symbol.Name, segments[^1], StringComparison.Ordinal)) return false;
        if (segments.Length == 1) return true;

        var tokens = Identifier().Matches(symbol.ContainerName ?? string.Empty).Select(m => m.Value);
        return tokens.Contains(segments[^2], StringComparer.Ordinal);
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();

    private static string FullName(SymbolInformation s) =>
        string.IsNullOrEmpty(s.ContainerName) ? s.Name : $"{s.Name}  {s.ContainerName}";

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
    /// incomplete answers that produced. An explicit <c>--sentinel</c> replaces the whole set
    /// with one probe scoped to the root, which is deliberately the weaker mode: it is the
    /// escape hatch for a workspace whose layout this inference cannot read.
    /// </summary>
    private static IReadOnlyList<Sentinel> Sentinels(Options opts) =>
        opts.Sentinel is { } explicitSentinel
            ? [new Sentinel(Path.GetFullPath(opts.Root), [explicitSentinel], [])]
            : InferSentinels(opts.Root);

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
    /// A root with no <c>.csproj</c> under it fails immediately. Roslyn loads nothing for such
    /// a root, so every sentinel is unresolvable and every query answers empty: waiting out the
    /// full timeout only delays the same conclusion. <c>--sentinel</c> bypasses this, which is
    /// the escape hatch for a layout the scan cannot read.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<Sentinel> InferSentinels(string root)
    {
        var projects = ProjectDirectories(root);
        if (projects.Count == 0)
        {
            // Naming the solution when there is one: "no .csproj under <root>" would be a
            // lie about a root whose solution simply lists no C# project, and would send the
            // reader looking for files that are sitting right there.
            throw new CslqException(SolutionFile(root) is { } solution
                ? $"{Path.GetFileName(solution)} lists no C# project; point --root at a "
                    + "workspace or pass --sentinel"
                : $"no .csproj under {root}; point --root at a workspace or pass --sentinel");
        }

        var sentinels = projects
            .Select(d => new Sentinel(d, Candidates(d, projects), NestedProjects(d, projects)))
            .ToList();

        return sentinels.Any(s => s.Candidates.Count > 0)
            ? sentinels
            : throw new CslqException($"could not infer a readiness sentinel under {root}; pass --sentinel");
    }

    /// <summary>
    /// Type names declared in a project's <em>own</em> files. Own excludes anything under a
    /// project nested inside this one, so a candidate taken from <c>A/B</c> cannot be what
    /// marks A ready. That is only half of it: the hit has to be scoped too, which is what
    /// <see cref="Sentinel.Nested"/> carries — <c>Web/</c> and <c>Web/Tests/</c> both
    /// declaring <c>Program</c> is the ordinary shape, and a scan of A's own files alone does
    /// not stop B's <c>Program</c> from answering A's query. A project left with no candidate
    /// of its own is reported as unprobed rather than assumed loaded.
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
    private static IReadOnlyList<string> Candidates(string directory, IReadOnlyList<string> projects)
    {
        var nested = NestedProjects(directory, projects);

        return SourceFiles(directory)
            .Where(f => !nested.Any(n => IsUnder(f, n)))
            .SelectMany(f => TypeDeclaration().Matches(NonCode().Replace(File.ReadAllText(f), " ")))
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
    }

    private static IReadOnlyList<string> NestedProjects(
        string directory, IReadOnlyList<string> projects) =>
        [.. projects.Where(p => p != directory && IsUnder(p, directory))];

    private static bool IsUnder(string path, string directory) => path.StartsWith(
        directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every project directory under the root: the solution's list when the root holds exactly
    /// one solution, and a <c>.csproj</c> scan otherwise. Either way it is an approximation of
    /// what Roslyn actually loaded, because <b>the server cannot be asked</b> —
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
    /// Exactly one solution, and only at the top of the root: two of them give no basis for
    /// choosing, and the scan — over-inclusive but never short — is the safer answer to a
    /// question this cannot answer. A project the solution lists but that is not on disk is
    /// dropped, since waiting on one would be the same unresolvable sentinel by another route.
    /// A <c>.slnf</c> solution filter is not read; it falls through to the scan.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ProjectDirectories(string root) =>
        (SolutionFile(root) is { } solution ? SolutionProjects(solution) : ScannedProjects(root))
        .Select(f => Path.GetFullPath(Path.GetDirectoryName(f)!))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// The root's own solution, if there is exactly one. Top-level only: a solution in a
    /// subdirectory describes that subtree rather than this root, and Roslyn would not open it
    /// for this root either.
    /// </summary>
    private static string? SolutionFile(string root)
    {
        var solutions = Directory
            .EnumerateFiles(root, "*.sln*", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetExtension(f) is ".sln" or ".slnx")
            .Take(2)
            .ToList();

        return solutions.Count == 1 ? solutions[0] : null;
    }

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
            return XDocument.Load(solution);
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
    /// The fallback when the root has no solution to read. Over-inclusive by construction — it
    /// cannot know what a project file is excluded from — but never short, which is the error
    /// worth making: missing a project is the incomplete-answer-at-exit-0 bug that readiness
    /// exists to close.
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
        .Order(StringComparer.OrdinalIgnoreCase);

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
        bool Daemon)
    {
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

            for (var i = 1; i < argv.Length; i++)
            {
                switch (argv[i])
                {
                    case "--root": root = Path.GetFullPath(Next(argv, ref i)); break;
                    case "--sentinel": sentinel = Next(argv, ref i); break;
                    case "--max": max = Int(argv, ref i, 1); break;
                    case "--context": context = Int(argv, ref i, 0); break;
                    // No floor of 1: a zero timeout is how `premature-query-fails-loudly`
                    // proves a query fired before load fails loudly rather than answering
                    // empty. Only a negative one is rejected.
                    case "--timeout": timeout = TimeSpan.FromSeconds(Int(argv, ref i, 0)); break;
                    case "--log-level": logLevel = Next(argv, ref i); break;
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

            return new Options(
                command, argument, root, sentinel, max, context, timeout, logLevel, errorsOnly, json,
                daemon);
        }

        private static string Next(string[] argv, ref int i)
        {
            if (++i >= argv.Length) throw new CslqException($"option '{argv[i - 1]}' needs a value");
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
