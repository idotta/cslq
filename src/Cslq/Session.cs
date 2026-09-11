using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cslq;

/// <summary>
/// The session: a background <c>cslq</c> that holds one live <see cref="LspClient"/> and
/// answers commands over a local named pipe, so the workspace load is paid once instead of
/// once per invocation. Measured on the fixture, a warm one-shot <c>hover</c> is ~2.2 s and
/// <c>cslq --version</c>, which starts no server at all, is ~0.19 s: essentially the whole
/// wall clock is an attach every run throws away. Held open, the same query is single-digit
/// milliseconds.
/// <para>
/// Ours is the <em>session</em>; the Roslyn one stays the <em>daemon</em>. They are
/// independent — a session talks to the daemon like any other client unless
/// <c>--no-daemon</c> says otherwise.
/// </para>
/// <para>
/// It is a latency optimisation and nothing more, so every failure in here falls through to
/// the in-process path rather than failing the query. See <see cref="FallbackNotice"/>.
/// </para>
/// </summary>
internal static class Session
{
    /// <summary>
    /// Printed on stderr whenever a run that asked for a session did not get one, in the
    /// shape of the existing <c>daemon unreachable</c> line: the answer is still correct, so
    /// the only thing between an agent and blaming the latency on us is this sentence.
    /// </summary>
    internal const string FallbackNotice =
        "cslq: session unavailable; this run loaded the workspace itself";

    /// <summary>The internal flag that turns a <c>cslq</c> into the session process.</summary>
    internal const string ServeFlag = "--serve";

    private const string PipeEnvironmentVariable = "CSLQ_SESSION_PIPE_NAME";
    private const string KeepaliveEnvironmentVariable = "CSLQ_SESSION_KEEPALIVE";

    /// <summary>
    /// Web defaults are camelCase and case-insensitive, which is the wire shape
    /// <c>{"version":...,"argv":[...]}</c> asks for. Null is omitted rather than written, so
    /// an ordinary response carries no <c>error</c> key at all.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// <c>CurrentUserOnly</c> is the whole of the pipe's security, and it is needed on both
    /// ends. The name is derived from public inputs and <see cref="LogPath"/> writes it into
    /// the shared temp directory, so it is not a secret: without this the server's ACL lets
    /// any local user drive a session — which runs arbitrary <c>cslq</c> commands as its
    /// owner and reads every file under the root — and, worse, a hostile process can own the
    /// name before we do and answer a developer's query with fabricated stdout at exit 0. On
    /// the client it is what refuses such a squatter. Same reasoning as the
    /// <c>CurrentUserOnly = true</c> on <see cref="StartupLock"/>'s mutex.
    /// </summary>
    private const PipeOptions PipeStreamOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    internal sealed record Request(string Version, string[] Argv);

    /// <summary>
    /// One answered command. <c>Error</c> is the session declining to answer at all — a
    /// version it does not match, or a root it was not started for — and is the client's
    /// signal to fall back rather than retry.
    /// </summary>
    internal sealed record Response(int Exit, string Stdout, string Stderr, string? Error = null);

    /// <summary>What <c>--serve</c> was launched with: the attach this session is fixed to.</summary>
    internal sealed record Serve(string Pipe, string Root, string LogLevel, bool Daemon);

