using System.Runtime.InteropServices;
using System.Text;

namespace RunKeyReplay;

internal static class NativeMethods
{
    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_NO_MORE_ITEMS = 259;

    internal const uint KEY_QUERY_VALUE = 0x0001;
    internal const uint KEY_WOW64_64KEY = 0x0100;

    internal const uint REG_SZ = 1;
    internal const uint REG_EXPAND_SZ = 2;
    internal const uint REG_BINARY = 3;

    // A console process launched by Explorer/userinit would not inherit this tool's console.
    internal const uint CREATE_NEW_CONSOLE = 0x00000010;

    internal const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    internal const uint SEE_MASK_NOASYNC = 0x00000100;
    internal const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    internal const int SW_SHOWNORMAL = 1;

    internal const uint COINIT_APARTMENTTHREADED = 0x2;
    internal const uint COINIT_DISABLE_OLE1DDE = 0x4;

    [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int RegOpenKeyExW(
        IntPtr hKey,
        string lpSubKey,
        uint ulOptions,
        uint samDesired,
        out IntPtr phkResult);

    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int RegQueryInfoKeyW(
        IntPtr hKey,
        StringBuilder? lpClass,
        IntPtr lpcchClass,
        IntPtr lpReserved,
        IntPtr lpcSubKeys,
        IntPtr lpcMaxSubKeyLen,
        IntPtr lpcMaxClassLen,
        out uint lpcValues,
        out uint lpcMaxValueNameLen,
        out uint lpcMaxValueLen,
        IntPtr lpcbSecurityDescriptor,
        IntPtr lpftLastWriteTime);

    [DllImport("advapi32.dll", EntryPoint = "RegEnumValueW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int RegEnumValueW(
        IntPtr hKey,
        uint dwIndex,
        StringBuilder lpValueName,
        ref uint lpcchValueName,
        IntPtr lpReserved,
        out uint lpType,
        [Out] byte[] lpData,
        ref uint lpcbData);

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int RegQueryValueExW(
        IntPtr hKey,
        string? lpValueName,
        IntPtr lpReserved,
        out uint lpType,
        [Out] byte[]? lpData,
        ref uint lpcbData);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    internal static extern int RegCloseKey(IntPtr hKey);

    [DllImport("kernel32.dll", EntryPoint = "ExpandEnvironmentStringsW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern uint ExpandEnvironmentStringsW(
        string lpSrc,
        StringBuilder? lpDst,
        uint nSize);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW pExecInfo);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoUninitialize();
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STARTUPINFOW
{
    internal uint cb;
    internal string? lpReserved;
    internal string? lpDesktop;
    internal string? lpTitle;
    internal uint dwX;
    internal uint dwY;
    internal uint dwXSize;
    internal uint dwYSize;
    internal uint dwXCountChars;
    internal uint dwYCountChars;
    internal uint dwFillAttribute;
    internal uint dwFlags;
    internal ushort wShowWindow;
    internal ushort cbReserved2;
    internal IntPtr lpReserved2;
    internal IntPtr hStdInput;
    internal IntPtr hStdOutput;
    internal IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    internal IntPtr hProcess;
    internal IntPtr hThread;
    internal uint dwProcessId;
    internal uint dwThreadId;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHELLEXECUTEINFOW
{
    internal uint cbSize;
    internal uint fMask;
    internal IntPtr hwnd;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? lpVerb;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? lpFile;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? lpParameters;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? lpDirectory;
    internal int nShow;
    internal IntPtr hInstApp;
    internal IntPtr lpIDList;
    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? lpClass;
    internal IntPtr hkeyClass;
    internal uint dwHotKey;
    internal IntPtr hIconOrMonitor;
    internal IntPtr hProcess;
}
