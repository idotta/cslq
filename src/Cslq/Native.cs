using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cslq;

/// <summary>
/// The platform calls cslq makes directly: the Win32 handle flags behind
/// <c>LspClient.DisableStdioInheritance</c>, whose caller treats a failure as nothing, and the
/// append-only file handle behind <see cref="AppendLog"/>, whose caller cannot.
/// </summary>
internal static partial class Native
{
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetHandleInformation(nint hObject, int dwMask, int dwFlags);

    private const uint FileAppendData = 0x0004;
    private const uint ShareAll = 0x0001 | 0x0002 | 0x0004;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x80;

    /// <summary>
    /// Opens <paramref name="path"/> for appending with <c>FILE_APPEND_DATA</c> and no
    /// <c>FILE_WRITE_DATA</c>: that combination is what makes the kernel ignore the file
    /// pointer and place each write at the end under the file's own lock. Shared with readers
    /// and with the other sessions writing it, deletion included, exactly as
    /// <c>Session.Pid</c> opens it.
    /// </summary>
    internal static SafeFileHandle AppendHandle(string path)
    {
        var handle = CreateFileW(
            path, FileAppendData, ShareAll, 0, OpenAlways, FileAttributeNormal, 0);
        if (handle == -1)
            throw new IOException(
                $"could not open {path} for appending: {Marshal.GetLastWin32Error()}");
        return new SafeFileHandle(handle, ownsHandle: true);
    }

    internal static void AppendAll(SafeFileHandle handle, byte[] bytes)
    {
        var written = 0;
        while (written < bytes.Length)
        {
            if (!WriteFile(handle, ref bytes[written], (uint)(bytes.Length - written), out var n, 0))
                throw new IOException($"could not append to the log: {Marshal.GetLastWin32Error()}");
            written += (int)n;
        }
    }

    // O_WRONLY is 1 everywhere; O_APPEND is not, and the BSD value is what macOS uses.
    private const int OWrOnly = 1;
    private const int OAppendLinux = 0x0400;
    private const int OAppendBsd = 0x0008;
    private const int Eintr = 4;

    /// <summary>
    /// <c>open(path, O_WRONLY|O_APPEND)</c>. POSIX makes the seek-to-end and the write one
    /// atomic step for a file opened this way, which is the guarantee a .NET
    /// <c>FileStream</c> does not give: it writes at an offset of its own through <c>pwrite</c>,
    /// which ignores <c>O_APPEND</c> everywhere but Linux.
    /// <para>
    /// Two arguments and no <c>O_CREAT</c>, which is why the caller creates the file through
    /// ordinary .NET first: <c>open</c> is variadic, and on osx-arm64 -- a RID we ship -- the
    /// Apple ARM64 ABI passes variadic arguments on the stack while a non-variadic
    /// <c>[LibraryImport]</c> declaration passes them in registers, so a third argument
    /// declared this way is read off the stack and the file is created with whatever was
    /// there. Nothing fails loudly. A variadic function called with no variadic arguments is
    /// safe under every ABI.
    /// </para>
    /// </summary>
    internal static int OpenAppend(string path)
    {
        var flags = OWrOnly | (OperatingSystem.IsMacOS() ? OAppendBsd : OAppendLinux);
        int fd;
        do { fd = Open(path, flags); }
        while (fd < 0 && Marshal.GetLastPInvokeError() == Eintr);
        return fd;
    }

    /// <summary>
    /// <c>O_APPEND</c> makes one <c>write</c> atomic, not this loop, so a short write would in
    /// principle let another session's line land inside ours. Accepted rather than locked
    /// around: a sub-100-byte write to a regular file is short only under a signal or a
    /// resource limit, and the alternative is cross-process locking on both platforms for
    /// every line.
    /// </summary>
    internal static void AppendAll(int fd, byte[] bytes)
    {
        var written = 0;
        while (written < bytes.Length)
        {
            var n = (int)Write(fd, ref bytes[written], (nuint)(bytes.Length - written));
            if (n < 0)
            {
                if (Marshal.GetLastPInvokeError() == Eintr) continue;
                throw new IOException(
                    $"could not append to the log: errno {Marshal.GetLastPInvokeError()}");
            }

            written += n;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(
        SafeFileHandle handle, ref byte buffer, uint count, out uint written, nint overlapped);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8,
        SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint Write(int fd, ref byte buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "close")]
    internal static partial int Close(int fd);
}
