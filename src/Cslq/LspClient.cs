using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StreamJsonRpc;

namespace Cslq;

internal sealed class LspClient : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly JsonRpc _rpc;
    private readonly Endpoints _endpoints;
    private readonly StringBuilder _stderr;
    private readonly HashSet<string> _open = new(PathUri.PathComparer);
    private readonly Dictionary<string, string[]> _lines = new(PathUri.PathComparer);
    private readonly Dictionary<string, (string? File, string? Tfm)> _projects = new(PathUri.PathComparer);
    private readonly bool _daemon;
    private readonly CancellationToken _ct;

    private static readonly TimeSpan BindBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long <see cref="WaitReadyAsync"/> keeps asking after this process saw
    /// <c>projectInitializationComplete</c>. Generous against the one thing that could still
    /// be in flight — the indexer catching up on a large solution — and short against the
    /// case it exists for, a candidate that can never resolve holding the full timeout.
    /// </summary>
    private static readonly TimeSpan PostLoadGrace = TimeSpan.FromSeconds(20);

    private const int HandleFlagInherit = 0x1;

    public string Root { get; }

    private LspClient(
        string root, Process proc, JsonRpc rpc, Endpoints endpoints, StringBuilder stderr,
        bool daemon, CancellationToken ct)
        => (Root, _proc, _rpc, _endpoints, _stderr, _daemon, _ct)
            = (root, proc, rpc, endpoints, stderr, daemon, ct);

    /// <summary>
    /// Starts the server, and on the one failure a first-time user always hits — the pinned
    /// tool never restored — restores it once and retries. Deliberately not a preflight
    /// check: `dotnet tool restore` costs a second even when everything is already there,
    /// and every run would pay it to save the first one.
    /// </summary>
    public static async Task<LspClient> StartAsync(string root, string logLevel, bool daemon, CancellationToken ct)
    {
        var manifestRoot = ServerArgs.ToolManifestRoot();
        try
        {
            return await StartCoreAsync(root, manifestRoot, logLevel, daemon, ct);
        }
        catch (CslqException ex) when (NotRestored(ex.Message))
        {
            Console.Error.WriteLine(
                $"cslq: the pinned language server is not restored; restoring it in {manifestRoot}. " +
                "This is a one-time ~300 MB download.");
            var pruned = await RestoreAsync(manifestRoot, ct);
            foreach (var line in Output.PruneLines(pruned)) Console.Error.WriteLine("cslq: " + line);
            return await StartCoreAsync(root, manifestRoot, logLevel, daemon, ct);
        }
    }

    /// <summary>
    /// The `dotnet tool run` message naming the fix. The prose around it is localised — this
    /// machine answers in Portuguese without <c>DOTNET_CLI_UI_LANGUAGE</c> — but the quoted
    /// command inside it is not, so match on that alone.
    /// </summary>
    internal static bool NotRestored(string stderr) =>
        stderr.Contains("dotnet tool restore", StringComparison.Ordinal);

    /// <summary>
    /// Also the whole of <c>cslq restore</c>, which is why this is not private: the pre-warm
    /// a Dockerfile or a CI job runs is exactly this restore, asked for rather than recovered
    /// from. A restore that succeeds is followed by <see cref="Prune.Run"/>: the new pin is
    /// now the only version this binary can run, so the others stop costing 300 MB each. The
    /// prune is reported, never fatal — null when the packages folder could not be found.
    /// </summary>
    internal static async Task<Prune.Result?> RestoreAsync(string manifestRoot, CancellationToken ct)
    {
        await RestoreCoreAsync(manifestRoot, ct);

        var pin = Prune.PinnedVersion(
            await File.ReadAllTextAsync(Path.Combine(manifestRoot, ".config", "dotnet-tools.json"), ct));
        var packages = await GlobalPackagesAsync(manifestRoot, ct);
        return pin is null || packages is null ? null : Prune.Run(packages, pin);
    }

    /// <summary>
    /// Where NuGet extracts packages, asked of the CLI rather than assumed: <c>NUGET_PACKAGES</c>
    /// and a <c>globalPackagesFolder</c> in any NuGet.Config both move it. The line is
    /// <c>global-packages: &lt;path&gt;</c>; the label is not localised with the UI language
    /// pinned, and the path is everything after the first <c>: </c> because on Windows it
    /// carries a colon of its own. Null when the CLI would not say. Run from the manifest
    /// root, as the restore was: NuGet.Config is resolved from the working directory, so a
    /// repository with its own <c>globalPackagesFolder</c> would otherwise name a folder the
    /// restore never wrote to.
    /// </summary>
    private static async Task<string?> GlobalPackagesAsync(string manifestRoot, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ServerArgs.Command)
        {
            WorkingDirectory = manifestRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in ServerArgs.GlobalPackages()) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using var proc = StartProcess(psi, "locate the NuGet global packages folder");
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        _ = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0) return null;

        foreach (var line in (await stdout).Split('\n'))
        {
            var sep = line.IndexOf(": ", StringComparison.Ordinal);
            if (sep < 0 || !line.StartsWith("global-packages", StringComparison.Ordinal)) continue;
            var path = line[(sep + 2)..].Trim();
            return path.Length == 0 ? null : Path.TrimEndingDirectorySeparator(path);
        }

        return null;
    }

    private static async Task RestoreCoreAsync(string manifestRoot, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ServerArgs.Command)
        {
            WorkingDirectory = manifestRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in ServerArgs.Restore()) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using (var proc = StartProcess(psi, $"restore the pinned language server in {manifestRoot}"))
        {
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C mid-restore: the payload behind the pin is ~300 MB, so leaving it
                // running orphans a download nothing will ever wait on. Tree, as
                // DisposeAsync does for a dedicated server — the child is ours alone.
                try
                {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already exited.
                }

                throw;
            }

            if (proc.ExitCode != 0)
            {
                var why = (await stderr).Trim();
                if (why.Length == 0) why = (await stdout).Trim();
                throw new CslqException(
                    $"'dotnet tool restore' failed in {manifestRoot} (exit {proc.ExitCode}): {Firstline(why)} "
                    + $"Run 'dotnet tool restore' in {manifestRoot} once that is fixed.");
            }
        }
    }

    /// <summary>
    /// Windows <c>CreateProcess</c> is called with <c>bInheritHandles=TRUE</c>, so cslq's own
    /// std handles reach the thin client and, through it, the daemon — which outlives us. A
    /// harness that captures our output then waits for EOF on a pipe the daemon still holds,
    /// so a launching call blocks for the whole keepalive and the daemon is dead by the time
    /// it returns. Clearing the inherit flag before every launch is what stops the leak.
    /// Unix is unaffected: .NET opens its own descriptors <c>O_CLOEXEC</c> and dup2s only the
    /// redirected ends.
    /// </summary>
    private static void DisableStdioInheritance()
    {
        if (!OperatingSystem.IsWindows()) return;

        // STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE. Best effort: a process with
        // no console has invalid std handles, and it must not be broken by this.
        foreach (var id in (ReadOnlySpan<int>)[-10, -11, -12])
        {
            var handle = Native.GetStdHandle(id);
            if (handle == nint.Zero || handle == -1) continue;
            Native.SetHandleInformation(handle, HandleFlagInherit, 0);
        }
    }

    /// <summary>
    /// Every <c>dotnet</c> launch goes through here. <see cref="Process.Start(ProcessStartInfo)"/>
    /// throws <see cref="System.ComponentModel.Win32Exception"/> when <c>dotnet</c> is off
    /// <c>PATH</c> — the first-time-user case exactly — and that escaped as a stack trace and
    /// exit 127 rather than as a <c>cslq:</c> line naming the fix.
    /// </summary>
    private static Process StartProcess(ProcessStartInfo psi, string what)
    {
        DisableStdioInheritance();
        try
        {
            return Process.Start(psi) ?? throw new CslqException($"could not {what}: no process started.");
        }
        catch (Exception ex) when (ex is not CslqException and not OperationCanceledException)
        {
            throw new CslqException(
                $"could not {what}: {Firstline(ex.Message)} "
                + $"cslq runs the language server with '{ServerArgs.Command}', so the .NET 10 SDK "
                + "must be installed and on PATH.");
        }
    }

    private static string Firstline(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "no output";
        return line.Length > 200 ? line[..200] : line;
    }

    private static async Task<LspClient> StartCoreAsync(
        string root, string manifestRoot, string logLevel, bool daemon, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ServerArgs.Command)
        {
            WorkingDirectory = manifestRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var args = daemon ? ServerArgs.Daemon(logLevel) : ServerArgs.Stdio(logLevel);
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Roslyn localises the display strings it puts in LSP responses. Pin English so
        // output is the same for an agent regardless of the developer's machine locale.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        var proc = StartProcess(psi, "start the language server");

        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) stderr.AppendLine(e.Data); } };
        proc.BeginErrorReadLine();

        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = Lsp.Options };
        var handler = new HeaderDelimitedMessageHandler(
            proc.StandardInput.BaseStream, proc.StandardOutput.BaseStream, formatter);

        var endpoints = new Endpoints();
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(endpoints);
        rpc.StartListening();

        var client = new LspClient(root, proc, rpc, endpoints, stderr, daemon, ct);
        try
        {
            await client.InitializeAsync(ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or CslqException)
        {
            // Escapes unchanged, but not uncleaned: without this the client built above is
            // dropped with its process and RPC connection still live. A CslqException means
            // initialize was answered and we rejected the answer — the encoding assertion —
            // so the connection-lost wrapping below would be a lie about a live server.
            await client.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            // The thin client can die before it answers initialize — a daemon that never came
            // up, for one — and StreamJsonRpc then reports nothing but a lost connection. Give
            // the process a moment to finish exiting so its stderr, the only thing that says
            // why, is flushed before we quote it. Quote it before disposing: StderrTail is the
            // only thing that turns "connection lost" into a diagnosis.
            await Task.WhenAny(proc.WaitForExitAsync(ct), Task.Delay(1000, ct));
            var tail = client.StderrTail();
            await client.DisposeAsync();
            throw new CslqException(
                $"the language server closed the connection during initialize: {ex.Message}{tail}");
        }

        return client;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var uri = PathUri.FromPath(Root);
        var init = new InitializeParams(
            Environment.ProcessId,
            new ClientInfo("cslq", Build.Version),
            "en",
            uri,
            new ClientCapabilities(
                new GeneralCapabilities([ServerArgs.ExpectedPositionEncoding]),
                new TextDocumentCapabilities(
                    new SynchronizationCapabilities(true),
                    new DiagnosticCapabilities(true, true),
                    new DocumentSymbolCapabilities(true, true),
                    new HoverCapabilities(true, ["plaintext"])),
                new WorkspaceCapabilities(true, true, new SymbolCapabilities(true)),
                new WindowCapabilities(true)),
            [new WorkspaceFolder(uri, Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar)))]);

        var result = await _rpc.InvokeWithParameterObjectAsync<InitializeResult>("initialize", init, ct);

        // Absent means utf-16 per LSP 3.17. A server that started answering utf-8 would
        // silently shift every column on a non-ASCII line, so refuse rather than adapt.
        var encoding = result.Capabilities.PositionEncoding ?? ServerArgs.ExpectedPositionEncoding;
        if (encoding != ServerArgs.ExpectedPositionEncoding)
        {
            throw new CslqException(
                $"Server negotiated positionEncoding '{encoding}'; cslq assumes '{ServerArgs.ExpectedPositionEncoding}'.");
        }

        await _rpc.NotifyWithParameterObjectAsync("initialized", new { });
    }

    /// <summary>
    /// A query fired before the workspace loads returns an empty result, not an error, so
    /// readiness has to be established rather than assumed. Polls from the start: a client
    /// attaching to an already-loaded daemon never sees
    /// <c>projectInitializationComplete</c> — it fired before this process existed — so
    /// waiting on the notification first burned the entire timeout on a workspace that was
    /// ready before we connected. The notification is kept only as diagnostic detail on the
    /// failure path.
    /// <para>
    /// One sentinel per project, and every project that has one has to resolve it — see the
    /// last paragraph for the ones that have none. A single sentinel only
    /// ever proved that <em>some</em> project loaded, which is the race behind every
    /// incomplete answer this client has produced: a cross-project <c>refs</c> or <c>impl</c>
    /// missing the half that had not loaded, and a <c>sym</c> search missing a whole project's
    /// hits — all of them exit 0, because a short answer is not an error. Measured on the wire
    /// 2026-09-05: against a <em>cold</em> server <c>workspace/symbol</c> answers nothing at
    /// all until <c>projectInitializationComplete</c> and then jumps straight to complete, so
    /// the partial window belongs to a client attaching to a daemon that is loading a root it
    /// has not loaded before — exactly the case where the notification cannot help.
    /// </para>
    /// <para>
    /// A hit only counts for the project that asked for it: its location has to sit under that
    /// project's own directory. Two projects declaring <c>Program</c> is the ordinary case in a
    /// real repo, and without the check one project's symbol would satisfy another's sentinel
    /// and readiness would lie again. Matching on <c>containerName</c> would be the obvious
    /// alternative and is wrong — it is localised display text.
    /// </para>
    /// <para>
    /// A project that contributed no candidate is not waited on — there is nothing to ask for
    /// — but it is named on the failure path so its absence from readiness is visible rather
    /// than silent. <c>Program.InferSentinels</c> guarantees at least one project does
    /// contribute, so this never degrades to waiting for nothing at all.
    /// </para>
    /// <para>
    /// The wait is bounded to <see cref="PostLoadGrace"/> past
    /// <c>projectInitializationComplete</c>, but only when that notification arrived <em>in
    /// this process</em> — which is what <c>ProjectInitialized.IsCompleted</c> means, since
    /// nothing but our own notification handler ever completes that task. The two cases
    /// differ because of the wire behaviour above: on a cold load the server answers nothing
    /// until the notification and then jumps straight to complete, so a candidate still
    /// unresolved after it is never going to resolve — a project whose only type the regex
    /// read out of an <c>#if false</c> branch is the shape that does this, and it should fail
    /// in seconds rather than hold the whole <c>--timeout</c>. On a daemon attach the
    /// notification fired before this process existed and there is nothing to bound from;
    /// that is also the case where the incomplete-answer window lives, so the full timeout is
    /// exactly what is wanted there. A <c>--timeout</c> shorter than the grace still wins.
    /// </para>
    /// </summary>
    public async Task WaitReadyAsync(
        IReadOnlyList<Sentinel> sentinels, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var pending = sentinels.Where(s => s.Candidates.Count > 0).ToList();
        var unprobed = sentinels.Where(s => s.Candidates.Count == 0 && !s.Skipped).ToList();
        var linked = sentinels.Where(s => s.Skipped).ToList();
        DateTime? loaded = null;

        while (true)
        {
            // One round for every project at once, so a warm workspace costs a single
            // round trip's wall-clock rather than one per project.
            var resolved = await Task.WhenAll(pending.Select(s => ResolvesAsync(s, ct)));
            pending = [.. pending.Where((_, i) => !resolved[i])];
            if (pending.Count == 0) return;
            if (_endpoints.ProjectInitialized.IsCompleted) loaded ??= DateTime.UtcNow;
            var bound = loaded is null || deadline < loaded.Value + PostLoadGrace
                ? deadline
                : loaded.Value + PostLoadGrace;
            if (DateTime.UtcNow >= bound) break;
            await Task.Delay(250, ct);
        }

        var fired = _endpoints.ProjectInitialized.IsCompleted ? "fired" : "never fired";
        var names = string.Join(", ", pending.Select(s => $"'{string.Join("' / '", s.Candidates)}'"));
        // Projects nothing probed are named too: readiness says nothing about them either way,
        // and leaving them out is the same quiet degradation the per-project set exists to end.
        var skipped = string.Concat(
            unprobed.Count == 0
                ? string.Empty
                : $" Not probed at all, for want of a type declaration: {Names(unprobed)}.",
            linked.Count == 0
                ? string.Empty
                : $" Not probed at all, having no sources of their own: {Names(linked)}.");
        throw new CslqException(
            $"Workspace did not become ready within {timeout.TotalSeconds:0}s: sentinel query {names} " +
            $"returned no symbols for project(s) {Names(pending)} " +
            $"(projectInitializationComplete {fired}).{skipped}{StderrTail()}");
    }

    /// <summary>
    /// Whether any of a project's candidate sentinels resolves to a hit the project accepts —
    /// see <see cref="Sentinel.Accepts"/> for what that scoping is worth. Several candidates
    /// because <c>Program.InferSentinels</c>'s type-declaration match is a regex, not a
    /// parser, and one bad guess would otherwise block readiness for the run.
    /// </summary>
    private async Task<bool> ResolvesAsync(Sentinel sentinel, CancellationToken ct)
    {
        foreach (var candidate in sentinel.Candidates)
        {
            var hits = await SymbolsAsync(candidate, ct);
            if (hits.Any(h => sentinel.Accepts(h.Location.Uri))) return true;
        }

        return false;
    }

    /// <summary>
    /// A project is named by its directory; the probe an explicit <c>--sentinel</c> adds is not
    /// a project and is named as what it is, since its directory is the root and would read as
    /// a project called after the repository.
    /// </summary>
    private static string Names(IEnumerable<Sentinel> sentinels) => string.Join(
        ", ",
        sentinels.Select(s => s.Explicit
            ? $"explicit sentinel '{string.Join("' / '", s.Candidates)}'"
            : Path.GetFileName(s.Directory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));

    public async Task<IReadOnlyList<SymbolInformation>> SymbolsAsync(string query, CancellationToken ct)
    {
        var result = await RequestAsync<SymbolInformation[]?>(
            "workspace/symbol", new WorkspaceSymbolParams(query), ct);
        return result ?? [];
    }

    public async Task<IReadOnlyList<Location>> ReferencesAsync(string uri, Position position, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await SettleAsync(async () =>
            await RequestAsync<Location[]?>(
                "textDocument/references",
                new ReferenceParams(new TextDocumentIdentifier(uri), position, new ReferenceContext(true)),
                ct) ?? [], ct);
    }

    /// <summary>
    /// Location[], not LocationLink[]: the client does not declare
    /// textDocument.definition.linkSupport, so per LSP 3.17 the server owes us the plain form.
    /// Deliberately no two-shape reader — if that ever stops holding, a deserialization
    /// failure is a better outcome than silently rendering half a response.
    /// </summary>
    public async Task<IReadOnlyList<Location>> DefinitionAsync(string uri, Position position, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await SettleAsync(async () =>
            await RequestAsync<Location[]?>(
                "textDocument/definition",
                new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position),
                ct) ?? [], ct);
    }

    /// <summary>
    /// Same <c>Location[]</c> reasoning as <see cref="DefinitionAsync"/>, and for a stronger
    /// reason: the client declares no <c>textDocument.implementation</c> capability node at
    /// all, so <c>linkSupport</c> is absent by construction. Roslyn does not answer empty for
    /// a member that simply has no implementations — it falls through to the declaration, so
    /// this degenerates to <c>definition</c> on an ordinary method. Empty means the position
    /// resolved to no symbol.
    /// </summary>
    public async Task<IReadOnlyList<Location>> ImplementationsAsync(string uri, Position position, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await SettleAsync(async () =>
            await RequestAsync<Location[]?>(
                "textDocument/implementation",
                new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position),
                ct) ?? [], ct);
    }

    /// <summary>
    /// Re-asks while the answer is decompiled metadata <em>and</em> the workspace declares the
    /// type itself. Roslyn binds a <c>ProjectReference</c> to the referenced project's built
    /// assembly until that project is loaded, so a query fired in the window between the
    /// sentinel resolving and the last project loading comes back pointing at a temp file under
    /// <c>MetadataAsSource</c> — a confident wrong answer, exit 0, no relation to the repo.
    /// <para>
    /// The second condition is what this used to lack, and the framework case paid for it.
    /// Measured 2026-09-07 against 5.12.0-1.26426.8: <c>def</c> at <c>Console.WriteLine</c>
    /// burned the whole 10 s budget over 40 identical queries and then returned the same
    /// decompiled answer it had on the first — that document <em>is</em> the definition, so
    /// there was never anything to wait for. The stale-binding case meanwhile did not occur
    /// once in four cold runs against the fixture, readiness-per-project having closed the
    /// window it needs; the guard is kept rather than deleted because that is an absence of
    /// evidence over three projects, and it now costs one <c>workspace/symbol</c> query on the
    /// metadata path instead of ten seconds.
    /// </para>
    /// <para>
    /// The discriminator is the workspace itself: a decompiled document is named after the type
    /// it stands for, so asking whether any source file under the root declares that name
    /// separates "bound to an assembly whose source is right here" from "bound to an assembly
    /// because that is all there is". A workspace that declares its own <c>Console</c> would
    /// pay the budget on a framework <c>def</c>, which is the accepted cost of keeping the
    /// guard.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<Location>> SettleAsync(
        Func<Task<IReadOnlyList<Location>>> query, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + BindBudget;
        while (true)
        {
            var locations = await query();
            var decompiled = locations.Where(l => PathUri.IsDecompiled(l.Uri)).ToList();
            if (decompiled.Count == 0) return locations;
            if (!await StaleBindingAsync(decompiled, ct)) return locations;
            if (DateTime.UtcNow >= deadline) return locations;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// Whether a decompiled answer is the not-yet-loaded fingerprint rather than the real
    /// definition — see <see cref="SettleAsync"/>. Asked again on every round rather than
    /// cached, because the whole point is that the workspace is still changing underneath.
    /// <para>
    /// The hits are filtered to an exact ordinal name match first, and that filter is the
    /// whole guard. <c>workspace/symbol</c> answers prefix and substring matches too, so a
    /// workspace that merely declares a <c>ConsoleBanner</c> would otherwise make every
    /// framework <c>def</c> at <c>Console.WriteLine</c> look like a stale binding and burn
    /// the entire budget — the exact failure the second condition was added to remove.
    /// </para>
    /// </summary>
    private async Task<bool> StaleBindingAsync(IEnumerable<Location> decompiled, CancellationToken ct)
    {
        foreach (var name in decompiled
            .Select(l => PathUri.MetadataTypeName(l.Uri))
            .Distinct(StringComparer.Ordinal))
        {
            var hits = await SymbolsAsync(name, ct);
            if (PathUri.AnyUnder(
                Root,
                hits.Where(h => string.Equals(h.Name, name, StringComparison.Ordinal))
                    .Select(h => h.Location.Uri))) return true;
        }

        return false;
    }

    /// <summary>
    /// The answer to "what is this". Roslyn's hover carries the full signature including
    /// parameter types and the doc-comment summary, so <c>textDocument/signatureHelp</c> is
    /// not wired: it would add a second request for information already in this one, and it
    /// only answers inside an argument list rather than at a symbol. Verified on the wire
    /// against 5.12.0-1.26426.8, 2026-09-07 — <c>void Console.WriteLine(string? value)
    /// (+ 19 overloads)</c> plus the summary and the <c>Exceptions:</c> list.
    /// <para>
    /// Null for a position that resolves to no symbol, which is how the empty answer arrives:
    /// the server sends JSON <c>null</c> rather than an empty <c>contents</c>. No
    /// <see cref="SettleAsync"/> around it — a hover names a type, it does not point at a
    /// document, so a metadata binding is not a wrong answer here.
    /// </para>
    /// </summary>
    public async Task<Hover?> HoverAsync(string uri, Position position, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        return await RequestAsync<Hover?>(
            "textDocument/hover",
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position),
            ct);
    }

    public async Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolsAsync(string uri, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        var result = await RequestAsync<DocumentSymbol[]?>(
            "textDocument/documentSymbol",
            new DocumentSymbolParams(new TextDocumentIdentifier(uri)),
            ct);
        return result ?? [];
    }

    /// <summary>
    /// One pull. This used to re-pull until two consecutive reports agreed, on the premise that
    /// a freshly opened document is bound against the misc-files state and under-reports until
    /// its project references resolve. <b>Measured false on 2026-09-06</b> against
    /// 5.12.0-1.26426.8: the endpoint does not answer early, it <em>blocks</em> until the
    /// document is bound. A cross-project error opened as the first document in a never-used
    /// daemon returns the correct CS0029 on the first pull — that pull costs ~4.2 s and the
    /// redundant second one ~0.7 s. Across six whole-fixture runs, cold daemon and warm, the
    /// second pull never once differed from the first, so the loop bought a mandatory 250 ms
    /// delay plus a duplicate round trip per file and nothing else: removing it halved the
    /// per-file cost, 570 ms to 294 ms warm. <c>cold-server-diag-reports-cross-project-error</c>
    /// in <c>probes/run.sh</c> is the guard — it is the only leg that pulls a document the
    /// daemon has never opened, which is the one state where answering before binding shows up.
    /// Note the old loop could not have caught that case anyway: two equally-wrong pulls agree.
    /// </summary>
    public async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(string uri, CancellationToken ct)
    {
        await OpenAsync(uri, ct);
        var report = await RequestAsync<DocumentDiagnosticReport?>(
            "textDocument/diagnostic",
            new DocumentDiagnosticParams(new TextDocumentIdentifier(uri)),
            ct);
        return report?.Items ?? [];
    }

    /// <summary>
    /// Roslyn will not answer requests for a document it does not consider open. Generated
    /// documents are the exception: they are the server's own, it answers for them without a
    /// didOpen, and there is no file to read the text from anyway.
    /// </summary>
    public async Task OpenAsync(string uri, CancellationToken ct)
    {
        if (PathUri.IsGenerated(uri) || !_open.Add(uri)) return;
        var text = await File.ReadAllTextAsync(PathUri.ToPath(uri), ct);
        await NotifyAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams(new TextDocumentItem(uri, "csharp", 1, text)));
    }

    /// <summary>
    /// Document text for rendering context lines: off disk for a real file, from the server
    /// for a generated one. An unreadable document yields no lines rather than failing —
    /// a hit with a correct position is still worth printing.
    /// </summary>
    public async Task<string[]> LinesAsync(string uri, CancellationToken ct)
    {
        if (_lines.TryGetValue(uri, out var cached)) return cached;

        string[] lines;
        if (PathUri.IsGenerated(uri))
        {
            var content = await RequestAsync<TextDocumentContentResult?>(
                "workspace/textDocumentContent", new TextDocumentContentParams(uri), ct);
            // Trailing newline dropped so a generated document splits the way
            // File.ReadAllLines would, instead of printing a phantom blank context row.
            var text = content?.Text.ReplaceLineEndings("\n");
            if (text is not null && text.EndsWith('\n')) text = text[..^1];
            lines = text is null ? [] : text.Split('\n');
        }
        else
        {
            var path = PathUri.ToPath(uri);
            lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, ct) : [];
        }

        _lines[uri] = lines;
        return lines;
    }

    /// <summary>
    /// The assembly a decompiled document was produced from, for its <c>&lt;metadata&gt;</c>
    /// label. Roslyn writes it into a <c>#region Assembly</c> header at the top of the file,
    /// which is the only place it exists — the URI is a temp path made of two run-specific
    /// guids and the type's name. Read through <see cref="LinesAsync"/>, so it costs the same
    /// single file read the context lines already pay for.
    /// </summary>
    public async Task<string?> AssemblyOfAsync(string uri, CancellationToken ct)
    {
        if (!PathUri.IsDecompiled(uri)) return null;
        var lines = await LinesAsync(uri, ct);
        return PathUri.MetadataAssembly(lines.FirstOrDefault());
    }

    /// <summary>
    /// The <c>.csproj</c> a document belongs to, or null if the server will not say. Only a
    /// source-generated document needs asking: a file URI already carries its own path, and
    /// the generated URI's query names the <em>generator</em> assembly, never the project
    /// consuming it — so one generator applied to several projects yields several distinct
    /// documents whose labels are otherwise identical.
    /// <para>
    /// <c>textDocument/_vs_getProjectContexts</c> is a VS protocol extension rather than LSP,
    /// and the server neither advertises it nor requires a matching client capability
    /// (verified against 5.12.0-1.26426.8, 2026-09-06). <c>_vs_id</c> is
    /// <c>&lt;projectId guid&gt;|&lt;absolute .csproj&gt; ($&lt;tfm&gt;)</c>: the guid is
    /// regenerated per load and the path is not, so only the path is read. A multi-targeted
    /// document has one context per TFM, all naming one <c>.csproj</c>, and
    /// <c>_vs_defaultIndex</c> picks among them.
    /// </para>
    /// <para>
    /// Failure is not fatal: an unanswerable label falls back to the generator-only form,
    /// which is what every label looked like before this existed. Cached because a generated
    /// document usually contributes several hits to one answer.
    /// </para>
    /// </summary>
    public async Task<string?> ProjectOfAsync(string uri, CancellationToken ct) =>
        (await ProjectContextAsync(uri, ct)).File;

    /// <summary>
    /// The same lookup with the target framework kept. The <c>($tfm)</c> suffix rides on the
    /// <c>_vs_id</c> and <see cref="ProjectOfAsync"/> throws it away; <c>cslq project</c> is
    /// the one caller that wants it, because "which project compiles this file" is only half
    /// answered without the framework a multi-targeted project compiles it for.
    /// </summary>
    public async Task<(string? File, string? Tfm)> ProjectContextAsync(string uri, CancellationToken ct)
    {
        if (_projects.TryGetValue(uri, out var cached)) return cached;

        (string? File, string? Tfm) project = (null, null);
        try
        {
            var list = await _rpc.InvokeWithParameterObjectAsync<ProjectContextList?>(
                "textDocument/_vs_getProjectContexts",
                new ProjectContextParams(new TextDocumentIdentifier(uri)),
                ct);

            var contexts = list?.Contexts ?? [];
            var index = list is not null && list.DefaultIndex >= 0 && list.DefaultIndex < contexts.Length
                ? list.DefaultIndex
                : 0;
            if (contexts.Length > 0) project = ProjectFile(contexts[index].Id);
        }
        catch (RemoteRpcException)
        {
            // RemoteRpcException, not RemoteInvocationException: a server that drops the
            // extension answers RemoteMethodNotFoundException, which is a sibling of the
            // latter, not a subclass -- catching the narrower type would turn a coarser label
            // into a crash. The generator-only label is a correct if coarser answer.
            //
            // The one call that deliberately bypasses RequestAsync, for that reason: this is
            // an optional VS extension the server need not implement, so a failure here is a
            // label to soften rather than a command to fail. Every other request wants the
            // wrapper's CslqException, which this catch would swallow into a silent (null,
            // null) project.
        }

        _projects[uri] = project;
        return project;
    }

    /// <summary>
    /// The path half of a <c>_vs_id</c>, with the <c>($tfm)</c> suffix the id carries for a
    /// multi-targeted project removed. Anything else shaped unexpectedly yields null rather
    /// than a guess: a wrong project in the label is worse than no project.
    /// </summary>
    internal static (string? File, string? Tfm) ProjectFile(string id)
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

    /// <summary>
    /// Whether the thin client gave up on the daemon and started its own server. It does that
    /// silently — a fallback run answers correctly, just cold, and these two lines on stderr
    /// are the only difference — so an agent would otherwise blame the latency on us. Read
    /// after the command has run, not right after connecting: the marker is written while the
    /// pipe is being established, which races the initialize response.
    /// </summary>
    public bool DaemonFallback
    {
        get
        {
            string text;
            lock (_stderr) { text = _stderr.ToString(); }
            return DaemonFallbackMarkers.Any(m => text.Contains(m, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Whether this run owns its server outright. A daemon run that fell back has a private
    /// server nothing else will ever attach to, so it is as dedicated as <c>--no-daemon</c>.
    /// </summary>
    private bool Dedicated => !_daemon || DaemonFallback;

    private static readonly string[] DaemonFallbackMarkers =
    [
        "Falling back to non-daemon mode",
        "non-daemon fallback mode",
    ];

    /// <summary>
    /// Every post-initialize request goes through here. <c>Main</c> catches
    /// <see cref="CslqException"/> and <see cref="OperationCanceledException"/> and nothing
    /// else, so a bare <c>_rpc.Invoke</c> turns a server-side rejection or a daemon that died
    /// mid-request into a stack trace and exit 127. <c>initialize</c> keeps its own wrapping
    /// in <see cref="StartCoreAsync"/> — it has a different message and its own disposal — and
    /// <see cref="ProjectContextAsync"/> is the one deliberate bypass; see the catch there.
    /// </summary>
    private async Task<T?> RequestAsync<T>(string method, object? @params, CancellationToken ct)
    {
        try
        {
            return await _rpc.InvokeWithParameterObjectAsync<T?>(method, @params, ct);
        }
        catch (Exception ex) when (Describe(method, ex, PipeName) is { } message)
        {
            throw new CslqException(message + (ex is ConnectionLostException ? StderrTail() : string.Empty));
        }
    }

    /// <summary>
    /// The notification twin. A notification is never answered, so it cannot be rejected —
    /// but it is still a write to the connection, and a daemon that has gone away fails it
    /// with the same <see cref="ConnectionLostException"/> a request would.
    /// </summary>
    private async Task NotifyAsync(string method, object? @params)
    {
        try
        {
            await _rpc.NotifyWithParameterObjectAsync(method, @params);
        }
        catch (Exception ex) when (Describe(method, ex, PipeName) is { } message)
        {
            throw new CslqException(message + (ex is ConnectionLostException ? StderrTail() : string.Empty));
        }
    }

    /// <summary>
    /// The message for a failed request, or null for an exception that must escape untouched
    /// — a cancellation above all, since Ctrl+C is exit 130 and wrapping it as a
    /// <see cref="CslqException"/> would report it as exit 1. Pure, so the wording is
    /// testable without a live connection.
    /// </summary>
    internal static string? Describe(string method, Exception ex, string? pipe) => ex switch
    {
        ConnectionLostException => $"the language server connection was lost during {method}" +
            (pipe is null ? string.Empty : $" (daemon pipe '{pipe}')") +
            "; rerun to relaunch it.",
        RemoteRpcException => $"{method} failed: {ex.Message}",
        _ => null,
    };

    /// <summary>
    /// The daemon pipe this run is talking over, for the connection-lost message, or null
    /// when there is no daemon to name — <c>--no-daemon</c>, a run that fell back to its own
    /// server, or a default pipe name only the thin client knows.
    /// </summary>
    private string? PipeName => Dedicated
        ? null
        : Environment.GetEnvironmentVariable("ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME");

    public string StderrTail(int lines = 12)
    {
        string text;
        lock (_stderr) { text = _stderr.ToString(); }
        if (text.Length == 0) return string.Empty;
        var tail = text.TrimEnd().Split('\n');
        return "\n--- server stderr ---\n" + string.Join('\n', tail[Math.Max(0, tail.Length - lines)..]);
    }

    /// <summary>
    /// Ctrl+C has to be felt at the prompt, so a cancelled teardown skips the polite shutdown
    /// and does not wait out the exit: the two budgets together cost ~8s, and the token was
    /// honoured everywhere except here. Only a server this run owns is then killed. The
    /// shared daemon is a child of the thin client this run started but serves every other
    /// client on the machine, so killing that process tree would take their workspace down
    /// with it; it is left to its own keepalive instead.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var cancelled = _ct.IsCancellationRequested;
        if (!cancelled)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _rpc.InvokeWithParameterObjectAsync<object?>("shutdown", null, cts.Token);
                await _rpc.NotifyWithParameterObjectAsync("exit");
            }
            catch
            {
                // A server that is already gone needs no polite shutdown.
            }
        }

        _rpc.Dispose();
        try
        {
            if (!_proc.WaitForExit(cancelled ? 250 : 3000) && (Dedicated || !cancelled))
            {
                // Tree only when the server is ours. The shared daemon is a child of this
                // thin client, so a tree kill on the timeout path would take it down too.
                _proc.Kill(entireProcessTree: Dedicated);
            }
        }
        catch
        {
            // Already exited.
        }

        _proc.Dispose();
    }

    /// <summary>Server-to-client calls. An unhandled request would fault the connection.</summary>
    private sealed class Endpoints
    {
        private readonly TaskCompletionSource _projectInitialized =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProjectInitialized => _projectInitialized.Task;

        [JsonRpcMethod("workspace/projectInitializationComplete")]
        public void OnProjectInitializationComplete() => _projectInitialized.TrySetResult();

        [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
        public object?[] OnConfiguration(ConfigurationParams p) => new object?[p.Items.Length];

        [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
        public object? OnRegisterCapability(JsonElement _) => null;

        [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
        public object? OnUnregisterCapability(JsonElement _) => null;

        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public object? OnWorkDoneProgressCreate(JsonElement _) => null;

        [JsonRpcMethod("workspace/_roslyn_restorableProjects", UseSingleObjectParameterDeserialization = true)]
        public string[] OnRestorableProjects(JsonElement _) => [];

        // Refresh requests for source-generated documents. cslq is one-shot, so there is
        // nothing to invalidate — but answering beats the alternative: an error response on
        // an unexpected server-to-client call, and a bad payload is already known to take the
        // server's whole request queue down with it.
        [JsonRpcMethod("workspace/_roslyn_refreshSourceGenerators", UseSingleObjectParameterDeserialization = true)]
        public object? OnRefreshSourceGenerators(JsonElement _) => null;

        [JsonRpcMethod("workspace/textDocumentContent/refresh", UseSingleObjectParameterDeserialization = true)]
        public object? OnTextDocumentContentRefresh(JsonElement _) => null;

        [JsonRpcMethod("workspace/diagnostic/refresh", UseSingleObjectParameterDeserialization = true)]
        public object? OnDiagnosticRefresh(JsonElement _) => null;

        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void OnLogMessage(JsonElement _) { }

        [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
        public void OnShowMessage(JsonElement _) { }

        [JsonRpcMethod("telemetry/event", UseSingleObjectParameterDeserialization = true)]
        public void OnTelemetry(JsonElement _) { }

        [JsonRpcMethod("$/progress", UseSingleObjectParameterDeserialization = true)]
        public void OnProgress(JsonElement _) { }
    }
}

/// <summary>
/// One project's readiness probe: the directory a resolving hit has to sit under, the
/// candidate type names to look for, in the order they were found, and the directories of
/// projects nested inside this one, which a hit must <em>not</em> sit under.
/// </summary>
/// <param name="Skipped">
/// Whether this project cannot be probed at all, as against merely having contributed no
/// candidate. A project whose sources are linked in from outside its directory — a
/// <c>*.projitems</c> import, a <c>&lt;Compile Include="../Shared/**"&gt;</c> — owns no
/// document whose location <see cref="Accepts"/> could ever accept, so no candidate could
/// prove it loaded even in principle. Fourteen of CommunityToolkit's twenty-six projects are
/// that shape, and before this they were counted in <c>ready --json</c>'s <c>projects</c> as
/// if they had been probed.
/// </param>
internal sealed record Sentinel(
    string Directory,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> Nested,
    bool Skipped = false,
    bool Explicit = false)
{
    /// <summary>
    /// Whether a sentinel hit proves <em>this</em> project loaded. Inside the directory and
    /// not inside a project nested within it: without that second half, <c>Web/</c> and
    /// <c>Web/Tests/</c> both declaring <c>Program</c> — the ordinary shape — lets Tests
    /// loading mark Web ready, which is the every-project-loaded guarantee failing quietly.
    /// A generated document is never accepted: it has no on-disk path to scope, and
    /// <c>ToPath</c> would answer a path-shaped lie for it.
    /// </summary>
    public bool Accepts(string uri) =>
        Under(uri, Directory) && !Nested.Any(n => Under(uri, n));

    private static bool Under(string uri, string directory)
    {
        if (PathUri.IsGenerated(uri)) return false;

        var path = Path.GetFullPath(PathUri.ToPath(uri));
        var dir = Path.GetFullPath(directory).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(dir + Path.DirectorySeparatorChar, PathUri.PathComparison);
    }
}
