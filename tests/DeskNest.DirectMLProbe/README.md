# Explicit NVIDIA DirectML developer runner

This is a **standalone P4 experiment**, not a selectable DeskNext App backend or a release package. It performs one explicit request on a hardware NVIDIA DXGI adapter using ONNX Runtime DirectML **1.24.4**. It is not in the solution's normal build/publish path; do not copy its native DLLs into the CPU application directory.

The current production `Probe.cs` is linked for request types, hashing, tokenizer and calibrated decision math. Only graph execution is different. Host tokenization, exact FP32 embedding-table row reads and postprocessing still use CPU. Transformer and decision-head profiles must contain DirectML kernels and no CPU kernels. A failed GPU run is not retried on CPU. There is no file-organization authority or automatic-runtime-selection policy.

## Prepare the isolated model

Use an already verified original multilingual CPU bundle and a new output directory. In the **developer export environment** (Python 3.11+, `onnx==1.18.0`, NumPy):

```sh
python tests/Inference.Tests/derive_directml_row_model.py \
  /path/to/original-cpu-bundle artifacts/directml-model
```

The script requires the pinned original manifest and verifies every input asset. It changes `Reshape.allowzero` only for the 44 encoder / 1 head nodes with a provable inferred dimension, then replaces the one token-embedding Gather with a new `embedding` input. The 256,000 × 768 FP32 table is read from disk by token ID, not loaded wholesale; the remaining encoder initializers are streamed into a compact 441,326,592-byte file. All FP32 values are preserved; there is no quantization/training or category change. The derived manifest records the changed graph input and derivation. Output bytes are separate copies, never edits to the CPU bundle. A partial failed derivation stays for diagnosis; choose a new directory to retry.

The executable pins all eight derived asset digests. A different exporter result fails closed; do not update the pins merely to silence that check. The model data are not committed or included in regular application packages.

## Build and run

```sh
dotnet build tests/DeskNest.DirectMLProbe -c Release -m:1 --nodeReuse:false -p:UseSharedCompilation=false
python tests/Inference.Tests/run_directml_check.py \
  artifacts/directml-model request.json artifacts/gpu-one-request --adapter 1
```

The adapter is a **DXGI index**, not an `nvidia-smi` ordinal. On the measured host, DXGI 0 is Intel and DXGI 1 is RTX 2050; the runner checks the selected device's NVIDIA vendor ID/dedicated memory and refuses Intel/software devices. Those indices are not universal.

Example `request.json` (no file contents or absolute document paths):

```json
{
  "request": {
    "requestId": "explicit-gpu-sample",
    "revision": 0,
    "state": "{\"name\":\"quarterly-report.txt\",\"directory\":false}",
    "instructions": "Choose a category. Use the ambiguous or insufficient option when appropriate.",
    "candidates": [
      {"id":"c0","description":"Documents and reports"},
      {"id":"filename-ambiguous","description":"The filename does not identify its subject."},
      {"id":"categories-insufficient","description":"None of the available categories fits."}
    ]
  }
}
```

Omit `tensors` to use the production tokenizer. Supplying previously frozen tensors bypasses tokenization and is labeled separately. With `--reference prior-result.json`, the Python driver checks exact tensor equality, logit/probability deltas ≤1e-4 and identical choice/routing against a prior `Probe.Result`; it never runs a CPU reference itself.

The driver uses one below-normal-priority process, a 90-second deadline, and emergency system-memory checks. It terminates only its own probe on failure. It does **not** guarantee the host will remain responsive: DirectML still allocates system memory, external applications compete for RAM/VRAM, and sampled checks can miss short peaks. It records the process working-set peak, not peak VRAM. Start no concurrent build/inference batch while this process is active.

## Explicit frozen features for a training experiment

