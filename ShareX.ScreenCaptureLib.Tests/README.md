# HDR capture evaluation

Evaluating an HDR→SDR tonemapper is three separate questions, and they need three different
kinds of answer:

1. **Did the output change?** Deterministic, hard pass/fail. `HdrPipelineTests` and
   `HdrCorpusTests` cover this.
2. **Did it change in a way that's measurably worse on a named axis?** Automatable and
   thresholded, but the thresholds are judgement calls.
3. **Do you prefer it?** Not automatable. Only blind, randomized A/B comparison makes this
   worth anything.

No metric says a tonemapper "looks good". Metrics catch regressions and rank candidates on
axes you name.

## Ground truth differs per scenario

| Scenario | Ground truth | How to get it |
| --- | --- | --- |
| Desktop / true HDR | **Exact.** Known sRGB content composited at SDR white must come back out as the same bytes. | Display a committed reference image, capture, compare. `Desktop_frame_round_trips_to_original_srgb` is the synthetic form of this. |
| Windows Auto-HDR | **Derivable.** The game's intent is its SDR output. | Capture the same scene with Auto-HDR off. Expensive (needs a game restart), so capture once and reuse the corpus forever. |
| Native HDR | **None.** The game's SDR mode is a different artistic grade, not a reference. | Invariant checks against the PQ master, plus blind human ranking. |

## The corpus

`HdrFrameDump` records the raw DXGI buffer of every HDR capture to a `.hdrframe` file
*before* tonemapping. `HdrFrameTonemapper.TonemapFrame` replays one back through the same
primitives the live capture path uses.

The point is to decouple "change the tonemap curve" from "boot a game". Capture a scene once;
after that, a curve change is evaluated across all three scenarios offline in seconds.

The payload holds the **raw DXGI bytes**, not the PQ `HdrMasterImage`. The master has already
been through `EncodeToPqBt2020`, which is lossy and is itself under test; storing the DXGI
bytes keeps decode, stats, curve and encode all inside the test boundary.

### Collecting frames

Arm the dump with environment variables — no rebuild, no settings change, and it can't be
left on for an ordinary user:

```bash
SHAREX_HDR_DUMP_DIR=D:\Work\Personal\ShareX-HDR\HdrCorpus SHAREX_HDR_DUMP_SCENARIO=NativeHdr SHAREX_HDR_DUMP_LABEL=cyberpunk-neon-alley SHAREX_HDR_DUMP_MAX=4
```

| Variable | Meaning |
| --- | --- |
| `SHAREX_HDR_DUMP_DIR` | Output directory. Unset means off. |
| `SHAREX_HDR_DUMP_SCENARIO` | `Desktop`, `AutoHdr` or `NativeHdr`. Selects per-scenario thresholds later. |
| `SHAREX_HDR_DUMP_LABEL` | Scene label, baked into the filename and metadata. |
| `SHAREX_HDR_DUMP_NOTES` | Free-form notes: game, settings, what the frame stresses. |
| `SHAREX_HDR_DUMP_MAX` | Stop after N frames. `0`/unset = unlimited. |
| `SHAREX_HDR_DUMP_COMPRESS` | `0`/`false` to store payloads uncompressed. |

The label is read at startup, so collect one scene per launch.

Each frame records the display's SDR white level, colour space, luminance range and mastering
primaries. Capture is display-referred — a frame without that metadata is not reproducible.

Full monitor outputs are dumped, not the requested crop, because Auto-mode stats are computed
over the whole output. Offline replay only reaches the same decision if it sees the same pixels.

### Repeatable scenes

The corpus needs determinism only once, but it does need it. Ranked by cost:

- **An HDR video played fullscreen with passthrough (e.g. mpv), seeked to an exact timestamp.**
  Perfectly repeatable, real native-HDR content, free. Best value by far.
- A game's photo or benchmark mode, or a paused frame with a fixed camera.
- Free-running gameplay — smoke test only, never a comparison.

### Storage

A 3840×2160 scRGB frame is 66 MB, and half-float data deflates at roughly 1.2:1, so payloads
stay out of version control (`HdrCorpus/` is gitignored). `HdrFrameCorpus.WriteManifest` writes
a `manifest.json` of headers and payload hashes, which *is* committed — so a corpus that changed
underneath you is detectable.


