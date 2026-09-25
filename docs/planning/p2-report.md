# P2 metadata studio verification (in progress)

P2 is **not a live organizer**: no desktop watcher, automated file move, ONNX worker integration or Jev API invocation is enabled. The P2 decision request/reply contract only evaluates metadata; file action authorization remains in later phases. F01–F13 end-to-end entries in `feature-tracker.md` remain unverified.

## Verified at commit `acd5a6e`

- Core `WorkspaceState`/`WorkspaceStore`: versioned spaces, files, pending triage, settings and model state; resilient atomic snapshot persistence with lock and corruption guard. Core unit tests: **20 passed** (including decision identity and duplicate-category regressions); Inference unit tests: **4 passed**. Independent targeted decision-boundary reviewer found no new defect after the pending-ID and parsed-GUID corrections.
- Avalonia metadata UI: 5-step resumable OOBE, managed/mapped space metadata, triage drawer, capsule manual-path registration, in-place state refresh and startup error guards. No physical file movement. Headless Windows self-contained `win-x64` publish/smoke passed 10 steps, including native `dn_layout` loading, 12 localization dictionaries (201 keys each), and real StudioView ListBox virtualization (7 realized containers for 10,000 metadata entries). This is a viewport measurement, **not** a native high-DPI test.
- [Three-platform CI run 36124350057](https://github.com/WSXYT/desknest-p1-probes/actions/runs/36124350057), branch `p2-probes` on a temporary private repository at `acd5a6e`, passed Windows 2022 x64, Ubuntu 24.04 x64, and macOS 15 ARM64. Each runner built the C++20 bridge, ran native CTest and .NET Core/Inference tests, published its RID-specific self-contained UI, then executed headless UI smoke with opt-in native ABI load. These tests establish headless runnability, **not** a complete desktop interaction or visual-appearance acceptance.

## Native-backend startup at commit `2f32f2c`

[CI run 36130732953](https://github.com/WSXYT/desknest-p1-probes/actions/runs/36130732953) passed all three platforms, including the earlier unit/headless/native-bridge tests and the new opt-in `--native-window-smoke`. The latter uses an isolated temporary WorkspaceStore, observes `Opened`, and checks a nonzero platform handle and positive window bounds before exiting.

| Environment | Observed bounds | Avalonia RenderScaling | Evidence scope |
| --- | --- | --- | --- |
| Windows 2022 x64 CI | 1028×749 | 1.0 | Native Windows backend startup |
| Ubuntu 24.04 x64 CI | 1280×760 | 1.0 | X11 window under Xvfb, not a physical desktop/display |
| macOS 15 ARM64 CI | 1024×653 | 1.0 | Native macOS backend startup |
| Windows development host | 1280×760 | 2.5 | Parent reran native startup successfully |

These observations do not prove a presented frame, correct clipping, physical keyboard navigation, or the 100%/150%/200% DPI matrix. Dispatcher render priority is not a frame-presentation acknowledgment. The in-process watchdog depends on dispatcher responsiveness; CI step timeouts provide the outer bound. The prior claim that the development host was at 100% was unsupported and is superseded by the observed Avalonia scale of 2.5, not by independent physical-display measurement.

## Remaining P2 acceptance work

- Explicit long-text, RTL, 1280×720 and 1600×900 visual review, actual keyboard input/focus traversal, and native 100%/150%/200% DPI evidence. Long-text tests currently prove no layout exception, not lack of text clipping; hotkey configuration and command invocation do not prove key routing.
- Parent inspected the opt-in offscreen PNG and found capture cropping: the window was arranged at 2560×1520 before rendering into a 1280×760 bitmap. This image is not valid target-viewport acceptance evidence. Gemini is correcting capture dimensions and adding focused key-event tests. Offscreen bitmap output must never be described as a physical-window screenshot.
- Unavailable physical-display evidence remains unverified; native-backend CI startup is not manual desktop UX acceptance.
- Check final P2 plan gate before starting P3. In particular, a documented manual-path capsule must not be reported as OS drag-and-drop; model selection UI must not be reported as an active inference engine.