The diagnostic CLI may take `--export-features` as a final argument (or use `run_directml_check.py --export-features`). It writes the `[1,sequence,768]` little-endian FP32 encoder output and `feature-input.json` into the new evidence directory, then still checks the original head. The versioned worker and normal workbench never set this option. Features encode supplied filename/hint/text and must stay in private ignored artifacts; do not extract private user data for public training. They are not labels or classifier-quality evidence. The [frozen-head adaptation report](../../docs/planning/p4-model-adaptation.md) describes the separate CUDA training environment and the one-example capability check.

## Self-contained runtime directory

For an isolated Windows x64 directory that does not require an installed .NET SDK/runtime:

```sh
dotnet publish tests/DeskNest.DirectMLProbe -c Release -r win-x64 --self-contained true \
  -m:1 --disable-build-servers -p:UseSharedCompilation=false -o artifacts/directml-runtime
```

A developer installation bundle can instead be built with `build/Publish-Windows.ps1 -IncludeExperimentalNvidia -Version <new-version>`. This opt-in adds the self-contained worker at `worker/ort-1.24.4/win-x64/directml` and its native license/notices files; default packages stay unchanged. `build/windows/Test-InstalledDirectML.ps1` verifies only the installed GPU workbench flow and scoped uninstall, using a separately supplied model. It does not create a public release or run the P3 recovery matrix.

Use a fresh directory and keep it **separate** from the App's CPU worker. Invoke `DeskNest.DirectMLProbe.exe` directly with the same arguments; the model remains separate. Python is only needed for derivation/the optional monitoring driver, not for the executable. The directory includes its own .NET runtime, DirectML/ORT and `tokenizers_proto.dll`. This is a developer publish, not an installed/signed release or a clean-machine test. A local published-directory raw-request check is recorded in the DirectML report.

## Versioned developer worker

The same executable also accepts the existing version-1 framed-worker protocol:

```text
DeskNest.DirectMLProbe.exe --adapter=1 --profiles=<new-evidence-root> --inference-worker <absolute-model> --protocol=1
```

Each stdin frame is a four-byte little-endian byte count followed by `Probe.WorkerRequest` JSON (1 MiB maximum). It requires the declared protocol version and selected manifest digest before model execution, and responds with the existing `Probe.WorkerReply`. Stdout contains only frames. EOF closes the process; errors stop it without a CPU retry. Profiling output uses a separate numbered directory per accepted request, and invalid protocol/manifest frames create no graph profiles.

`LocalPreviewSession`/`LocalPreviewClient` can use this worker **without modification**: construct a `ProcessStartInfo` pointing at this executable, add `--adapter=1` and `--profiles=<new-root>` to `ArgumentList`, then let the client append the worker verb, model path and protocol argument. This is explicit developer wiring, not an application setting. The OS process is reusable but graph sessions and tokenizer are loaded/released per request to bound residency; this does not claim warm GPU session caching or faster latency. The shared client owns cancellation, timeout, idle retirement and process cleanup. The worker's profiling/native runtime directory must remain separate from the production CPU assets.

Hardware checks are opt-in in `tests/DeskNest.Inference.Tests/DirectMlWorkerTests.cs`. Set `DESKNEXT_DIRECTML_PROBE`, `DESKNEXT_DIRECTML_MODEL`, `DESKNEXT_DIRECTML_CASES`, `DESKNEXT_DIRECTML_EVIDENCE` and `DESKNEXT_DIRECTML_ADAPTER` explicitly. `CASES` holds the three fixed `<0|1|2>-input.json` request files and prior `<0|1|2>-reference.json` `Probe.Result` files. No test creates new CPU reference responses or downloads weights. An unset fixture skips and does **not** count as GPU evidence. A monitored emergency memory cancellation is a failed test, not a numerical pass.

## Explicit interactive session (experimental)

After completing normal first-run setup, you may launch the **source-built App** with this explicitly supplied trusted worker and derived model:

