<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Display: DeskNext; retain internal `DeskNest.*` names/paths. Follow `PLAN.md` P0–P7; mark phases only after gates pass.
- Stack: .NET 10/Avalonia, C# Core/Platform/Inference, Pogget C++. Execution arrangements: `PLAN.md`. One writer/worktree; serialize builds; disable shared compilation.
- P3 remains incomplete. `FileSystemVolume` guards preparation and forward/reverse moves; reject reparse ancestors and unsupported/cross-volume moves. Windows NTFS receipts require handle-derived volume serial/128-bit ID plus hash/size/time; legacy hashes cannot authorize rollback/undo.
- `WorkspaceStore.OrganizationGate` serializes through cleanup. Retain journals until metadata and `CommittedTransactionId` persist together; checkpoint/verify backup before acknowledgement. Cut ID/path/source-space must agree; check revision under both gates.
- Preserve corruption evidence. Rollback checkpoints `Restored` plus `.rollback-started`; delete backup, primary, then fence. Uncheckpointed renames require reconciliation. Recovery inspection is read-only; retry reuses its owned store.
- Sorted topology: 100,000 entries/depth 128. Legacy null topology loads but cannot authorize undo/recovery.
- Managed deletion uses app recovery storage, not OS trash. Open/reveal/preview rejects relative/URI/reparse paths; previews are bounded. Production Copy/copy-Paste stays gated. Unsupported streams fail closed. Retain intents; never auto-adopt/delete unconfirmed copies. Validate native ancestry before creating parents.
- Catalog existing in-root items; external import stays gated. Clipboard is UI-thread-only. Core creates managed folders, never missing mapped roots.
- Release singleton subscriptions with UI owners.
- Monitor Desktop and selected folders: whitelist, baseline, health, pause/rescan, target binding. No takeover before S01–S08.
- CI: physical macOS temp paths, hosted-only aliases, terminal probe JSON. Skips aren't native evidence.
- Preserve provenance, twelve-locale parity, F01–F13/inference evidence. Local preview never moves files. Python/models stay in ignored `artifacts/`; synthetic images cannot prove desktop compositing.
<!-- pi-agents-md:end -->
