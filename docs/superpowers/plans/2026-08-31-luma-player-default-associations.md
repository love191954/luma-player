# Luma Player Default Associations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Embed a stable Luma Player icon in the executable, polish the existing WinForms player in the approved dark cinema style, harden mpv lifetime handling, and ship a repeatable per-user installer without removing any current controls or playback behavior.

**Architecture:** Keep the existing native WinForms host and embedded mpv/named-pipe architecture. Put the immutable association contract in a small C# helper used by the UI and tests, use the executable's embedded icon as the registry `DefaultIcon`, and keep installation as a PowerShell script with a batch entry point that stages the complete release payload and registers HKCU associations.

**Tech Stack:** C#/.NET Framework WinForms, mpv 0.41+ with D3D11/gpu-next, Windows Registry HKCU, PowerShell, Windows shell notification APIs, Windows C# compiler (`csc.exe`). No new third-party dependency.

---

## File Map

- Create: `src/FileAssociationSpec.cs` - the shared 14-extension/ProgID/command/icon contract.
- Create: `src/luma.ico` - the multi-size Luma play-mark icon embedded into the EXE.
- Create: `tests/FileAssociationSpecTests.cs` - dependency-free console tests for extension count, quoting, and icon command values.
- Create: `tools/generate_luma_icon.py` - standard-library icon generator so the binary asset is reproducible without installing a package.
- Create: `Install-LumaPlayer.ps1` - per-user installer that copies a release payload, creates a Start Menu shortcut, registers associations, and refreshes Explorer.
- Create: `安装 Luma Player.bat` - one-click wrapper for the PowerShell installer.
- Modify: `src/LumaPlayer.cs` - use the shared association contract, invoke shell refresh, polish the existing controls/empty state, and harden UI/mpv lifetime handling.
- Modify: `build.ps1` - compile `FileAssociationSpec.cs`, embed `src/luma.ico`, and compile the test harness when requested.
- Modify: `package.ps1` - include installer files, icon metadata, and the complete payload in the release archive.
- Modify: `README.md` - document the installer, association limitation, and the preserved control set.
- Create: `verify-installation.ps1` - validate staged payloads and association source without changing the user's current associations.

The project has no Git metadata, so implementation checkpoints will be recorded in the working tree and verified with file hashes/build output instead of commits.

### Task 1: Lock the association contract with a test first

**Files:**
- Create: `tests/FileAssociationSpecTests.cs`
- Create: `src/FileAssociationSpec.cs`

- [ ] **Step 1: Write the failing test**

Create a small console test with no test framework so it can run on the machine's existing .NET Framework:

```csharp
using System;

namespace LumaPlayerTests
{
    internal static class Program
    {
        private static void Main()
        {
            Assert(LumaPlayer.FileAssociationSpec.Extensions.Length == 14, "extension count");
            Assert(LumaPlayer.FileAssociationSpec.Extensions[0] == ".mp4", "first extension");
            Assert(LumaPlayer.FileAssociationSpec.Extensions[13] == ".ogv", "last extension");
            Assert(LumaPlayer.FileAssociationSpec.BuildOpenCommand("C:\\Apps\\Luma Player\\LumaPlayer.exe") ==
                "\"C:\\Apps\\Luma Player\\LumaPlayer.exe\" \"%1\"", "open command quoting");
            Assert(LumaPlayer.FileAssociationSpec.BuildIconValue("C:\\Apps\\Luma Player\\LumaPlayer.exe") ==
                "\"C:\\Apps\\Luma Player\\LumaPlayer.exe\",0", "icon value quoting");
            Console.WriteLine("File association contract tests passed.");
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("Failed: " + name);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run from `D:\workspace-agent-bb5dab10\luma player`:

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe /out:"tests\FileAssociationSpecTests.exe" "tests\FileAssociationSpecTests.cs"
```

Expected: compilation fails because `LumaPlayer.FileAssociationSpec` does not exist.

- [ ] **Step 3: Add the minimal association contract**

Create `src/FileAssociationSpec.cs` with immutable accessors and explicit command builders:

