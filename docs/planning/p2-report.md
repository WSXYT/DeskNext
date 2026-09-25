# P2 metadata studio verification (in progress)

P2 is **not a live organizer**: no desktop watcher, automated file move, ONNX worker integration or Jev API invocation is enabled. The P2 decision request/reply contract only evaluates metadata; file action authorization remains in later phases. F01–F13 end-to-end entries in `feature-tracker.md` remain unverified.

## Verified at commit `acd5a6e`

- Core `WorkspaceState`/`WorkspaceStore`: versioned spaces, files, pending triage, settings and model state; resilient atomic snapshot persistence with lock and corruption guard. Core unit tests: **20 passed** (including decision identity and duplicate-category regressions); Inference unit tests: **4 passed**. Independent targeted decision-boundary reviewer found no new defect after the pending-ID and parsed-GUID corrections.
- Avalonia metadata UI: 5-step resumable OOBE, managed/mapped space metadata, triage drawer, capsule manual-path registration, in-place state refresh and startup error guards. No physical file movement. Headless Windows self-contained `win-x64` publish/smoke passed 10 steps, including native `dn_layout` loading, 12 localization dictionaries (201 keys each), and real StudioView ListBox virtualization (7 realized containers for 10,000 metadata entries). This is a viewport measurement, **not** a native high-DPI test.
- [Three-platform CI run 36124350057](https://github.com/WSXYT/desknest-p1-probes/actions/runs/36124350057), branch `p2-probes` on a temporary private repository at `acd5a6e`, passed Windows 2022 x64, Ubuntu 24.04 x64, and macOS 15 ARM64. Each runner built the C++20 bridge, ran native CTest and .NET Core/Inference tests, published its RID-specific self-contained UI, then executed headless UI smoke with opt-in native ABI load. These tests establish headless runnability, **not** a complete desktop interaction or visual-appearance acceptance.

## Remaining P2 acceptance work

- Explicit long-text, RTL, 1280×720 and 1600×900 layout review, keyboard navigation, real window startup on each supported OS, and native 100%/150%/200% DPI evidence; automated large-window headless layout sizing is not a native display scaling test. A Gemini-3.8-flash-high UI validation pass is in progress. Unavailable physical-display evidence must be marked unverified rather than fabricated.
- Check final P2 plan gate before starting P3. In particular, a documented manual-path capsule must not be reported as OS drag-and-drop; model selection UI must not be reported as an active inference engine.
