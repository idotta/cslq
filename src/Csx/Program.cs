using System.Text.RegularExpressions;

namespace Csx;

internal static partial class Program
{
    private const string Usage = """
        csx - semantic C# queries over the official roslyn-language-server

        usage:
          csx ready   [--sentinel <symbol>]
          csx refs    <symbol | file:line:col> [--max N] [--context N]
          csx def     <symbol | file:line:col> [--max N] [--context N]
          csx impl    <symbol | file:line:col> [--max N] [--context N]
          csx sym     <query> [--max N]
          csx outline <file | symbol> [--max N]
          csx diag    [path] [--errors-only] [--max N] [--context N]

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
        """;

    private static readonly string[] Commands =
        ["ready", "refs", "def", "impl", "sym", "outline", "diag"];

    private static async Task<int> Main(string[] argv)
    {
        try
        {
            return await RunAsync(argv);
        }
        catch (CsxException ex)
        {
            Console.Error.WriteLine("csx: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return argv.Length == 0 ? 2 : 0;
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
                    "csx: daemon unreachable; this run used its own cold server");
            }
        }
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
        var target = opts.Argument ?? throw new CsxException("refs needs a symbol or file:line:col");

        // Gate on a sentinel that must exist, never on the symbol being asked about:
        // otherwise a genuinely absent symbol is indistinguishable from a workspace that
        // has not finished loading, and the caller waits out the whole timeout for it.
        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, ct);
        var locations = await client.ReferencesAsync(uri, position, ct);
        await Output.WriteLocationsAsync(
            opts.Root, locations, opts.Max, opts.Context, opts.Json, u => client.LinesAsync(u, ct));
        return locations.Count == 0 ? 1 : 0;
    }

    private static async Task<int> DefAsync(
        LspClient client, Options opts, IReadOnlyList<Sentinel> sentinels, CancellationToken ct)
    {
        var target = opts.Argument ?? throw new CsxException("def needs a symbol or file:line:col");

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, ct);
        var locations = await client.DefinitionAsync(uri, position, ct);
        await Output.WriteLocationsAsync(
            opts.Root, locations, opts.Max, opts.Context, opts.Json, u => client.LinesAsync(u, ct));
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
        var target = opts.Argument ?? throw new CsxException("impl needs a symbol or file:line:col");

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var (uri, position) = await LocateAsync(client, opts.Root, target, ct);
        var locations = await client.ImplementationsAsync(uri, position, ct);
        await Output.WriteLocationsAsync(
            opts.Root, locations, opts.Max, opts.Context, opts.Json, u => client.LinesAsync(u, ct));
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
        var query = opts.Argument ?? throw new CsxException("sym needs a query");

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var matches = Distinct(await client.SymbolsAsync(query, ct));
        Output.WriteSymbols(opts.Root, matches, opts.Max, opts.Json);
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
        var target = opts.Argument ?? throw new CsxException("outline needs a file or symbol");

        await client.WaitReadyAsync(sentinels, opts.Timeout, ct);

        var uri = await OutlineTargetAsync(client, opts.Root, target, ct);
        var symbols = await client.DocumentSymbolsAsync(uri, ct);
        await Output.WriteOutlineAsync(
            opts.Root, uri, symbols, opts.Max, opts.Json, u => client.LinesAsync(u, ct));
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
        if (Directory.Exists(full)) throw new CsxException($"outline needs a file, not a directory: {path}");
        if (LooksLikePath(path)) throw new CsxException($"no such file: {path}");

        // Overloads are not ambiguity here: several matches that share a document all outline
        // to the same thing, so collapse by document and only complain if they really differ.
        var matches = await MatchSymbolsAsync(client, target, ct);
        var uris = matches.Select(m => m.Location.Uri).Distinct(StringComparer.Ordinal).ToList();
        if (uris.Count > 1)
        {
            var listing = string.Join('\n', uris.Select(u => "  " + PathUri.Display(root, u)));
            throw new CsxException($"'{target}' is declared in several documents; pick one:\n{listing}");
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
                throw new CsxException($"no such file or directory: {target}");
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
            opts.Root, findings, opts.Max, opts.Context, opts.Json, u => client.LinesAsync(u, ct));
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
            if (!File.Exists(full)) throw new CsxException($"no such file: {file}");
            return (PathUri.FromPath(full), new Position(line - 1, column - 1));
        }

        var matches = await MatchSymbolsAsync(client, target, ct);
        if (matches.Count > 1)
        {
            var listing = string.Join('\n', matches.Select(m =>
                $"  {FullName(m)}  {PathUri.Display(root, m.Location.Uri)}:{m.Location.Range.Start.Line + 1}"));
            throw new CsxException($"'{target}' is ambiguous; qualify it further:\n{listing}");
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
            throw new CsxException($"no symbol matched '{target}'{seen}");
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
    /// are one-based everywhere in <c>csx</c>, and <c>line - 1</c> would otherwise hand Roslyn
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
        ? value > 0 ? value : throw new CsxException("line and column are one-based")
        : throw new CsxException($"{name} out of range: {text}");

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
            throw new CsxException($"'{argument}' is not a position: expected file:line:col");
        }
    }

    [GeneratedRegex(@"\b(?:class|struct|record|interface|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclaration();

    /// <summary>
    /// One readiness probe per project, because a single one only ever proved that
    /// <em>some</em> project had loaded — see <see cref="LspClient.WaitReadyAsync"/> for the
    /// incomplete answers that produced. An explicit <c>--sentinel</c> replaces the whole set
    /// with one probe scoped to the root, which is deliberately the weaker mode: it is the
    /// escape hatch for a workspace whose layout this inference cannot read.
    /// </summary>
    private static IReadOnlyList<Sentinel> Sentinels(Options opts) =>
        opts.Sentinel is { } explicitSentinel
            ? [new Sentinel(Path.GetFullPath(opts.Root), [explicitSentinel])]
            : InferSentinels(opts.Root);

    /// <summary>
    /// A project per <c>.csproj</c>, and per project the type names declared in its own files,
    /// most-shallow-file-first. Several candidates rather than one because
    /// <see cref="TypeDeclaration"/> is a regex, not a parser: it matches inside comments and
    /// string literals — <c>fixture/Gen/BuildInfoGenerator.cs</c> has a
    /// <c>public static class BuildInfo</c> inside a raw string literal — and a project whose
    /// first match is <c>// class Removed</c> would otherwise block readiness forever. Capped
    /// because the list is a fallback chain, not an index.
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
    private static IReadOnlyList<Sentinel> InferSentinels(string root)
    {
        var projects = ProjectDirectories(root);
        if (projects.Count == 0)
        {
            throw new CsxException(
                $"no .csproj under {root}; point --root at a workspace or pass --sentinel");
        }

        var sentinels = projects.Select(d => new Sentinel(d, Candidates(d, projects))).ToList();

        return sentinels.Any(s => s.Candidates.Count > 0)
            ? sentinels
            : throw new CsxException($"could not infer a readiness sentinel under {root}; pass --sentinel");
    }

    /// <summary>
    /// Type names declared in a project's <em>own</em> files. Own excludes anything under a
    /// project nested inside this one: <c>LspClient.Under</c> counts a hit for a project when
    /// it lands anywhere below its directory, so a candidate taken from <c>A/B</c> would let B
    /// loading mark A ready. That is the every-project-loaded guarantee failing quietly, which
    /// is the whole bug this readiness model exists to close. A project left with no candidate
    /// of its own is reported as unprobed rather than assumed loaded.
    /// </summary>
    private static IReadOnlyList<string> Candidates(string directory, IReadOnlyList<string> projects)
    {
        var nested = projects.Where(p => p != directory && IsUnder(p, directory)).ToList();

        return SourceFiles(directory)
            .Where(f => !nested.Any(n => IsUnder(f, n)))
            .Select(f => TypeDeclaration().Match(File.ReadAllText(f)))
            .Where(m => m.Success)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
    }

    private static bool IsUnder(string path, string directory) => path.StartsWith(
        directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every project directory under the root, by <c>.csproj</c> scan. An approximation of what
    /// Roslyn actually loaded — the server exposes no project list to ask instead
    /// (<c>workspace/_roslyn_restorableProjects</c> is a server-to-client request carrying
    /// none) — so a <c>.csproj</c> excluded from a solution would be waited on needlessly. It
    /// errs that way on purpose: the alternative, missing a project, is the bug this exists to
    /// close.
    /// </summary>
    private static IReadOnlyList<string> ProjectDirectories(string root) => Directory
        .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Select(f => Path.GetFullPath(Path.GetDirectoryName(f)!))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

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

    private sealed record Options(
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
            // `csx bogus --root <dir with no .csproj>` reported the missing project.
            string command = argv[0] is var c && Commands.Contains(c)
                ? c
                : throw new CsxException($"unknown command '{argv[0]}'\n\n{Usage}");
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
                        if (argv[i].StartsWith('-')) throw new CsxException($"unknown option '{argv[i]}'");
                        if (argument is not null) throw new CsxException($"unexpected argument '{argv[i]}'");
                        argument = argv[i];
                        break;
                }
            }

            if (!Directory.Exists(root)) throw new CsxException($"no such directory: {root}");

            // Here rather than in LocateAsync: that runs after StartAsync and WaitReadyAsync,
            // so `file:0:1` would start a server and wait out readiness before printing an
            // argument error. PositionSpec cannot match a bare symbol name, so checking every
            // command's argument has no false positives.
            if (argument is not null) ValidatePosition(argument);

            return new Options(
                command, argument, root, sentinel, max, context, timeout, logLevel, errorsOnly, json,
                daemon);
        }

        private static string Next(string[] argv, ref int i)
        {
            if (++i >= argv.Length) throw new CsxException($"option '{argv[i - 1]}' needs a value");
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
                throw new CsxException(magnitude.Length > 0 && magnitude.All(char.IsAsciiDigit)
                    ? $"{name} out of range: {text}"
                    : $"{name} needs an integer");
            }

            if (value < floor) throw new CsxException($"{name} needs to be {floor} or more");
            return value;
        }
    }
}