```powershell
& .\src\DeskNest.App\bin\Release\net10.0\DeskNest.App.exe `
  --experimental-nvidia `
  --gpu-worker=D:/absolute/path/DeskNest.DirectMLProbe.exe `
  --gpu-model=D:/absolute/path/derived-model `
  --gpu-adapter=1
```

Close another instance of the same workspace first. This opens the regular workbench with a **session-only local NVIDIA override**. The saved provider/model path is neither used for inference nor replaced by Save Settings; normal startup without these arguments uses the saved settings again. Provider controls and CPU download/repair/removal are hidden for this session, and startup configuration reset is refused. The chosen model path and experimental label are visible in Settings and preview results. No inference starts just by opening the window, and file moves still require the existing explicit user action. Cloud calls are not made by this session.

The worker and its model are **trusted developer inputs**: validating a path/file name is not publisher authentication. This is not a supported installer download source or an automatic GPU-selection policy. Unsupported GPU/model execution fails visibly without CPU fallback. Profiles remain in the printed/local temporary GPU-session directory for diagnosis; do not use this experimental session for unattended operation. Starting with no completed onboarding refuses and asks you to finish normal setup first. Malformed/mixed GPU launch arguments return exit code 2 before UI startup rather than silently opening a CPU session.

## Isolated workbench classification workflow

The application also provides an **explicit diagnostic-only** workflow that exercises its real view-model commands with this worker:

```sh
node tests/run-app-probe.mjs src/DeskNest.App/bin/Release/net10.0/DeskNest.App.exe \
  --directml-preview-smoke \
  --gpu-worker=<absolute-self-contained-DeskNest.DirectMLProbe.exe> \
  --local-model=<absolute-derived-model> --gpu-adapter=1
```

This creates only an isolated temporary workspace, uses headless rendering and labels the local provider **NVIDIA (experimental)**. It checks file/pending previews, editable hint invalidation, worker reuse/cancellation, category creation and explicit confirmation/import/undo through the existing coordinator. It does not start the broad UI/Core/Flow/packaging suite or a CPU model reference. The supplied worker is trusted developer code; paths are accepted only from explicit launch arguments, never workspace metadata or a Flow document. The diagnostic mode is not the interactive launch itself and does not persist GPU configuration. It additionally checks that a saved Jev provider/different model path are preserved through settings save while the session uses the explicitly supplied local GPU model.

The process runs below normal priority, with a 75-second workflow cancellation deadline and a low-memory monitor that cancels the active command. No threshold grants automatic file operations. The terminal `DirectMlPreviewVerified` flag requires the actual workflow plus at least eight completed DirectML-only graph profiles, not just an exit code. The fixture and profiles remain under its printed `EvidenceDirectory` for inspection; no user workspace or clipboard is used.

## Current evidence and limits

See [the DirectML report](../../docs/planning/p4-directml-report.md). Three fixed-tensor cases passed in the isolated row-lookup prototype at 571–573 MiB process peak; the source-controlled runner additionally passed one full raw-request/tokenizer case at 679 MiB. A later standalone attempt was terminated at the emergency memory limit; that attempt is not a pass.

The subsequent v1 integration check passed **three raw requests through the unmodified `LocalPreviewSession` and one reusable NVIDIA process**, with exact tokenizer tensors, matching choice/routing and logit/probability deltas below 1e-4. Cancelling another request retired the worker and no replacement/CPU fallback started. Oversized frames, protocol mismatch and wrong model digest were refused without graph execution. Two hardware tests executed and passed in `artifacts/p4-gpu/versioned-worker-followup/directml-worker-followup.trx`; the earlier `versioned-worker/directml-worker.trx` retains its memory-cancelled failure. No claim that the later pass solved system-wide memory pressure.

These are bounded numerical/execution checks, not classifier quality, production App backend selection, physical UI cancellation, an installed/signed GPU distribution, per-adapter performance certification, or P4 acceptance. Normal application startup and the existing public releases retain their CPU behavior; the new experimental interactive override is explicit and session-only. The user-facing manual-operation safeguards are unchanged.