```csharp
using System;

namespace LumaPlayer
{
    internal static class FileAssociationSpec
    {
        public const string ApplicationName = "Luma Player";
        public const string ProgId = "LumaPlayer.Video";

        public static readonly string[] Extensions = new string[]
        {
            ".mp4", ".mkv", ".m4v", ".mov", ".avi", ".webm", ".ts", ".m2ts",
            ".mts", ".mpg", ".mpeg", ".wmv", ".flv", ".ogv"
        };

        public static string BuildOpenCommand(string executable)
        {
            return Quote(executable) + " \"%1\"";
        }

        public static string BuildIconValue(string executable)
        {
            return Quote(executable) + ",0";
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run:

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe /out:"tests\FileAssociationSpecTests.exe" "src\FileAssociationSpec.cs" "tests\FileAssociationSpecTests.cs"
& "tests\FileAssociationSpecTests.exe"
```

Expected: `File association contract tests passed.`

### Task 2: Create and embed the Luma icon

**Files:**
- Create: `tools/generate_luma_icon.py`
- Create: `src/luma.ico`
- Modify: `build.ps1`

- [ ] **Step 1: Add a reproducible icon generator**

Use only Python's standard library. Render supersampled rounded graphite tiles with a centered warm-orange play mark, then write 16, 24, 32, 48, 64, 128, and 256 pixel BGRA DIB entries into one ICO file. The generator must write only `src/luma.ico` relative to the project root and must fail if the output directory is outside the project root.

- [ ] **Step 2: Generate and inspect the asset**

Run:

```powershell
python tools\generate_luma_icon.py
```

Expected: `src\luma.ico` exists and contains all seven sizes. Use the desktop image viewer or the browser companion to inspect the icon at 16px and 32px; the triangle must remain recognizable without text.

- [ ] **Step 3: Embed the icon during C# compilation**

Update the existing `csc.exe` command in `build.ps1` to include:

```powershell
/win32icon:"$(Join-Path $sourceDir 'luma.ico')" `
"$(Join-Path $sourceDir 'FileAssociationSpec.cs')" `
```

Keep `/target:winexe`, `/optimize+`, `/platform:x64`, and the existing assembly references unchanged.

- [ ] **Step 4: Build and verify the icon resource**

Run:

```powershell
.\build.ps1 -SkipMpvDownload
```

Expected: `dist\LumaPlayer.exe` is rebuilt successfully. Verify its file size changes from the previous build and that the Windows shell displays the Luma icon for the EXE.

### Task 3: Make file association registration stable and shell-refreshable

**Files:**
- Modify: `src/LumaPlayer.cs`
- Modify: `Install-LumaPlayer.ps1`
- Modify: `tests/FileAssociationSpecTests.cs`

- [ ] **Step 1: Add shell notification support**

Add `using System.Runtime.InteropServices;` and a small P/Invoke declaration in `PlayerForm`:

```csharp
[DllImport("shell32.dll")]
private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

private static void RefreshShellAssociations()
{
    SHChangeNotify(0x08000000U, 0x00002000U, IntPtr.Zero, IntPtr.Zero);
}
```

The constants are `SHCNE_ASSOCCHANGED` and `SHCNF_FLUSHNOWAIT`; no Explorer process is killed.

- [ ] **Step 2: Replace duplicate association data in the UI**

In `RegisterFileAssociations`, remove the local `applicationName`, `progId`, and `extensions` declarations. Use `FileAssociationSpec.ApplicationName`, `FileAssociationSpec.ProgId`, `FileAssociationSpec.Extensions`, `BuildOpenCommand`, and `BuildIconValue`. Keep the existing HKCU paths, supported types, capabilities, `RegisteredApplications`, and `OpenWithProgids` writes intact.

After the `OpenWithProgids` loop and before the success message, call `RefreshShellAssociations()`.

- [ ] **Step 3: Add registration contract checks to the installer validation**

Extend `tests/FileAssociationSpecTests.cs` to assert every extension begins with `.` and every extension is unique. The test must still run without touching the registry.

- [ ] **Step 4: Verify registration behavior without changing current defaults**

Run the console contract test, build the app, then inspect the source and installer validation output. The user-facing registration flow should still open `ms-settings:defaultapps?...`; it must not write `UserChoice` keys.

### Task 4: Apply the approved dark cinema visual system without removing controls

**Files:**
- Modify: `src/LumaPlayer.cs`

- [ ] **Step 1: Define shared visual tokens**

Replace the current button palette with named colors near the existing `PlayerForm` colors:

