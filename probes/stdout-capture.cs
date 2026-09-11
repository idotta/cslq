// Nothing that outlives a cslq call may inherit the caller's stdio. Two processes do
// outlive one -- the daemon and the session -- and each platform had its own leak.
//
// Windows: CreateProcess passes bInheritHandles=TRUE, so cslq's stdout handle reached the
// thin client and, through it, the daemon -- and a harness capturing cslq's output then
// waited for EOF on a pipe the daemon still held. Every call was a launching call that
// blocked for the whole keepalive and returned with the daemon dead.
//
// Unix: the handle flag above is a Win32 call and a no-op off Windows, and a Unix child
// inherits fds 0/1/2 verbatim unless the parent redirects them. Session.Spawn did not, so
// the session held the capture pipe for its whole keepalive instead: 62.7 s a call against
// a 60 s keepalive on ubuntu and macos, every request inside it answered in milliseconds.
// This file ran on Windows alone for as long as that lasted.
//
// No bash leg can pin this: `> file` hands cslq a real file handle, and bash waits for
// exit rather than EOF, so the failure is invisible from the shell that runs the suite.
// A .NET process with RedirectStandardOutput is the harness under test.
//
// A file-based app rather than a project, like probes/hold-mutex.cs.
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: dotnet run probes/stdout-capture.cs -- <path to cslq>");
    return 2;
}

// Keepalive well above the bound below: the failure mode is keepalive + a cold load, the
// pass is the cold load alone, and the gap between them is the whole assertion. Wide enough
// that a slow cold load on a CI runner cannot reach the bound.
const int Keepalive = 30;
const int BoundSeconds = 25;

var cslq = args[0];
var pipe = $"cslq-capture-{Environment.ProcessId}";

var (exit, elapsed, output) = await RunAsync(pipe, session: true);
var alive = Alive(pipe, out var how);

var notes = new StringBuilder();
notes.Append(string.Create(
    CultureInfo.InvariantCulture,
    $"captured stdout: exit {exit} after {elapsed:F1}s, daemon on {pipe}: {how}"));
notes.Append("; ").Append(Socket(pipe));
if (Servers() is { Length: > 0 } servers) notes.Append("; ").Append(servers);

// The control, run only when the liveness half has already failed: it costs a second cold
// load, and what it separates is worth that when the answer is in doubt. --no-session is the
// pre-session chain -- cslq launches the thin client itself -- so a daemon missing here too
// is a property of the daemon on this platform rather than of anything the session does.
if (!alive)
{
    var control = $"{pipe}-nosession";
    var (cx, cs, _) = await RunAsync(control, session: false, "--no-session");
    notes.Append(string.Create(
        CultureInfo.InvariantCulture,
        $"; --no-session control: exit {cx} after {cs:F1}s, daemon on {control}: "));
    Alive(control, out var chow);
    notes.Append(chow).Append("; ").Append(Socket(control));
}

Console.WriteLine(notes.ToString());

if (exit == 0 && elapsed < BoundSeconds && alive) return 0;

Console.Error.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"exit {exit} after {elapsed:F1}s, daemon alive: {alive} "
    + $"(wanted 0 under {BoundSeconds}s with the daemon alive): {output}"));
return 1;

