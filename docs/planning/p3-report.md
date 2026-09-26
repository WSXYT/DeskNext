# P3 manual organization closure

Status: implementation in progress. Desktop takeover and watcher-driven automatic moves remain disabled.

## Verified at the current worktree

- `DesktopOrganizationTransaction` performs fail-closed journaled regular-file moves and same-volume directory moves.
- Directory moves reject reparse-point members, self-nesting, duplicate source/destination paths and existing destinations. Journals store sorted file-content manifests; these are not native object IDs or complete directory-topology snapshots.
- New local-volume guards run during preparation and immediately before forward/reverse file and directory moves. Windows uses volume-mount APIs; Linux uses physical paths plus component-bounded mount IDs (including bind mounts); macOS uses physical paths plus mounted filesystem enumeration. Unsupported/ambiguous lookups fail closed. Cross-volume file moves are also disabled to prevent implicit copy/delete through `File.Move`.
- Recovery recognizes primary, backup-only, and persistent `.recovery-required` markers. Invalid copies are preserved, not quarantined into apparent absence. Double corruption blocks subsequent instances even if damaged copies are later removed.
- Unacknowledged moves are cleared only when the source still matches and the destination is absent; a crash after rename but before receipt persistence requires manual reconciliation. Backup is deleted before primary, and cancellation cannot abandon an in-flight journal write.
- `ManualOrganizationCoordinator` now retains the physical journal across move/rename/undo until workspace file metadata and `CommittedTransactionId` are persisted together. Explicit matching-ID acknowledgement then removes the journal. Startup rolls back uncommitted journals, but only cleans up a fully acknowledged journal whose ID already appears in durable workspace metadata. Cleanup failure must not overwrite commit evidence. All coordinators for the same store share a store-owned organization gate through cleanup; workspace disposal waits for that gate before releasing exclusive ownership.
- New commit-boundary tests leave actual retained journals and reopen the workspace before/after metadata commit across file, directory and undo paths. These simulate restart states, not physical power loss. Four additional deterministic barrier tests cover concurrent recovery through a second coordinator for files/directories and shared/distinct transaction objects. A disposal test checks exclusive-owner lifetime. Independent review approved this bounded correction (run `4e24909b-e5ca-4130-b20b-16fd724c9b32`), not overall S08.
- File undo verifies length, last-write ticks, and SHA-256. Directory undo verifies the persisted recursive manifest before restoring the directory. Any mismatch marks the operation `RecoveryRequired` and leaves the external content in place.
- Managed-file rename uses the same transaction and identity-checked undo path. Mapped-reference removal only removes the catalog metadata and leaves the source file untouched.
- Source and destination leaf boundaries are checked against their owning space roots. Managed directory manifests are validated before persistence.
- The Avalonia surface exposes localized callback-only gates for open, preview, copy, cut, paste, rename, and mapped-reference removal. No UI callback performs direct filesystem I/O. Managed and mapped rows show distinct capability labels.
- Latest local Windows verification: Release build has 0 warnings / 0 errors; Core tests pass 75/75; Inference tests pass 4/4; headless smoke passes with 257-key parity across 12 locales. New tests cover native Windows volume lookup, synthetic Linux mount records (all 24 covered-child/overmount orderings), backup-only recovery, double corruption across restarts, unacknowledged file/directory moves, and malformed null-path primary/backup receipts. Linux/macOS runtime execution of these new changes is not yet verified.

## Remaining P3 gates

- Connect platform open/preview and clipboard providers through concrete cross-platform implementations and tests; the current UI delegates are intentionally unbound or callback-only.
- Implement a cross-platform managed delete-to-trash/recovery policy before enabling managed deletion. The UI correctly keeps managed deletion disabled and only enables mapped-reference removal when its callback is attached.
- Complete native S01 evidence: actual Linux bind/overmounts, macOS APFS/firmlinks, Windows mounted folders/junctions, network and unavailable-volume refusals. Parser fixtures and local Windows temp-directory tests do not establish the full platform gate.
- S02/S08 remain open: content hashes are not native file IDs; empty-directory topology, ancestor-link and mount-change races, restartable partial rollback, filesystem power-loss durability and stale workspace-backup recovery after journal cleanup need additional hardening. Do not infer recovery completeness from the current passing tests.
- Add Windows install/publish evidence and fault-injection coverage for rename/delete/recovery lifecycle. Do not enable desktop takeover until S01-S08 and disk-fault gates pass.
