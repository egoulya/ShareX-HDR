# HDR quality fixes + HDR master PNG

**Status:** Approved (user: A+B, 2026-08-18)  
**Scope:** Recording/screenshot handoff hardening, race fix, even-size scoping, tests/README, optional HDR PNG companion.

## Goals

1. Keep HDR screenshot latency and correctness after recording ends.
2. Make mid-recording screenshot DXGI contention diagnosable (log `NOT_CURRENTLY_AVAILABLE`).
3. Serialize concurrent HDR screenshot captures.
4. Even capture size only for two-pass / yuv420p paths that need it.
5. Document ShareX-HDR in README.
6. Optional companion HDR master: `shot_hdr.png` (PQ + cICP); clipboard/upload stay tonemapped SDR.

## Non-goals (this pass)

- Full borrow-from-recorder screenshot path (deferred; documented as known limitation).
- HDR annotations / uploading the master by default.
- JPEG XL / JXR / AVIF as primary HDR master.

## Design

### A. Handoff

- On HDR pipe teardown (`RecordUsingHdrDxgiPipe` finally): `Screenshot.WarmHdrCapture()`.
- In `TryCreateDuplication`: if HR == `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE` (0x887A0022), log distinctly (recording or another app holds the output).

### B. Concurrency

- Static `SemaphoreSlim(1,1)` around `CaptureRectangleHDR` body (wait/release in try/finally).

### C. Even size

- `IsEvenSizeRequired => !IsAnimatedImage` restored for property meaning.
- ScreenRecordManager: even when `ScreenRecordTwoPassEncoding || CaptureHDREnabled` (HDR pipe always yuv420p).
- HDR pipe keeps its own `EvenRectangleSize`.

### D. Tests

- Comment ±2 LSB (Bayer + encode LUT) and ±3 cross-format.

### E. README

- Replace with provided ShareX-HDR README (upstream details stub TODO).

### F. HDR master PNG

- Setting: `TaskSettingsCapture.SaveHdrMasterPng` + checkbox under HDR capture.
- Capture produces optional PQ R16G16B16A16 (or similar) buffer alongside tonemapped bitmap when option on.
- Save as `FileHelpers.AppendTextToFileName(path, "_hdr")` with `.png`.
- Inject PNG chunks before IDAT: cICP `9,16,0,1`; cLLI from MaxCLL/MaxFALL; mDCV from DXGI_OUTPUT_DESC1 when available.
- Clipboard / history / upload: tonemapped only.
- No editor annotations on master.

## Success criteria

- After recording, first screenshot is warm (no full cold start).
- Mid-record screenshot logs `NOT_CURRENTLY_AVAILABLE` clearly.
- Concurrent HDR captures do not corrupt PackedBuffer.
- Odd-sized direct GIF without two-pass not force-cropped unless HDR/two-pass.
- With option on: `foo.png` + `foo_hdr.png`; clipboard paste is SDR.