```csharp
private static readonly Color WindowColor = Color.FromArgb(9, 11, 15);
private static readonly Color PanelColor = Color.FromArgb(20, 24, 31);
private static readonly Color PanelRaisedColor = Color.FromArgb(27, 32, 41);
private static readonly Color AccentColor = Color.FromArgb(242, 108, 76);
private static readonly Color AccentHoverColor = Color.FromArgb(255, 130, 96);
private static readonly Color MutedColor = Color.FromArgb(151, 160, 173);
private static readonly Color TextColor = Color.FromArgb(242, 245, 248);
```

- [ ] **Step 2: Restyle the existing control containers**

Keep the current control names, event handlers, timeline layout, and button order. Use the new tokens for `_controls`, timeline/table/flow backgrounds, empty state, labels, and the volume slider. Keep the existing 116px control height unless the narrow-window verification demonstrates clipping; if needed, increase only to the smallest height that prevents overlap.

- [ ] **Step 3: Simplify `SkeuomorphicButton.OnPaint`**

Keep hover, pressed, disabled, primary, focus, and double-buffer behavior, but render one flat rounded face with a 1px border and a subtle top highlight. Use `AccentColor`/`AccentHoverColor` for primary state and `PanelRaisedColor`/`WindowColor` for normal state. Remove the large black shadow and vertical gradient that make the current controls look metallic. Continue using `TextRenderer.DrawText` with ellipsis and centered alignment so button text never changes layout width.

- [ ] **Step 4: Add the centered Luma empty-state mark**

Add a small custom `LumaMark` control that draws a rounded graphite tile and warm-orange play triangle with `System.Drawing`; do not add an image runtime dependency. Insert it above `_emptyTitle` while retaining click handlers on the parent panel and labels. Keep the existing HDR/Dolby Vision hint text and all open-file behavior.

- [ ] **Step 5: Preserve and verify every interaction**

Confirm these controls still exist by field and are still wired to their current handlers: `_openButton`, `_backButton`, `_playButton`, `_forwardButton`, `_muteButton`, `_volumeBar`, `_speedButton`, `_audioButton`, `_subtitleButton`, `_associateButton`, `_fullScreenButton`. Confirm `Ctrl+O`, Space, Left/Right, Up/Down, M, F, Esc, drag/drop, and double-click full screen remain unchanged.

### Task 5: Harden mpv startup, IPC, and shutdown

**Files:**
- Modify: `src/LumaPlayer.cs`
- Modify: `tests/FileAssociationSpecTests.cs` only if a pure lifecycle helper is extracted

- [ ] **Step 1: Cache the engine path for the form lifetime**

Add a nullable `_mpvPath` field. In `FindMpv`, return the cached existing path when present; otherwise probe the current three candidates once, cache the first valid path, and cache the missing result only until the executable directory changes. Do not scan any video directories.

- [ ] **Step 2: Make UI marshalling disposal-aware**

Update `SafeUi` to return when the form is disposed, disposing, or has no created handle. Wrap `BeginInvoke(action)` in a narrow `InvalidOperationException`/`ObjectDisposedException` guard so a late reader-thread event cannot crash the process during shutdown.

- [ ] **Step 3: Make engine failure a single UI transition**

In `OnMpvFailed`, set `_hasFile = false`, disable engine-dependent controls, set `_statusLabel.Text = "播放引擎已停止"`, and show the existing error once through the existing `MpvProcess.RaiseFailed` guard. Do not create a replacement mpv process automatically in the failure callback.

- [ ] **Step 4: Bound and order IPC disposal**

In `MpvProcess.Dispose`, retain the current quit command but close the writer/reader/pipe after a bounded `WaitForExit(900)`. Join `_readerThread` for at most 300ms when it is not the current thread, then kill only the known `_process` if it is still alive, and dispose all resources in `finally` blocks. Keep the method idempotent through `_disposing`.

- [ ] **Step 5: Verify abnormal engine paths**

Run the existing verification scripts with the real `dist\mpv.exe`, then run a controlled smoke test with a temporary renamed copy of `mpv.exe` in a separate staging directory. Confirm no unhandled exception, duplicate error dialog, or close hang. Restore the staging directory by deleting only the temporary test directory after verification.

### Task 6: Add the one-click per-user installer

