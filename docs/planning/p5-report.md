# P5 folder observation and Flow — partial implementation

## Ordinary-directory move extension (2026-10-06)

The same individually confirmed move path now accepts cataloged ordinary directories as well as files. Only the native Move node's ordinary-object type check changed; the coordinator still captures and verifies the full directory manifest/native root and child IDs, commits metadata and owns undo. A lexical self-nesting refusal runs before confirmation; the existing Core/native ancestry checks remain authoritative at execution. Trailing source/target separators are normalized only in the transient submission so native `filename()` cannot produce an empty leaf; the user's draft stays unchanged.

Two targeted native Flow-move cases pass locally, including empty-directory/receipt preservation, self-nesting refusal and tamper-refusing undo. The existing UI workflow now exercises both file and directory confirmation, cancellation, move/undo, stale revision and close-draining paths. `artifacts/p5-flow-tests/directory-move-ui.log` reports terminal Success/ConfirmedFlowMoveVerified and dictionary parity true (23,587 ms for the compound smoke); App/Release bridge builds succeed. A test-edit omission initially failed compilation and was fixed before this result. Active editor diagnostics still disagree with the compiling C++20/C# source and were not used to weaken it. The earlier three-platform run below predates this small extension; no full CI, P3 or installer matrix was repeated. Other Flow actions, bindings and automatic triggers stay closed.

## Individually confirmed catalog-file moves (2026-10-06)

Manual Flow runs now accept literal prompts plus **regular-file moves from the catalog into an existing, uniquely identified space folder**. `ConfirmedFlowMoves` resolves the catalog subject and target, reuses the coordinator's source-boundary check, and displays exact read-only source/destination paths for each move. Confirmation captures the workspace revision; only `ManualOrganizationCoordinator.MoveFileAsync` performs the operation and its existing journal/commit/undo protocol. The native runner only requests it. Directory, map, rename, read-text, control, binding and automatic-trigger execution remain refused.

The bridge stops on native runtime errors instead of falling through after a skipped file step. Both callback kinds must match the literal step sequence. Unlike prompt waiting, an in-flight file callback is **not abandoned** by a cancellation wrapper: the native destroy/join waits for the host's operation to finish its commit/recovery boundary. Cancellation prevents subsequent steps; already committed moves remain in Operation History. Closing the workbench drains the active Flow before allowing the window to close; UI refresh after a committed move is posted, not awaited by the native callback.

Related evidence: six Flow Core cases pass locally with a real Release bridge, including confirmed move/normal undo, decline, stale revision, cancellation held across an already-committed host result, and native precheck failure stopping later prompts. App build is clean. `artifacts/p5-flow-tests/confirmed-move-ui.log` reports terminal `Success=true`, `ConfirmedFlowMoveVerified=true`, `ManualPromptFlowVerified=true` and twelve-locale parity (456 keys), exercising the production path controls, cancel/confirm/undo, stale confirmation and close-with-pending-review. This is local Windows evidence; the prior three-platform checkpoint below predates this move extension. Active LSP still reports stale runner syntax/member errors absent from the clean compiler and executable checks; no source was changed to silence that cache. Full P5, unattended execution and installation acceptance remain open.

### Confirmed-move integration follow-up

