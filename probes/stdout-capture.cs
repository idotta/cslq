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

var (exit, elapsed, output, watched) = await RunAsync(pipe, session: true);
var alive = Alive(pipe, out var how);

var notes = new StringBuilder();
notes.Append(string.Create(
    CultureInfo.InvariantCulture,
    $"captured stdout: exit {exit} after {elapsed:F1}s, daemon on {pipe}: {how}"));
notes.Append("; during the call: ").Append(watched);

// Everything below is for the failure, where the question is which of the three things this
// leg watches went wrong, and none of it is worth a second cold load on a passing run.
if (!alive)
{
    notes.Append("; ").Append(Socket(pipe)).Append("; ").Append(Listening());

    // --no-session is the pre-session chain -- cslq launches the thin client itself -- so a
    // daemon missing there too is a property of this platform rather than of the session.
    var control = $"{pipe}-nosession";
    var (cx, cs, _, cwatched) = await RunAsync(control, session: false, "--no-session");
    Alive(control, out var chow);
    notes.Append(string.Create(
        CultureInfo.InvariantCulture,
        $"; --no-session control: exit {cx} after {cs:F1}s, daemon on {control}: {chow}"));
    notes.Append("; during the call: ").Append(cwatched);
}

Console.WriteLine(notes.ToString());

if (exit == 0 && elapsed < BoundSeconds && alive) return 0;

Console.Error.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"exit {exit} after {elapsed:F1}s, daemon alive: {alive} "
    + $"(wanted 0 under {BoundSeconds}s with the daemon alive): {output}"));
return 1;

async Task<(int Exit, double Seconds, string Output, string Watched)> RunAsync(
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
    // Watching the socket path while the call runs is what separated a daemon that never bound
    // it from one that bound it and died with the call -- the measurement behind the platform
    // split in Alive below. It is kept because it is the evidence: off Windows this says
    // `socket never appeared` on every run, and the day it stops saying that, the reasoning
    // there needs re-reading.
    using var watching = new CancellationTokenSource();
    var watch = WatchAsync(daemon, started, watching.Token);
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEndAsync();
    var stderr = proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();
    started.Stop();
    await watching.CancelAsync();
    return (
        proc.ExitCode,
        started.Elapsed.TotalSeconds,
        (await stdout + await stderr).Trim().ReplaceLineEndings(" "),
        await watch);
}

static async Task<string> WatchAsync(string daemon, Stopwatch clock, CancellationToken ct)
{
    if (OperatingSystem.IsWindows()) return "n/a";

    var path = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + daemon);
    double first = -1, last = -1;
    while (!ct.IsCancellationRequested)
    {
        if (File.Exists(path))
        {
            var at = clock.Elapsed.TotalSeconds;
            if (first < 0) first = at;
            last = at;
        }

        try
        {
            await Task.Delay(25, ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    return first < 0
        ? "socket never appeared"
        : string.Create(CultureInfo.InvariantCulture, $"socket present from +{first:F1}s to +{last:F1}s");
}

// The daemon that was launched must still be serving, and what proves that differs by
// platform because the daemon itself does.
//
// Windows: connect to the pipe. That is the bug this leg was written for -- the daemon died
// with the captured call -- and a connect is exactly the question. Looking for the name is
// not an option: .NET normalises `\\.\pipe\` into a path Directory.GetFiles throws on. A bare
// connect-and-close leaves the daemon serving; measured, it answers the next client warm.
//
// Unix: a connect can never answer, and that is a property of the daemon rather than of this
// probe. Measured on ubuntu and macos with the socket watch above and with `ss -xl` taken
// while the gate's own daemon was serving the whole suite: no CoreFxPipe socket for the
// daemon's pipe name is ever created, on either platform, with or without a session in the
// chain -- every CoreFxPipe socket listening on that runner belonged to a cslq session. So
// the pipe name is a key the daemon chain passes in its environment rather than a socket it
// binds, and a live process carrying ours is the liveness test that means something here.
// `ps axeww` prints the environment of one's own processes on both platforms; the match is on
// the whole `NAME=value` token, so the -nosession control's name cannot satisfy the main run's.
static bool Alive(string daemon, out string how)
{
    if (!OperatingSystem.IsWindows())
    {
        var token = "ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME=" + daemon;
        var mine = Carriers(token);
        how = mine.Length == 0
            ? $"unreachable (no live process carries {token}; {Listening()})"
            : $"{mine.Length} live process(es) carry {token}: {Short(mine[0])}";
        return mine.Length > 0;
    }

    var plain = Connect(daemon, PipeOptions.None);
    if (plain is null)
    {
        how = "connected";
        return true;
    }

    // The two sides do not agree on options: the server sets CurrentUserOnly and this client
    // does not. Either connect counts as alive; which one answered is in the line, so a
    // difference shows up as a measurement rather than as a green row.
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

    static string Short(string line) => line[..Math.Min(line.Length, 120)];
}

// The live processes whose environment carries one NAME=value token. /proc on Linux, where a
// process's environment is a file and needs no parsing of ps output; `ps axeww` on macos,
// which has no /proc and prints the environment of one's own processes after the command.
static string[] Carriers(string token)
{
    if (OperatingSystem.IsLinux())
    {
        var carriers = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out _)) continue;
            try
            {
                if (!File.ReadAllText($"{dir}/environ").Split('\0').Contains(token)) continue;
                var cmd = File.ReadAllText($"{dir}/cmdline").Replace('\0', ' ').Trim();
                carriers.Add($"{Path.GetFileName(dir)} {cmd}");
            }
            catch (Exception)
            {
                // A process that exited between the listing and the read, or one that is not
                // ours: neither is an answer about the daemon.
            }
        }

        return [.. carriers];
    }

    return Run("ps", "axeww -o pid= -o command=") is { } text
        ? [.. text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token))]
        : [];
}

// What .NET's Unix named pipes actually are: a socket file under the temp directory.
static string Socket(string daemon)
{
    if (OperatingSystem.IsWindows()) return @"socket: n/a (\\.\pipe\)";
    var path = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + daemon);
    return $"socket {path}: {(File.Exists(path) ? "present" : "absent")}";
}

// What the kernel says is listening, which is the one answer that does not depend on this
// probe guessing a path. It is what proved the Unix reasoning in Alive above.
static string Listening()
{
    if (OperatingSystem.IsWindows()) return "";
    var (cmd, cmdArgs) = OperatingSystem.IsLinux() ? ("ss", "-xl") : ("lsof", "-U");
    if (Run(cmd, cmdArgs) is not { } text) return $"{cmd} failed";

    var hits = text.Split('\n')
        .Where(l => l.Contains("CoreFxPipe", StringComparison.Ordinal))
        .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        .Select(l => l[..Math.Min(l.Length, 140)])
        .Take(5)
        .ToArray();
    return hits.Length == 0
        ? $"{cmd} {cmdArgs}: no CoreFxPipe socket listening"
        : $"{cmd} {cmdArgs}: {string.Join(" | ", hits)}";
}

static string? Run(string file, string arguments)
{
    try
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var text = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return text;
    }
    catch (Exception)
    {
        return null;
    }
}
