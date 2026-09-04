# Luma Player 0.1.0 acceptance record

Validated on Windows 11 on 2026-08-13 with the user-supplied file:

`Unsettled.Case.S01E01.2026.2160p.WEB-DL.HQ.DV.60FPS.H.265.AAC-HiveWeb.mp4`

## Source media

- Size: 5,301,284,226 bytes
- Duration: 45:16.52
- Video: HEVC Main 10, 3840×1920, 60.000 fps, 10-bit full range
- Dolby Vision: Profile 5, Level 9, RPU present, base layer present, no enhancement layer
- Audio: AAC-LC, stereo, 44.1 kHz, 256 kbps

## Performance

- Direct D3D11VA decode benchmark: 720 frames / 12 seconds decoded in 1.301 seconds
- Decode throughput: 553 fps, 9.22× realtime
- Embedded release-package playback: 12.033 seconds advanced during a 12-second observation
- Hardware decoder: `d3d11va`
- Renderer: `gpu-next` / libplacebo
- Decoder drops: 0
- Total presentation drops in the final run: 3
- Audio output: WASAPI; measured A/V synchronization available and stable

## Color path

- Dolby Vision detected in the actual embedded player: yes
- Source after Dolby Vision reshaping: BT.2020 / PQ
- Test display reported by DXGI: 1920×1080, 8-bit SDR, `RGB_FULL_G22_NONE_P709`
- Correct target on this display: BT.709 / gamma 2.2
- Result: Profile 5 was reshaped by libplacebo and tone-mapped to the real SDR target; it was not decoded as ordinary HEVC with incorrect colors.

On an HDR-capable display with Windows HDR enabled, the same `target-colorspace-hint=auto` path negotiates the HDR target instead. A physical HDR display was not connected during this acceptance run, so a display-side HDR indicator could not be observed here.

## Player controls and packaging

The final ZIP was extracted into a clean directory and launched from that directory. Automated end-to-end assertions passed for:

- responsive native host window and embedded mpv child window;
- play/pause, exact seek, volume, mute and WASAPI audio;
- full-screen messages forwarded from the embedded video surface;
- external subtitle loading and selection;
- dedicated input configuration and disabled duplicate mpv OSC;
- automatic hardware decoding and display-aware color mapping.

Final release archive: `LumaPlayer-0.1.0-win-x64.zip`

SHA-256: `989F8BDE764C3F44DD260EDB8BE74C4EC5390B3D8B0F902F011A9CE368B58E94`