CI [37468770233](https://github.com/WSXYT/DeskNext/actions/runs/37468770233), source `3a9a92d4893b734fc4d3e7973fb33982df712942`, passed on Windows x64, Linux x64 and macOS ARM64. Each platform executed all **six** Flow Core cases without skips, then the published App reported `ConfirmedFlowMoveVerified=true` alongside editor/persistence/prompt/success/parity flags. Downloaded TRX and JSON were checked under `artifacts/p5-flow-tests/ci-37468770233/`. Compound headless times: 19,945 / 14,786 / 11,632 ms respectively. This covers the new host callback and UI confirmation/undo/close path on native runtimes, not physical UI input, an OS installer, directories, additional Flow actions or automatic execution.

## Focused native integration (2026-10-06)

CI [37460672092](https://github.com/WSXYT/DeskNext/actions/runs/37460672092), source `093d9472ce488f87b357cd77715a76cd131e8d23`, passed on **Windows x64, Linux x64 and macOS ARM64**. Each runner compiled its own Release bridge, executed the five Flow Core checks with **zero skips**, and published the App with that native library beside it. The terminal-evidence driver requires editor, persistence and prompt-runtime flags as literal true; its ten self-checks also passed.

Downloaded TRX and terminal JSON were inspected by the parent: all three report `Success`, `FlowDefinitionEditorVerified`, `FlowDefinitionPersistenceVerified`, `ManualPromptFlowVerified` and dictionary parity true, with 12 locales. Combined headless workflow times were Windows 19,426 ms / Linux 13,678 ms / macOS 10,986 ms, not per-Flow performance figures. Evidence is under `artifacts/p5-flow-tests/ci-37460672092/`.

This closes native ABI/definition/acknowledgement/cancellation/disposal integration for the restricted prompt runner. It does not establish OS-native input, installation/signing, file/control execution, automatic triggers, full P5 or any P4 quality gate. No P3 disk/drag/kill, model-download or installer matrix was repeated.

## Explicit prompt-only native execution (2026-10-06)

**Run prompt steps** now invokes the existing Pogget `FlowRuntime` for one explicit, transient manual submission. Saved/draft definitions remain disabled. The runner accepts only 1–100 literal prompt nodes and rejects every file/control node (including disabled ones), automatic triggers, bindings, aliases, branches and `${...}` substitutions before creating the native runtime. No file executor, map adapter, automatic tick or published monitoring definition is installed.

Each native prompt must match the expected literal sequence and is presented in the production acknowledgement overlay. Continue acknowledges it; Cancel stops subsequent prompts. `dn_flow_busy` exposes the existing queue/worker busy state, not a success receipt: success additionally requires every expected prompt acknowledgement. Managed callback exceptions are contained and reported, never allowed to escape into C++; cancellation interrupts pending callback tasks. Native cancellation/drain/destruction run off UI/callback threads. Workspace restart/disposal cancels and drains the native task without waiting on a UI command continuation.

Evidence: fresh Release bridge and App build succeed, two focused Core tests pass (including real Unicode sequence, decline/cancel, callback failure and a clean subsequent run), and the existing headless editor workflow reports `Success=true`, `ManualPromptFlowVerified=true`, `FlowDefinitionEditorVerified=true`, `FlowDefinitionPersistenceVerified=true` in `artifacts/p5-flow-tests/prompt-run-ui.log` (16,912 ms for the complete smoke). It exercises acknowledgement/cancel controls, rejects the five-action file draft, disposes during a pending prompt, and verifies unchanged workspace revision and saved definition bytes. Native fixture tests are explicitly skipped if their library is absent; no skip is runtime evidence. Cross-platform delivery of this increment and the file/control executor remain unaccepted. Active LSP still reports the new Core runner/record missing in the App partial, while the compiler and runtime above resolve them; no source was weakened for stale editor state. No P3 suite, model corpus, installer or full CI repetition.

## Sequential literal-parameter editor (2026-10-06)

Flow now defaults to an ordered step form, with an advanced-JSON switch. Users can rename the definition, add prompt/move/map/regex-rename/read-text nodes, edit their literal parameters, reorder them and remove a selected step. Parameters are projected from the upstream JSON rather than rebuilt as another workflow schema; native validation remains the save gate. Empty/incomplete drafts can still be edited. No step is executed, including read-text.

Node IDs stay stable through reordering. Existing input bindings, aliases, extensions and nested branches remain intact; unsupported/conditional nodes are shown with an instruction to use JSON. Full nested-branch editing and automatic triggers are not enabled. Form changes update only the current draft; saving still uses the existing disabled-manual catalog/revision path.

Relevant App build: zero warnings/errors. The existing checked headless workflow drives actual form controls and all five action types, verifies Unicode field edits, move-up/down and removal, preserved IDs and opaque conditional/extension data, absent source/target side effects, native validation and save/reopen. `artifacts/p5-flow-tests/flow-steps.log`: terminal `Success=true`, `FlowDefinitionEditorVerified=true`, `FlowDefinitionPersistenceVerified=true`; twelve-locale parity (445 keys), 15,551 ms for the combined smoke, not an editor latency claim. No full suite/CI/installer repetition. This is bounded manual definition editing, not full P5 runtime/scheduling acceptance.

## Saved manual-Flow definitions (2026-10-06)

The sidebar **Flow definitions** entry now provides an advanced JSON draft editor: new, reload, native validation and save. It reuses the upstream format, not a second node validator. Definitions remain `enabled=false` with a manual trigger; there is deliberately no Run button, automatic trigger or file executor. This is not yet the planned visual sequential editor.

`ManualFlowStore` holds at most 100 definitions in the separate, bounded `flow-definitions.json` catalog under the owned workspace directory. It uses the existing organization gate and `ResilientJsonStore`, checks the selected ID and revision, increments only the definition revision, and preserves a recovery marker after double corruption rather than resetting to an empty catalog. Editing definitions does not alter workspace revision, classification settings or file-operation history. Reload/new/selection replace the current draft, as stated in the UI.

Validation requires the compiled native bridge beside the app. Source builds may pass `-p:PoggetNativeLibrary=<absolute-library-path>`; Windows packaging accepts the corresponding `-PoggetNativeLibrary` parameter. No path from definition JSON selects native code. Without the component the editor reports it unavailable and save refuses. Existing public preview assets are unchanged and do not acquire this module retroactively.

Bounded evidence: three relevant Core checks passed (`artifacts/p5-flow-tests/flow-storage.trx`); a fresh Windows Release bridge and App build succeeded; the existing checked headless workflow reports `Success=true`, `FlowDefinitionEditorVerified=true`, `FlowDefinitionPersistenceVerified=true`, and twelve-locale parity (422 keys) in `flow-editor.log`. It drives the real editor commands, saves/reopens a definition and refuses an enabled draft without changing saved bytes. No full P3 suite, three-OS CI or installer cycle was repeated. Unix UI/native-module packaging, visual step editing, runtime integration and scheduling remain unaccepted.

## Independent manual-Flow foundation (2026-10-06)

P4's quality/live-service gates remain unpassed. This is independent definition/validation work permitted by PLAN.md, not P4 completion or automatic execution enablement.

`ManualFlowDefinitions` emits the existing `pogget.flow` schema with a manual trigger and **enabled=false**. Its bounded reader rejects enabled/automatic definitions and NUL-bearing strings; the native `dn_flow_validate` remains the authority for module parameters and dataflow. The wrapper loads only the application-directory bridge by default (an explicit developer path is separate from JSON), distinguishes invalid definitions from unavailable native support, and never creates a Flow runtime or executor.

Two focused Core checks passed with the explicitly supplied compiled Windows bridge (`artifacts/p5-flow-tests/flow-definition-final.trx`), including Unicode prompt validation, rejected unknown modules and a valid move definition whose source/destination stayed untouched. Without the native-library fixture the native test is explicitly skipped, not counted as native evidence. This earlier foundation checkpoint did not establish an editor or persistence; the later saved-definition increment above supplies those bounded capabilities. Host-executor integration, schedules and full P5 acceptance remain open.

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

Durable per-source pause/health semantics, capsule/extension bindings, complete rescan reconciliation, durable event attribution, all overflow/257/1000-event guarantees, native root-replacement policy, aggregate multi-source resource limits and 24-hour testing, and nested-branch Flow editing/scheduling/unified execution remain open. Observation is **not** automatic organization and cannot authorize file moves. P5 and the full F05 row remain incomplete; the accepted P3 manual scope is unchanged.
