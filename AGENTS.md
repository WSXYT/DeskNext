<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Display DeskNext; retain `DeskNest.*` internals/paths. `PLAN.md` owns execution; mark P0–P7 only after passed gates.
- .NET 10/Avalonia, C# Core/Platform/Inference, Pogget C++. One writer/worktree; serialize builds; disable shared compilation.
- P3 incomplete. `FileSystemVolume` guards preparation/forward/reverse against reparse ancestors and unsupported/cross-volume moves. Windows: handle-relative/no-replace; NTFS volume/128-bit ID+hash/size/time and root/child directory IDs. Undo requires recorded evidence, never legacy hashes.
- `OrganizationGate` serializes through cleanup. Persist metadata+`CommittedTransactionId`; verify checkpointed backup before acknowledgement. Clipboard ID/path/source-space agree; check revision under both gates.
- Preserve corruption evidence. Rollback uses `Restored`+`.rollback-started`; delete backup→primary→fence. Uncheckpointed renames require reconciliation. Inspection read-only; retry reuses store; unenrolled-copy archiving requires consent.
- Sorted topology: 100,000 entries/depth 128. Legacy null topology loads but cannot authorize undo/recovery. Handles don't freeze contents.
- Managed deletion uses app recovery storage. Open/reveal/preview reject relative/URI/reparse paths; previews bounded.
- Copy: cataloged Windows NTFS items; managed/existing mapped targets; no copy-undo. Unsupported streams fail closed. Retain intents; never auto-adopt/delete unconfirmed copies. Validate ancestry before creation.
- Drops catalog in-root; confirmed Windows imports retain undo origins. Outgoing drags: Copy file references, never source deletion. Clipboard: UI thread. Never create missing mapped roots.
- Release singleton subscriptions; space windows borrow workbench. Observation: session→Pending only, no models/moves. Pause/dispose releases watchers; nested sources own subtrees. Resume starts a new baseline; scan missed items explicitly. No takeover before S01–S08.
- CI: physical macOS temp paths, hosted-only aliases, terminal JSON; skips aren't evidence.
- Preserve provenance, twelve-locale parity, F01–F13/inference evidence. Suggestions never move files. Jev consent session-only; saved keys use OS storage, never JSON. Python/models: ignored `artifacts/`. Synthetic images don't prove desktop compositing.
<!-- pi-agents-md:end -->
