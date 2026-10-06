<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- DeskNext display; retain `DeskNest.*` internals/paths. `PLAN.md` owns execution; P0–P7 gated.
- .NET 10/Avalonia; C# Core/Platform/Inference, Pogget C++. One writer/worktree; serial builds, shared compilation disabled.
- P3-manual accepted. `FileSystemVolume`: guard preparation/forward/reverse; reject reparse/unsupported/cross-volume paths. Windows NTFS IDs; Unix device/inode/birth IDs. Handle-relative/no-replace renames. Unix name races remain; failed bindings/receipts retain journals.
- Files: identity+hash/size/time. Directories: sorted root/child IDs, 100,000 entries/depth 128. Legacy loads never authorize undo/recovery. Handles don't freeze contents.
- `OrganizationGate` serializes through cleanup. Persist metadata+`CommittedTransactionId`; checkpoint/verify backup before acknowledgement. Clipboard ID/path/source-space agree; revision checked under both gates.
- Preserve corruption evidence. Rollback: `Restored`+`.rollback-started`; delete backup→primary→fence. Uncheckpointed renames require reconciliation. Inspection read-only; retries reuse store; copy-intent archiving requires consent.
- Managed deletion uses app recovery storage. Open/reveal/preview reject relative/URI/reparse paths; previews bounded.
- Copy: cataloged Windows NTFS, managed/existing mapped targets, no copy-undo. Unsupported streams fail closed. Retain intents; never adopt/delete unconfirmed copies. Validate ancestry before creation.
- Drops catalog in-root; confirmed imports retain undo origins. Outgoing drags: Copy references, never delete sources. Clipboard: UI thread. Never create missing mapped roots/recovery parents.
- Release singleton subscriptions; space windows borrow workbench. Observation: session→Pending only. Pause/dispose releases watchers; nested sources own subtrees. Resume baselines; explicitly scan missed items. No takeover before S01–S08.
- CI: storage_only/native_manual_only/linux_drag_only/macos_drag_only/windows_storage_only; physical macOS temps; hosted-only mounts/aliases/shares; terminal JSON/TRX. Skips aren't evidence.
- Preserve provenance/twelve-locale/F01–F13/inference evidence. Suggestions never move files; Flow definitions disabled/manual/validation-only. Jev consent session-only; saved keys OS-only, never JSON. Python/models: ignored `artifacts/`. Synthetic images don't prove desktop compositing.
<!-- pi-agents-md:end -->
