using System.IO.Pipes;
using System.Text;

namespace Cslq.Tests;

/// <summary>
/// The session log's two guarantees: a line is written whole or not at all, and two writers
/// holding the same file interleave without overwriting each other. The second is what
/// <c>FileMode.Append</c> does not give — .NET seeks to the end at open and then writes at a
/// position it tracks itself — and it matters because a failing bind respawns sessions on one
/// pipe: a torn start line is a pid <c>Session.Pid</c> cannot read, and a session the probe
/// suite's EXIT trap then leaks with its Roslyn server.
/// </summary>
public class AppendLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cslq-log-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_dir, name);

    /// <summary>
    /// Shared with the writer that is still holding the file, exactly as <c>Session.Pid</c>
    /// reads a running session's log: the default read denies writers and fails on the only
    /// log worth reading.
    /// </summary>
    private static string Read(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    [Fact]
    public void Nothing_is_written_until_the_line_is_complete()
    {
        var path = Path("partial.log");
        using (var log = new AppendLog(path))
        {
            log.Write("half a ");
            log.Flush();
            Assert.Equal("", Read(path));

            log.WriteLine("line");
            Assert.Equal("half a line" + Environment.NewLine, Read(path));
        }
    }

    /// <summary>A line with no newline after it is still the caller's, so closing emits it.</summary>
    [Fact]
    public void A_final_unterminated_line_is_emitted_on_close()
    {
        var path = Path("tail.log");
        using (var log = new AppendLog(path)) log.Write("no newline");
        Assert.Equal("no newline", File.ReadAllText(path));
    }

    [Fact]
    public void An_existing_log_is_appended_to_rather_than_truncated()
    {
        var path = Path("reopen.log");
        using (var log = new AppendLog(path)) log.WriteLine("first");
        using (var log = new AppendLog(path)) log.WriteLine("second");
        Assert.Equal(["first", "second"], File.ReadAllLines(path));
    }

    /// <summary>
    /// Two open writers, as two session processes sharing one <c>Session.LogPath</c> are.
    /// Each holds a file handle of its own, which is the whole of what a second process adds,
    /// so this goes red on the defect: through <c>FileMode.Append</c> the second writer's
    /// lines land on top of the first's and the count comes back short.
    /// </summary>
    [Fact]
    public void Two_writers_on_one_file_do_not_overwrite_each_other()
    {
        var path = Path("shared.log");
        const int lines = 500;
        var body = new string('x', 180);

        using (var a = new AppendLog(path))
        using (var b = new AppendLog(path))
        {
            for (var i = 0; i < lines; i++)
            {
                a.WriteLine($"a {i:0000} {body}");
                b.WriteLine($"b {i:0000} {body}");
            }
        }

        var written = File.ReadAllLines(path);
        Assert.Equal(lines * 2, written.Length);
        Assert.All(written, line =>
        {
            var parts = line.Split(' ');
            Assert.Equal(3, parts.Length);
            Assert.Contains(parts[0], (string[])["a", "b"]);
            Assert.Equal(4, parts[1].Length);
            Assert.Equal(body, parts[2]);
        });
    }

    /// <summary>
    /// The writer is <c>Console.SetOut</c>'s target as well as the session's own, so it is
    /// written by several threads at once. <c>TextWriter.Synchronized</c> is what covers that,
    /// exactly as <c>Session.ServeAsync</c> wraps it.
    /// </summary>
    [Fact]
    public async Task Synchronized_writers_in_one_process_interleave_whole_lines()
    {
        var path = Path("threads.log");
        var body = new string('y', 120);

        using (var log = TextWriter.Synchronized(new AppendLog(path)))
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
            {
                for (var i = 0; i < 200; i++) log.WriteLine($"{t} {i:0000} {body}");
            })));
        }

        var written = File.ReadAllLines(path);
        Assert.Equal(800, written.Length);
        Assert.All(written, line => Assert.EndsWith(" " + body, line, StringComparison.Ordinal));
    }

    /// <summary>
    /// The log is created through ordinary .NET and <c>open(2)</c> is then called with two
    /// arguments and no mode, because <c>open</c> is variadic and the Apple ARM64 ABI passes
    /// variadic arguments on the stack: a third argument declared non-variadically is read
    /// off the stack on osx-arm64 and the file gets whatever was there, silently. This is the
    /// assertion that goes red on that, and it runs on CI's ubuntu and macos legs.
    /// </summary>
    [Fact]
    public void A_new_log_is_created_with_an_ordinary_mode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes.");
            return;
        }

        var path = Path("mode.log");
        using (var log = new AppendLog(path)) log.WriteLine("line");

        var mode = File.GetUnixFileMode(path);
        Assert.True(mode.HasFlag(UnixFileMode.UserRead), $"{mode}");
        Assert.True(mode.HasFlag(UnixFileMode.UserWrite), $"{mode}");
        // Anything the umask may have cleared is fine; anything beyond a data file is not,
        // which is the shape a mode read off the stack takes.
        Assert.Equal(default, mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute |
            UnixFileMode.OtherExecute | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite |
            UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit));
    }

    [Fact]
    public void The_bytes_are_utf8_without_a_bom()
    {
        var path = Path("utf8.log");
        using (var log = new AppendLog(path)) log.Write("é\n");
        Assert.Equal([0xC3, 0xA9, (byte)'\n'], File.ReadAllBytes(path));
        Assert.Equal("é\n", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
    }
}

