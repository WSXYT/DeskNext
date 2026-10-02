<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Display: DeskNext; retain `DeskNest.*` internals/paths. Follow `PLAN.md` P0–P7; mark phases only after gates pass.
- Stack: .NET 10/Avalonia; C# Core/Platform/Inference; Pogget C++. Execution: `PLAN.md`. One writer/worktree; serialize builds; disable shared compilation.
- P3 incomplete. `FileSystemVolume`: guard preparation/forward/reverse; reject reparse ancestors and unsupported/cross-volume moves. Windows: handle-relative/no-replace; NTFS: volume/128-bit ID+hash/size/time and root/child directory IDs. Undo preparation requires recorded evidence, never legacy hashes.
- `WorkspaceStore.OrganizationGate` serializes through cleanup. Retain journals until metadata and `CommittedTransactionId` persist together; checkpoint/verify backup before acknowledgement. Cut ID/path/source-space must agree; check revision under both gates.
- Preserve corruption evidence. Rollback checkpoints `Restored` plus `.rollback-started`; delete backup, primary, then fence. Uncheckpointed renames require reconciliation. Read-only inspection; retry reuses store; unenrolled-copy archiving requires consent.
- Sorted topology: 100,000 entries/depth 128. Legacy null topology loads but cannot authorize undo/recovery. Handles don't freeze contents.
- Managed deletion uses app recovery storage. Open/reveal/preview rejects relative/URI/reparse paths; previews are bounded. Production Copy/copy-Paste stays gated. Unsupported streams fail closed. Retain intents; never auto-adopt/delete unconfirmed copies. Validate ancestry before creating parents.
- Space drops catalog in-root; confirmed Windows imports retain origins for undo. Outgoing drags: file references/Copy only, never delete sources. Clipboard: UI thread. Core creates managed folders, never missing mapped roots.
- Release singleton subscriptions; space windows borrow the workbench.
- Monitor Desktop/selected folders: whitelist, baseline, health, pause/rescan, target binding. No takeover before S01–S08.
- CI: physical macOS temp paths, hosted-only aliases, terminal probe JSON. Skips aren't evidence.
- Preserve provenance, twelve-locale parity, F01–F13/inference evidence. Local preview never moves files. Python/models stay in ignored `artifacts/`; synthetic images cannot prove desktop compositing.
<!-- pi-agents-md:end -->
