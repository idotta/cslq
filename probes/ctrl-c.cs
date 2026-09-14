// Ctrl+C. Program.Main installs Console.CancelKeyPress, cancels the token and answers the
// OperationCanceledException with `cslq: interrupted.` and exit 130; that path was measured by
// hand once, on 2026-09-06, and by nothing since. A handler that is removed, a catch folded
// into the generic CslqException one, or an `e.Cancel` that is dropped -- so the runtime kills
// the process at 134 -- is invisible to every other leg and every unit test.
//
// From Git Bash `kill -INT <pid>` does not test it: MSYS terminates the native .NET process
// without raising a console control event, so the handler never runs and the 130 that shows up
// is bash's own signal status. A leg written that way passes on a build with the handler
// deleted, which is the regression this exists to catch. The real path is a child launched with
// CREATE_NEW_PROCESS_GROUP and GenerateConsoleCtrlEvent against that group -- and it has to be
// CTRL_BREAK_EVENT, because CreateProcess disables CTRL+C for a new process group. .NET raises
// CancelKeyPress for ConsoleSpecialKey.ControlBreak as well, which is what the 2026-09-06
// measurement confirmed this build answers.
//
// Windows only, deliberately: off it a plain `kill -INT` raises a real SIGINT and .NET's
// handler fires for it, so probes/run.sh takes the cheap path there instead of porting this.
//
// Process.Start cannot ask for a process group, so the launch is CreateProcessW by hand. A
// file-based app rather than a project, like probes/hold-mutex.cs.
//
// [LibraryImport] generates unsafe code, which a file-based app does not allow by default:
// without this the build fails with SYSLIB1062 before anything here runs.
#:property AllowUnsafeBlocks=true
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run probes/ctrl-c.cs -- <path to cslq> <absolute root>");
    return 2;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("probes/ctrl-c.cs is the Windows path; run.sh sends a real SIGINT elsewhere");
    return 2;
}

var cslq = args[0];
// Absolute, and the caller's job: `dotnet run` on a file-based app sets the working directory
// to the .cs file's folder on some SDK feature bands and to the caller's on others.
var root = args[1];

// Interrupted mid-work rather than mid-teardown: a cold --no-daemon --no-session `ready` takes
// seconds, and both opt-outs mean this run owns the server it launches rather than borrowing
// the gate's. The 300 s timeout is what makes the elapsed bound below mean something -- the
// failure it catches is a handler that answers only once the whole timeout has run out.
var command = $"\"{cslq}\" ready --root \"{root}\" --timeout 300 --no-daemon --no-session";

var logPath = Path.GetTempFileName();
using var log = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
using var nul = new FileStream("NUL", FileMode.Open, FileAccess.Read);
// CreateProcess is called with bInheritHandles, and a handle named in STARTUPINFO reaches the
// child only if it is inheritable. A file rather than a pipe on purpose: what outlives this
// call is a server tree, and handing one of those a pipe is the trap probes/stdout-capture.cs
// is about.
Inherit(log.SafeFileHandle.DangerousGetHandle());
Inherit(nul.SafeFileHandle.DangerousGetHandle());

var si = new Win32.StartupInfo
{
    Size = Marshal.SizeOf<Win32.StartupInfo>(),
    Flags = Win32.STARTF_USESTDHANDLES,
    StdInput = nul.SafeFileHandle.DangerousGetHandle(),
    StdOutput = log.SafeFileHandle.DangerousGetHandle(),
    StdError = log.SafeFileHandle.DangerousGetHandle(),
};
var line = (command + "\0").ToCharArray();
if (!Win32.CreateProcess(
        null, ref line[0], 0, 0, true, Win32.CREATE_NEW_PROCESS_GROUP, 0, null, ref si, out var pi))
{
    Console.Error.WriteLine($"CreateProcessW failed: {Marshal.GetLastWin32Error()} for {command}");
    return 1;
}

Win32.CloseHandle(pi.Thread);
using var child = Process.GetProcessById(pi.ProcessId);

