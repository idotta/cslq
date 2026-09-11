// The session's accept loop in isolation, with no workspace and no cslq behind it.
//
// Why it exists: on ubuntu-latest and macos-latest every probe case passed and every one of
// them took a uniform ~62 s, which is `MutexWait` (10 s) plus `WaitForPipeAsync` (30 s) plus
// the in-process load -- the signature of a client that never reaches a session, spawns one,
// waits out both timeouts and does the work itself. The fallback is why nothing went red.
// A 30-minute gate is no way to test that, so this reproduces the shape in seconds: a
// platform that cannot hold a session says so before the fixture is even restored.
//
// The shape is Session.AcceptAsync's, deliberately, down to the pipe options and the
// hand-off: a fresh NamedPipeServerStream per iteration, the accepted one answered on a task
// of its own and disposed there -- after the loop has already created and bound the next
// instance. On Windows a pipe name is a reference-counted kernel object; on Unix .NET
// implements it as a Unix domain socket file under $TMPDIR, and a path is not a kernel
// object. Each round is therefore two connections rather than one, because that is what a
// real call makes: Session.ListeningAsync probes, and Session.SendAsync asks.
//
// A file-based app rather than a project, like probes/hold-mutex.cs and probes/hangup.cs.
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

const int ConnectMs = 2000;

var pipe = args.Length > 0 ? args[0] : $"cslq-smoke-{Environment.ProcessId}";
var rounds = args.Length > 1 ? int.Parse(args[1]) : 3;
// Optional, and the half that covers the real thing: the path to a built cslq, and the root
// to serve. Given both, the second part below spawns an actual session and round-trips
// against it. The in-process part alone would pass on a platform whose *spawn* is what
// fails, and say nothing.
var cslq = args.Length > 2 ? args[2] : null;
var serveRoot = args.Length > 3 ? Path.GetFullPath(args[3]) : Environment.CurrentDirectory;
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// Where .NET puts the socket file off Windows. Reported rather than asserted: if the
// hypothesis is right, this is the line that says so -- the file is there for round one and
// gone afterwards.
var socket = OperatingSystem.IsWindows()
    ? null
    : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipe);

using var stopping = new CancellationTokenSource();
var server = Task.Run(() => AcceptAsync(stopping.Token));

var failures = 0;
for (var round = 1; round <= rounds; round++)
{
    if (socket is not null)
    {
        Console.WriteLine($"round {round}: socket file {(File.Exists(socket) ? "present" : "MISSING")} ({socket})");
    }

    failures += await RoundTripAsync(round, "ping") ? 0 : 1;
    failures += await RoundTripAsync(round, "request") ? 0 : 1;
}

await stopping.CancelAsync();
try
{
    await server;
}
catch (OperationCanceledException)
{
}

Console.WriteLine(
    failures == 0
        ? $"pipe smoke: {rounds * 2} of {rounds * 2} connections answered on {pipe}"
        : $"pipe smoke: {failures} of {rounds * 2} connections failed on {pipe}");

if (cslq is not null) failures += await ServedAsync();
return failures == 0 ? 0 : 1;

// The same question asked of a real session, across a process boundary. Everything here is
// answered by Session.ServeOneAsync before it touches a workspace -- an empty argv is the
// liveness ping and a lone --session-stop is the stop lever, both answered off the request
// gate -- so this costs a process start and nothing else, and it works on a root that has
// never been restored.
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
        if (await ServeRoundTripAsync(served, quiet: true)) { listening = true; break; }
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
        if (!await ServeRoundTripAsync(served, quiet: false)) bad++;
    }

    // The stop lever, so this leaves nothing behind even where the trap cannot see it.
    await ServeRoundTripAsync(served, quiet: true, argv: "\"--session-stop\"");
    if (!session.WaitForExit(10_000))
    {
        Console.Error.WriteLine("serve: the session did not stop when asked");
        session.Kill(entireProcessTree: true);
        bad++;
    }

    if (bad > 0) Dump(log);
    Console.WriteLine(
        bad == 0
            ? $"pipe smoke: a real session answered {rounds} of {rounds} pings on {served}"
            : $"pipe smoke: a real session failed {bad} of {rounds} pings on {served}");
    return bad;
}

async Task<bool> ServeRoundTripAsync(string name, bool quiet, string argv = "")
{
    var started = Stopwatch.StartNew();
    try
    {
        using var client = new NamedPipeClientStream(
            ".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(ConnectMs);

        var writer = new StreamWriter(client, utf8) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(client, utf8);
        // Hand-rolled, because this file is not allowed to reference Cslq: the shape is
        // Session.Request, and a ping is the empty argv.
        await writer.WriteLineAsync($"{{\"version\":\"probe\",\"argv\":[{argv}]}}");
        var answer = await reader.ReadLineAsync();
        started.Stop();

        if (answer is null)
        {
            if (!quiet) Console.Error.WriteLine("serve: connected but was not answered");
            return false;
        }

        if (!quiet) Console.WriteLine($"serve: answered in {started.ElapsedMilliseconds} ms");
        return true;
    }
    catch (Exception ex)
    {
        started.Stop();
        if (!quiet)
        {
            Console.Error.WriteLine(
                $"serve: {ex.GetType().Name}: {ex.Message} ({started.ElapsedMilliseconds} ms)");
        }

        return false;
    }
}

static void Dump(string log)
{
    Console.Error.WriteLine($"serve: session log {log}");
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

async Task<bool> RoundTripAsync(int round, string kind)
{
    var started = Stopwatch.StartNew();
    try
    {
        using var client = new NamedPipeClientStream(
            ".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(ConnectMs);

        var writer = new StreamWriter(client, utf8) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(client, utf8);
        await writer.WriteLineAsync($"{kind} {round}");
        var answer = await reader.ReadLineAsync();

        started.Stop();
        if (answer is null)
        {
            Console.Error.WriteLine($"round {round} {kind}: connected but was not answered");
            return false;
        }

        Console.WriteLine($"round {round} {kind}: {answer} ({started.ElapsedMilliseconds} ms)");
        return true;
    }
    catch (Exception ex)
    {
        started.Stop();
        Console.Error.WriteLine(
            $"round {round} {kind}: {ex.GetType().Name}: {ex.Message} ({started.ElapsedMilliseconds} ms)");
        return false;
    }
}

// Session.AcceptAsync, with the workspace and the keepalive taken out.
async Task AcceptAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var accepted = new NamedPipeServerStream(
            pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

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
            await accepted.DisposeAsync();
            Console.Error.WriteLine($"accept failed: {ex.GetType().Name}: {ex.Message}");
            await Task.Delay(25, CancellationToken.None);
            continue;
        }

        // Answered on a task of its own and disposed there, which is the ordering under test:
        // the loop binds the next instance before this one lets go of the last.
        _ = ServeOneAsync(accepted, ct);
    }
}

async Task ServeOneAsync(NamedPipeServerStream accepted, CancellationToken ct)
{
    try
    {
        await using (accepted)
        {
            var reader = new StreamReader(accepted, utf8);
            var writer = new StreamWriter(accepted, utf8) { AutoFlush = true, NewLine = "\n" };
            if (await reader.ReadLineAsync(ct) is not { } line) return;
            await writer.WriteLineAsync($"answered {line}".AsMemory(), ct);
            if (OperatingSystem.IsWindows() && accepted.IsConnected) accepted.WaitForPipeDrain();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"serve failed: {ex.GetType().Name}: {ex.Message}");
    }
}
