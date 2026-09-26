<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Product: 栖格 · DeskNest. Follow `PLAN.md` P0–P7; mark steps only after gates pass. P1 probes and P2 automated UI acceptance passed; physical multi-DPI, IME, material fidelity and accessibility remain unverified.
- UI views/themes/text/diagnostics belong to Gemini-3.8-flash-high; parent owns Core, integration and acceptance. Keep one writer per worktree, serial builds and independent safety review. DeskBox is the primary visual reference; never equate offscreen images with native screenshots.
- Core owns persistence/file operations. P3 remains incomplete: journaled file/directory moves, content-manifest undo, managed rename and mapped-reference removal exist. Content hashes are not native object IDs or complete directory snapshots.
- `FileSystemVolume` guards preparation and forward/reverse moves. Unsupported mounts fail closed; cross-volume file and directory moves are disabled. Linux mount fixtures do not prove native Linux/macOS runtime safety.
- Recovery checks primary, backup and `.recovery-required`; preserve corrupt evidence. Unacknowledged renames require manual reconciliation. Await durable writes before releasing locks; remove backup before primary.
- S08 metadata commit coordination and rollback restartability remain open. No desktop takeover, watcher/model moves or Flow activation before S01–S08 gates. Managed deletion and platform open/preview/clipboard remain gated.
- Preserve provenance in `docs/upstream.md`; Python/models stay in ignored `artifacts/`. End-user inference needs no Python. Maintain 12-locale parity, honest F01–F13 tracking, and P4 gates in `docs/planning/inference-validation.md`.
<!-- pi-agents-md:end -->
