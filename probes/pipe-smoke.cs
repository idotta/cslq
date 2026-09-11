// The session's transport, exercised in isolation and then for real, in seconds rather than in
// the half-hour a full gate costs.
//
// Why it exists: on ubuntu-latest and macos-latest every probe case passed and every one took a
// uniform ~62 s -- the signature of a client that never reaches a session, spawns one, waits out
// its timeouts and does the work itself. The fallback is why nothing went red. This reproduced
// it in 52 s and named it.
//
// What it has found so far, in order:
//   * The in-process accept loop answers 6 of 6 on Unix, so the shape is not simply broken --
//     but the socket file under $TMPDIR flickers between rounds, present then missing.
//   * A client gets `connected but was not answered`: a connect that succeeds and then reads
//     EOF, which is a path nobody is accepting on rather than a reset.
// On Unix a NamedPipeServerStream is the listener and the connection in one object: the
// constructor binds the path, WaitForConnectionAsync accepts on it, and disposing it closes the
// listening socket and unlinks the path. So an accepted instance disposed *after* the loop has
// bound the next one may unlink a path that now belongs to its successor. Part 1 tests that
// directly by running four loop shapes side by side; on Windows, where a pipe name is a
// reference-counted kernel object, all four are expected to pass and say nothing.
//
// Only two of those four are gated, and that is the point of the split. `successor` is
// Session.AcceptAsync's own ordering and `single` never unlinks at all; `overlap` and `serial`
// are the two shapes Session deliberately does not use, so their failures are the platform
// property being demonstrated rather than a regression. Adding every shape's failures to the
// exit code made the leg red on Unix roughly one gate run in six, on a diagnostic. Measured
// 2026-09-11 under CPU contention (WSL Ubuntu, .NET 10.0.105, eight processes pinned to two
// cores, 300 connections each): overlap 11 of 2400 failed, serial 7 of 2400, single 0 of 2400,
// successor 0 of 2400 -- the failures all `IOException: Broken pipe` or `Connection reset by
// peer`. Uncontended the same run is clean for all four, which is why a low round count only
// flakes on a loaded runner. On Windows, 600 connections per shape: 0 failures anywhere.
//
// A file-based app rather than a project, like probes/hold-mutex.cs and probes/hangup.cs.
// A file-based app is AOT-shaped by default, which turns reflection-based System.Text.Json off
// and leaves JsonRpc unable to deserialise even the empty result of a `ping`. The remedy is a
// source-generated TypeInfoResolver (Wire below), not the reflection switch: cslq is meant to be
// AOT-able, and turning reflection back on is the direction away from that.
#:package StreamJsonRpc@2.25.29
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

const int ConnectMs = 2000;

var pipe = args.Length > 0 ? args[0] : $"cslq-smoke-{Environment.ProcessId}";
var rounds = args.Length > 1 ? int.Parse(args[1]) : 3;
// Optional, and the half that covers the real thing: the path to a built cslq, and the root to
// serve. Given both, parts 2 and 3 below spawn an actual session. The in-process part alone
// would pass on a platform whose spawn is what fails, and say nothing.
var cslq = args.Length > 2 ? args[2] : null;
var serveRoot = args.Length > 3 ? Path.GetFullPath(args[3]) : Environment.CurrentDirectory;
// A root whose `ready` fails in Sentinels, before any server is started: the cheapest command
// that still makes a whole round trip through a session. 95 ms warm on Windows, measured.
var hammerRoot = args.Length > 4 ? Path.GetFullPath(args[4]) : serveRoot;

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var failures = 0;

// Part 1. Four accept-loop shapes, one after another, each on a name of its own. Two of them
// are run for contrast and cannot fail the leg: see the header.
await ShapeAsync("overlap", Shape.Overlap, gated: false);
await ShapeAsync("serial", Shape.Serial, gated: false);
failures += await ShapeAsync("single", Shape.Single, gated: true);
failures += await ShapeAsync("successor", Shape.Successor, gated: true);

if (cslq is not null)
{
    failures += await ServedAsync();
    failures += await HammeredAsync();
}

return failures == 0 ? 0 : 1;

