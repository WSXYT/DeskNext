<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Product: 栖格 · DeskNest. Follow `PLAN.md` in strict P0–P7 order; mark each Plannotator step only after its entire gate passes. P1 probes passed; see `docs/planning/p1-report.md` for evidence and historical native-crash residual risk.
- P2 unit/headless/native-backend startup passed Windows x64, Ubuntu x64 and macOS ARM64 in [CI 36130732953](https://github.com/WSXYT/desknest-p1-probes/actions/runs/36130732953). Linux uses Xvfb, not a physical display. P2 stays **open** for physical keyboard/IME, complete visual UX and DPI acceptance. See `docs/planning/p2-report.md`; no P3 until P2 closes.
- DeskBox is the primary visual reference for the current UI pass: borrow material layering, compact desktop utility composition and open file surfaces without copying branding/layout/assets. Do not equate RenderTargetBitmap with native screenshots, dispatcher priority with presented frames, command execution with key events, or window bounds with unclipped text.
- Preserve DeskBox/Pogget/Laya provenance and adaptations in `docs/upstream.md` and adjacent source notices. Development-only Python/models stay in ignored `artifacts/`.
- UI views/themes/text/diagnostics belong to Gemini-3.8-flash-high; parent owns contracts, integration and acceptance. Serial builds on this RAM-constrained host; one writer per area; independent review for safety-critical changes.
- Core alone owns persistence/file operations. P2 decisions are metadata-only, fail-closed, pending-ID-bound; no desktop moves before P3 recovery/fault injection. Native bridge/model worker cannot independently move files.
- End-user inference requires no Python. P1 Windows multilingual CPU parity covers three requests only; P4 quality/install/GPU gates remain per `docs/planning/inference-validation.md`.
- Use resource keys for twelve-language UI/RTL/keyboard support. GPL-3.0-only permits paid redistribution; retain notices/source. Track F01–F13 honestly in `docs/planning/feature-tracker.md`.
<!-- pi-agents-md:end -->
