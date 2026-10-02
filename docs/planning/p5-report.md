# P5 folder observation — partial implementation

## Explicit observation session (2026-10-03)

Settings → Monitoring now provides folder pickers, saved source/exclusion lists, and **Start observing / Stop observing**. The Desktop is an ordinary selectable source, not a special takeover target. Starting establishes a baseline; only later new paths enter Pending. No model is invoked, no import is confirmed, and no file is moved. Nothing starts automatically on launch.

The existing `DesktopOrganizationMonitor` supplies the watchers, bounded event channel and stability sampling. Excluded subtrees are pruned while building the baseline; baseline traversal rejects links and has entry/depth bounds. The workspace, model cache and space directories are excluded. `ManualOrganizationCoordinator.RecordObservedPathsAsync` serializes review-metadata enrollment with file transactions, rechecks saved roots/exclusions and duplicates under the metadata gate, and never performs a physical operation.

The workbench owns the session. Stop cancels queued enrollments and disposes watchers; closing the workbench or saving changed source/exclusion/storage settings also stops it. Runtime failures and event overflow appear in the monitoring notice. A restart establishes a **new baseline**, not a repair of missed events.

## Bounded evidence

- App Release build: zero warnings/errors.
- Existing monitor/stability tests: **8 passed**, no skips.
- Existing checked headless workflow: `artifacts/p3-publication-tests/headless-folder-observation.log`, terminal `Success=true`, `FolderObservationVerified=true`, `ManagedClipboardWorkflowVerified=true`, dictionary parity true; 15,763 ms for the whole probe.
- The new isolated workflow uses real filesystem notifications across two source folders, excludes an existing item and an excluded subtree, verifies two new review entries with untouched content/zero operations, then stops, restarts and disposes the workbench. This is local Windows evidence, not an OS folder-picker interaction or cross-platform acceptance.
- Active LSP still reports the newly added coordinator method missing in the App partial file; the compiler and runtime above both resolve it. No working code was weakened to suppress that editor state.
- No full-suite, CI, package or long-duration run was repeated for this increment.

## Still open

Per-source target bindings, explicit rescan/reconciliation, durable event attribution, all overflow/257/1000-event guarantees, root replacement/unavailability policy, 24-hour resource testing, and Flow editor/scheduling/unified execution remain open. Observation is **not** automatic organization and cannot authorize file moves. P3/P5 and the full F05 row remain incomplete.
