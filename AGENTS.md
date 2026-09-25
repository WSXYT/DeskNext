<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Product: 栖格 · DeskNest. Follow `PLAN.md` in strict P0–P7 order; mark Plannotator steps only after their gates pass. P1 probes passed; see `docs/planning/p1-report.md`.
- P2 unit/headless/native startup passed Windows x64, Ubuntu x64 and macOS ARM64 in [CI 36130732953](https://github.com/WSXYT/desknest-p1-probes/actions/runs/36130732953). Linux uses Xvfb. P2 remains open for physical keyboard/IME, visual UX and DPI acceptance; no P3 release claim until P2 closes.
- DeskBox is the primary visual reference: borrow material layering and compact utility composition without copying branding/layout/assets. Do not equate offscreen bitmaps with native screenshots or command execution with key events.
- Preserve DeskBox/Pogget/Laya provenance in `docs/upstream.md`; development Python/models stay in ignored `artifacts/`.
- UI views/themes/text/diagnostics belong to Gemini-3.8-flash-high; parent owns contracts, integration and acceptance. Use serial builds and independent safety review.
- Core alone owns persistence/file operations. P2 decisions are metadata-only and fail-closed. P3 transaction work must keep desktop takeover disabled until recovery/fault-injection gates pass.
- P3 transaction core now supports regular-file moves and same-volume directory manifests with reparse-point rejection and identity-checked recovery. Cross-volume copy, full watcher integration and desktop takeover remain unimplemented until tested.
- End-user inference requires no Python; P4 quality/install/GPU gates remain in `docs/planning/inference-validation.md`. Use resource keys for 12-language UI and track F01–F13 honestly.
<!-- pi-agents-md:end -->