// One shape, run for `rounds` rounds of the two connections a real call makes:
// Session.ListeningAsync probes and hangs up, then Session.SendAsync asks. The socket file is
// reported before each round -- off Windows that line is the diagnosis when it flickers.
async Task<int> ShapeAsync(string label, Shape shape, bool gated)
{
    var name = $"{pipe}-{label}";
    // A shape nothing gates on has to say so on every line it writes, not only on its summary:
    // a `Broken pipe` on stderr is the first thing anyone greps a red-looking gate log for.
    var what = gated ? label : $"{label}, diagnostic";
    var socket = OperatingSystem.IsWindows()
        ? null
        : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name);

    using var stopping = new CancellationTokenSource();
    var server = Task.Run(() => AcceptAsync(name, shape, stopping.Token));

    var bad = 0;
    for (var round = 1; round <= rounds; round++)
    {
        if (socket is not null)
        {
            Console.WriteLine(
                $"{what} round {round}: socket file {(File.Exists(socket) ? "present" : "MISSING")}");
        }

        bad += await RoundTripAsync(name, what, round, "ping") ? 0 : 1;
        bad += await RoundTripAsync(name, what, round, "request") ? 0 : 1;
    }

    await stopping.CancelAsync();
    try
    {
        await server;
    }
    catch (OperationCanceledException)
    {
    }

    var of = rounds * 2;
    var why = gated ? string.Empty : " -- expected off Windows, not gated";
    Console.WriteLine(
        bad == 0
            ? $"pipe smoke [{what}]: {of} of {of} connections answered"
            : $"pipe smoke [{what}]: {bad} of {of} connections failed{why}");
    return bad;
}

async Task<bool> RoundTripAsync(string name, string label, int round, string kind)
{
    var started = Stopwatch.StartNew();
    try
    {
        using var client = new NamedPipeClientStream(
            ".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(ConnectMs);

        var writer = new StreamWriter(client, utf8) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(client, utf8);
        await writer.WriteLineAsync($"{kind} {round}");
        var answer = await reader.ReadLineAsync();
        started.Stop();

        if (answer is null)
        {
            // A connect that succeeded and then read EOF: a path nobody is accepting on, which
            // is a different fault from a reset and the one Unix actually shows.
            Console.Error.WriteLine(
                $"{label} round {round} {kind}: connected but was not answered "
                + $"({started.ElapsedMilliseconds} ms)");
            return false;
        }

        Console.WriteLine($"{label} round {round} {kind}: {answer} ({started.ElapsedMilliseconds} ms)");
        return true;
    }
    catch (Exception ex)
    {
        started.Stop();
        Console.Error.WriteLine(
            $"{label} round {round} {kind}: {ex.GetType().Name}: {ex.Message} "
            + $"({started.ElapsedMilliseconds} ms)");
        return false;
    }
}

async Task AcceptAsync(string name, Shape shape, CancellationToken ct)
{
    // Single and Successor both keep an instance listening for the life of the loop; the other
    // two bind one per iteration.
    var alwaysBound = shape is Shape.Single or Shape.Successor;
    var held = alwaysBound ? Bind(name) : null;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var accepted = held ?? Bind(name);

            try
            {
                await accepted.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await accepted.DisposeAsync();
                return;
            }
            catch (Exception ex)
            {
                if (shape == Shape.Successor)
                {
                    // Session.RebindAsync: a failed instance is replaced successor-first too.
                    held = Bind(name);
                    await accepted.DisposeAsync();
                }
                else if (held is null)
                {
                    await accepted.DisposeAsync();
                }

                Console.Error.WriteLine($"accept failed: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(25, CancellationToken.None);
                continue;
            }

            switch (shape)
            {
                case Shape.Overlap:
                    // Disposed inside, on a task of its own, after the loop has bound the next.
                    _ = ServeOneAsync(accepted, dispose: true, ct);
                    break;
                case Shape.Serial:
                    await ServeOneAsync(accepted, dispose: true, ct);
                    break;
                case Shape.Successor:
                    // Session.AcceptAsync's two lines, in its order: the successor is bound
                    // before the accepted instance is handed to the task that disposes it, so
                    // the reference count never reaches zero and the path is never unlinked
                    // under a client that is connecting to it.
                    held = Bind(name);
                    _ = ServeOneAsync(accepted, dispose: true, ct);
                    break;
                default:
                    await ServeOneAsync(accepted, dispose: false, ct);
                    if (accepted.IsConnected) accepted.Disconnect();
                    break;
            }
        }
    }
    finally
    {
        if (held is not null) await held.DisposeAsync();
    }
}

