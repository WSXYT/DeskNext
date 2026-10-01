<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Display name: DeskNext; internal `DeskNest.*` names and paths remain. Follow `PLAN.md` P0–P7; mark phases only after gates pass.
- Stack: .NET 10, Avalonia UI, C# Core/Platform/Inference, Pogget C++ bridge. Execution arrangements live in `PLAN.md`. One writer per worktree; serialize builds and disable shared compilation.
- P3 remains incomplete. `FileSystemVolume` guards preparation and forward/reverse moves; reject reparse ancestors and unsupported/cross-volume operations. Windows NTFS receipts require handle-derived volume serial/128-bit ID plus hash/size/time; legacy hash-only evidence cannot authorize rollback/undo.
- `WorkspaceStore.OrganizationGate` serializes coordinators through cleanup; retain journals until metadata and `CommittedTransactionId` persist together; checkpoint/verify backup before acknowledgement. Cut IDs/path/source-space must agree; check expected revision under organization and metadata gates.
- Preserve corruption evidence. Rollback uses per-item `Restored` checkpoints and `.rollback-started`; delete backup, primary, then fence. Uncheckpointed renames require manual reconciliation. Recovery inspection is read-only; retry reuses its owned store rather than reopening its lock.
- Directory topology is sorted and bounded to 100,000 entries/depth 128. Legacy null-topology history loads but cannot authorize undo/recovery.
- Managed deletion uses app recovery storage, not OS trash. Open/reveal/preview rejects relative/URI/reparse paths; previews are bounded. Production Copy/copy-Paste stays gated pending complete acceptance. Unsupported copy streams fail closed. Intents survive restart; never auto-adopt or delete unconfirmed copies. Validate native destination ancestry before creating missing parents.
- Monitoring supports Desktop plus selected folders with whitelist, baseline, health, pause/rescan, target binding. No desktop takeover before S01–S08.
- Preserve provenance, twelve-locale parity, F01–F13/inference evidence and screenshot honesty. Python/models stay in ignored `artifacts/`; synthetic images cannot prove native desktop compositing.
<!-- pi-agents-md:end -->
