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

## Current evidence and limits

See [the DirectML report](../../docs/planning/p4-directml-report.md). Three fixed-tensor cases passed in the isolated row-lookup prototype at 571–573 MiB process peak; the source-controlled runner additionally passed one full raw-request/tokenizer case at 679 MiB. A later run was terminated at the emergency memory limit as unrelated host pressure increased. That run is not a pass; the chained third raw-request check did not start.

These are bounded numerical/execution checks, not classifier quality, a production worker protocol, cancellation semantics inside the App, a packaged GPU runtime, per-adapter performance certification, or P4 acceptance. The application and public model release remain CPU-only. The user-facing manual-operation safeguards are unchanged.