NamedPipeServerStream Bind(string name) => new(
    name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

async Task ServeOneAsync(NamedPipeServerStream accepted, bool dispose, CancellationToken ct)
{
    try
    {
        var reader = new StreamReader(accepted, utf8);
        var writer = new StreamWriter(accepted, utf8) { AutoFlush = true, NewLine = "\n" };
        if (await reader.ReadLineAsync(ct) is { } line)
        {
            await writer.WriteLineAsync($"answered {line}".AsMemory(), ct);
            if (OperatingSystem.IsWindows() && accepted.IsConnected) accepted.WaitForPipeDrain();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"serve failed: {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        if (dispose) await accepted.DisposeAsync();
    }
}

// Part 2. The same question asked of a real session, across a process boundary. Everything here
// is answered by the session's `ping` and `stop` methods before it touches a workspace, both of
// them off the request gate -- so this costs a process start and nothing else, and it works on a
// root that has never been restored. It is also the path `cslq session status` takes, so a
// failure here is a live session reported as absent.
async Task<int> ServedAsync()
{
    var served = $"{pipe}-serve";
    var log = Path.Combine(Path.GetTempPath(), $"cslq-session-{served}.log");
    Console.WriteLine($"serve: temp directory {Path.GetTempPath()}");

    var psi = new ProcessStartInfo(cslq!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in new[] { "--serve", served, "--root", serveRoot }) psi.ArgumentList.Add(a);

    using var session = Process.Start(psi)!;
    var up = Stopwatch.StartNew();
    var listening = false;
    while (up.Elapsed < TimeSpan.FromSeconds(30) && !session.HasExited)
    {
        if (await PingAsync(served, quiet: true) is not null)
        {
            listening = true;
            break;
        }
    }

    up.Stop();
    if (!listening)
    {
        Console.Error.WriteLine(
            $"serve: the session never accepted on {served} after {up.ElapsedMilliseconds} ms "
            + $"(exited: {session.HasExited})");
        Dump(log);
        if (!session.HasExited) session.Kill(entireProcessTree: true);
        return 1;
    }

    Console.WriteLine($"serve: listening after {up.ElapsedMilliseconds} ms");

    var bad = 0;
    for (var round = 1; round <= rounds; round++)
    {
        if (await PingAsync(served, quiet: false) is null) bad++;
    }

    // The stop lever, so this leaves nothing behind even where the trap cannot see it.
    await PingAsync(served, quiet: true, method: "stop");
    if (!session.WaitForExit(10_000))
    {
        Console.Error.WriteLine("serve: the session did not stop when asked");
        session.Kill(entireProcessTree: true);
        bad++;
    }

    if (bad > 0) Dump(log);
    Console.WriteLine(
        bad == 0
            ? $"pipe smoke [serve]: a real session answered {rounds} of {rounds} pings"
            : $"pipe smoke [serve]: a real session failed {bad} of {rounds} pings");
    return bad;
}

// One call against a real session, over the same StreamJsonRpc wire the client speaks: this file
// may not reference Cslq, so the three method names are the whole of the contract between them.
// Null is no answer.
async Task<long?> PingAsync(string name, bool quiet, string method = "ping")
{
    var started = Stopwatch.StartNew();
    try
    {
        using var client = new NamedPipeClientStream(
            ".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(ConnectMs);

        // Web defaults, because Session.Json uses them: a formatter left on the defaults agrees
        // with the session only while every payload here is empty, and mismatches on casing the
        // first time one is not.
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            client, client, new SystemTextJsonFormatter
            {
                JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    TypeInfoResolver = Wire.Default,
                },
            }));
        rpc.StartListening();
        await rpc.InvokeAsync(method);
        started.Stop();

        if (!quiet) Console.WriteLine($"serve: answered in {started.ElapsedMilliseconds} ms");
        return started.ElapsedMilliseconds;
    }
    catch (Exception ex)
    {
        started.Stop();
        if (!quiet)
        {
            Console.Error.WriteLine(
                $"serve: {ex.GetType().Name}: {ex.Message} ({started.ElapsedMilliseconds} ms)");
        }

        return null;
    }
}