async Task<(int Exit, double Seconds, string Output)> RunAsync(
    string daemon, bool session, params string[] extra)
{
    var psi = new ProcessStartInfo(cslq)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var a in new[] { "ready", "--root", "fixture", "--timeout", "300" }) psi.ArgumentList.Add(a);
    foreach (var a in extra) psi.ArgumentList.Add(a);
    psi.Environment["ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME"] = daemon;
    psi.Environment["ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE"] = Keepalive.ToString();
    // A cslq session of its own, so this run really does launch the daemon on the pipe probed
    // below rather than being answered by a session the suite already holds. It also puts the
    // leg back where the bug lives: the session is a third process in the chain now, spawned by
    // a cslq whose stdout is the redirected pipe above, and it is the one that would go on
    // holding that pipe for its whole life if either link stopped redirecting or clearing the
    // inherit flag.
    // Inherited when the caller named one -- probes/run.sh does, so its EXIT trap can find the
    // session this leaves behind -- and invented otherwise, so the file runs on its own too.
    if (session && Environment.GetEnvironmentVariable("CSLQ_SESSION_PIPE_NAME") is { Length: > 0 } named)
    {
        psi.Environment["CSLQ_SESSION_PIPE_NAME"] = named;
    }
    else
    {
        // An invented name is a session nothing else knows about, so it also gets a keepalive
        // short enough to clean up after itself: the default is 900 s and a standalone run would
        // leave the process and its server behind for a quarter of an hour. Only on this path --
        // a suite that named the pipe has its own trap and its own keepalive, and overriding it
        // here would answer for the suite.
        psi.Environment["CSLQ_SESSION_PIPE_NAME"] = $"cslq-capture-session-{daemon}";
        psi.Environment["CSLQ_SESSION_KEEPALIVE"] = Keepalive.ToString();
    }

    var started = Stopwatch.StartNew();
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEndAsync();
    var stderr = proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();
    started.Stop();
    return (proc.ExitCode, started.Elapsed.TotalSeconds, (await stdout + await stderr).Trim().ReplaceLineEndings(" "));
}

// Connecting is the liveness test rather than looking for the name: .NET normalises
// `\.\pipe\` into a path that does not exist and Directory.GetFiles throws. A bare
// connect-and-close leaves the daemon serving -- measured: it answers the next client warm.
//
// Twice, because the two sides do not agree on options: the server sets CurrentUserOnly and
// this client did not, which on Windows is an ACL the client never checks and off Windows is
// a different question entirely. Either connect counts as alive; which one answered is in
// the line above, so a difference shows up as a measurement rather than as a green row.
static bool Alive(string daemon, out string how)
{
    var plain = Connect(daemon, PipeOptions.None);
    if (plain is null)
    {
        how = "connected";
        return true;
    }

    var owned = Connect(daemon, PipeOptions.CurrentUserOnly);
    how = owned is null
        ? $"connected with CurrentUserOnly only (plain: {plain})"
        : $"unreachable (plain: {plain}; CurrentUserOnly: {owned})";
    return owned is null;

    static string? Connect(string daemon, PipeOptions options)
    {
        try
        {
            using var probe = new NamedPipeClientStream(".", daemon, PipeDirection.InOut, options);
            probe.Connect(TimeSpan.FromSeconds(2));
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}";
        }
    }
}

// What .NET's Unix named pipes actually are: a socket file under the temp directory. Printed
// whether or not the connect worked, because "no server listening" and "no socket at all" are
// different failures and the connect alone cannot tell them apart.
static string Socket(string daemon)
{
    if (OperatingSystem.IsWindows()) return @"socket: n/a (\\.\pipe\)";
    var path = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + daemon);
    return $"socket {path}: {(File.Exists(path) ? "present" : "absent")}";
}

// A process listing filtered to the language server, which separates a daemon that died from
// one that was never started. ps rather than /proc so macos answers too.
static string Servers()
{
    if (OperatingSystem.IsWindows()) return "";
    try
    {
        var psi = new ProcessStartInfo("ps", "ax -o pid=,command=")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var text = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        var hits = text.Split('\n')
            .Where(l => l.Contains("roslyn", StringComparison.OrdinalIgnoreCase)
                || l.Contains("LanguageServer", StringComparison.Ordinal))
            .Select(l => l.Trim()[..Math.Min(l.Trim().Length, 140)])
            .Take(3)
            .ToArray();
        return hits.Length == 0 ? "no server process" : $"server processes: {string.Join(" | ", hits)}";
    }
    catch (Exception ex)
    {
        return $"ps failed: {ex.GetType().Name}";
    }
}
