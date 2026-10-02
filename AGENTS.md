<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Display DeskNext; retain `DeskNest.*` paths/internals. Follow `PLAN.md` P0–P7; mark only passed gates.
- .NET 10/Avalonia; C# Core/Platform/Inference; Pogget C++. Execution arrangements: `PLAN.md`. One writer/worktree; serialize builds; disable shared compilation.
- P3 incomplete. `FileSystemVolume` guards preparation/forward/reverse; reject reparse ancestors, unsupported/cross-volume moves. Windows: handle-relative/no-replace; NTFS volume/128-bit ID+hash/size/time, root/child directory IDs. Undo preparation requires recorded evidence, never legacy hashes.
- `WorkspaceStore.OrganizationGate` serializes through cleanup. Persist metadata+`CommittedTransactionId` together; checkpoint/verify backup before journal acknowledgement. Clipboard ID/path/source-space must agree; verify revision under both gates.
- Preserve corruption evidence. Rollback: `Restored` checkpoints+`.rollback-started`; delete backup→primary→fence. Uncheckpointed renames require reconciliation. Inspection read-only; retry reuses store; unenrolled-copy archiving requires consent.
- Sorted topology: 100,000 entries/depth 128. Legacy null topology loads but cannot authorize undo/recovery. Handles don't freeze contents.
- Managed deletion uses app recovery storage. Open/reveal/preview reject relative/URI/reparse paths; previews bounded.
- Copy: explicit cataloged NTFS item, Windows only; managed/existing mapped targets; no copy-undo. Unsupported streams fail closed. Retain intents; never auto-adopt/delete unconfirmed copies. Validate ancestry before parent creation.
- Space drops catalog in-root; confirmed Windows imports retain undo origins. Outgoing drags: file references/Copy only; never delete sources. Clipboard: UI thread. Core creates managed folders, never missing mapped roots.
- Release singleton subscriptions; space windows borrow workbench.
- Monitor Desktop/selected folders: whitelist/baseline/health/pause/rescan/target binding. No takeover before S01–S08.
- CI: physical macOS temp paths, hosted-only aliases, terminal probe JSON; skips aren't evidence.
- Preserve provenance, twelve-locale parity, F01–F13/inference evidence. Previews never move files; Jev keys/consent stay session-only. Python/models: ignored `artifacts/`. Synthetic images cannot prove desktop compositing.
<!-- pi-agents-md:end -->
