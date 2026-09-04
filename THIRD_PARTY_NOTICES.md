# Third-party notices

Luma Player distributes the following unmodified runtime components beside its own WinForms host:

## mpv Windows build

- Package: mpv 0.41.0 shinchiro-based Windows installer
- Runtime: `mpv v0.41.0-244-gaf9c81fa1`, built 2026-03-02
- libplacebo: `v7.360.0`
- FFmpeg: `N-123099-g862338fe3`
- Upstream: <https://github.com/mpv-player/mpv>
- Windows builds: <https://github.com/shinchiro/mpv-winbuild-cmake>
- Reproducible package source: <https://github.com/0GMou/mpv2winget/releases/download/v0.41.0/mpv_installer_x64.exe>
- Installer SHA-256 from Microsoft's signed WinGet index: `159441C52FD74755CB74C830F661FCD9517C77056887C27AC0569876B600F0C9`
- License: GPL-2.0-or-later. See `licenses/mpv/GPL-2.0.txt`.

The corresponding source code is available from the upstream repositories and the build revision listed above. Luma Player invokes `mpv.exe` as a separate process and does not modify its binary.

## DirectX shader compiler compatibility runtime

`d3dcompiler_43.dll` is distributed as part of the verified mpv Windows package above and is used by the D3D11 rendering path.
