using System.Runtime.InteropServices;

namespace Cslq;

/// <summary>
/// The Win32 handle calls behind <c>LspClient.DisableStdioInheritance</c>. Both are on
/// kernel32 everywhere cslq runs on Windows; the caller still treats a failure as nothing.
/// </summary>
internal static partial class Native
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetHandleInformation(nint hObject, int dwMask, int dwFlags);
}
