# RunKeyReplay 1.0.1

`RunKeyReplay` is a .NET 8 x64 Windows console application that replays enabled values in these two native 64-bit registry keys:

- `HKLM\Software\Microsoft\Windows\CurrentVersion\Run`
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

> Warning: a normal invocation starts the enabled programs configured in the selected Run keys. Use `--dry-run` first, especially on a system with untrusted or unknown startup entries. Running it after logon can start duplicate copies of normal startup programs. `--include-disabled` also starts items that Task Manager has disabled.

## Usage

```text
RunKeyReplay [--quiet] [--dry-run] [--include-disabled] [--hkcu-only | --hklm-only]
```

Options:

- `--quiet` suppresses normal status messages. Failures and their Win32 codes are still written to standard error.
- `--dry-run` reads, validates, expands, and displays the commands without launching them (unless `--quiet` is also supplied).
- `--include-disabled` also replays Run values that Windows Task Manager's **Startup apps** page marks as disabled.
- `--hkcu-only` replays only the current-user key.
- `--hklm-only` replays only the local-machine key.

The scope switches are mutually exclusive. The default is HKLM first and then HKCU. By default, the tool consults the matching `Explorer\StartupApproved\Run` key and skips values that Task Manager has disabled. A missing `StartupApproved` key or value means the Run value is enabled, which matches Windows' normal default. Windows does not guarantee the order in which multiple Run values are started, so the project keeps the registry's native enumeration order rather than sorting entries.

## Native behavior

- Calls explicit Unicode APIs: `RegOpenKeyExW`, `RegQueryInfoKeyW`, `RegEnumValueW`, `RegQueryValueExW`, `ExpandEnvironmentStringsW`, `CreateProcessW`, and, for unmistakable shell objects, `ShellExecuteExW`.
- Reads the native 64-bit registry view explicitly with `KEY_WOW64_64KEY`.
- Honors Task Manager's `StartupApproved\Run` state by default. `--include-disabled` bypasses that state check and reproduces the previous all-values behavior. A malformed or unreadable approval state is skipped in the default mode rather than being launched.
- Accepts `REG_SZ` and `REG_EXPAND_SZ`. `REG_EXPAND_SZ` is expanded with `ExpandEnvironmentStringsW`; `REG_SZ` is deliberately passed literally because it is not an expandable registry type.
- Keeps an executable Run value as its raw native command line, using `CreateProcessW` with `lpApplicationName = NULL` and a writable UTF-16 command-line buffer. It does not parse/requote an executable command line or use `ProcessStartInfo`.
- Uses `ShellExecuteExW` only for clearly shell-owned targets (a URI/protocol or a `.lnk`/`.url` shortcut), where there is no executable command line for `CreateProcessW` to resolve. It does not use `ShellExecuteExW` as a generic failure fallback or for arbitrary file associations.
- Does not use `cmd.exe` or PowerShell to interpret entries. A direct `.cmd`/`.bat`/PowerShell-script value therefore remains a direct native launch and fails unless the stored Run value itself explicitly invokes an interpreter; changing that stored command would no longer be a replay.
- Does not wait for child processes. It closes the returned handles immediately and continues after every failure.
- Starts console children in a new console, avoiding accidental inheritance of this tool's console. It uses `%SystemRoot%\System32` as an Explorer-like working-directory approximation.

Every registry, expansion, or launch failure is reported with a decimal and hexadecimal Win32 error code. The process exits `0` if all selected work completed, `1` after one or more operational failures, and `2` for invalid arguments.

## Build and publish

The project is configured for x64, `win-x64`, self-contained, single-file publishing. Version 1.0.1 build output goes under `bin\x64\Release\v1.0.1\`, and publishing goes to `releases\RunKeyReplay-v1.0.1\`; this keeps it separate from an existing 1.0.0 output. With the .NET 8 SDK installed:

```powershell
dotnet build -c Release -p:Platform=x64
dotnet publish -c Release
```

The publish output is a self-contained Windows x64 single-file executable. Native runtime pieces may be extracted to the standard .NET single-file extraction cache at runtime.

The project deliberately does not cover RunOnce, Startup folders, policies, scheduled tasks, services, Safe Mode filtering, alternate users/sessions, elevation, or logon-time startup delays. To best approximate an ordinary interactive logon, run it unelevated in the target user's interactive session.

Microsoft documents Run values as command lines, with indeterminate order and possible delayed execution: [Run and RunOnce Registry Keys](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys). The raw command-line behavior follows [CreateProcessW](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw), and the shell path follows [ShellExecuteExW](https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shellexecuteexw).
