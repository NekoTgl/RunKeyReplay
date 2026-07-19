using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RunKeyReplay;

internal static class Program
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const int ErrorInvalidData = 13;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInvalidDatatype = 1804;
    private const int ErrorFilenameExceedsRange = 206;
    private const int MaxCreateProcessCommandLength = 32_766; // Excludes the terminating NUL.
    private const int MaxRegistryBufferBytes = 1_048_576;

    // Predefined HKEY values are sign-extended LONG constants on 64-bit Windows.
    private static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));
    private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));

    // Explorer normally runs with %SystemRoot%\System32 as its working directory.  Supplying
    // it avoids giving console children this replay tool's arbitrary current directory.
    private static readonly string? LogonLikeWorkingDirectory = string.IsNullOrWhiteSpace(Environment.SystemDirectory)
        ? null
        : Environment.SystemDirectory;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("RunKeyReplay can run only on Windows.");
            return 1;
        }

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.Error.WriteLine("RunKeyReplay must run as an x64 process.");
            return 1;
        }

        if (!TryParseOptions(args, out var options, out var parseError))
        {
            Console.Error.WriteLine(parseError);
            PrintUsage();
            return 2;
        }

        if (options.Help)
        {
            PrintUsage();
            return 0;
        }

        // ShellExecuteExW may activate COM-based shell handlers.  CreateProcessW does not
        // require COM, and a failure to initialize COM does not prevent direct launches.
        var comResult = NativeMethods.CoInitializeEx(
            IntPtr.Zero,
            NativeMethods.COINIT_APARTMENTTHREADED | NativeMethods.COINIT_DISABLE_OLE1DDE);
        var shouldUninitializeCom = comResult >= 0;

        try
        {
            var summary = new ReplaySummary();
            var locations = GetLocations(options);
            var values = new List<RunValue>();

            // Snapshot the requested keys before starting any entry.  A launched program can
            // otherwise modify a Run key and change which later values this tool observes.
            foreach (var location in locations)
            {
                var locationValues = ReadValues(location, options, summary);
                if (!options.IncludeDisabled)
                {
                    ApplyStartupApprovalStates(location, locationValues, summary);
                }

                values.AddRange(locationValues);
            }

            foreach (var value in values)
            {
                ReplayValue(value, options, summary);
            }

            if (!options.Quiet)
            {
                Console.WriteLine(
                    $"Completed: {summary.Enumerated} value(s) read, {summary.Attempted} valid command(s), " +
                    $"{summary.Started} started, {summary.Planned} planned, {summary.Skipped} skipped, " +
                    $"{summary.Failures} failure(s).");
            }

            return summary.Failures == 0 ? 0 : 1;
        }
        finally
        {
            if (shouldUninitializeCom)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }

    private static IReadOnlyList<RegistryLocation> GetLocations(Options options)
    {
        var locations = new List<RegistryLocation>(2);

        if (!options.HkcuOnly)
        {
            locations.Add(new RegistryLocation("HKLM", HkeyLocalMachine, RunKeyPath));
        }

        if (!options.HklmOnly)
        {
            locations.Add(new RegistryLocation("HKCU", HkeyCurrentUser, RunKeyPath));
        }

        return locations;
    }

    private static List<RunValue> ReadValues(
        RegistryLocation location,
        Options options,
        ReplaySummary summary)
    {
        var values = new List<RunValue>();
        var access = NativeMethods.KEY_QUERY_VALUE | NativeMethods.KEY_WOW64_64KEY;
        var status = NativeMethods.RegOpenKeyExW(location.Root, location.Path, 0, access, out var key);

        if (status == NativeMethods.ERROR_FILE_NOT_FOUND)
        {
            WriteInfo(options, $"[{location.Hive}] {location.Path}: key is not present.");
            return values;
        }

        if (status != NativeMethods.ERROR_SUCCESS)
        {
            WriteFailure(location, null, "RegOpenKeyExW", status);
            summary.Failures++;
            return values;
        }

        try
        {
            status = NativeMethods.RegQueryInfoKeyW(
                key,
                null,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                out var valueCount,
                out var maximumValueNameLength,
                out var maximumValueDataLength,
                IntPtr.Zero,
                IntPtr.Zero);

            if (status != NativeMethods.ERROR_SUCCESS)
            {
                WriteFailure(location, null, "RegQueryInfoKeyW", status);
                summary.Failures++;
                return values;
            }

            var initialNameCapacity = BufferCapacity(maximumValueNameLength, includeNullTerminator: true);
            var initialDataCapacity = BufferCapacity(maximumValueDataLength, includeNullTerminator: false);

            for (uint index = 0; index < valueCount; index++)
            {
                status = TryReadValue(key, index, initialNameCapacity, initialDataCapacity, out var valueName, out var valueType, out var data);
                if (status == NativeMethods.ERROR_NO_MORE_ITEMS)
                {
                    break;
                }

                if (status != NativeMethods.ERROR_SUCCESS)
                {
                    WriteFailure(location, null, $"RegEnumValueW(index {index})", status);
                    summary.Failures++;
                    continue;
                }

                values.Add(new RunValue(location, valueName, valueType, data));
                summary.Enumerated++;
            }
        }
        finally
        {
            NativeMethods.RegCloseKey(key);
        }

        return values;
    }

    private static void ApplyStartupApprovalStates(
        RegistryLocation location,
        List<RunValue> values,
        ReplaySummary summary)
    {
        if (values.Count == 0)
        {
            return;
        }

        var approvalLocation = location with { Path = StartupApprovedRunKeyPath };
        var access = NativeMethods.KEY_QUERY_VALUE | NativeMethods.KEY_WOW64_64KEY;
        var status = NativeMethods.RegOpenKeyExW(
            approvalLocation.Root,
            approvalLocation.Path,
            0,
            access,
            out var approvalKey);

        // Windows creates StartupApproved values only after a user or the system records a
        // choice.  A missing key therefore means every Run value keeps its normal enabled state.
        if (status == NativeMethods.ERROR_FILE_NOT_FOUND)
        {
            return;
        }

        if (status != NativeMethods.ERROR_SUCCESS)
        {
            WriteFailure(approvalLocation, null, "RegOpenKeyExW", status);
            summary.Failures++;
            SetApprovalState(values, StartupApprovalState.Undetermined);
            return;
        }

        try
        {
            for (var index = 0; index < values.Count; index++)
            {
                var value = values[index];
                status = TryReadStartupApprovalState(approvalKey, value.ValueName, out var approvalState);
                if (status != NativeMethods.ERROR_SUCCESS)
                {
                    WriteFailure(approvalLocation, value.ValueName, "Read StartupApproved state", status);
                    summary.Failures++;
                    values[index] = value with { ApprovalState = StartupApprovalState.Undetermined };
                    continue;
                }

                values[index] = value with { ApprovalState = approvalState };
            }
        }
        finally
        {
            NativeMethods.RegCloseKey(approvalKey);
        }
    }

    private static int TryReadStartupApprovalState(
        IntPtr approvalKey,
        string valueName,
        out StartupApprovalState approvalState)
    {
        approvalState = StartupApprovalState.Undetermined;
        uint dataLength = 0;
        var status = NativeMethods.RegQueryValueExW(
            approvalKey,
            valueName,
            IntPtr.Zero,
            out var valueType,
            null,
            ref dataLength);

        // An absent value has never been disabled through Startup Apps and is enabled by default.
        if (status == NativeMethods.ERROR_FILE_NOT_FOUND)
        {
            approvalState = StartupApprovalState.Enabled;
            return NativeMethods.ERROR_SUCCESS;
        }

        if (status != NativeMethods.ERROR_SUCCESS)
        {
            return status;
        }

        if (valueType != NativeMethods.REG_BINARY)
        {
            return ErrorInvalidDatatype;
        }

        if (dataLength == 0)
        {
            return ErrorInvalidData;
        }

        var capacity = BufferCapacity(dataLength, includeNullTerminator: false);
        if (capacity < dataLength)
        {
            return NativeMethods.ERROR_MORE_DATA;
        }

        // The value can change while it is being queried, so apply the same bounded retry
        // strategy used for Run-value enumeration.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var data = new byte[capacity];
            uint copiedLength = checked((uint)data.Length);
            status = NativeMethods.RegQueryValueExW(
                approvalKey,
                valueName,
                IntPtr.Zero,
                out valueType,
                data,
                ref copiedLength);

            if (status == NativeMethods.ERROR_SUCCESS)
            {
                if (valueType != NativeMethods.REG_BINARY)
                {
                    return ErrorInvalidDatatype;
                }

                if (copiedLength == 0 || copiedLength > (uint)data.Length)
                {
                    return ErrorInvalidData;
                }

                // StartupApproved's binary payload is intentionally opaque, but Windows uses
                // the low bit of its first byte as the state: even is enabled and odd is
                // disabled.  This covers the observed 02/03, 06/07, and later state pairs.
                approvalState = (data[0] & 1) == 0
                    ? StartupApprovalState.Enabled
                    : StartupApprovalState.Disabled;
                return NativeMethods.ERROR_SUCCESS;
            }

            if (status != NativeMethods.ERROR_MORE_DATA ||
                !TryGrowBuffer(capacity, copiedLength, includeNullTerminator: false, out capacity))
            {
                return status;
            }
        }

        return NativeMethods.ERROR_MORE_DATA;
    }

    private static void SetApprovalState(List<RunValue> values, StartupApprovalState approvalState)
    {
        for (var index = 0; index < values.Count; index++)
        {
            values[index] = values[index] with { ApprovalState = approvalState };
        }
    }

    private static int TryReadValue(
        IntPtr key,
        uint index,
        int initialNameCapacity,
        int initialDataCapacity,
        out string valueName,
        out uint valueType,
        out byte[] data)
    {
        valueName = string.Empty;
        valueType = 0;
        data = Array.Empty<byte>();

        var nameCapacity = initialNameCapacity;
        var dataCapacity = initialDataCapacity;

        // The key can change after RegQueryInfoKeyW.  Retry with the returned size so a
        // concurrent value growth does not terminate replay of the rest of the key.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var nameBuffer = new StringBuilder(nameCapacity);
            var dataBuffer = new byte[dataCapacity];
            uint nameLength = checked((uint)nameBuffer.Capacity);
            uint dataLength = checked((uint)dataBuffer.Length);

            var status = NativeMethods.RegEnumValueW(
                key,
                index,
                nameBuffer,
                ref nameLength,
                IntPtr.Zero,
                out valueType,
                dataBuffer,
                ref dataLength);

            if (status == NativeMethods.ERROR_SUCCESS)
            {
                if (dataLength > (uint)dataBuffer.Length)
                {
                    return NativeMethods.ERROR_MORE_DATA;
                }

                if (dataLength != (uint)dataBuffer.Length)
                {
                    Array.Resize(ref dataBuffer, checked((int)dataLength));
                }

                valueName = nameBuffer.ToString();
                data = dataBuffer;
                return NativeMethods.ERROR_SUCCESS;
            }

            if (status != NativeMethods.ERROR_MORE_DATA)
            {
                return status;
            }

            if (!TryGrowBuffer(nameCapacity, nameLength, includeNullTerminator: true, out nameCapacity) ||
                !TryGrowBuffer(dataCapacity, dataLength, includeNullTerminator: false, out dataCapacity))
            {
                return NativeMethods.ERROR_MORE_DATA;
            }
        }

        return NativeMethods.ERROR_MORE_DATA;
    }

    private static void ReplayValue(RunValue value, Options options, ReplaySummary summary)
    {
        if (!options.IncludeDisabled && value.ApprovalState != StartupApprovalState.Enabled)
        {
            var reason = value.ApprovalState == StartupApprovalState.Disabled
                ? "Task Manager marks this startup item as disabled"
                : "its StartupApproved state could not be determined";
            WriteInfo(
                options,
                $"[{value.Location.Hive}] {FormatValueName(value.ValueName)}: skipped because {reason}.");
            summary.Skipped++;
            return;
        }

        if (!TryPrepareCommand(value, out var commandLine, out var errorCode, out var errorOperation))
        {
            WriteFailure(value.Location, value.ValueName, errorOperation, errorCode);
            summary.Skipped++;
            summary.Failures++;
            return;
        }

        summary.Attempted++;
        var useShellExecute = TryCreateShellInvocation(commandLine, out var shellInvocation);
        var method = useShellExecute ? "ShellExecuteExW" : "CreateProcessW";

        if (options.DryRun)
        {
            WriteInfo(
                options,
                $"[{value.Location.Hive}] {FormatValueName(value.ValueName)}: would use {method}: {EscapeForDisplay(commandLine)}");
            summary.Planned++;
            return;
        }

        if (useShellExecute)
        {
            if (!TryShellExecute(shellInvocation, out errorCode))
            {
                WriteFailure(value.Location, value.ValueName, method, errorCode);
                summary.Failures++;
                return;
            }
        }
        else if (!TryCreateProcess(commandLine, out var processId, out errorCode))
        {
            WriteFailure(value.Location, value.ValueName, method, errorCode);
            summary.Failures++;
            return;
        }
        else
        {
            WriteInfo(options, $"[{value.Location.Hive}] {FormatValueName(value.ValueName)}: {method} started PID {processId}.");
            summary.Started++;
            return;
        }

        WriteInfo(options, $"[{value.Location.Hive}] {FormatValueName(value.ValueName)}: {method} started.");
        summary.Started++;
    }

    private static bool TryPrepareCommand(
        RunValue value,
        out string commandLine,
        out int errorCode,
        out string operation)
    {
        commandLine = string.Empty;
        errorCode = ErrorInvalidData;
        operation = "Read command line";

        if (value.ValueType is not (NativeMethods.REG_SZ or NativeMethods.REG_EXPAND_SZ))
        {
            errorCode = ErrorInvalidDatatype;
            operation = $"Unsupported registry type {value.ValueType}";
            return false;
        }

        if ((value.Data.Length & 1) != 0)
        {
            operation = "Malformed UTF-16 registry data";
            return false;
        }

        var raw = Encoding.Unicode.GetString(value.Data);
        var terminator = raw.IndexOf('\0');
        if (terminator >= 0)
        {
            // A normal REG_SZ/REG_EXPAND_SZ has exactly one trailing NUL.  Additional trailing
            // NULs are harmless, but embedded text after a NUL is malformed and is not launched.
            for (var index = terminator; index < raw.Length; index++)
            {
                if (raw[index] != '\0')
                {
                    operation = "Malformed embedded-NUL registry data";
                    return false;
                }
            }

            raw = raw[..terminator];
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            errorCode = ErrorInvalidParameter;
            operation = "Empty command line";
            return false;
        }

        if (value.ValueType == NativeMethods.REG_EXPAND_SZ)
        {
            if (!TryExpandEnvironmentStrings(raw, out commandLine, out errorCode))
            {
                operation = "ExpandEnvironmentStringsW";
                return false;
            }
        }
        else
        {
            // REG_SZ is intentionally passed literally.  Only REG_EXPAND_SZ designates an
            // expandable registry string, which preserves normal Windows registry semantics.
            commandLine = raw;
        }

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            errorCode = ErrorInvalidParameter;
            operation = "Empty expanded command line";
            return false;
        }

        if (commandLine.Length > MaxCreateProcessCommandLength)
        {
            errorCode = ErrorFilenameExceedsRange;
            operation = "Command line exceeds CreateProcessW limit";
            return false;
        }

        return true;
    }

    private static bool TryExpandEnvironmentStrings(string source, out string expanded, out int errorCode)
    {
        expanded = string.Empty;
        errorCode = NativeMethods.ERROR_SUCCESS;

        var required = NativeMethods.ExpandEnvironmentStringsW(source, null, 0);
        if (required == 0)
        {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        // The return value includes the terminating NUL.  Repeat only if the environment
        // changes between the sizing and copying calls.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new StringBuilder(checked((int)required));
            var written = NativeMethods.ExpandEnvironmentStringsW(source, buffer, checked((uint)buffer.Capacity));
            if (written == 0)
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }

            if (written <= (uint)buffer.Capacity)
            {
                expanded = buffer.ToString();
                return true;
            }

            required = written;
        }

        errorCode = NativeMethods.ERROR_MORE_DATA;
        return false;
    }

    private static bool TryCreateProcess(string commandLine, out uint processId, out int errorCode)
    {
        processId = 0;
        errorCode = NativeMethods.ERROR_SUCCESS;
        var startupInfo = new STARTUPINFOW
        {
            cb = checked((uint)Marshal.SizeOf<STARTUPINFOW>()),
        };

        // CreateProcessW may modify this buffer.  Passing lpApplicationName = null is deliberate:
        // Run values are command lines, and Windows performs the normal native first-token lookup.
        var mutableCommandLine = new StringBuilder(commandLine, commandLine.Length + 1);
        if (!NativeMethods.CreateProcessW(
                null,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.CREATE_NEW_CONSOLE,
                IntPtr.Zero,
                LogonLikeWorkingDirectory,
                ref startupInfo,
                out var processInformation))
        {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            processId = processInformation.dwProcessId;
            return true;
        }
        finally
        {
            CloseHandleSilently(processInformation.hThread);
            CloseHandleSilently(processInformation.hProcess);
        }
    }

    private static bool TryShellExecute(ShellInvocation invocation, out int errorCode)
    {
        errorCode = NativeMethods.ERROR_SUCCESS;
        var executeInfo = new SHELLEXECUTEINFOW
        {
            cbSize = checked((uint)Marshal.SizeOf<SHELLEXECUTEINFOW>()),
            fMask = NativeMethods.SEE_MASK_NOCLOSEPROCESS |
                    NativeMethods.SEE_MASK_NOASYNC |
                    NativeMethods.SEE_MASK_FLAG_NO_UI,
            lpFile = invocation.File,
            lpParameters = invocation.Parameters,
            lpDirectory = LogonLikeWorkingDirectory,
            nShow = NativeMethods.SW_SHOWNORMAL,
        };

        if (!NativeMethods.ShellExecuteExW(ref executeInfo))
        {
            errorCode = Marshal.GetLastWin32Error();
            // ShellExecuteExW normally supplies a Win32 last-error code.  A few legacy shell
            // paths instead leave it unset and return an SE_ERR_* value through hInstApp.
            if (errorCode == NativeMethods.ERROR_SUCCESS &&
                executeInfo.hInstApp.ToInt64() is > 0 and <= 32)
            {
                errorCode = checked((int)executeInfo.hInstApp.ToInt64());
            }

            return false;
        }

        CloseHandleSilently(executeInfo.hProcess);
        return true;
    }

    private static bool TryCreateShellInvocation(string commandLine, out ShellInvocation invocation)
    {
        invocation = default;
        if (!TrySplitFirstToken(commandLine, out var target, out var parameters) || !IsShellTarget(target))
        {
            return false;
        }

        invocation = new ShellInvocation(target, parameters);
        return true;
    }

    private static bool TrySplitFirstToken(string commandLine, out string target, out string? parameters)
    {
        target = string.Empty;
        parameters = null;

        var index = 0;
        while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
        {
            index++;
        }

        if (index == commandLine.Length)
        {
            return false;
        }

        if (commandLine[index] == '"')
        {
            var start = ++index;
            for (; index < commandLine.Length; index++)
            {
                if (commandLine[index] == '"' && HasEvenNumberOfPrecedingBackslashes(commandLine, index))
                {
                    target = commandLine[start..index];
                    index++;
                    goto ReadParameters;
                }
            }

            return false;
        }

        var tokenStart = index;
        while (index < commandLine.Length && !char.IsWhiteSpace(commandLine[index]))
        {
            index++;
        }

        target = commandLine[tokenStart..index];

    ReadParameters:
        while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
        {
            index++;
        }

        if (index < commandLine.Length)
        {
            parameters = commandLine[index..];
        }

        return target.Length > 0;
    }

    private static bool HasEvenNumberOfPrecedingBackslashes(string value, int quoteIndex)
    {
        var count = 0;
        for (var index = quoteIndex - 1; index >= 0 && value[index] == '\\'; index--)
        {
            count++;
        }

        return (count & 1) == 0;
    }

    private static bool IsShellTarget(string target)
    {
        // A URI has no executable command line for CreateProcessW to resolve.  The shell is the
        // appropriate launcher for it.  Fully qualified drive paths are excluded from this URI test.
        if (!Path.IsPathFullyQualified(target) && !LooksLikeDriveQualifiedPath(target) &&
            Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            return true;
        }

        // Do not turn every arbitrary extension into a ShellExecuteExW launch.  That changes
        // CreateProcessW semantics and can activate an unexpected file association.  Shortcuts
        // and Internet shortcuts are unambiguously shell-owned objects; all other strings remain
        // raw CreateProcessW command lines.
        var extension = Path.GetExtension(target);
        return string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDriveQualifiedPath(string target) =>
        target.Length >= 2 &&
        ((target[0] >= 'A' && target[0] <= 'Z') || (target[0] >= 'a' && target[0] <= 'z')) &&
        target[1] == ':';

    private static int BufferCapacity(uint requested, bool includeNullTerminator)
    {
        var capacity = (long)requested + (includeNullTerminator ? 1 : 0);
        capacity = Math.Max(capacity, 1);
        return checked((int)Math.Min(capacity, MaxRegistryBufferBytes));
    }

    private static bool TryGrowBuffer(int currentCapacity, uint reportedSize, bool includeNullTerminator, out int nextCapacity)
    {
        var reportedCapacity = (long)reportedSize + (includeNullTerminator ? 1 : 0);
        var doubledCapacity = (long)currentCapacity * 2;
        var requestedCapacity = Math.Max(reportedCapacity, doubledCapacity);

        if (requestedCapacity > MaxRegistryBufferBytes)
        {
            nextCapacity = currentCapacity;
            return false;
        }

        nextCapacity = checked((int)Math.Max(requestedCapacity, 1));
        return true;
    }

    private static void CloseHandleSilently(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static bool TryParseOptions(string[] args, out Options options, out string error)
    {
        var quiet = false;
        var dryRun = false;
        var includeDisabled = false;
        var hkcuOnly = false;
        var hklmOnly = false;
        var help = false;

        foreach (var argument in args)
        {
            switch (argument)
            {
                case "--quiet":
                    quiet = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--include-disabled":
                    includeDisabled = true;
                    break;
                case "--hkcu-only":
                    hkcuOnly = true;
                    break;
                case "--hklm-only":
                    hklmOnly = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    help = true;
                    break;
                default:
                    options = default;
                    error = $"Unknown argument: {argument}";
                    return false;
            }
        }

        if (hkcuOnly && hklmOnly)
        {
            options = default;
            error = "--hkcu-only and --hklm-only cannot be used together.";
            return false;
        }

        options = new Options(quiet, dryRun, includeDisabled, hkcuOnly, hklmOnly, help);
        error = string.Empty;
        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: RunKeyReplay [--quiet] [--dry-run] [--include-disabled] [--hkcu-only | --hklm-only]");
        Console.WriteLine();
        Console.WriteLine("Replays enabled x64 HKLM/HKCU CurrentVersion\\Run values in the current user context.");
        Console.WriteLine("  --quiet      Suppress normal status output (errors are still written).");
        Console.WriteLine("  --dry-run    Read, validate, expand, and show planned launches without launching.");
        Console.WriteLine("  --include-disabled  Also replay items disabled in Task Manager Startup Apps.");
        Console.WriteLine("  --hkcu-only  Replay only HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run.");
        Console.WriteLine("  --hklm-only  Replay only HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Run.");
    }

    private static void WriteInfo(Options options, string message)
    {
        if (!options.Quiet)
        {
            Console.WriteLine(message);
        }
    }

    private static void WriteFailure(RegistryLocation location, string? valueName, string operation, int errorCode)
    {
        var valueSegment = valueName is null ? string.Empty : $" {FormatValueName(valueName)}";
        var message = new Win32Exception(errorCode).Message;
        Console.Error.WriteLine(
            $"[{location.Hive}] {location.Path}{valueSegment}: {operation} failed. " +
            $"Win32 error {errorCode} (0x{errorCode:X8}): {message}");
    }

    private static string FormatValueName(string valueName) =>
        valueName.Length == 0 ? "(Default)" : $"\"{EscapeForDisplay(valueName)}\"";

    private static string EscapeForDisplay(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            escaped.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(character) => $"\\u{(int)character:X4}",
                _ => character.ToString(),
            });
        }

        return escaped.ToString();
    }

    private readonly record struct Options(
        bool Quiet,
        bool DryRun,
        bool IncludeDisabled,
        bool HkcuOnly,
        bool HklmOnly,
        bool Help);

    private readonly record struct RegistryLocation(string Hive, IntPtr Root, string Path);

    private readonly record struct RunValue(
        RegistryLocation Location,
        string ValueName,
        uint ValueType,
        byte[] Data,
        StartupApprovalState ApprovalState = StartupApprovalState.Enabled);

    private readonly record struct ShellInvocation(string File, string? Parameters);

    private enum StartupApprovalState
    {
        Enabled,
        Disabled,
        Undetermined,
    }

    private sealed class ReplaySummary
    {
        internal int Enumerated { get; set; }
        internal int Attempted { get; set; }
        internal int Started { get; set; }
        internal int Planned { get; set; }
        internal int Skipped { get; set; }
        internal int Failures { get; set; }
    }
}
