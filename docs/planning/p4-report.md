# P4 classification previews — partial implementation

P3 and P4 are **not complete**. These previews are read-only: they do not authorize any file move or copy. Manual file operations have their separate P3 evidence.

## Usable path

Choose a trusted offline Laya model bundle directory (containing `manifest.json`) with **Browse** in Settings, or enter its path, then use the existing **Save settings** action. Choosing/cancelling only edits the draft; no download or inference starts. Select **Laya · Local CPU**, save settings, then use **Classification preview** in a file row's context menu. The result shows ranked probabilities including filename-ambiguous and categories-insufficient. Cancellation is available in that menu. No file content enters the request and no file or workspace mutation is performed.

`MainWindowViewModel` builds the request from the selected file's name/kind and current space names/descriptions. `LocalPreviewClient` runs one application-hosted CPU worker using the existing P1 framed protocol; `Program.Main` dispatches `--inference-worker` before Avalonia or workspace initialization. Frames are capped at 1 MiB, diagnostics retained up to 4096 characters, and each preview has a two-minute deadline. Cancellation and normal exit release the worker. Request ID/revision, probability consistency, and the current workspace revision are checked before display.

The existing model hash/provenance check is reused, **not publisher authentication**. Only explicitly selected, trusted local bundles are supported; no network download, Python discovery, GPU selection, or cloud fallback was introduced. The one-shot worker cold-loads on each preview; no latency or memory acceptance is claimed.

## Bounded evidence

- App Release build: zero warnings/errors.
- Inference tests: 6 passed, including stale/inconsistent result refusal and pre-start oversized/cancelled input checks.
- `node tests/run-app-probe.mjs src/DeskNest.App/bin/Release/net10.0/DeskNest.App.exe --headless-smoke --local-model=<trusted-model-directory>`: passed with the pinned multilingual ONNX bundle. `LocalClassificationPreviewVerified`, `ManagedClipboardWorkflowVerified`, dictionary parity, and terminal `Success` are true; complete smoke elapsed 15575 ms. Log: `artifacts/p3-publication-tests/headless-local-model-preview.log`.
- This invokes the actual production UI callback and framed CPU worker over an isolated temporary workspace, checks unchanged source content/revision/operations, then cancels another preview and verifies no result is displayed. Ordinary smoke also verifies the context-menu binding and missing-model setup guidance without loading weights.
- The single fixture returned Documents 57.2%, filename-ambiguous 41.2%, categories-insufficient 1.6%. This demonstrates execution, **not classification quality or permission to move**. The UI renderer remains headless and this is not an installed-package or cross-platform inference result.

## Model-folder selection follow-up

The native folder picker reuses Avalonia's storage provider and existing localized labels, requires a local directory with `manifest.json`, and leaves actual bundle verification to the worker. A relative manually typed path now reports validation instead of silently erasing the previously saved model location. The existing headless workflow checks the visible browse entry, draft-only selection/cancellation and save behavior; it is not evidence of physical picker interaction or model authenticity. Relevant checks: App build zero warnings/errors and checked `headless-model-folder-picker.log` Success/ManagedClipboardWorkflowVerified true (5080 ms on the final cancellation check). No full suite, model reload, installer or CI repetition for this small UI change. Active LSP still reports stale placement-type errors in shared files despite the passing compiler, not a clean LSP result.

## Jev session preview (2026-10-02)

Settings > Engine now offers Laya or Jev. Save the provider choice, enter a masked **session-only** TypeSafe API key, and explicitly allow sending before using the file-row classification preview. The permission names the transmitted fields (filename, item type, all space names/descriptions), excludes contents/full paths, and mentions possible API charges. Changing the key/provider or clearing the session revokes permission and cancels an active request; closing the workbench drops its key reference. This is in-memory storage, not secure memory erasure or a persisted credential vault. Neither key nor permission enters workspace JSON/backups.

`JevPreviewClient` uses the documented [Choice API](https://docs.typesafe.ai/primitives/choice) at `https://api.typesafe.ai/v1/systemone`, pinned to `jev-1.13.0`: bearer authorization, `questions[requestId]` with `type/instructions/criteria`, and `answers[requestId].probabilities` mapped by candidate ID. It rejects mismatched model/question/candidates/ranking, bounds request/response to 1 MiB, has a 45-second deadline, disables redirects, and never echoes response bodies on HTTP errors. There are no automatic POST retries or fallback from local to cloud; retry is a new explicit user action. The existing revision/cancellation checks prevent stale result display, and the existing preview panel labels cloud output separately. Neither provider's confidence is treated as permission to move files.

Bounded checks: Inference tests **12/12** (six new cases with an in-memory HTTP handler, no live service); App Release build **0 warnings/0 errors**; checked `artifacts/p3-publication-tests/headless-jev-session-preview.log` terminal Success/ManagedClipboardWorkflowVerified true, **329 keys × 12 locales**, 11469 ms. The UI check covers masked input, missing-key/refused-consent gates, clearing, and key absence from primary/backup saves. **No real Jev API call was made**; authentication, live response compatibility, billing, quality and installed/cross-platform cloud operation remain unverified. No full Core, installer or CI repetition for this read-only increment. Active LSP still reports a stale missing `JevPreviewClient` at the App call site despite the clean build and executable smoke; it is not a clean LSP result.

## Pending-item suggestions (2026-10-03)

The pending drawer now invokes the same Laya/Jev preview from the actual review row, with cancellation and the same cloud consent/revision checks. The best real category is offered as a button even if an ambiguous/insufficient candidate wins. Clicking that button only fills the target draft; it does not import or persist a model decision. The full probability ranking remains visible in the existing preview panel. An unsuitable suggestion can instead be replaced by **Create space**: that action now targets its clicked pending row and immediately selects the newly created category. Import still requires the existing explicit confirmation, and remains independent of the inference client.

Bounded evidence: App Release build **0 warnings/errors**; existing checked headless smoke with the real pinned local CPU bundle passed (`artifacts/p3-publication-tests/headless-pending-classification.log`, terminal `Success=true`, `LocalClassificationPreviewVerified=true`, 20390 ms). It checks the pending-row button, actual worker response, no implicit target selection/file movement/metadata change, explicit draft selection, cancellation, and creating a category without importing the item. Existing import/undo coverage runs in the same smoke. No new protocol, dependency, full suite, installer or CI run; no live Jev call or quality claim. Active LSP still reported stale file-tail errors despite the clean compiler/runtime result.

## Installed CPU preview (2026-10-03)

The installed `0.3.0-installed-explorer-20261003` App hosted the actual CPU inference worker with the separately supplied pinned bundle. `artifacts/package-evidence/installed-explorer-20261003-final/installed-local-model.log` reports terminal `Success=true, LocalClassificationPreviewVerified=true`, including the pending-item draft-only suggestion and cancellation checks (28900 ms for the whole smoke). This is a real installed-executable worker/UI path with headless rendering, not a bundled model, authenticated deployment, full A–D equivalence, live Jev, automatic organization or quality acceptance. The fixture's ambiguous outcome remained a review result; no model-authorized move occurred. The same installation subsequently uninstalled while retaining its unknown-file sentinel.

## Still open

Live Jev/credential-vault acceptance; authenticated deployment/download/activation/repair; offline import UX; production worker negotiation and model identity; broader lifecycle/error handling; unattended classification; independent quality data and full A–D installed-path acceptance; GPU and per-platform runtime evidence. No P4 gate is marked passed by this preview.
