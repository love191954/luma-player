# Luma Player: File Associations, Visual Polish, and Installer

Date: 2026-08-31
Status: Approved design

## Goal

Improve Luma Player for daily Windows use without removing any existing controls or playback behavior.

The accepted visual direction is a dark cinema interface inspired by Apple's restraint: graphite surfaces, warm orange as the single primary action color, quiet borders, consistent spacing, and no heavy metallic skeuomorphism.

## Scope

In scope:

- Give associated video files a stable Luma Player icon instead of a blank or generic window icon.
- Keep all current 14 video extensions associated by the existing registration flow.
- Preserve all current buttons, keyboard shortcuts, drag-and-drop, menus, and full-screen behavior.
- Improve launch and shutdown behavior around the embedded mpv process and named pipe.
- Provide a repeatable, per-user one-click installation path and retain the portable ZIP path.
- Verify the new behavior on Windows 10/11 and with the existing HDR/Dolby Vision playback path.

Out of scope:

- A COM Shell thumbnail provider that decodes a frame for Explorer previews.
- Changing the mpv/libplacebo color pipeline or making claims about TV Dolby Vision metadata passthrough.
- Adding a resident background service.
- Removing or replacing existing playback commands.

## Accepted Decisions

### File icon strategy

Create a multi-size `luma.ico` and embed it into `LumaPlayer.exe` during the existing C# build. The registry `DefaultIcon` value continues to point to the executable at index 0. This keeps the icon valid when the program is moved as a portable folder and avoids an external icon path becoming stale.

The existing extension list remains unchanged:

`.mp4`, `.mkv`, `.m4v`, `.mov`, `.avi`, `.webm`, `.ts`, `.m2ts`, `.mts`, `.mpg`, `.mpeg`, `.wmv`, `.flv`, `.ogv`.

Registration remains per-user under `HKCU`. The app registers its ProgID, capabilities, supported types, open command, and `OpenWithProgids` values, then asks Windows to confirm the default application. It does not attempt to bypass Windows `UserChoice` protection.

After registration, the implementation refreshes the shell association/icon cache so Explorer can pick up the embedded icon without requiring a reboot.

### Interface direction

The video surface remains the visual focus and stays black during playback. The empty state remains clickable and continues to open the file dialog. It receives a centered Luma play mark, compact title, and a short HDR/Dolby Vision hint.

The bottom control area keeps the existing timeline, elapsed/remaining/total labels, status text, and every existing control in the same functional groups:

- Open
- -10 seconds
- Play/Pause
- +10 seconds
- Mute
- Volume
- Speed
- Audio track
- Subtitle
- File association
- Full screen

Controls use graphite surfaces, thin borders, restrained highlights, approximately 8px corners, and warm orange only for the primary play/pause action and related active states. Text uses the existing Segoe UI family with clear primary, secondary, and muted levels. Long filenames and status strings ellipsize instead of pushing controls out of the layout.

The current double-buffered, delayed-opacity first paint behavior is retained. Empty, loading, playing, missing-engine, error, full-screen, and narrow-window states must not overlap or jump.

### Startup and stability

The player continues to start mpv only when a video is requested. No directory scan, resident service, or additional startup dependency is introduced.

The implementation will:

- Cache the resolved mpv path and avoid repeated filesystem probing during one app session.
- Preserve the existing D3D11, `gpu-next`, hardware decode, and display-aware HDR/Dolby Vision mapping options.
- Harden named-pipe connection timeout and process-start failure handling.
- Make process-exit and reader-thread failures idempotent so users receive one actionable error state instead of duplicate dialogs.
- Stop UI callbacks from targeting disposed forms or controls.
- Dispose the pipe reader/writer and reader thread predictably on close, with a bounded wait and process cleanup if mpv does not exit.
- Keep controls disabled until a file is accepted and re-disable them after engine shutdown.

### Installation

The portable release ZIP remains supported. A release folder also contains a one-click installation entry point that:

1. Copies the complete player payload into `%LocalAppData%\Programs\LumaPlayer`.
2. Creates a Start Menu shortcut.
3. Registers the existing 14 video extensions for the current user.
4. Refreshes shell associations and starts the Windows default-app confirmation page.

The primary one-click entry point is a batch wrapper around PowerShell so no separate runtime is required. The packaging script may additionally produce a single `LumaPlayer-Setup.exe` through the Windows-built-in IExpress when available. The batch/PowerShell path remains the fallback and the source of truth. Re-running installation is supported for upgrades. The installer does not delete unrelated files or touch user media.

## Architecture and Data Flow

`build.ps1` compiles `src/LumaPlayer.cs` with `src/luma.ico` embedded into the executable. `package.ps1` stages the executable, mpv runtime, input configuration, notices, license, icon metadata, and installer entry point into the release directory, then creates the ZIP and manifest.

At runtime, `RegisterFileAssociations` uses the executable path from `Application.ExecutablePath` for both the open command and the embedded icon. Windows launches the command with the selected video path. `Main` accepts the first existing file argument and schedules loading after the form is shown, preserving the existing anti-flicker startup path.

The WinForms host owns the lifetime of one `MpvProcess`. The process owns the named-pipe IPC connection and reader thread. Engine events are marshalled to the UI through a disposal-aware callback boundary. On engine failure, the host updates the status and disables playback controls once.

## Error Handling

- Missing `mpv.exe`: show the existing actionable engine-missing message and keep the window usable for installation or manual recovery.
- Failed process start or pipe connection: dispose partial resources, clear the engine reference, and show one error message.
- mpv exits unexpectedly: surface one status/error notification, disable engine-dependent controls, and avoid repeated dialogs.
- Invalid or missing file path: reject before starting mpv and show the path in the existing error flow.
- Installer cannot write the destination: show the failing destination and stop without deleting the source package.
- Windows default-app confirmation is canceled: keep the registration and tell the user it can be confirmed later from Default Apps.

## Verification Plan

Static/build checks:

- Compile the WinForms host with the embedded icon.
- Run the project packaging script and validate required files and SHA-256 manifest.
- Confirm the generated executable exposes an icon resource.
- Validate installer source and staged payload paths without touching unrelated directories.

Behavior checks:

- Launch with no argument and confirm the empty state is stable and clickable.
- Launch by double-clicking a video path and confirm the correct file loads.
- Confirm all 14 extensions register to the same ProgID and executable command.
- Confirm `DefaultIcon` points to `LumaPlayer.exe,0` and survives a portable directory move.
- Confirm existing buttons, keyboard shortcuts, drag-and-drop, menus, full screen, timeline, volume, subtitles, audio tracks, speed, and association flow still work.
- Exercise repeated file loads, mpv missing, mpv early exit, and application close.
- Run the existing HDR/Dolby Vision verification path and confirm the color-management arguments are unchanged.
- Check empty, loading, error, narrow-window, and full-screen states for overlap and clipping.

## Acceptance Criteria

The change is complete when:

1. The EXE builds with the new Luma icon embedded.
2. All existing controls and behaviors remain available.
3. The current 14 video extensions use the Luma ProgID and display the Luma icon after shell refresh/default-app confirmation.
4. The player does not show duplicate engine errors or hang on normal close.
5. The portable ZIP still works and the one-click installer installs a complete runnable copy for another Windows PC.
6. Build, packaging, and relevant playback verification pass, with remaining hardware/display limitations documented.