// Part 3, and the only part whose subject is what a user would notice: the sequence that broke,
// driven through the real client rather than through this file's own sockets, because the retry
// under test lives in the client. Connect, ping, hang up, ask -- the hang-up is what re-binds
// the socket path on Unix. The assertion is not "the query worked" (it always did, through the
// fallback) but "no fallback notice", which can only hold if the client reached the session.
// Every iteration is timed: a run that survives by burning a retry window each time is a run
// where the transport is still wrong, and the wall clock is the only thing that says so.
async Task<int> HammeredAsync()
{
    const int Hammers = 10;
    var hammered = $"{pipe}-hammer";
    var log = Path.Combine(Path.GetTempPath(), $"cslq-session-{hammered}.log");

    var first = Stopwatch.StartNew();
    var (exit, output) = await AskAsync(hammered);
    first.Stop();
    Console.WriteLine($"hammer: first call exit {exit} in {first.ElapsedMilliseconds} ms");
    if (output.Contains("session unavailable", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"hammer: the first call could not reach a session at all: {output}");
        Dump(log);
        return 1;
    }

    var bad = 0;
    for (var i = 1; i <= Hammers; i++)
    {
        var ping = Stopwatch.StartNew();
        var pinged = await PingAsync(hammered, quiet: true);
        ping.Stop();

        var ask = Stopwatch.StartNew();
        var (code, text) = await AskAsync(hammered);
        ask.Stop();

        var fellBack = text.Contains("session unavailable", StringComparison.Ordinal);
        Console.WriteLine(
            $"hammer {i}: ping {(pinged is null ? "unanswered" : "answered")} in "
            + $"{ping.ElapsedMilliseconds} ms, ask exit {code} in {ask.ElapsedMilliseconds} ms"
            + (fellBack ? " THROUGH THE FALLBACK" : string.Empty));

        if (code == exit && !fellBack) continue;
        Console.Error.WriteLine($"hammer {i} of {Hammers}: {text.Trim()}");
        bad++;
    }

    await PingAsync(hammered, quiet: true, method: "stop");

    // Always, not only on failure: a leg that passes slowly is the thing under investigation,
    // and the session's own log is the only record of a session that died and was replaced.
    Dump(log);
    Console.WriteLine(
        bad == 0
            ? $"pipe smoke [hammer]: {Hammers} of {Hammers} requests survived a probe-then-send"
            : $"pipe smoke [hammer]: {bad} of {Hammers} requests fell back after a probe-then-send");
    return bad;
}

// One real cslq run against its own session, output captured.
async Task<(int Exit, string Output)> AskAsync(string name)
{
    var psi = new ProcessStartInfo(cslq!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var a in new[] { "ready", "--root", hammerRoot }) psi.ArgumentList.Add(a);
    psi.Environment["CSLQ_SESSION_PIPE_NAME"] = name;

    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEndAsync();
    var stderr = proc.StandardError.ReadToEndAsync();
    using var cap = new CancellationTokenSource(TimeSpan.FromSeconds(180));
    await proc.WaitForExitAsync(cap.Token);
    return (proc.ExitCode, (await stdout + await stderr).ReplaceLineEndings(" "));
}

static void Dump(string log)
{
    Console.Error.WriteLine($"session log {log}");
    try
    {
        using var stream = new FileStream(
            log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) Console.Error.WriteLine($"      | {line}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"      | (unreadable: {ex.GetType().Name}: {ex.Message})");
    }
}

// How the loop treats the object that owns the socket path.
enum Shape
{
    // A fresh instance per iteration, the accepted one answered on a task of its own and
    // disposed there -- after the loop has bound the next. Diagnostic only: see the header.
    Overlap,

    // The same, minus the overlap: the accepted instance is answered and disposed before the
    // next one is created. Isolates "two instances alive at once" from "disposal at all".
    // Diagnostic only.
    Serial,

    // One instance for the life of the loop: Disconnect() after each request and wait again on
    // the same object, so the path is never unlinked while the server lives. The trade is one
    // connection at a time.
    Single,

    // Session.AcceptAsync as it stands, and the only shape whose failure is a regression: an
    // instance is always listening, and the successor is bound before the accepted one is
    // handed off to be served and disposed.
    Successor,
}

// `ping` and `stop` take nothing and return nothing, so the whole wire here is the empty result
// JsonRpc still has to deserialise.
[JsonSerializable(typeof(object))]
partial class Wire : JsonSerializerContext;