## The offline runner

`ShareX.HdrEval` replays a corpus through every mode and exposure, measures each result against
the frame's own pre-tonemap reference, and writes `metrics.csv`, a `report.html` contact sheet,
full-resolution PNGs and a `debug.log`.

```bash
dotnet run --project ShareX.HdrEval -- --corpus HdrCorpus
```

No corpus yet? Generate synthetic test patterns to calibrate thresholds and smoke-test the tool:

```bash
dotnet run --project ShareX.HdrEval -- --synthesize HdrCorpus/synth
```

These are known-value targets, not a substitute for real captures: paper white must come back at
255, an sRGB palette must round-trip to its own bytes, a shallow gradient must not band, and
`autohdr-like` is a tuned positive control that must classify as AutoHDR rather than Desktop.

Useful invocations:

```bash
dotnet run --project ShareX.HdrEval -- --corpus HdrCorpus --exposures 0.7,0.85,1.0,1.15,1.3 --no-png --no-thumbs
```

```bash
dotnet run --project ShareX.HdrEval -- --corpus HdrCorpus --baseline HdrCorpus/eval/metrics.csv
```

`--help` lists everything. `--fail-on-flags` exits non-zero, for wiring into CI later.

### What the metrics mean

The reference is the frame decoded to linear BT.709 with 1.0 = SDR white, which is exactly the
space the curve consumes. Two design points worth knowing:

**Bands key on the maximum channel, never luminance.** Clipping is per-channel: a saturated red at
luminance 0.21 already has R = 1.0 and must clip. Banding this on luminance would call correct
behaviour a defect.

**Only some metrics have absolute meaning.** These are thresholded, and a flag means the output is
wrong:

| Metric | Why it is absolute |
| --- | --- |
| `round trip` | In-range content must survive unchanged. The scenario-1 ground truth. Desktop only. |
| `blew out` | Content with every channel ≤ 0.90 that hit 255 anyway. |
| `crushed` | Reference had signal, output is pure black. |
| `hue` / `chroma` | In-range colour should not rotate or desaturate. |
| `code gap` | Compared against the reference's own gap, so discrete source content is not mistaken for banding. |
| `mono` | Brighter input producing darker output is wrong regardless of intent. |
| `paper white` | Known SDR white must land at 253-255. Desktop only. |

