<p align="center"><a href="https://getsharex.com"><img src="https://getsharex.com/img/ShareX_Banner.png" alt="ShareX Banner"/></a></p>
<h3 align="center">Screen capture, file sharing and productivity tool</h3>
<br>
<div align="center">
  <a href="./LICENSE.txt"><img src="https://img.shields.io/badge/License-GPL%20v3-brightgreen" alt="License"/></a>
  <a href="https://github.com/ShareX/ShareX"><img src="https://img.shields.io/badge/Upstream-ShareX%2FShareX-blue" alt="Upstream"/></a>
  <a href="https://discord.gg/ShareX"><img src="https://img.shields.io/discord/194170124859736065?label=Discord&cacheSeconds=3600" alt="Discord"/></a>
</div>
<br>
<p align="center"><a href="https://getsharex.com"><img src="https://getsharex.com/img/ShareX_Screenshot.png" alt="ShareX Screenshot"/></a></p>

# ShareX-HDR (modified ShareX fork)

This repository is a **modified fork** of [ShareX](https://github.com/ShareX/ShareX) focused on better **HDR → SDR** capture for screenshots, color picking, video, and GIF recording on Windows HDR displays.

It is **not** an official ShareX build. Upstream ShareX is developed by the [ShareX Team](https://github.com/ShareX/ShareX).

## License (GPL v3)

ShareX and this fork are licensed under the **GNU General Public License v3.0**. See [LICENSE.txt](./LICENSE.txt).

If you distribute binaries built from this fork, you must:

- Keep the software under **GPL v3**
- Provide corresponding **source code**
- Clearly state that this is a **modified** version of ShareX

## What this fork adds

### DXGI HDR capture (screenshot path)

When **HDR capture (DXGI tonemap)** is enabled in Task Settings → Capture, ShareX uses DXGI Desktop Duplication instead of GDI BitBlt for HDR outputs:

1. Creates a D3D11 device and enumerates DXGI outputs (monitors).
2. For each monitor that intersects the capture region, probes the format via `DuplicateOutput1`.
3. HDR monitors (`RGBA16F` / `R10G10B10A2`) go through DXGI capture + tonemap.
4. SDR monitors (`B8G8R8A8`) use the fast GDI path.
5. Results are composited onto one bitmap at virtual-desktop offsets.

| Format | Pipeline | Description |
|--------|----------|-------------|
| RGBA16F (scRGB) | Normalize by SDR white level, BT.2390-style tonemap, linear → sRGB | Typical Windows HDR / DWM |
| R10G10B10A2 (HDR10) | PQ decode, BT.2020 → BT.709, tonemap, linear → sRGB | HDR10 output mode |
| B8G8R8A8 (SDR) | Direct GDI copy | Standard SDR monitors |

Tonemapping keeps SDR content (luminance ≤ 1.0) unchanged and soft-compresses highlights above that.

This screenshot approach is based on the work in [Psyda/ShareX-scRGB-Proper-HDR-Fix](https://github.com/Psyda/ShareX-scRGB-Proper-HDR-Fix), including later frame-acquisition improvements.

### HDR screen recording and GIF

This fork also records HDR desktops with the same color math as screenshots:

- Persistent DXGI session + CPU tonemap to BGR24
- Pipe into FFmpeg (x264 / GIF two-pass)
- Correct playback timing when capture cannot sustain the configured FPS (duplicate frames instead of speeding up)

**Note:** High FPS at fullscreen ultrawide is resource-heavy. Capture throughput is the limit; use moderate FPS unless you accept duplicated frames / higher CPU use.

### Upstream HDR color corrector

Official ShareX also added **HDR screenshot color correction** (GDI capture + DXGI/WIC correction). This fork keeps that option available so you can compare:

- **HDR screenshot color corrector** — upstream approach
- **HDR capture (DXGI tonemap)** — Psyda-style replace-capture path (also used for recording)

## Credits

- [ShareX Team](https://github.com/ShareX/ShareX) — original application
- [Psyda / ShareX-scRGB-Proper-HDR-Fix](https://github.com/Psyda/ShareX-scRGB-Proper-HDR-Fix) — DXGI HDR screenshot capture and tonemap approach this fork builds on
- This fork — HDR recording/GIF integration, FPS pacing, and maintenance against current upstream `develop`

## Build

Requires **.NET 10 SDK** (upstream ShareX targets `net10.0-windows`).

```bash
dotnet build ShareX/ShareX.csproj -c Release
```

## Links

* This fork: https://github.com/egoulya/ShareX-HDR
* Upstream ShareX: https://github.com/ShareX/ShareX
* Official website: https://getsharex.com
* Psyda HDR fork: https://github.com/Psyda/ShareX-scRGB-Proper-HDR-Fix
* License: [LICENSE.txt](./LICENSE.txt)

---

# ShareX - Free Screen Capture, Screenshot, File Sharing and Productivity Tool

ShareX is a free and open source screenshot tool, screen recorder, file sharing tool and productivity application for Windows. It is designed for users who need fast screen capture, powerful screenshot editing, automated sharing, custom upload destinations and practical utilities in one lightweight desktop app.

With ShareX, you can capture any area of your screen, record video or GIFs, annotate screenshots, upload files, copy shareable links, extract text with OCR, scan QR codes, pick colors and run custom workflows from hotkeys. ShareX is built for speed and control: capture a screenshot, edit it, save it, copy it, upload it or pass it through your own task chain with minimal manual work.

## Why ShareX?

ShareX combines screen capture, screen recording, image editing, file uploading and automation features that are often split across multiple applications. It is completely free, open source, lightweight, privacy focused and has no advertisements. No account is required to use ShareX.

ShareX is especially useful for developers, designers, support teams, content creators, technical writers, QA testers and power users who frequently create screenshots, record short clips, share files or document workflows. It can be used as a simple screenshot app, but it also supports advanced workflows for users who want precise control over capture methods, after-capture tasks, upload destinations and hotkeys.

## Screenshot and Screen Recording Features

ShareX supports many ways to capture your screen:

* Fullscreen capture
* Active window capture
* Active monitor capture
* Region capture
* Scrolling screenshot capture
* Last region capture
* Custom region capture
* Screen recording
* GIF screen recording
* Auto capture

After capturing a screenshot or recording, ShareX can automatically copy the result to the clipboard, save it to a file, open it in the image editor, upload it, print it, show it in Windows Explorer, run an action, scan a QR code or recognize text with OCR. These after-capture tasks make ShareX a flexible screenshot workflow tool instead of only a basic snipping utility.

## Region Capture and Annotation

ShareX region capture includes tools for selecting exactly what you want to capture and marking it before saving, copying or uploading. You can draw rectangles, ellipses, freehand lines, arrows, text, speech balloons, step numbers, highlights, blur effects, pixelation, magnification and spotlight effects.

These annotation tools help create clear screenshots for bug reports, documentation, tutorials, support replies, pull requests and release notes. Sensitive information can be hidden with blur, pixelate or smart eraser tools before a screenshot is shared.

## Built-in Image Editor

The ShareX image editor lets you crop, annotate, redact, highlight and prepare screenshots after capture. It includes common editing tools such as shapes, arrows, text, freehand drawing, image insertion, cursor insertion, blur, pixelate, magnify, spotlight, crop, cut out, background editing and image effects.

Because the editor is part of the capture workflow, you can take a screenshot, mark the important area, hide private details and then copy, save or upload the edited image without switching between separate apps.

## File Sharing and Upload Automation

ShareX can upload images, text, files, folders, clipboard content and URLs to many different destinations. After uploading, it can automatically copy the URL to the clipboard, open the URL, shorten the URL, show a QR code or run other configured tasks.

Advanced users can create custom uploaders for services that are not built in. ShareX also provides guides for destinations such as Amazon S3, Google Cloud Storage and Cloudflare R2, making it suitable for both personal screenshot sharing and team workflows where files need to be uploaded to controlled storage.

## Productivity Tools

ShareX includes many utilities that support everyday desktop work:

* Color picker
* Screen color picker
* Ruler
* Pin to screen
* Image editor
* Image beautifier
* Image effects
* Image viewer
* Background remover
* Image comparer
* Image combiner
* Image splitter
* Image thumbnailer
* Video converter
* Video thumbnailer
* Analyze image
* OCR for recognizing text in images
* QR code
* Hash checker
* Metadata viewer
* Directory indexer
* Clipboard viewer
* Borderless window
* Inspect window
* Monitor test

These tools make ShareX useful beyond screenshots. It can help inspect images, prepare assets, extract information, verify files and speed up repetitive tasks.

## Custom Workflows and Hotkeys

ShareX is built around configurable workflows. You can assign hotkeys to capture methods, choose what happens after capture, decide what happens after upload and create actions that run external tools or scripts. This makes it possible to build a workflow such as capture region, annotate image, save locally, upload to a destination, shorten the URL and copy the final link to the clipboard.

The workflow system is one of the main reasons ShareX is popular with power users. Simple tasks can stay simple, while advanced users can automate detailed screenshot, screen recording and file sharing processes.

## Download ShareX

ShareX is available from the official website, GitHub releases, Microsoft Store and Steam. You can install the regular setup version, use a portable version or try development builds if you want the newest changes before a stable release.

For the safest download options, use the official links below.

## Links
* This fork (ShareX-HDR): https://github.com/egoulya/ShareX-HDR
* Official website: https://getsharex.com
* Downloads: https://getsharex.com/downloads
* Upstream GitHub: https://github.com/ShareX/ShareX
* Changelog: https://getsharex.com/changelog
* Screenshots: https://getsharex.com/screenshots
* Privacy policy: https://getsharex.com/privacy-policy
* Donate: https://getsharex.com/donate
* X: https://x.com/ShareX
* Discord: https://discord.gg/ShareX
* Reddit: https://www.reddit.com/r/sharex
* Steam page: https://store.steampowered.com/app/400040/ShareX/
* Microsoft Store page: https://apps.microsoft.com/detail/9nblggh4z1sp
* ShareX related projects on GitHub: https://github.com/topics/sharex
* Psyda HDR fork: https://github.com/Psyda/ShareX-scRGB-Proper-HDR-Fix
* License: [LICENSE.txt](./LICENSE.txt)

## Documents
* Image effects: https://getsharex.com/image-effects
* Actions: https://getsharex.com/actions
* Dev builds: https://getsharex.com/docs/dev-builds
* Keybinds: https://getsharex.com/docs/keybinds
* Region capture: https://getsharex.com/docs/region-capture
* Image editor: https://getsharex.com/docs/image-editor
* Background remover: https://getsharex.com/docs/background-remover
* Pin to screen: https://getsharex.com/docs/pin-to-screen
* Scrolling screenshot: https://getsharex.com/docs/scrolling-screenshot
* Command line arguments: https://getsharex.com/docs/command-line-arguments
* Translation: https://getsharex.com/docs/translation
* OCR: https://getsharex.com/docs/ocr
* Custom uploader: https://getsharex.com/docs/custom-uploader
* Amazon S3 guide: https://getsharex.com/docs/amazon-s3
* Google Cloud Storage guide: https://getsharex.com/docs/google-cloud-storage
* Cloudflare R2 guide: https://getsharex.com/docs/cloudflare-r2
* Brand assets: https://getsharex.com/brand-assets
