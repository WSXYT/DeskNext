<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Product: 栖格 · DeskNest. Follow `PLAN.md` P0–P7; mark phases only after gates pass. P1 probes/P2 automated acceptance passed; physical multi-DPI, IME, materials and accessibility remain unverified.
- Delegate UI views/themes/text/diagnostics to Gemini-3.8-flash-high; critical safety reviews use gpt-6-astra per PLAN.md. Parent owns Core/acceptance. One writer per worktree; serialize builds. DeskBox remains the visual reference; offscreen renders are not native screenshots.
- P3 is incomplete. `FileSystemVolume` guards preparation and forward/reverse moves; unsupported/cross-volume operations fail closed. Windows tests/Linux parser fixtures do not prove native Linux/macOS safety.
- `WorkspaceStore.OrganizationGate` serializes coordinators through cleanup; disposal waits. Retain journals until metadata and `CommittedTransactionId` persist together, then checkpoint/verify the workspace backup before acknowledgement, including startup.
- Preserve primary/backup/corruption evidence. Never abandon durable writes on cancellation. Rollback uses per-item `Restored` checkpoints and a transaction-bound `.rollback-started` fence. Delete backup, primary, then fence. Uncheckpointed renames require manual reconciliation.
- Directory receipts include sorted files/subdirectories, bounded to 100,000 combined entries/depth 128. Null-topology legacy history retains prior bounded metadata validation for loading; missing topology never authorizes undo/recovery. This correction passed bounded independent review, not P3 acceptance.
- Native object IDs, path races, long-scan cancellation and power-loss guarantees remain open. No desktop takeover, watcher/model moves or Flow activation before S01–S08 gates; platform actions/managed deletion remain gated.
- Preserve upstream provenance, 12-locale parity and F01–F13/inference-validation evidence. Python/models stay in ignored `artifacts/`; end-user inference requires no Python.
<!-- pi-agents-md:end -->
