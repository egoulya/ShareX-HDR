<p align="center"><a href="https://getsharex.com"><img src="https://getsharex.com/img/ShareX_Banner.png" alt="ShareX Banner"/></a></p>
<h3 align="center">ShareX with proper HDR screenshots, recording and GIFs</h3>
<br>
<div align="center">
  <a href="./LICENSE.txt"><img src="https://img.shields.io/badge/License-GPL%20v3-brightgreen" alt="License"/></a>
  <a href="https://github.com/egoulya/ShareX-HDR/releases/latest"><img src="https://img.shields.io/github/v/release/egoulya/ShareX-HDR?label=Download" alt="Download"/></a>
  <a href="https://github.com/egoulya/ShareX-HDR/actions"><img src="https://img.shields.io/github/actions/workflow/status/egoulya/ShareX-HDR/build.yml?branch=hdr-dev" alt="Build"/></a>
  <a href="https://github.com/ShareX/ShareX"><img src="https://img.shields.io/badge/Upstream-ShareX%2FShareX-blue" alt="Upstream"/></a>
</div>

# ShareX-HDR

Screenshots taken on a Windows HDR display come out washed out, grey and low-contrast. This is a **modified fork of [ShareX](https://github.com/ShareX/ShareX)** that fixes it properly — screenshots, screen recording and GIFs all come out looking like what is actually on your screen.

Everything else about ShareX is unchanged: region capture, the annotation editor, workflows, hotkeys, and every upload destination work exactly as they always have.

<!-- TODO: before/after comparison image goes here -->
<!-- Left: vanilla ShareX on an HDR display. Right: ShareX-HDR. Same scene, same monitor. -->

> This is **not** an official ShareX build. Upstream ShareX is developed by the [ShareX Team](https://github.com/ShareX/ShareX).

## Download

Grab the latest build from [**Releases**](https://github.com/egoulya/ShareX-HDR/releases/latest).

**Requirements**

- Windows 10 1903 or later (Windows 11 recommended)
- An HDR-capable display with HDR enabled in Windows
- .NET 10 Desktop Runtime

## Enable it

1. Open **Task Settings → Capture**
2. Tick **HDR capture (DXGI tonemap)**
3. Leave **HDR tonemap mode** on **Auto**

That's it. SDR monitors keep using the normal fast path, so there is nothing to switch when you move between displays.

Optional: **Also save HDR master PNG (PQ / cICP)** writes a companion `*_hdr.png` next to the tonemapped shareable file. Clipboard, history thumbnails, and uploads always use the tonemapped SDR image.

<!-- TODO: screenshot of the Task Settings → Capture panel with the HDR options visible -->

### Tonemap modes

`Auto` is the right answer almost always — it picks between the desktop and game curves based on frame content, with hysteresis so consecutive captures of the same scene stay consistent.

| Mode | Use for |
|------|---------|
| **Auto** | Default. Detects from content. |
| **Desktop / true HDR** | Desktop, browsers, editors. Preserves UI white exactly. |
| **Auto-HDR games** | Auto-HDR titles. Softer highlight rolloff. |
| **Filmic** | Stylised, higher contrast. |
| **Windows WIC** | Windows' own converter. Screenshots only, for comparison. |

**Exposure** is adjustable from 0.70 to 1.30 if a particular game sits brighter or darker than you want. Default is 1.00.

## Why this fork

There are several HDR forks of ShareX. Here is what this one does differently.

**Correct on multi-monitor setups.** SDR white level is read per output from the display config, rather than one global value applied to every monitor. Forks that use a single value produce blown-out captures on mixed HDR/SDR setups, depending on which monitor Windows happens to enumerate first.

**No seam across monitors.** Luminance statistics are gathered across every monitor the capture region touches, then a single tonemap curve is built and applied to all of them. Per-monitor curves produce a visible brightness step down the boundary of a spanning capture.

**HDR10 output mode is correct.** The PQ path normalizes against the real SDR white level rather than a fixed 80 nits, so HDR10 captures aren't ~2.5× too bright.

**Fast.** The D3D11 device and per-output duplication are cached across captures and invalidated on display changes, so repeat captures skip DWM warm-up entirely instead of paying up to two seconds each time.

**Verified, not just eyeballed.** The colour pipeline has an automated test suite that runs in CI on every build. It asserts that ordinary SDR content survives a round trip within 2 LSB, that scRGB and HDR10 inputs agree after tonemapping, and that all of this holds at 80, 203 and 400 nit SDR white levels.

**Recording and GIF too.** HDR screen recording and GIF export use the same colour maths as screenshots. No other ShareX fork does this.

## How it works

With HDR capture enabled, ShareX uses DXGI Desktop Duplication instead of GDI BitBlt for HDR outputs:

1. Creates a D3D11 device and enumerates DXGI outputs.
2. For each monitor intersecting the capture region, probes the surface format via `DuplicateOutput1`.
3. Reads that monitor's SDR white level from the display configuration.
4. HDR monitors go through DXGI capture and tonemap; SDR monitors use the fast GDI path.
5. Luminance statistics from all HDR slices are merged into one BT.2390 curve.
6. Results are composited onto a single bitmap at virtual-desktop offsets.

| Format | Pipeline |
|--------|----------|
| RGBA16F (scRGB) | Normalize by per-output SDR white → BT.2390 EETF in PQ space → linear to sRGB |
| R10G10B10A2 (HDR10) | PQ decode → BT.2020 to BT.709 → normalize → BT.2390 EETF → linear to sRGB |
| B8G8R8A8 (SDR) | Direct GDI copy |

SDR content — anything at or below paperwhite — passes through unchanged. That's a tested guarantee, not an aspiration: your screenshots of VS Code and Chrome come out byte-for-byte the same as vanilla ShareX. Only highlights above paperwhite get compressed.

Output is dithered on encode, which avoids the banding that shows up in gradients when 16-bit float is quantised straight to 8-bit.

### Recording

- Persistent DXGI session with CPU tonemap to BGR24
- Piped into FFmpeg (x264, or two-pass palette for GIF)
- Frames are duplicated rather than dropped when capture can't sustain the configured FPS, so playback timing stays correct

## Known limitations

- **Screenshots taken while recording** fall back to the GDI path and will look washed out. DXGI permits only one duplication per output, and the recorder holds it. The debug log notes `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE` when this happens.
- **High FPS at fullscreen ultrawide** is throughput-bound. Use moderate FPS unless you're happy with duplicated frames and higher CPU load.
- **Mixed-monitor spans** use an area-weighted SDR white level for the shared curve, so a capture spanning displays with very different paperwhite settings is a compromise between them.
- Output is **tonemapped SDR** — standard PNG and JPEG that display correctly everywhere. The optional HDR master is a companion file, not the shareable default. This fork does not replace uploads with HDR formats.

## Upstream's HDR colour corrector

Official ShareX has its own **HDR screenshot colour corrector** (GDI capture plus DXGI/WIC correction). This fork keeps that option available so you can compare the two:

- **HDR screenshot colour corrector** — upstream's approach
- **HDR capture (DXGI tonemap)** — this fork's replace-capture path, also used for recording

## Build

Requires the **.NET 10 SDK** (upstream ShareX targets `net10.0-windows`).

```bash
dotnet build ShareX/ShareX.csproj -c Release
```

Run the colour pipeline tests:

```bash
dotnet test ShareX.ScreenCaptureLib.Tests/ShareX.ScreenCaptureLib.Tests.csproj
```

## Bugs and feedback

Please use [this fork's issue tracker](https://github.com/egoulya/ShareX-HDR/issues) — **not** the upstream ShareX repository or Discord. They don't maintain this build and can't help with it.

A debug log with the resolved SDR white level per output is very useful in reports.

## License (GPL v3)

ShareX and this fork are licensed under the **GNU General Public License v3.0**. See [LICENSE.txt](./LICENSE.txt).

If you distribute binaries built from this fork, you must:

- Keep the software under **GPL v3**
- Provide corresponding **source code**
- Clearly state that this is a **modified** version of ShareX

## Credits

- [ShareX Team](https://github.com/ShareX/ShareX) — the original application
- [Psyda / ShareX-scRGB-Proper-HDR-Fix](https://github.com/Psyda/ShareX-scRGB-Proper-HDR-Fix) — the DXGI HDR capture approach this fork originally built on
- This fork — per-output white level handling, merged multi-monitor tonemapping, HDR10 correction, duplication caching, the tested colour pipeline, and HDR recording/GIF

## Links

- This fork: https://github.com/egoulya/ShareX-HDR
- Upstream ShareX: https://github.com/ShareX/ShareX
- Official website: https://getsharex.com
- Psyda's HDR fork: https://github.com/Psyda/ShareX-scRGB-Proper-HDR-Fix

---

<details>
<summary><b>About upstream ShareX</b></summary>

<!-- TODO: paste the existing upstream ShareX README content here, unchanged -->

</details>
