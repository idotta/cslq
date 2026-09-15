using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cslq;

/// <summary>
/// The session log's writer: one complete line is one atomic append, so several session
/// processes sharing a <see cref="Session.LogPath"/> cannot overwrite each other's regions.
/// <para>
/// <see cref="TextWriter.Synchronized(TextWriter)"/> serialises the threads inside one
/// process and nothing across processes, and <c>FileMode.Append</c> is not the cross-process
/// answer either: .NET seeks to the end at open and then writes at a position it tracks
/// itself. Measured 2026-09-15 on Windows, four processes appending 2 000 lines each to one
/// file through <c>FileMode.Append</c>: 4 314 and 4 000 lines of the 8 000 survived, two of
/// them torn. The same shape through this writer: 8 000 of 8 000, none torn, three runs.
/// That is not cosmetic — the start line is the only record of a session's pid,
/// <c>Session.Pid</c> parses it and <c>probes/run.sh</c>'s EXIT trap kills by it, so a torn
/// start line leaks a session and its Roslyn server past the end of a gate run.
/// </para>
/// <para>
/// The file is opened <c>FILE_APPEND_DATA</c> alone on Windows and <c>O_APPEND</c> on Unix,
/// where the kernel places each write at the end under the file's own lock rather than at an
/// offset the caller chose. The unit that has to be atomic is a <em>line</em>, not a buffer
/// flush: this writer is also <c>Console.SetOut</c>'s target, so arbitrary <c>Write</c> calls
/// land here and are held until the newline that completes them.
/// </para>
/// </summary>
internal sealed class AppendLog : TextWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SafeFileHandle? _windows;
    private readonly int _fd = -1;
    private readonly StringBuilder _pending = new();

    internal AppendLog(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            _windows = Native.AppendHandle(path);
        }
        else
        {
            // The file is created here rather than by open(2): see Native.OpenAppend for why
            // it is called with no mode argument at all. A file deleted between these two
            // steps is an ordinary open failure.
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite).Dispose();
            _fd = Native.OpenAppend(path);
            if (_fd < 0)
                throw new IOException(
                    $"could not open {path} for appending: errno {Marshal.GetLastPInvokeError()}");
        }
    }

    public override Encoding Encoding => Utf8;

    /// <summary>
    /// A no-op: every completed line is already on disk, and the tail of an unfinished one is
    /// deliberately held back — flushing it would be exactly the partial write this class
    /// exists to prevent. <see cref="Dispose(bool)"/> is what emits a final unterminated line.
    /// </summary>
    public override void Flush() { }

    public override void Write(char value)
    {
        _pending.Append(value);
        if (value == '\n') Emit();
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _pending.Append(value);
        if (value.Contains('\n')) Emit();
    }

    public override void Write(char[] buffer, int index, int count)
    {
        var span = buffer.AsSpan(index, count);
        _pending.Append(span);
        if (span.Contains('\n')) Emit();
    }

    /// <summary>Appends every complete line held, one write call each, and keeps the rest.</summary>
    private void Emit()
    {
        var text = _pending.ToString();
        var last = text.LastIndexOf('\n');
        if (last < 0) return;
        _pending.Remove(0, last + 1);

        var start = 0;
        while (start <= last)
        {
            var end = text.IndexOf('\n', start);
            Append(text.AsSpan(start, end - start + 1));
            start = end + 1;
        }
    }

    private void Append(ReadOnlySpan<char> line)
    {
        var bytes = Utf8.GetBytes(line.ToString());
        if (_windows is not null) Native.AppendAll(_windows, bytes);
        else Native.AppendAll(_fd, bytes);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_pending.Length > 0)
            {
                Append(_pending.ToString());
                _pending.Clear();
            }

            _windows?.Dispose();
            if (_fd >= 0) Native.Close(_fd);
        }

        base.Dispose(disposing);
    }
}