/// <summary>
/// The accept loop's other half: no single bind may end it either. See
/// <c>Session.BindAsync</c>.
/// </summary>
public class SessionBindTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private static string[] Lines(StringWriter log) =>
        log.ToString().TrimEnd().Split(Environment.NewLine);

    /// <summary>
    /// A transient failure — a client hanging up into the instance being replaced — is what
    /// the retry is for, and it costs two log lines however many attempts it took: the log is
    /// the only record of the session's pid and a line per attempt would bury it.
    /// </summary>
    [Fact]
    public async Task A_transient_bind_failure_is_retried_and_logged_twice()
    {
        var log = new StringWriter();
        var attempts = 0;
        var name = "cslq-test-" + Guid.NewGuid().ToString("n")[..12];

        var pipe = await Session.BindAsync(
            () => ++attempts < 3
                ? throw new UnauthorizedAccessException("Access to the path is denied.")
                : new NamedPipeServerStream(name, PipeDirection.InOut, 1),
            log,
            Window,
            CancellationToken.None);

        using (pipe)
        {
            Assert.NotNull(pipe);
            Assert.Equal(3, attempts);
        }

        Assert.Equal(
            [
                "cslq session: bind failed: UnauthorizedAccessException: Access to the path is denied.",
                "cslq session: bound after 2 failed attempts.",
            ],
            Lines(log));
    }

    /// <summary>
    /// The failure this was written for is permanent: a pipe whose first instance was created
    /// under another token denies ours for as long as that instance lives. Retried forever,
    /// the session would spin, log and hold a Roslyn server for as long as the machine was up,
    /// never reaching the <c>WaitForConnectionAsync</c> that arms its keepalive. So the window
    /// is bounded and a session that cannot have the name ends instead.
    /// </summary>
    [Fact]
    public async Task A_permanent_bind_failure_gives_up_inside_its_window()
    {
        var log = new StringWriter();
        var attempts = 0;
        var window = TimeSpan.FromMilliseconds(250);
        var started = DateTime.UtcNow;

        var pipe = await Session.BindAsync(
            () =>
            {
                attempts++;
                throw new UnauthorizedAccessException("Access to the path is denied.");
            },
            log,
            window,
            CancellationToken.None);

        var elapsed = DateTime.UtcNow - started;
        Assert.Null(pipe);
        Assert.True(attempts > 1, $"{attempts} attempts");
        Assert.True(elapsed >= window, $"{elapsed} < {window}");
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"{elapsed} is unbounded");

        var lines = Lines(log);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("cslq session: bind failed: UnauthorizedAccessException", lines[0], StringComparison.Ordinal);
        Assert.Contains("stopping.", lines[1], StringComparison.Ordinal);
        Assert.Contains("UnauthorizedAccessException", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The session going down also ends the retry, and it answers null rather than throwing:
    /// the accept loop's <c>finally</c> still owns the instance it was holding.
    /// </summary>
    [Fact]
    public async Task A_cancelled_session_ends_the_retry_with_null()
    {
        using var cts = new CancellationTokenSource();
        var log = new StringWriter();

        var pipe = await Session.BindAsync(
            () =>
            {
                cts.Cancel();
                throw new UnauthorizedAccessException("denied");
            },
            log,
            Window,
            cts.Token);

        Assert.Null(pipe);
    }

    /// <summary>
    /// A bind that succeeds first time says nothing at all, which is every ordinary rebind in
    /// the accept loop.
    /// </summary>
    [Fact]
    public async Task A_bind_that_works_writes_nothing()
    {
        var log = new StringWriter();
        var name = "cslq-test-" + Guid.NewGuid().ToString("n")[..12];

        using var pipe = await Session.BindAsync(
            () => new NamedPipeServerStream(name, PipeDirection.InOut, 1), log, Window, CancellationToken.None);

        Assert.NotNull(pipe);
        Assert.Equal("", log.ToString());
    }
}
