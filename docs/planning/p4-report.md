# P4 local classification preview — partial implementation

P3 and P4 are **not complete**. This is non-dependent, read-only work allowed by PLAN.md; it does not enable automatic organization or production Copy.

## Usable path

Save a trusted offline Laya model bundle directory (containing `manifest.json`) in Settings, then use **Local Laya classification preview** in a file row's context menu. The result shows ranked probabilities including filename-ambiguous and categories-insufficient. Cancellation is available in that menu. No file content enters the request and no file or workspace mutation is performed.

`MainWindowViewModel` builds the request from the selected file's name/kind and current space names/descriptions. `LocalPreviewClient` runs one application-hosted CPU worker using the existing P1 framed protocol; `Program.Main` dispatches `--inference-worker` before Avalonia or workspace initialization. Frames are capped at 1 MiB, diagnostics retained up to 4096 characters, and each preview has a two-minute deadline. Cancellation and normal exit release the worker. Request ID/revision, probability consistency, and the current workspace revision are checked before display.

The existing model hash/provenance check is reused, **not publisher authentication**. Only explicitly selected, trusted local bundles are supported; no network download, Python discovery, GPU selection, or cloud fallback was introduced. The one-shot worker cold-loads on each preview; no latency or memory acceptance is claimed.

## Bounded evidence

- App Release build: zero warnings/errors.
- Inference tests: 6 passed, including stale/inconsistent result refusal and pre-start oversized/cancelled input checks.
- `node tests/run-app-probe.mjs src/DeskNest.App/bin/Release/net10.0/DeskNest.App.exe --headless-smoke --local-model=<trusted-model-directory>`: passed with the pinned multilingual ONNX bundle. `LocalClassificationPreviewVerified`, `ManagedClipboardWorkflowVerified`, dictionary parity, and terminal `Success` are true; complete smoke elapsed 15575 ms. Log: `artifacts/p3-publication-tests/headless-local-model-preview.log`.
- This invokes the actual production UI callback and framed CPU worker over an isolated temporary workspace, checks unchanged source content/revision/operations, then cancels another preview and verifies no result is displayed. Ordinary smoke also verifies the context-menu binding and missing-model setup guidance without loading weights.
- The single fixture returned Documents 57.2%, filename-ambiguous 41.2%, categories-insufficient 1.6%. This demonstrates execution, **not classification quality or permission to move**. The UI renderer remains headless and this is not an installed-package or cross-platform inference result.

## Still open

Jev and secure credentials; authenticated deployment/download/activation/repair; offline import UX; production worker negotiation and model identity; broader lifecycle/error handling; interactive triage integration; independent quality data and full A–D installed-path acceptance; GPU and per-platform runtime evidence. No P4 gate is marked passed by this preview.