// Sent once the call is demonstrably working rather than after a fixed sleep: consumed CPU is
// the cheap signal that says so, and it costs no process-tree walk. A call that has already
// exited is not the thing this leg interrupts, and treating it as one would make the leg pass
// or fail by timing.
var waited = Stopwatch.StartNew();
while (!child.HasExited && child.TotalProcessorTime < TimeSpan.FromMilliseconds(250))
{
    if (waited.Elapsed > TimeSpan.FromSeconds(20)) break;
    await Task.Delay(25);
}

if (child.HasExited)
{
    Console.Error.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"the call exited on its own after {waited.Elapsed.TotalSeconds:F1}s (exit {Exit(pi.Process)}) "
        + $"before it could be interrupted: {Read(logPath)}"));
    Win32.CloseHandle(pi.Process);
    return 1;
}

var busy = child.TotalProcessorTime;
var interrupted = Stopwatch.StartNew();
if (!Win32.GenerateConsoleCtrlEvent(Win32.CTRL_BREAK_EVENT, (uint)pi.ProcessId))
{
    Console.Error.WriteLine($"GenerateConsoleCtrlEvent failed: {Marshal.GetLastWin32Error()}");
    Win32.CloseHandle(pi.Process);
    child.Kill(entireProcessTree: true);
    return 1;
}

var exited = child.WaitForExit(60_000);
interrupted.Stop();
if (!exited)
{
    Console.Error.WriteLine("the call never exited after CTRL_BREAK_EVENT");
    child.Kill(entireProcessTree: true);
    return 1;
}

var output = Read(logPath);
// GetExitCodeProcess rather than Process.ExitCode: this Process object attached to a pid it
// did not start, and .NET refuses to report an exit code for one of those.
var exit = Exit(pi.Process);
var elapsed = interrupted.Elapsed.TotalSeconds;
Win32.CloseHandle(pi.Process);
log.Dispose();
try
{
    File.Delete(logPath);
}
catch (IOException)
{
    // Best effort: an interrupted server tree can still be holding its inherited copy of this
    // handle for a moment, and a temp file left behind is not a failure of the path under test.
}

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"CTRL_BREAK_EVENT after {waited.Elapsed.TotalSeconds:F1}s and {busy.TotalMilliseconds:F0}ms of CPU: "
    + $"exit {exit} in {elapsed * 1000:F0}ms: {output}"));

var ok = exit == 130;
// 134 is the unhandled-cancellation regression, and it reaches stderr as a stack trace rather
// than as this line.
ok &= output.Contains("cslq: interrupted.", StringComparison.Ordinal);
// The bound rather than the message is what catches a handler that only answers once the whole
// --timeout has run out: that build prints exactly the right line, five minutes late. Measured
// by hand at 62 ms on 2026-09-06, so two seconds is headroom for a loaded runner and still two
// orders of magnitude under the failure.
ok &= elapsed < 2;

// The orphan half is deliberately left out. The 2026-09-06 note also saw the run's own server
// tree gone afterwards, but checking that means recording the tree before the interrupt and
// re-checking it once cslq -- its parent -- is gone, and the server is a `dotnet` process
// indistinguishable by name from the daemon and the sessions the gate is running alongside. A
// check that cannot tell those apart either goes red on a healthy machine or can never go red
// at all, and the second is worse than no check.

if (ok) return 0;

Console.Error.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"wanted exit 130 with `cslq: interrupted.` under 2s; got exit {exit} in {elapsed:F3}s: {output}"));
return 1;

static int Exit(nint process) =>
    Win32.GetExitCodeProcess(process, out var code) ? unchecked((int)code) : -1;

static void Inherit(nint handle) =>
    Win32.SetHandleInformation(handle, Win32.HANDLE_FLAG_INHERIT, Win32.HANDLE_FLAG_INHERIT);

static string Read(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd().Trim().ReplaceLineEndings(" ");
}

internal static partial class Win32
{
    internal const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    internal const uint CTRL_BREAK_EVENT = 1;
    internal const int STARTF_USESTDHANDLES = 0x00000100;
    internal const int HANDLE_FLAG_INHERIT = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    // The command line has to be a writable buffer: CreateProcessW may modify it in place.
    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcess(
        string? applicationName,
        ref char commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetHandleInformation(nint handle, int mask, int flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
