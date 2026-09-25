# P3 manual organization closure

Status: implementation in progress. Desktop takeover and watcher-driven automatic moves remain disabled.

## Verified at the current worktree

- `DesktopOrganizationTransaction` performs fail-closed journaled regular-file moves and same-volume directory moves.
- Directory moves reject reparse points, self-nesting, duplicate paths, existing destinations, and cross-volume destinations. Every directory journal stores a sorted recursive file identity manifest.
- Startup recovery distinguishes an absent journal from a corrupt primary/backup journal. Recovery refuses missing, changed, or occupied destinations and preserves the journal for manual reconciliation.
- `ManualOrganizationCoordinator` updates workspace metadata only after physical movement succeeds. It records `PendingUser`, `Completed`, `Undone`, and `RecoveryRequired` states.
- File undo verifies length, last-write ticks, and SHA-256. Directory undo verifies the persisted recursive manifest before restoring the directory. Any mismatch marks the operation `RecoveryRequired` and leaves the external content in place.
- Managed-file rename uses the same transaction and identity-checked undo path. Mapped-reference removal only removes the catalog metadata and leaves the source file untouched.
- Source and destination leaf boundaries are checked against their owning space roots. Managed directory manifests are validated before persistence.
- The Avalonia surface exposes localized callback-only gates for open, preview, copy, cut, paste, rename, and mapped-reference removal. No UI callback performs direct filesystem I/O. Managed and mapped rows show distinct capability labels.
- Release verification: solution build is 0 warnings / 0 errors; Core tests pass 41/41; Inference tests pass 4/4; headless smoke passes with 257-key parity across 12 locales and reports physical DPI/IME/native display evidence honestly as unverified.

## Remaining P3 gates

- Connect platform open/preview and clipboard providers through concrete cross-platform implementations and tests; the current UI delegates are intentionally unbound or callback-only.
- Implement a cross-platform managed delete-to-trash/recovery policy before enabling managed deletion. The UI correctly keeps managed deletion disabled and only enables mapped-reference removal when its callback is attached.
- Complete S01 volume/device identity adaptation. `Path.GetPathRoot` remains a conservative same-volume gate, not proof for Linux mount points, network shares, or other devices.
- Add Windows install/publish evidence and fault-injection coverage for rename/delete/recovery lifecycle. Do not enable desktop takeover until S01-S08 and disk-fault gates pass.
