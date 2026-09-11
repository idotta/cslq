// The daemon must not inherit the launching client's stdio. It used to: Windows
// CreateProcess passes bInheritHandles=TRUE, so cslq's stdout handle reached the thin
// client and, through it, the daemon that outlives the call -- and a harness capturing
// cslq's output then waited for EOF on a pipe the daemon still held. Every call was a
// launching call that blocked for the whole keepalive and returned with the daemon dead.
//
// No bash leg can pin this: Git Bash `> file` hands cslq a file handle, and bash waits for
// exit rather than EOF, so the failure is invisible from the shell that runs the suite.
// A .NET process with RedirectStandardOutput is the harness under test.
//
// A file-based app rather than a project, like probes/hold-mutex.cs.
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

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

var pipe = $"cslq-capture-{Environment.ProcessId}";
var psi = new ProcessStartInfo(args[0])
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
foreach (var a in new[] { "ready", "--root", "fixture", "--timeout", "300" }) psi.ArgumentList.Add(a);
psi.Environment["ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME"] = pipe;
psi.Environment["ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE"] = Keepalive.ToString();
// A cslq session of its own, so this run really does launch the daemon on the pipe probed
// below rather than being answered by a session the suite already holds. It also puts the
// leg back where the bug lives: the session is a third process in the chain now, spawned by
// a cslq whose stdout is the redirected pipe above, and it is the one that would go on
// holding that pipe for its whole life if either link stopped clearing the inherit flag.
// Inherited when the caller named one -- probes/run.sh does, so its EXIT trap can find the
// session this leaves behind -- and invented otherwise, so the file runs on its own too.
psi.Environment["CSLQ_SESSION_PIPE_NAME"] =
    Environment.GetEnvironmentVariable("CSLQ_SESSION_PIPE_NAME") is { Length: > 0 } named
        ? named
        : $"cslq-capture-session-{Environment.ProcessId}";

var started = Stopwatch.StartNew();
using var proc = Process.Start(psi)!;
var stdout = proc.StandardOutput.ReadToEndAsync();
var stderr = proc.StandardError.ReadToEndAsync();
await proc.WaitForExitAsync();
var output = (await stdout + await stderr).Trim().ReplaceLineEndings(" ");
started.Stop();

var elapsed = started.Elapsed.TotalSeconds;
// Connecting is the liveness test rather than looking for the name: .NET normalises
// `\.\pipe\` into a path that does not exist and Directory.GetFiles throws. A bare
// connect-and-close leaves the daemon serving -- measured: it answers the next client warm.
var alive = true;
try
{
    using var probe = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
    probe.Connect(TimeSpan.FromSeconds(2));
}
catch (Exception ex) when (ex is TimeoutException or IOException)
{
    alive = false;
}

if (proc.ExitCode == 0 && elapsed < BoundSeconds && alive)
{
    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"captured stdout returned in {elapsed:F1}s with the daemon still on {pipe}"));
    return 0;
}

Console.Error.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"exit {proc.ExitCode} after {elapsed:F1}s, daemon alive: {alive} "
    + $"(wanted 0 under {BoundSeconds}s with the daemon alive): {output}"));
return 1;