These are **reported and diffed against a baseline, never thresholded**, because they measure the
tonemapper doing its job or depend on content: `clip hi` and `hl clip` (how much of a frame sits
above range is a property of the frame), `contrast P10` (compressing 10× headroom into SDR reduces
log-contrast by definition), `hue hi` / `chroma hi` (the roll-off's trade to make), and `mid-tone`
outside the Desktop scenario.

Thresholds are starting points. Calibrate them once against outputs you have already judged by eye.
And none of this measures preference — that still needs blind A/B.


## Reading the report

Open it in a **real browser**, not a preview pane: the contact sheet uses relative image paths, and
the HDR reference needs a browser to render as HDR.

### The one rule

**Compare down a column within one frame. Never compare a number across frames.** Every metric is
relative to that frame's own reference, so "hue 3.9° on frame A" against "hue 0.9° on frame B" means
nothing. "Desktop 3.9° against Filmic 9.4° on the same frame" is the comparison that means
something.

### Three things it is for

**1. Regression — the daily loop.** Change a curve, re-run with `--baseline`, read *only* the
"Changes against baseline" section. Empty means nothing moved. Most of the value is here.

**2. Mode comparison.** Read a frame's table top to bottom to see how each curve treats identical
input, with the HDR original at the head of the contact sheet.

**3. Diagnosis — turning a reaction into a number.** Look at an image, notice something, then find
the column that corresponds:

| What you would say | Column |
| --- | --- |
| "washed out, flat" | `contrast P10`, `chroma` |
| "too dark" / "too bright" | `mid-tone`, `paper white` |
| "highlights look plasticky, blown" | `hl clip` |
| "the UI or text is blown out" | `blew out`, `round trip` |
| "colours are off" | `hue`, `chroma` |
| "banding in the gradient" | `code gap` |
| "shadow detail is gone" | `crushed` |
| "something inverted oddly" | `mono` |

Work in that direction — eye first, then number. Once you have matched a few, you know what
"chroma 0.85" looks like, and only then do the numbers work as a proxy for your judgement. That is
also how the thresholds get calibrated honestly.

### The three originals

Each contact sheet leads with three views of the same untonemapped frame, outlined in blue.

**`HDR original`** is written by `HdrPngWriter` — the same writer behind the app's HDR master PNG
feature — so it carries cICP (9, 16, 0, 1): BT.2020 primaries, PQ transfer, full range. Chrome shows
that as genuine HDR on an HDR display. Two consequences: on an SDR display, or with Windows HDR off,
the browser applies **its own** tonemap (a useful comparison, but not the original); and because it
is the shipping writer, a reference that looks wrong means the app's HDR PNG export is wrong too.

That file is also **unviewable in anything that ignores cICP** — paste it into a chat client or a
basic image viewer and PQ/BT.2020 codes get read as sRGB, which looks washed-out and desaturated in a
very convincing way. That is the viewer, not your capture. Hence the other two:

**`original · in-range`** hard-clips at paper white: exactly what was already inside SDR, with
everything above it blown white by construction. Answers "did the capture preserve what was already
in range?"

**`original · full range`** scales linearly so the frame's peak lands at 1.0: uniformly darker than
intended, but nothing clips, so it shows the detail a curve has to fit into SDR. Answers "what was
there to lose?"

Both are fixed transforms with no free parameters — not candidate curves, so they cannot flatter or
penalise any mode — and both render identically everywhere. `--no-reference` skips all three.

## Debug capture from inside the app

`HotkeyType.HDRDebugCapture` — "HDR debug capture (dump + report)" in the Tools category of the
hotkey list — does the whole loop in one keypress: captures the full screen through the normal HDR
path, records the raw DXGI frame, runs `ShareX.HdrEval` over it, and opens the report.

Output goes to `<personal folder>\HDRDebug\<label>_<scenario>_<timestamp>\` — **one folder per app
launch**. Every keypress adds a frame and re-runs the evaluation, so the session's single report grows
to cover every capture taken since launch.

Re-running is cheap because it is incremental: frames already measured by the same build are reused
from `metrics.csv` and only the new frame is processed, so a press costs one frame whether it is the
first or the twentieth. Concurrent presses are serialised, so two quick captures cannot race on the
merge.

Rebuilding `ShareX.ScreenCaptureLib` invalidates that reuse — a changed tonemap always re-measures
everything rather than mixing old and new numbers in one report. Note that .NET builds are
deterministic, so recompiling *without* a source change keeps the same identity and correctly reuses.

So the loop is: set the env vars, launch, capture as many scenes as you like, quit, repeat for the
next environment. Each session ends up with its own self-contained report and the whole `HDRDebug`
folder is shareable as-is.

Each frame gets a `<frame>__sharex-output.png` beside it: what the app itself produced, for
eyeballing against what the runner reconstructs.

### Tag as you go

Scenario selects which thresholds apply, and Desktop is the only one with an exact ground truth — so
an untagged desktop capture silently loses its round-trip check, the strongest measurement in the
suite. The hotkey honours `SHAREX_HDR_DUMP_SCENARIO` and `SHAREX_HDR_DUMP_LABEL`, so set them before
launching:

```bash
SHAREX_HDR_DUMP_SCENARIO=Desktop SHAREX_HDR_DUMP_LABEL=explorer-dark ./ShareX/bin/Debug/ShareX.exe
```

Untagged frames land as `Unknown` (loosest thresholds, no round-trip check) and say so in their
notes.

### Fixing tags afterwards

You will forget, so `--retag` rewrites the scenario on frames already captured. It is a **dry run
until `--yes`**, so a bad filter cannot quietly rewrite a corpus:

```bash
dotnet run --project ShareX.HdrEval -- --corpus "%USERPROFILE%\Documents\ShareX\HDRDebug\2026-09-02" --filter 14-32 --retag Desktop
```

Add `--yes` (and optionally `--retag-label <text>`) to apply. Only metadata changes; the payload
hash still has to verify afterwards, and the rewrite is checked before it replaces the original, so
a failed retag cannot destroy a captured frame.

It deliberately does **not** go through `CaptureBase`. That would run the whole after-capture
pipeline — saving to your output folder, uploading to your configured destination, writing history.
A diagnostic action must not upload anything, so it drives `Screenshot` directly and keeps
everything inside its own session folder.

Notes:

- It finds `ShareX.HdrEval.exe` next to the app, then in the sibling project output a development
  checkout produces. If it is missing, the frames are still written and the session folder opens.
- It saves and restores whatever `HdrFrameDump` had armed, so triggering it will not clobber an
  in-progress corpus collection.
- "recorded no frames" means the HDR pipeline never ran — almost always an SDR display or HDR
  capture switched off.
- The enum member carries a `[Description]` rather than a resource key.
  `GetLocalizedDescription` falls back to it when the key is absent, so this dev-only entry needs no
  edits across the ~25 translation catalogues.

## Sharing a session

Full-resolution PNGs dominate the size: five modes plus an HDR reference for a 4K frame runs to
roughly 100 MB *per frame*, so a few sessions reach a gigabyte. `--png-divisor` shrinks every image
by n in each direction — n=5 turns 3840×2160 into 768×432, which is 25× fewer pixels and still
plenty to judge banding, hue and clipping:

```bash
dotnet run --project ShareX.HdrEval -- --corpus "<session>" --out "<session>\share" --png-divisor 5
```

This keeps everything — contact sheet, all three originals, clickable full images, metrics CSV,
report — just smaller. `--no-png` is the more aggressive option if you only need the contact sheet.

Keep the `.hdrframe` files too if the other side needs to re-run anything; they are the only
irreplaceable part, and they are what makes a shared session reproducible rather than just viewable.

## Findings from the first run

Two things the runner surfaced immediately on synthetic patterns:

**`WindowsWIC` mode is broken on HDR10 outputs.** `IWICBitmapToneMapper.InitializeForSdrTarget`
rejects `Format32bppR10G10B10A2HDR10` with `WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT`. It is a
format-level refusal, not a data problem: all fp16 frames succeed. On a display whose DXGI output
colour space is HDR10, `CaptureOutputHdrWic` returns false, `anyOutputCaptured` stays false, and the
whole HDR capture returns null. Either convert HDR10 to fp16 before handing it to WIC, or hide the
mode when the output is HDR10.

**Exposure defeats the tonemap ceiling** (the issue below, now quantified). On the 10× ramp,
hard-clipping of out-of-range pixels rises from 12.5% at exposure 0.70 to 47.6% at 1.30. A 30%
exposure increase nearly quadruples clipping, because `CreateCurve` derives `maxInputNorm` from
pre-exposure stats while `Map` applies exposure *before* `ApplyPqLut` clamps with
`MathF.Min(lum, maxInputNorm)`.

## Known issue worth measuring

`HdrTonemap.CreateCurve` derives `maxInputNorm` from pre-exposure stats, but
`HdrTonemapCurve.Map` applies `exposure` *before* `ApplyPqLut`, which clamps with
`MathF.Min(lum, maxInputNorm)`. At exposure 1.30 everything above `peakNorm / 1.3` collapses
onto the LUT's top entry — a hard clip across the top of the highlight range, exactly where a
single image hides it. Either fold exposure into `peakNorm` when building the curve, or apply
it after. Measured on the synthetic 10× ramp: 12.5% of out-of-range pixels hard-clip at exposure
0.70, rising to 47.6% at 1.30. Re-run
`--exposures 0.7,0.85,1.0,1.15,1.3` after any fix and watch the `hl clip` column flatten.

## Still to build

- Golden regression test over the corpus with committed metric bounds, wired to
  `--fail-on-flags`.
- Blind A/B tool: hidden labels, randomized left/right, Bradley-Terry ranking per scenario.
  Repeat ~15% of pairs to measure self-consistency.
- External anchoring: same scenes through Special K, Game Bar, NVIDIA app, and the existing
  `WindowsWIC` mode, scored with the identical metric suite.
