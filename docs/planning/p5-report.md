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

## Scan existing items (2026-10-03)

Settings now offers a cancellable **Scan existing items** command, including while observation is stopped. It reuses bounded baseline enumeration, adds existing eligible paths to Pending in one metadata save, respects saved exclusions, and represents a directory once rather than adding its children again. Repeated scans preserve IDs and do not save duplicates. No model calls, file moves, removal of missing records or watcher restart is performed; this is review discovery, not proof of stable content or complete event-gap repair. Stop, configuration changes and disposal cancel the scan. Relevant App build: zero warnings/errors; existing two-source workflow includes baseline discovery, exclusions, directory grouping and idempotence (`headless-folder-scan.log`, Success/FolderObservationVerified/ManagedClipboardWorkflowVerified true, 16347 ms). No full-suite/CI/package rerun.

## Per-source suggested space (2026-10-03)

Settings can bind or clear a suggested space for each **saved** observation source. The binding persists in `WorkspaceSettings.MonitoredFolderTargets`; missing sources/spaces are rejected, and saving a removed source drops its obsolete binding. Newly discovered review items receive `SuggestedSpaceId` from the most specific selected source. Clearing a preset does not rewrite existing review decisions. This is not a routing rule or import permission: nothing is moved, no model runs, and import still requires confirmation.

The existing two-source workflow uses the actual source/space pickers and binding command, checks persisted JSON, verifies one source suggests its bound space while the other has no preset, and confirms clearing retains earlier review choices. App build: zero warnings/errors; related WorkspaceStore tests: 15 passed; checked UI workflow: `headless-observation-targets.log`, terminal Success/FolderObservationVerified/ManagedClipboardWorkflowVerified true, 16,990 ms. No full-suite, CI or package rerun.

## Per-source pause and status (2026-10-03)

The saved-source picker now shows that source's runtime status and Pause/Resume controls, alongside an active-source count. Each source reuses the existing bounded monitor; pausing cancels/drains its callback and disposes only its watcher. A selected nested source owns its subtree even while paused, so an active parent cannot bypass the pause. Startup failure of one source does not stop the others. Unavailable sources have a visible status and an explicit retry entry.

Pause is session-only and does not change workspace metadata. Resume establishes a new baseline and explicitly asks for a manual scan of missed items; it does not silently replay them. The existing explicit scan can still include paused sources. Global Stop, configuration changes and disposal continue to release every watcher. No model calls or physical moves were added.

App build: zero warnings/errors. The existing workflow verifies the actual controls, a paused nested source with its parent and another source still running, resume without adopting pause-period items, unavailable-source status/retry, and unchanged source content/zero file operations. `headless-observation-pause.log`: terminal Success/FolderObservationVerified/ManagedClipboardWorkflowVerified/dictionary parity true, 11,098 ms. LSP still reports the already-compiled ScanPaths/MonitoredFolderTargets members missing; no source was weakened for stale diagnostics. No full suite, CI or package repetition.

## Still open

Durable per-source pause/health semantics, capsule/extension bindings, complete rescan reconciliation, durable event attribution, all overflow/257/1000-event guarantees, native root-replacement policy, aggregate multi-source resource limits and 24-hour testing, and Flow editor/scheduling/unified execution remain open. Observation is **not** automatic organization and cannot authorize file moves. P3/P5 and the full F05 row remain incomplete.
