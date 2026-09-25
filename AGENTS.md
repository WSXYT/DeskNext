<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Product: 栖格 · DeskNest. Follow `PLAN.md` in strict P0–P7 order; mark Plannotator steps only after their gates pass. P1 probes passed; P2 automated UI acceptance is complete at commit `ce142a7`; physical 150%/200% DPI, IME, DWM material and hardware accessibility remain explicitly unverified.
- DeskBox is the primary visual reference: borrow material layering and compact utility composition without copying branding/layout/assets. Do not equate offscreen bitmaps with native screenshots or command execution with key events.
- Preserve DeskBox/Pogget/Laya provenance in `docs/upstream.md`; development Python/models stay in ignored `artifacts/`.
- UI views/themes/text/diagnostics belong to Gemini-3.8-flash-high; parent owns contracts, Core integration and acceptance. Use serial builds and independent safety review.
- Core alone owns persistence/file operations. P2 remains metadata-only. P3 supports fail-closed regular-file and same-volume directory transactions, sorted manifest-backed identity-checked undo, startup recovery, multi-root observe-only monitoring, bounded deduplicated events, managed rename and mapped-reference removal; cross-volume directory copy remains disabled.
- Desktop takeover, automatic watcher-driven moves, inference-triggered moves and Flow activation remain forbidden until all transaction fault-injection, recovery and S01–S08 gates pass. Managed delete-to-trash and platform open/preview/clipboard providers are still unbound.
- End-user inference requires no Python; P4 quality/install/GPU gates remain in `docs/planning/inference-validation.md`. Use resource keys for 12-language UI and track F01–F13 honestly.
<!-- pi-agents-md:end -->