**Files:**
- Create: `Install-LumaPlayer.ps1`
- Create: `安装 Luma Player.bat`
- Modify: `package.ps1`

- [ ] **Step 1: Implement payload validation**

`Install-LumaPlayer.ps1` should resolve `$PSScriptRoot`, require `LumaPlayer.exe`, `mpv.exe`, `mpv.com`, `d3dcompiler_43.dll`, `input.conf`, `README.md`, and `THIRD_PARTY_NOTICES.md`, and stop with a readable error before creating the destination when any required file is missing.

- [ ] **Step 2: Implement a recoverable per-user copy**

Copy the release payload into `%LocalAppData%\Programs\LumaPlayer` using `Copy-Item -Recurse -Force`, preserving the `rembg_worker` directory and `licenses` directory when present. Do not remove files outside that exact destination. Create the destination before copying and report the final EXE path.

- [ ] **Step 3: Register the same 14 extensions in HKCU**

Use the same ProgID and extension list as `FileAssociationSpec`. Write the ProgID default description, `DefaultIcon` as `"<install>\LumaPlayer.exe",0`, `shell\open\command` as `"<install>\LumaPlayer.exe" "%1"`, capabilities, `RegisteredApplications`, and `OpenWithProgids`. Do not touch `HKLM` or `UserChoice`.

- [ ] **Step 4: Create a Start Menu shortcut and refresh the shell**

Create `%AppData%\Microsoft\Windows\Start Menu\Programs\Luma Player.lnk` through `WScript.Shell`, with target `$installExe`, working directory `$installRoot`, and icon location `$installExe,0`. Call the same `SHChangeNotify` association refresh through a tiny in-process `Add-Type` declaration; do not restart Explorer.

- [ ] **Step 5: Add the one-click wrapper**

The batch wrapper must invoke PowerShell without loading the user's profile and preserve paths containing spaces:

```bat
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-LumaPlayer.ps1"
if errorlevel 1 pause
endlocal
```

- [ ] **Step 6: Include installer files in releases**

Update `package.ps1` so the staged release contains the PowerShell installer, batch wrapper, and icon/source metadata while the portable root remains runnable. If `iexpress.exe` is present, generate `LumaPlayer-Setup.exe` from the staged payload; if not, write a manifest flag indicating the batch wrapper is the supported one-click entry point. Do not make the release depend on IExpress.

### Task 7: Add packaging verification and update documentation

**Files:**
- Create: `verify-installation.ps1`
- Modify: `README.md`
- Modify: `package.ps1`

- [ ] **Step 1: Validate staged artifacts without changing user associations**

`verify-installation.ps1` accepts a release directory, checks required files, checks the installer scripts, parses the package manifest, checks the 14 extension strings, and verifies that the open command and icon value contain the same EXE path. It must not call the installer and must not write registry keys.

- [ ] **Step 2: Document the two installation paths**

Update README usage to explain: portable ZIP extraction, double-clicking `安装 Luma Player.bat`, the Windows default-app confirmation step, the fact that the new icon is an executable icon rather than a video-frame thumbnail, and the unchanged control/shortcut set.

- [ ] **Step 3: Run the full verification sequence**

Run from `D:\workspace-agent-bb5dab10\luma player`:

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe /out:"tests\FileAssociationSpecTests.exe" "src\FileAssociationSpec.cs" "tests\FileAssociationSpecTests.cs"
& "tests\FileAssociationSpecTests.exe"
.\build.ps1 -SkipMpvDownload
.\package.ps1 -Version "0.4.0"
.\verify-installation.ps1 ".\release\LumaPlayer-0.4.0-win-x64"
.\verify-startup-ui.ps1 ".\release\LumaPlayer-0.4.0-win-x64"
```

Expected: the association contract test passes, the build succeeds, the ZIP and manifest are produced, installation validation passes, and the existing startup UI verification reports success. Run `diagnose-hdr.ps1`/`verify-player.ps1` with the existing sample when available; confirm the HDR/Dolby Vision arguments remain unchanged.

- [ ] **Step 4: Review the final diff and residual risks**

Review only the `luma player` tree, confirm no existing control field or handler was removed, confirm generated release output is not mistaken for source changes, and document that true Explorer frame thumbnails and physical TV Dolby Vision indicator behavior remain outside scope.

