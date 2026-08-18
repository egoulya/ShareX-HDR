# Plan: HDR quality + master PNG

> **For agentic workers:** COMPLETE ALL TASKS. Mark checkboxes `[x]`.

**Goal:** Ship Track A+B from the approved design.

**Architecture:** Harden DXGI session lifecycle and concurrency in `Screenshot_HDR*`; scope even-size at record manager; add optional PQ PNG companion via capture + WorkerTask save; refresh README.

**Tech stack:** C# / .NET 10 WinForms+Avalonia ShareX fork, DXGI Desktop Duplication, System.Drawing PNG + manual chunk injection.

---

### Task 1: Recording handoff + NOT_CURRENTLY_AVAILABLE logging

**Files:**
- Modify `ShareX.ScreenCaptureLib/Screenshot_HDR.cs`
- Modify `ShareX.ScreenCaptureLib/ScreenRecording/ScreenRecorder.cs`

- [ ] Add `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE`
- [ ] Log distinctly in `TryCreateDuplication` (and recording path if same pattern)
- [ ] `WarmHdrCapture()` in HDR pipe `finally`

### Task 2: Semaphore + even-size scoping

**Files:**
- Modify `Screenshot_HDR.cs` (`CaptureRectangleHDR`)
- Modify `FFmpegOptions.cs`, `ScreenRecordManager.cs`

- [ ] `SemaphoreSlim` around capture
- [ ] Scope even size to two-pass or HDR recording

### Task 3: Test comments + README

**Files:**
- Modify `ShareX.ScreenCaptureLib.Tests/HdrPipelineTests.cs`
- Replace `README.md`

### Task 4: HDR master setting + UI

**Files:**
- Modify `TaskSettings.cs`, `TaskSettingsPageBuilder.cs`

### Task 5: HDR master encode + save

**Files:**
- Modify capture path to produce PQ buffer when enabled
- Add PNG cICP/cLLI/mDCV writer helper
- Modify `WorkerTask` / `TaskHelpers` to write `*_hdr.png`
- Keep clipboard tonemapped

### Task 6: Build + test

- [ ] `dotnet build` Release x64
- [ ] `dotnet test` ScreenCaptureLib.Tests
