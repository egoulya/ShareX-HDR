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

<!-- TODO: before/after comparison image. Left: vanilla ShareX on an HDR display. Right: ShareX-HDR. Same scene, same monitor. -->

> This is **not** an official ShareX build. Upstream ShareX is developed by the [ShareX Team](https://github.com/ShareX/ShareX).

## Download

Grab the latest build from [**Releases**](https://github.com/egoulya/ShareX-HDR/releases/latest).

**Requirements**

- Windows 10 1903 or later (Windows 11 recommended)
- An HDR-capable display with HDR enabled in Windows
- .NET 10 Desktop Runtime

## Enable it

1. Open **Task Settings → Capture**
2. Set **HDR capture** to **On** (or **Dynamic** to let it decide per capture)
3. Leave **HDR tonemap mode** on **Auto**

That's it. SDR monitors keep using the normal fast path, so there is nothing to switch when you move between displays.

Two optional outputs, both off by default:

- **Also save Ultra HDR JPEG (gain map)** — a shareable file that looks like SDR everywhere and like the original on an HDR display. See [Ultra HDR output](#ultra-hdr-output).
- **Also save HDR master PNG (PQ / cICP)** — a lossless 16-bit companion `*_hdr.png` for archiving. Do not share this one; see the table below for what platforms do to it.

Clipboard, history thumbnails and uploads always use the tonemapped SDR image.

<!-- TODO: screenshot of the Task Settings → Capture panel with the HDR options visible -->

### Tonemap modes

`Auto` is the right answer almost always. It classifies each frame by its luminance statistics and picks a curve, with hysteresis so consecutive captures of the same scene stay consistent. In practice it selected the Desktop curve on all 17 frames of the test corpus, including the Windows Auto-HDR ones — real Auto-HDR content peaks well above the threshold that reads as native HDR.

| Mode | Use for |
|------|---------|
| **Auto** | Default. Detects from content. |
| **Desktop / true HDR** | Desktop, browsers, editors, games. Preserves UI white exactly. |
| **Filmic** | Stylised, higher contrast. A deliberate look, not a fidelity mode. |

The list used to include *Auto-HDR games* and *Windows WIC*. Both were withdrawn after measurement: the Auto-HDR curve's fixed ceiling lost to Desktop on every axis and was never once selected across a 17-frame corpus, and Windows' own converter loses on every axis too — 18.2 code values of round-trip error against 1.8, and 9.2° of highlight hue drift against 0.44° — besides refusing HDR10 outputs outright. Both remain in the code so the evaluation harness can still measure against them.

**HDR paper white (nits)** overrides the SDR white level Windows reports for your display. Leave it at **0** unless the detected value is wrong — the settings row shows what was detected. This replaces the old 0.70–1.30 exposure dial, which was removed because every setting other than the default measured worse: round-trip error was 1.8 code values at 1.00 against 16–34 everywhere else, and above 1.00 it did nothing at all.

## Why this fork

There are several HDR forks of ShareX. Here is what this one does differently.

**Correct on multi-monitor setups.** SDR white level is read per output from the display config, rather than one global value applied to every monitor. Forks that use a single value produce blown-out captures on mixed HDR/SDR setups, depending on which monitor Windows happens to enumerate first.

**No seam across monitors.** Luminance statistics are gathered across every monitor the capture region touches, then a single tonemap curve is built and applied to all of them. Per-monitor curves produce a visible brightness step down the boundary of a spanning capture.

**HDR10 output mode is correct.** The PQ path normalizes against the real SDR white level rather than a fixed 80 nits, so HDR10 captures aren't ~2.5× too bright.

**Fast.** The D3D11 device and per-output duplication are cached across captures and invalidated on display changes, so repeat captures skip DWM warm-up entirely instead of paying up to two seconds each time.

**Verified, not just eyeballed.** The colour pipeline has 77 automated tests that run in CI on every release build, and an offline harness that measures capture quality against the untouched HDR original. The tests assert that ordinary SDR content survives a round trip within 2 LSB, that scRGB and HDR10 inputs agree after tonemapping, that this holds at 80, 203 and 400 nit SDR white levels, and that gain-map files reconstruct correctly and stay structurally valid. See [Measuring it](#measuring-it).

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
- The default output is **tonemapped SDR** — a standard PNG that displays correctly everywhere. Uploads, clipboard and thumbnails always use it; the Ultra HDR JPEG and HDR master PNG are opt-in companions, never a substitute.
- **Gain maps survive fewer places than you would hope.** Telegram keeps them when a file is sent as a document; Discord strips them. The result is still a correct SDR picture, but HDR only reaches viewers whose path preserves the map.
- **The paper white override applies to every output.** On a multi-monitor setup with genuinely different white levels it will be right for one display and wrong for the others, which is why the default leaves it to the per-output detection.

## Ultra HDR output

A tonemapped SDR screenshot can only be so faithful, and the reason is arithmetic rather than tuning: **sRGB has about six code values between linear 0.9 and 1.0**. Bright detail has nowhere to go. Measured on a game inventory screen, white card interiors spanning 48 code values in the source came out spanning 14, and no curve setting recovers them — compressing harder measurably makes it worse.

A gain map fixes that by not trying. **Also save Ultra HDR JPEG (gain map)** writes a `*_ultrahdr.jpg`: an ordinary SDR JPEG with a small map of per-pixel ratios appended, following ISO 21496-1 / Google's Ultra HDR. A viewer that understands it reconstructs the HDR image; a viewer that doesn't shows the SDR base and ignores the rest.

That asymmetry is the point. **The worst case is the file you would have shipped anyway.**

| Where | Result |
|---|---|
| Chrome, opened locally | Full HDR, visually identical to the original |
| Telegram, sent **as a file** | Gain map preserved — full HDR |
| Telegram, sent as a photo | Correct SDR |
| Discord, image or file | Correct SDR |

Tested on three HDR devices. No path produced a broken image.

Compare that with sending the **HDR master PNG**, which is why it is an archival format and not a sharing one: Discord re-encodes it without a real PQ tonemap and wrecks the hues, and Telegram ignores the `cICP` tag entirely and renders PQ as if it were sRGB — a flat grey mess. That is also the practical answer to [ShareX/ShareX#6688](https://github.com/ShareX/ShareX/issues/6688), which proposed tagging 8-bit PNGs with colour-space metadata and letting HDR displays re-expand them. Major platforms do not honour the tag.

Reconstruction is near-exact: full-resolution round-trip recovers the source HDR to **0.1–0.2% median error**. The map is stored at full resolution on purpose — where the SDR base clipped, it is the *only* carrier of detail, so downsampling discards precisely what it exists to preserve (a divisor of 4 measured roughly 50× worse).

The gain map is derived on the worker thread rather than during capture, so taking screenshots in quick succession is not held up. If you crop or edit before saving, the two layers no longer line up and the write is skipped with a logged reason rather than shipping a mismatched file.

## Measuring it

Judging HDR conversion by eye does not converge, so this fork ships the tooling that produced the numbers above.

**Debug capture hotkey.** Bind *HDR debug capture (dump + report)* under Task Settings → Hotkeys. Each press captures the screen, saves the raw HDR frame, and regenerates an HTML report comparing every tonemap mode side by side against the untouched original, with a built-in fullscreen viewer. Reports accumulate per launch.

**`ShareX.HdrEval`.** Replays a corpus of captured frames through the production tonemap and writes a metrics CSV and report:

```bash
ShareX.HdrEval --corpus <dir> [--modes Auto,Desktop,Filmic] [--ultrahdr]
```

It measures clipping, highlight span against the source, CIE xy chromaticity error against a just-noticeable difference, hue drift, local contrast, and round-trip fidelity for content that was already in range.

This is not decoration — it found real bugs in the shipping capture path: a lost duplication that broke HDR capture until restart, a luminance peak estimated from 4096 of 4.95M pixels (an extremum cannot be subsampled), a gamut clamp that destroyed highlight chroma, and Filmic applying its curve per channel. Cumulative effect on a 17-frame corpus: clipping 31.3% → 0%, highlight chroma retention 0.465 → 1.000, hue drift 3.3° → 0.3°.

## Build

Requires the **.NET 10 SDK** (upstream ShareX targets `net10.0-windows`).

```bash
dotnet build ShareX/ShareX.csproj -c Release
```

Run the colour pipeline tests:

```bash
dotnet test ShareX.ScreenCaptureLib.Tests/ShareX.ScreenCaptureLib.Tests.csproj
```

Build the evaluation harness:

```bash
dotnet build ShareX.HdrEval/ShareX.HdrEval.csproj -c Release
```

Translations are validated separately, and the check is strict about catalogue parity, placeholders and encoding:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File ValidateTranslations.ps1
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
- This fork — per-output white level handling, merged multi-monitor tonemapping, HDR10 correction, duplication caching, Ultra HDR gain-map output, the measurement harness, the tested colour pipeline, and HDR recording/GIF

## Links

- This fork: https://github.com/egoulya/ShareX-HDR
- Upstream ShareX: https://github.com/ShareX/ShareX
- Official website: https://getsharex.com
- Psyda's HDR fork: https://github.com/Psyda/ShareX-scRGB-Proper-HDR-Fix

---

<details>
<summary><b>About upstream ShareX</b></summary>

ShareX is a free, open-source screen capture, screen recording, file-sharing and productivity tool for Windows, developed by the [ShareX Team](https://github.com/ShareX/ShareX). It is not ad-supported and needs no account.

Everything below is upstream's work and is unchanged in this fork:

- **Capture** — region, window, monitor, scrolling and full-screen capture; screen recording to video or GIF
- **Annotate** — a built-in image editor with shapes, text, arrows, blur, pixelation, highlighting and step numbering
- **Share** — automated upload to a large set of destinations, or your own custom uploader, with the link on your clipboard when it finishes
- **Automate** — after-capture and after-upload task chains bound to hotkeys, so a capture can be edited, saved, uploaded and copied in one keypress
- **Tools** — OCR, QR codes, colour picker, image effects, video converter and trimmer, and more

This fork changes only the HDR capture path. For full documentation, see the [official website](https://getsharex.com) and the [upstream README](https://github.com/ShareX/ShareX/blob/develop/README.md).

</details>