    /// <summary>
    /// The pipe name, and with it the identity of a session. The attach is fixed by the root,
    /// the log level and whether the Roslyn daemon is used, so those are the key — plus the
    /// user, since the pipe is machine-wide, and the version, because an upgraded <c>cslq</c>
    /// must never talk to a session running the old code.
    /// </summary>
    internal static string PipeName(string root, string logLevel, bool daemon, string version)
    {
        var key = string.Join(
            '\n',
            root,
            logLevel.ToLowerInvariant(),
            daemon ? "daemon" : "own-server",
            Environment.UserName,
            version);
        return "cslq-" + Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(key)))[..16];
    }

    /// <summary>
    /// <c>CSLQ_SESSION_PIPE_NAME</c> overrides the derived name outright — the same escape
    /// hatch <c>ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME</c> is for the daemon, and what
    /// scopes the probe suite to a session of its own. Only here, never in the derivation
    /// above, so a suite that exports it still gets a derivation the unit tests can pin.
    /// </summary>
    internal static string PipeName(Program.Options opts) =>
        Environment.GetEnvironmentVariable(PipeEnvironmentVariable) is { Length: > 0 } literal
            ? literal
            : PipeName(opts.Root, opts.LogLevel, opts.Daemon, Build.Version);

    /// <summary>
    /// The argv the session is asked to run. It has to be independent of the caller's working
    /// directory, because the session's is its own: <see cref="Program.Options.Parse"/>
    /// resolves <c>--root</c> against the cwd and every relative file argument against the
    /// root, so substituting the already-absolute root in is sufficient. <c>--no-session</c>
    /// is stripped — it cannot reach here, since it is what stops a request being sent at
    /// all, but a session that read it would be a session opting itself out of itself.
    /// </summary>
    internal static string[] Normalise(string[] argv, string root)
    {
        var normalised = new List<string>(argv.Length + 2);
        var sawRoot = false;

        for (var i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--no-session":
                    break;
                case "--root":
                    // The value is skipped rather than copied: it may be relative, and the
                    // absolute one already parsed is what goes on the wire.
                    i++;
                    sawRoot = true;
                    normalised.Add("--root");
                    normalised.Add(root);
                    break;
                default:
                    normalised.Add(argv[i]);
                    break;
            }
        }

        if (!sawRoot)
        {
            normalised.Add("--root");
            normalised.Add(root);
        }

        return [.. normalised];
    }

    /// <summary>
    /// Runs the command through a session, starting one if there is none. Null means no
    /// session answered and the caller must run the query itself — every failure here is one
    /// of those, never an exception.
    /// </summary>
    internal static async Task<int?> TryRunAsync(
        string[] argv, Program.Options opts, CancellationToken ct)
    {
        var pipe = PipeName(opts);
        var request = new Request(Build.Version, Normalise(argv, opts.Root));

        try
        {
            if (await SendAsync(pipe, request, FastConnectMs, ct) is { } answered) return answered;
            return await StartAndSendAsync(pipe, opts, request, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DeliveryFailure ex)
        {
            // The one failure that must not fall back: part of the answer is already on the
            // caller's stdout. See Deliver.
            throw new CslqException(ex.Message);
        }
        catch (Exception)
        {
            // A session is an optimisation. Anything at all — a pipe that vanished mid-round
            // trip, a response that did not parse, a spawn that was refused — is answered by
            // running the query here instead.
            return null;
        }
    }

    /// <summary>
    /// One round trip against a session that may not exist. Null when there is nothing
    /// listening, or when the session declined the request.
    /// </summary>
    private static async Task<int?> SendAsync(
        string pipe, Request request, int connectMs, CancellationToken ct)
    {
        using var client = new NamedPipeClientStream(
            ".", pipe, PipeDirection.InOut, PipeStreamOptions);
        try
        {
            await client.ConnectAsync(connectMs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        var writer = new StreamWriter(client, Utf8) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(client, Utf8);

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), ct);
        if (await reader.ReadLineAsync(ct) is not { } line) return null;

        var response = JsonSerializer.Deserialize<Response>(line, Json);
        if (response is null || response.Error is not null) return null;

        return Deliver(response, Console.Out, Console.Error);
    }

    /// <summary>
    /// Hands a session's answer to the caller, and marks the run committed the moment the
    /// first byte goes out. Everything before this point falls back on failure, which is
    /// right — nothing has been printed, so running the query here is invisible. After it the
    /// fallback is a bug: a stdout that closed mid-write (a <c>| head</c>-shaped consumer)
    /// was swallowed into a null, and the caller then printed the fallback notice and the
    /// whole answer a second time on top of the partial one. A failure with nothing written
    /// yet still falls back, which is what the <c>wrote</c> guard is for.
    /// </summary>
    internal static int Deliver(Response response, TextWriter stdout, TextWriter stderr)
    {
        var wrote = false;
        try
        {
            if (response.Stdout.Length > 0)
            {
                wrote = true;
                stdout.Write(response.Stdout);
            }

            if (response.Stderr.Length > 0)
            {
                wrote = true;
                stderr.Write(response.Stderr);
            }
        }
        catch (Exception ex) when (wrote && ex is not OperationCanceledException)
        {
            throw new DeliveryFailure(ex);
        }

        return response.Exit;
    }

    /// <summary>
    /// A session answered and the answer could not be fully written. Its own type so that
    /// <see cref="TryRunAsync"/>'s blanket catch — which exists to turn every other session
    /// failure into a fallback — lets exactly this one through.
    /// </summary>
    internal sealed class DeliveryFailure(Exception inner)
        : Exception($"the session's answer could not be written: {inner.Message}", inner);

    /// <summary>
    /// Takes the startup mutex, re-tries the connect under it — another client may have won
    /// the race and already started one — and otherwise spawns the session and waits for its
    /// pipe to come up. The mutex covers the spawn alone, not the request: the first query
    /// pays the whole cold load, and holding the lock across it would make every other client
    /// wait out that load and then fall back instead of queueing on the session.
    /// </summary>
    private static async Task<int?> StartAndSendAsync(
        string pipe, Program.Options opts, Request request, CancellationToken ct)
    {
        using var start = await StartupLock.AcquireAsync($@"Global\{pipe}.start", MutexWait, ct);
        if (start is null) return null;

        // Patiently, unlike the probe on the fast path: the lock is already paid for, and a
        // live session that is merely slow to accept -- a loaded machine, a request mid-flight
        // -- read as absent twice and had a *second* session spawned beside it, each with its
        // own Roslyn attach and neither reporting the other.
        if (!await ListeningAsync(pipe, PatientConnectMs, ct))
        {
            using var session = Spawn(pipe, opts);
            if (!await WaitForPipeAsync(pipe, session, ct)) return null;
        }

        start.Dispose();
        return await SendAsync(pipe, request, PatientConnectMs, ct);
    }

    /// <summary>
    /// Long enough to cover a spawn — the session binds its pipe before it loads anything, so
    /// the holder holds this for about a second — and short enough that a client which cannot
    /// have it falls back rather than stalling. <c>probes/hold-mutex.cs</c> holds it to force
    /// exactly that.
    /// </summary>
    private static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for a connection. The fast one is the opening probe, which every call
    /// pays and which is answered by taking the startup lock; the patient one is everything
    /// after that lock is held, where the only thing being raced is a session that exists.
    /// </summary>
    private const int FastConnectMs = 300;

    private const int PatientConnectMs = 2000;

    /// <summary>
    /// A named mutex has thread affinity — <see cref="Mutex.ReleaseMutex"/> has to run on the
    /// thread that acquired it, and an <c>await</c> resumes wherever the pool puts it, which
    /// threw <c>ApplicationException</c> out of the <c>finally</c> and turned a session that
    /// had already answered into a fallback that answered twice. So the mutex lives on a
    /// thread of its own: it acquires, hands back a handle, and blocks until that handle is
    /// disposed.
    /// <para>
    /// Both <see cref="NamedWaitHandleOptions"/> are load-bearing and neither is a default:
    /// <c>CurrentUserOnly</c> because the name is <c>Global\</c>-prefixed and machine-wide,
    /// and <c>CurrentSessionOnly = false</c> because .NET otherwise rejects that prefix. See
    /// CLAUDE.md — either one wrong throws instead of contending.
    /// </para>
    /// </summary>
    private sealed class StartupLock : IDisposable
    {
        private readonly SemaphoreSlim _release = new(0, 1);
        private int _disposed;

        public static Task<StartupLock?> AcquireAsync(string name, TimeSpan wait, CancellationToken ct)
        {
            var held = new StartupLock();
            var acquired = new TaskCompletionSource<StartupLock?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    using var mutex = new Mutex(
                        false,
                        name,
                        new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });

                    bool taken;
                    try
                    {
                        taken = mutex.WaitOne(wait);
                    }
                    catch (AbandonedMutexException)
                    {
                        // A client that died holding it. It is ours either way.
                        taken = true;
                    }

                    if (!taken)
                    {
                        acquired.TrySetResult(null);
                        return;
                    }

                    acquired.TrySetResult(held);
                    held._release.Wait();
                    mutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    acquired.TrySetException(ex);
                }
            })
            { IsBackground = true, Name = "cslq-session-start" };

            thread.Start();
            return acquired.Task.WaitAsync(ct);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _release.Release();
        }
    }

    /// <summary>
    /// The session process. No redirects at all, and the inherit flag cleared first: a
    /// redirected child whose parent exits blocks forever on a full pipe, and an inherited
    /// handle means the session holds a capturing harness's stdout for its whole life — the
    /// 25 s-to-4 s bug behind <see cref="LspClient.DisableStdioInheritance"/>, one level up.
    /// The session redirects its own console to a log file instead.
    /// </summary>
    private static Process Spawn(string pipe, Program.Options opts)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "cslq")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in ServeArgv(pipe, opts)) psi.ArgumentList.Add(a);

        LspClient.DisableStdioInheritance();
        return Process.Start(psi) ?? throw new CslqException("could not start a cslq session");
    }

    internal static string[] ServeArgv(string pipe, Program.Options opts)
    {
        var argv = new List<string>
        {
            ServeFlag, pipe, "--root", opts.Root, "--log-level", opts.LogLevel,
        };
        if (!opts.Daemon) argv.Add("--no-daemon");
        return [.. argv];
    }

    /// <summary>
    /// Polls for the session's pipe, and gives up the moment the process we started exits —
    /// a session that cannot run at all (no <c>dotnet</c> on PATH, a binary that does not
    /// understand <c>--serve</c>) would otherwise cost the caller the whole poll window
    /// before the in-process path reports the real reason.
    /// </summary>
    private static async Task<bool> WaitForPipeAsync(string pipe, Process session, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await ListeningAsync(pipe, FastConnectMs, ct)) return true;
            if (session.HasExited) return false;
            await Task.Delay(50, ct);
        }

        return false;
    }

    /// <summary>
    /// Whether a session is accepting on the pipe, asked by exchanging a ping rather than by
    /// connecting and hanging up: a half-open connection is what races the server's accept,
    /// and this is polled hard enough to hit that race. A ping is answered off the request
    /// gate, so a session in the middle of a cold load or a long <c>diag</c> still says yes
    /// instead of reading as absent.
    /// </summary>
    private static async Task<bool> ListeningAsync(string pipe, int connectMs, CancellationToken ct)
    {
        using var client = new NamedPipeClientStream(
            ".", pipe, PipeDirection.InOut, PipeStreamOptions);
        try
        {
            await client.ConnectAsync(connectMs, ct);
            var writer = new StreamWriter(client, Utf8) { AutoFlush = true, NewLine = "\n" };
            var reader = new StreamReader(client, Utf8);
            await writer.WriteLineAsync(JsonSerializer.Serialize(Ping, Json).AsMemory(), ct);
            return await reader.ReadLineAsync(ct) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// The liveness probe's request. An empty argv is not a command any client could send --
    /// <see cref="Normalise"/> always appends <c>--root</c> and its value -- so it needs no
    /// flag of its own, and it is answered whatever version sent it.
    /// </summary>
    private static readonly Request Ping = new(Build.Version, []);

    /// <summary>
    /// <c>cslq session stop</c>'s request. Like the ping it is answered before the parse and
    /// cannot collide with a command: <see cref="Normalise"/> appends <c>--root</c> and its
    /// value to every real one, so no client argv is ever a single token.
    /// </summary>
    internal const string StopArgv = "--session-stop";

    /// <summary>What <c>cslq session status</c> found. <c>Pid</c> is null when no session has
    /// ever written the log, which is also the only place a pid can be read from: the session
    /// is spawned detached and nothing else knows it.</summary>
    internal sealed record Status(string Pipe, string Root, string Log, bool Running, int? Pid);

    internal static async Task<Status> StatusAsync(Program.Options opts, CancellationToken ct)
    {
        var pipe = PipeName(opts);
        var log = LogPath(pipe);
        return new Status(pipe, opts.Root, log, await ListeningAsync(pipe, PatientConnectMs, ct), Pid(log));
    }

    /// <summary>
    /// Asks the session to stop, and reports whether one was there to ask. It answers before
    /// it goes, so a caller that gets true knows the process is on its way out rather than
    /// that a signal was sent somewhere.
    /// </summary>
    internal static async Task<bool> StopAsync(Program.Options opts, CancellationToken ct)
    {
        var pipe = PipeName(opts);
        try
        {
            return await SendAsync(pipe, new Request(Build.Version, [StopArgv]), PatientConnectMs, ct)
                is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// The pid off the last start line the log carries. Best effort by design: the log is
    /// append-only across restarts and a session that died left its line behind, so this is
    /// reported beside <see cref="Status.Running"/> rather than instead of it.
    /// <para>
    /// Opened by hand rather than through <see cref="File.ReadLines(string)"/>, which shares
    /// reads alone: a <em>running</em> session holds the file open for writing, so the default
    /// read failed with a sharing violation on exactly the log worth reading, and
    /// <c>session status</c> printed no pid for every session it found running.
    /// </para>
    /// </summary>
    private static int? Pid(string log)
    {
        try
        {
            using var stream = new FileStream(
                log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8);
            foreach (var line in Lines(reader).Reverse())
            {
                var parts = line.Split(' ');
                var at = Array.IndexOf(parts, "pid");
                if (at >= 0 && at + 1 < parts.Length && int.TryParse(parts[at + 1], out var pid)) return pid;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No log is the ordinary state before the first session starts.
        }

        return null;

        static IEnumerable<string> Lines(StreamReader reader)
        {
            while (reader.ReadLine() is { } line) yield return line;
        }
    }

    /// <summary>
    /// <c>cslq --serve &lt;pipe&gt; --root &lt;abs&gt; [--no-daemon] [--log-level L]</c>, or
    /// null for an ordinary invocation. Parsed here rather than in
    /// <see cref="Program.Options.Parse"/> because the flag is internal: we spawn it, nobody
    /// types it, and it stays out of the usage block.
    /// </summary>
    internal static Serve? ServeRequest(string[] argv)
    {
        if (argv.Length == 0 || argv[0] != ServeFlag) return null;

        string? pipe = null;
        string? root = null;
        var logLevel = "Warning";
        var daemon = true;

        for (var i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case ServeFlag: pipe = Value(argv, ref i); break;
                case "--root": root = Value(argv, ref i); break;
                case "--log-level": logLevel = Value(argv, ref i); break;
                case "--no-daemon": daemon = false; break;
                default: throw new UsageException($"unknown option '{argv[i]}'");
            }
        }

        if (pipe is null || root is null)
        {
            throw new UsageException("--serve needs a pipe name and a root");
        }

        return new Serve(pipe, root, logLevel, daemon);

        static string Value(string[] argv, ref int i)
        {
            if (++i >= argv.Length || string.IsNullOrWhiteSpace(argv[i]))
            {
                throw new UsageException($"option '{argv[i - 1]}' needs a value");
            }

            return argv[i];
        }
    }

    /// <summary>
    /// How long an idle session lives. <c>-1</c> never exits, which is what a developer
    /// pinning one for a day wants; the probe suite sets a short one so the gate leaks
    /// nothing.
    /// </summary>
    private static TimeSpan Keepalive()
    {
        var text = Environment.GetEnvironmentVariable(KeepaliveEnvironmentVariable);
        if (!int.TryParse(text, out var seconds)) return TimeSpan.FromSeconds(900);
        return seconds < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
    }

    internal static string LogPath(string pipe) =>
        Path.Combine(Path.GetTempPath(), $"cslq-session-{pipe}.log");

    /// <summary>
    /// The session process. Binds the pipe first and builds the workspace lazily on the first
    /// request: a client only ever waits for a process to start listening, and the cold load
    /// is then governed by that request's own <c>--timeout</c> and reported through it, which
    /// is exactly what the in-process path does. The client and the sentinels are built once
    /// and then held for every later request — the whole point of the thing.
    /// </summary>
    internal static async Task<int> ServeAsync(Serve serve, CancellationToken ct)
    {
        // Synchronized because the accept loop and every in-flight request write it from
        // threads of their own, and StreamWriter is not thread-safe. It is not cosmetic: the
        // start line below is the only place a session's pid is recorded, Session.Pid parses
        // it and probes/run.sh's EXIT trap kills by it, so two interleaved writes leak a
        // session and its Roslyn server past the end of a gate run.
        var log = TextWriter.Synchronized(new StreamWriter(
            new FileStream(LogPath(serve.Pipe), FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            Utf8)
        { AutoFlush = true });
        // Console is redirected too, for anything printed outside a request -- the LSP
        // client's own warnings, most of all -- but the session's own lines go to the writer
        // directly: inside a request Console is a StringWriter belonging to that request's
        // response, and a session-level line written there would be answered to a client that
        // did not cause it.
        Console.SetOut(log);
        Console.SetError(log);

        // The pid is on this line so that something which did not spawn the session can still
        // end it: the probe suite's EXIT trap reads it out of the log, and a developer who
        // wants their session gone has the same handle.
        log.WriteLine(
            $"cslq session {Build.Version} pid {Environment.ProcessId} on {serve.Pipe} " +
            $"for {serve.Root} at {DateTime.UtcNow:O}");

        // A session is shared background state, not a child of whoever happened to spawn it.
        // It is started with UseShellExecute = false, which leaves it attached to the spawning
        // console, and .NET has no managed DETACHED_PROCESS; so a Ctrl+C — or anything else
        // that raises a console control event on that console — would otherwise take a session
        // down under every other client using it. Ignoring the event is what detaches it. It
        // still idles out, and it still dies with the terminal that owns the console.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;

        var keepalive = Keepalive();
        await using var state = new State(serve, log);
        using var stopping = new CancellationTokenSource();
        state.Stop = stopping;
        using var gate = new SemaphoreSlim(1, 1);

        try
        {
            return await AcceptAsync(serve, state, gate, keepalive, ct);
        }
        catch (Exception ex)
        {
            // Whatever ends a session belongs in its log: from the outside a session that died
            // is indistinguishable from one that was never started, and the client answers both
            // by starting another and paying the load again.
            log.WriteLine($"cslq session: stopped: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static async Task<int> AcceptAsync(
        Serve serve, State state, SemaphoreSlim gate, TimeSpan keepalive, CancellationToken ct)
    {
        var stopped = state.Stop?.Token ?? CancellationToken.None;

        while (!ct.IsCancellationRequested && !stopped.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                serve.Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeStreamOptions);

            using var idle = new CancellationTokenSource();
            // Armed from when the session last went idle, not from this accept: a request
            // longer than the keepalive rolls through several of these windows, and arming
            // each one afresh meant the window current when the request finished fired
            // moments later with nothing in flight and took the session down. With a 60 s
            // keepalive and a 55 s walk -- the probe suite's own shape -- the session died at
            // t=60, five seconds after answering rather than sixty, and the next call
            // silently paid the whole load again.
            if (keepalive != Timeout.InfiniteTimeSpan) idle.CancelAfter(state.Remaining(keepalive));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token, stopped);

            try
            {
                await pipe.WaitForConnectionAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await pipe.DisposeAsync();

                // `cslq session stop` has already been answered by the time this fires; the
                // line is here rather than there so that every way a session ends is one
                // line in its own log.
                if (stopped.IsCancellationRequested)
                {
                    state.Log.WriteLine("cslq session: asked to stop.");
                    return 0;
                }

                // Only idle out with nothing in flight. Counted rather than read off the
                // gate: a request that has been accepted and is still reading its line off the
                // pipe holds no gate at all, so the keepalive firing in that window took the
                // process down under it -- and the client, seeing the pipe die, paid the whole
                // load again for no visible reason.
                if (Volatile.Read(ref state.InFlight) != 0) continue;

                // A request may have finished while this window was running, which restarts
                // the keepalive: the next iteration arms what is left of it.
                if (keepalive != Timeout.InfiniteTimeSpan && state.Remaining(keepalive) > TimeSpan.Zero)
                {
                    continue;
                }

                state.Log.WriteLine($"cslq session: idle for {keepalive}; stopping.");
                return 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // No single connection may end the session. A client that hangs up
                // mid-handshake -- which the liveness probe below used to do, twenty times a
                // second, from WaitForPipeAsync -- races WaitForConnectionAsync and the accept
                // gets `IOException: the pipe is being closed` instead of a connection. That
                // used to be rethrown, so the process logged `stopped:` and exited, the caller
                // fell back, and the next call paid the whole load again: 4.5 s and three
                // processes for one query. The delay is a spin guard, not a wait: an accept
                // that fails instantly and forever would otherwise be a busy loop.
                await pipe.DisposeAsync();
                state.Log.WriteLine($"cslq session: accept failed: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(25, ct);
                continue;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }

            // Accepted on this loop and answered on another, so a second client can connect
            // while the first request is still running. `gate` is what keeps them one at a
            // time: Console.Out is process-global and the redirect below owns it. The count
            // goes up here rather than in the task, which has not started yet.
            Interlocked.Increment(ref state.InFlight);
            _ = ServeOneAsync(pipe, state, gate, ct);
        }

        return 0;
    }

    private static async Task ServeOneAsync(
        NamedPipeServerStream pipe, State state, SemaphoreSlim gate, CancellationToken ct)
    {
        var stop = false;
        try
        {
            await using (pipe)
            {
                var reader = new StreamReader(pipe, Utf8);
                var writer = new StreamWriter(pipe, Utf8) { AutoFlush = true, NewLine = "\n" };

                if (await reader.ReadLineAsync(ct) is not { } line) return;
                var request = JsonSerializer.Deserialize<Request>(line, Json);

                // An empty argv is the liveness probe and a lone --session-stop is the stop
                // lever, both answered off the request gate: what they ask is whether the
                // process is there, and a session loading a workspace holds that gate for the
                // whole load. Neither can be a command -- Normalise always appends --root and
                // its value, so a real argv is never one token.
                stop = request is { Argv: [StopArgv] };
                var response = request switch
                {
                    null => new Response(1, "", "", "the request did not parse"),
                    { Argv.Length: 0 } => new Response(0, "", "", null),
                    _ when stop => new Response(0, "", "", null),
                    _ => await AnswerAsync(request, state, gate, ct),
                };

                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), ct);
                // Windows closes a named pipe on dispose without draining it, so a response
                // written and then disposed can reach the client truncated. Off Windows the
                // stream is socket-backed and the kernel holds the bytes.
                if (OperatingSystem.IsWindows() && pipe.IsConnected) pipe.WaitForPipeDrain();
            }

            // After the connection is closed, so the caller has its answer before the accept
            // loop below goes away underneath it.
            if (stop) state.Stop?.Cancel();
        }
        catch (Exception ex)
        {
            // The client is gone, or the connection broke. It falls back; the session lives.
            state.Log.WriteLine($"cslq session: request failed: {ex.Message}");
        }
        finally
        {
            // The keepalive is measured from here, so the log line's "idle for" is the truth.
            if (Interlocked.Decrement(ref state.InFlight) == 0) state.WentIdle();
        }
    }

    /// <summary>
    /// The gate is taken before the parse, not after it: <c>Console</c> is process-global and
    /// every branch below redirects it, so a usage error arriving while a query was running
    /// used to redirect over that query's buffers and then restore them — the query's output
    /// landed in the usage error's response, its own was lost, and nothing threw. One session,
    /// one console: a usage error queues behind a long <c>diag</c> instead.
    /// </summary>
    internal static async Task<Response> AnswerAsync(
        Request request, State state, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await AnsweredAsync(request, state, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<Response> AnsweredAsync(
        Request request, State state, CancellationToken ct)
    {
        if (request.Version != Build.Version)
        {
            return new Response(
                1, "", "",
                $"this session is {Build.Version} and cannot answer a {request.Version} request");
        }

        Program.Options opts;
        try
        {
            opts = Program.Options.Parse(request.Argv);
        }
        catch (UsageException ex)
        {
            return Captured(2, () => WriteFailure(ex.Message, request.Argv, usage: true));
        }
        catch (CslqException ex)
        {
            return Captured(1, () => WriteFailure(ex.Message, request.Argv, usage: false));
        }

        // The key the pipe name is derived from, checked rather than trusted: a stale
        // CSLQ_SESSION_PIPE_NAME would otherwise have one root's session answering for
        // another, silently and wrongly.
        if (opts.Root != state.Serve.Root ||
            !opts.LogLevel.Equals(state.Serve.LogLevel, StringComparison.OrdinalIgnoreCase) ||
            opts.Daemon != state.Serve.Daemon)
        {
            return new Response(1, "", "", $"this session serves {state.Serve.Root}");
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var realOut = Console.Out;
        var realError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await RunAsync(opts, state, ct);
            return new Response(exit, stdout.ToString(), stderr.ToString());
        }
        catch (UsageException ex)
        {
            WriteFailure(ex.Message, request.Argv, usage: true);
            return new Response(2, stdout.ToString(), stderr.ToString());
        }
        catch (CslqException ex)
        {
            WriteFailure(ex.Message, request.Argv, usage: false);
            return new Response(1, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(realOut);
            Console.SetError(realError);
        }
    }

    /// <summary>
    /// One command against the held client, with the two lines the in-process path prints
    /// around <see cref="Program.DispatchAsync"/>. A failure inside a request is that
    /// request's exit code, never the session's death.
    /// </summary>
    private static async Task<int> RunAsync(Program.Options opts, State state, CancellationToken ct)
    {
        Program.WarnNonCsharp(opts.Root);
        var client = await state.ClientAsync(ct);
        // The workspace index this request may read is built from the text of the documents
        // this client holds open, so the sweep comes before the command rather than inside it.
        await client.RefreshOpenAsync(ct);
        try
        {
            return await Program.DispatchAsync(
                client, opts, Program.Sentinels(opts, state.Inferred), ct);
        }
        finally
        {
            if (opts.Daemon && client.DaemonFallback)
            {
                Console.Error.WriteLine("cslq: daemon unreachable; this run used its own cold server");
            }
        }
    }

    /// <summary>A failure rendered exactly as <c>Main</c> renders it.</summary>
    private static void WriteFailure(string message, string[] argv, bool usage)
    {
        Output.WriteError(message, Program.WantsJson(argv));
        if (!usage) return;
        Console.Error.WriteLine();
        Console.Error.WriteLine(Program.Usage);
    }

    /// <summary>
    /// Runs <paramref name="write"/> with the console pointed at a pair of buffers, so what
    /// it prints reaches the client rather than the session's log.
    /// </summary>
    private static Response Captured(int exit, Action write)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var realOut = Console.Out;
        var realError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            write();
        }
        finally
        {
            Console.SetOut(realOut);
            Console.SetError(realError);
        }

        return new Response(exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// The one client and the one inferred sentinel set, built on the first request that
    /// needs them and held for the life of the session. A start that fails is not cached:
    /// the next request tries again, exactly as a fresh process would.
    /// </summary>
    internal sealed class State(Serve serve, TextWriter log) : IAsyncDisposable
    {
        public Serve Serve { get; } = serve;

        /// <summary>The session's own log, which is never a request's captured buffer.</summary>
        public TextWriter Log { get; } = log;

        /// <summary>Requests accepted and not yet answered. Read and written with interlocks.</summary>
        public int InFlight;

        private long _idleSince = DateTime.UtcNow.Ticks;

        /// <summary>Called when <see cref="InFlight"/> drops to zero: the keepalive restarts.</summary>
        public void WentIdle() => Interlocked.Exchange(ref _idleSince, DateTime.UtcNow.Ticks);

        /// <summary>What is left of <paramref name="keepalive"/>, never negative.</summary>
        public TimeSpan Remaining(TimeSpan keepalive)
        {
            var idleFor = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _idleSince));
            var remaining = keepalive - idleFor;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        /// <summary>
        /// Cancelled by a <c>--session-stop</c> request, which is what ends the accept loop.
        /// Set by <see cref="ServeAsync"/> alone, so a state built for a test has none and
        /// nothing here has to be disposed by the state.
        /// </summary>
        public CancellationTokenSource? Stop;

        public IReadOnlyList<Sentinel>? Inferred { get; private set; }

        private LspClient? _client;
        private FileSystemWatcher? _watcher;
        private string _solutions = "";
        private int _dirty;

        /// <summary>
        /// The held client, re-attached first if the project graph changed under it. Roslyn's
        /// own file watcher keeps ordinary <c>.cs</c> edits inside a loaded project current;
        /// nothing keeps the <em>graph</em> current, so a <c>.csproj</c> added or removed, or
        /// the root's solution edited, leaves the attach describing a repository that no
        /// longer exists — and <see cref="Inferred"/> caches the sentinel set on top of it.
        /// That costs one reload, and only when the shape actually changed.
        /// </summary>
        public async Task<LspClient> ClientAsync(CancellationToken ct)
        {
            if (_client is not null && ShapeChanged()) await ReattachAsync();

            if (_client is null)
            {
                _watcher ??= Watch();
                // Cleared before the load rather than after it: a change arriving while the
                // workspace is loading then leaves the flag set and the next request reloads,
                // instead of being swallowed by the load it raced.
                Interlocked.Exchange(ref _dirty, 0);
                _solutions = Program.SolutionSignature(Serve.Root);
                Inferred ??= Program.InferSentinels(Serve.Root);
                _client = await LspClient.StartAsync(Serve.Root, Serve.LogLevel, Serve.Daemon, ct);
            }

            return _client;
        }

        private bool ShapeChanged() =>
            Interlocked.Exchange(ref _dirty, 0) != 0 ||
            Program.SolutionSignature(Serve.Root) != _solutions;

        private async Task ReattachAsync()
        {
            Log.WriteLine(
                $"cslq session: the project graph changed under {Serve.Root}; re-attaching.");
            var client = _client;
            _client = null;
            Inferred = null;
            if (client is not null) await client.DisposeAsync();
        }

        /// <summary>
        /// Watches the root for project and solution files appearing, vanishing, being renamed
        /// or being written. A watcher that cannot start — some paths and filesystems refuse
        /// one — must not take the session down: it is logged, and the solution
        /// <c>stat</c> in <see cref="ShapeChanged"/> runs either way.
        /// </summary>
        private FileSystemWatcher? Watch()
        {
            try
            {
                var watcher = new FileSystemWatcher(Serve.Root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Filters.Add("*.csproj");
                watcher.Filters.Add("*.sln");
                watcher.Filters.Add("*.slnx");
                watcher.Created += OnShapeEvent;
                watcher.Deleted += OnShapeEvent;
                watcher.Changed += OnShapeEvent;
                watcher.Renamed += OnShapeEvent;
                // A dropped event is indistinguishable from a change that happened, so an
                // overflowed buffer is answered by assuming one did. Nothing is logged from
                // here: the log writer belongs to the request loop and these run on the
                // watcher's own thread.
                watcher.Error += (_, _) => Interlocked.Exchange(ref _dirty, 1);
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception ex)
            {
                Log.WriteLine(
                    $"cslq session: cannot watch {Serve.Root} for project changes "
                    + $"({ex.GetType().Name}: {ex.Message}); the solution stat is the backstop.");
                return null;
            }
        }

        /// <summary>
        /// <c>bin</c> and <c>obj</c> are skipped the way every other scan in the codebase
        /// skips them: a build writes project files under <c>obj</c>, and reloading the
        /// workspace because something built it is a reload for nothing.
        /// </summary>
        private void OnShapeEvent(object sender, FileSystemEventArgs e)
        {
            if (Ignored(e.FullPath)) return;
            if (e is RenamedEventArgs renamed && Ignored(renamed.OldFullPath)) return;
            Interlocked.Exchange(ref _dirty, 1);
        }

        private static bool Ignored(string path) =>
            path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
            path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

        public async ValueTask DisposeAsync()
        {
            _watcher?.Dispose();
            if (_client is not null) await _client.DisposeAsync();
        }
    }
}
