# P2 first usable native version verification (automated gate complete; physical matrix explicit)

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

## Keyboard and offscreen capture follow-up at `daca4cd`

Parent reran the Release build, all 20 Core and 4 Inference tests, and the headless smoke with native ABI loading plus opt-in PNG output. The smoke passed in 2797 ms with 7 realized containers for 10,000 rows. An active LSP probe reported no errors in the changed smoke runner.

- Headless `KeyPress`/`KeyRelease` now exercise Escape dismissal, Enter submission of a new space, and a Tab focus transition. This replaces direct command invocation; it does not verify physical keyboard/IME behavior or all navigation paths.
- The previous capture cropping defect is corrected by setting each window's target dimensions before rendering. The parent inspected `artifacts/p2-visual-verified/studio-1280x720-ar.png` and `oobe-1280x720-de.png`: the sampled views fit the image and Arabic Studio navigation is mirrored. These are offscreen component fixtures, not full MainWindow or physical-window screenshots, and not exhaustive visual acceptance.
- The capture currently covers OOBE step 1 at 1280×720 and Studio at 1280×720/1600×900 in en/de/ar. It does not cover every wizard step, tab, theme or long-text state. German OOBE fixture still selects English in its language field and shows raw `Light`; fixture settings and localized theme labels need follow-up. Temporary paths still contain the host username because the OS temp directory is beneath the profile; do not call these anonymous screenshots or share them publicly without sanitization.

## Phase 1 visual pass at working tree after `daca4cd`

The first visual pass was rejected after direct PNG inspection because it still read as a generic white administration form. The second pass was reviewed against the DeskBox Mica/Acrylic screenshot and now provides a more appropriate reference-led baseline:

- Dark and light themes use layered graphite/slate surfaces, a thin chrome divider, a compact navigation rail, and an open file canvas. Dense file text stays on opaque surfaces; the offscreen renderer does not prove DWM Mica/Acrylic.
- The studio rail uses stable space rows with managed/mapped edge markers, quiet filters, and fixed path/count spacing. File and empty-state marks use local Avalonia vector geometries rather than emoji or textual `[FILE]`/`[DIR]` badges.
- OOBE step 1 has a restrained managed-space/mapped-space architecture anchor instead of a draft slogan card. The existing five-tab bindings, localization contract, ListBox virtualization and metadata-only boundary remain intact.
- Parent inspected dark/light Studio, dark OOBE, and Arabic Studio fixtures under `artifacts/p2-visual-industrial`. This is visual regression evidence for in-memory offscreen composition only, not physical-window, DWM, ClearType or DPI evidence.

The final cleanup removed the remaining decorative `✦` from all 12 dictionaries and replaced empty-state glyphs with vector marks. Release build completed with 0 warnings/0 errors; Core tests passed 20/20; Inference tests passed 4/4; the headless smoke with native ABI and opt-in dark/light offscreen fixtures passed all 10 steps, 12-language parity, RTL path isolation, injected Escape/Enter/Tab checks and 10 realized containers for 10,000 metadata rows.

## Final P2 automated acceptance at `ce142a7`

- Release build completed with 0 warnings and 0 errors; `DeskNest.Core.Tests` passed 36/36 and `DeskNest.Inference.Tests` passed 4/4.
- `--headless-smoke` passed in 3108 ms with 226-key exact parity across all 12 locales, RTL path isolation, 1280x720/1600x900 long-text bounds, real StudioView virtualization of 10,000 metadata rows, genuine Escape/Enter/Tab/Shift+Tab/arrow/Space input injection, drag/drop routing, operation lifecycle rendering, identity-checked manual move and undo callback boundaries.
- `--native-window-smoke` has been verified on the Windows development host with a valid HWND, 1280x760 bounds, and observed RenderScaling 2.50 (250%). CI evidence covers native startup at scale 1.0 on Windows, Linux/Xvfb, and macOS.
- The P2 implementation/automated acceptance gate is complete. P2 does not claim physical 150%/200% display matrices, physical IME behavior, DWM material fidelity, or hardware accessibility behavior; those remain explicit deployment verification items because this host/CI setup cannot safely change display settings.
- The P2 UI remains metadata-only for file enrollment. Physical moves, monitoring takeover, model execution, and Jev calls are not enabled by this phase.
