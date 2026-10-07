# DirectML feasibility — developer-only execution, not an App backend

## Explicit discrete-GPU continuation and row lookup (2026-10-07)

After the user reported severe host pressure, they explicitly requested continuing on the discrete GPU rather than another CPU batch. Fresh DXGI enumeration identifies NVIDIA RTX 2050 as **adapter 1** (the same device is NVIDIA-tool index 0). No drivers/display settings or other user processes were changed. Early admission checks refused before loading; after the user explicitly requested continuing under low memory, a below-normal single probe retained an emergency memory cutoff and a 90-second deadline.

The initial DirectML load was terminated when available system RAM reached **18 MiB**. Running encoder/head sequentially and skipping tokenizer loading allowed one frozen input to finish (1,644 MiB process peak), but another attempt still reached the emergency cutoff. This was not a general memory solution, and the successful retry does not erase those failures.

The dominant encoder initializer is a **256,000 × 768 FP32 token table (786,432,000 bytes)**. The experimental graph now removes its single `Gather(axis=0, input_ids)` and accepts the exact selected rows as an `embedding` input. Host code reads only those rows; the remaining 441,326,592 bytes of encoder weights and unchanged head run on DirectML. No floating-point conversion, training, probability threshold or category text is changed. The earlier narrowly checked Reshape fix remains. The original CPU model/release is not overwritten.

Three supplied-tensor cases passed using this decomposition, sequential graph residency, no CPU arena/prepacking and one host thread per session:

| Case | Torch logit delta | Rounded official probability delta | Complete call | Process peak |
| --- | ---: | ---: | ---: | ---: |
| mixed language | 6.1781e-6 | 4.4840e-5 | 5,774 ms | 572 MiB |
| literal mask / reversed candidates | 1.2017e-5 | 4.6827e-5 | 5,479 ms | 571 MiB |
| long multilingual | 2.2149e-6 | 2.3450e-5 | 5,159 ms | 573 MiB |

Tensors, choice and pending/proposed routing match their prior references. Both profiles contain one DirectML kernel and no CPU kernel. Host file hashing, row reads, tensor copies and postprocessing still use CPU; this is not an all-GPU program. Whole-adapter memory samples are not process-specific peak VRAM. Host available-memory minima in these runs were 1,669 / 1,615 / 1,550 MiB; this is a changing local host, not a controlled performance benchmark. Evidence: `artifacts/p4-gpu/discrete-check-20261007-153738`, `153842`, `153848`; failed probes remain beside them.

### Reproducible developer entry

[`tests/DeskNest.DirectMLProbe`](../../tests/DeskNest.DirectMLProbe/README.md) now builds a **separate** DirectML 1.24.4 executable, linking the current production `Probe.cs` for tokenization and calibrated decision math. It verifies the NVIDIA DXGI identity, pins all eight derived assets, executes one request and refuses success if a profile lacks DirectML or contains CPU graph kernels. No CPU retry is implemented. It is not referenced by the App/solution or copied to normal releases.

`tests/Inference.Tests/derive_directml_row_model.py` reproduces the graph/streamed-weight transformation from the pinned CPU bundle and verifies the resulting ONNX graphs. Its outputs matched all eight experiment asset digests. Unlike the first scratch manifest, it also updates `graphIO.encoder.inputs` with `embedding`; the scratch manifest's stale IO description is not the reproducible contract. ONNX 1.18.0 is the measured export tool, in the existing isolated developer environment. No Python dependency is added to end-user runtime.

The separate runner passed an **actual raw request through the shared production tokenizer**, matching the saved CPU tensors/choice/routing, with max logit delta 6.4e-6 and probability delta 7.85e-8. It used 679 MiB process peak and took 10,632 ms; the host started with 953 MiB available. The runner rejected both an Intel adapter and the original CPU bundle before model execution. Evidence: `artifacts/p4-gpu/standalone-0/`, `refusal-intel.log`, `refusal-stock-cpu.log`. The next raw case started with only 377 MiB available and was terminated at 187 MiB (`standalone-1/`); the chained third raw case did not start. Do not present three supplied-tensor passes as three complete production-tokenizer passes.

No new 536-case batch or CPU reference was run. Production selection, model distribution, general shape/adapter coverage, worker protocol integration, graceful in-App cancellation and quality remain open. This delivers explicit developer GPU computation requested by the user, **not** automatic GPU enablement or P4 acceptance.

## Original 2026-10-06 scope

All changes were isolated under ignored `artifacts/p4-gpu/`. Production remains on the existing CPU runtime and original model package. No driver, display setting or released asset was changed. The earlier P1 report establishes CPU parity, not an accepted DirectML backend.

DXGI enumeration identified adapter 0 as Intel Iris Xe and adapter 1 as NVIDIA RTX 2050 (about 4 GiB dedicated memory, driver `31.0.15.1700`). Software adapter 4 was not treated as usable hardware. Adapter indices came from DXGI, not the differently ordered WMI display list.

## Failure and bounded experimental correction

The original model failed at encoder `node_Reshape_81` on both hardware adapters with DirectML 1.22.1. DirectML 1.24.4—the latest stable DirectML NuGet found during this check—reproduced it on RTX 2050, while CPU control runs passed. Merely supplying actual free-dimension sizes did not fix the original graph.

The pinned runtime's [ReshapeHelper implementation](https://github.com/microsoft/onnxruntime/blob/v1.22.1/onnxruntime/core/providers/dml/OperatorAuthorHelper/OperatorHelper.cpp#L2278) copies the requested shape directly when `allowzero=1`, omitting `-1` inference. The failing graph uses `[batch, -1, 3, 12, 64]`. An isolated transformation changes `allowzero` only where a literal `-1` is provable through constants/shape concatenation: 44 encoder nodes and one head node. For valid ONNX shapes containing `-1`, `allowzero=1` forbids a simultaneous zero dimension; the changed attribute preserves that valid nonzero-shape interpretation. This is not a blanket transformation of arbitrary models or a claim of identical behavior for invalid inputs.

The original graphs/data were not overwritten; final hashing confirmed the original manifest's assets unchanged. The experimental graphs passed the ONNX checker and retained unchanged weights, tokenizer, configuration and candidate inputs. They are not installed by the product or published as a GPU package.

The diagnostic initially read UTF-8 input through Windows Console.In's default code page, corrupting non-ASCII input. Its tensor equality check caught this; those early results are not parity evidence. Reading the standard-input stream directly corrected it. Profiling prefix configuration also had to precede enabling profiling. These are diagnostic corrections, not product tokenizer changes.

## Measured checks

With DirectML 1.24.4 on adapter 1, sequential execution, memory patterns disabled, and actual `batch/seq/markers` dimensions supplied to each fresh session:

| Fixed case | Maximum Torch logit delta | Maximum official probability delta | GPU run portion | Full cold call |
| --- | ---: | ---: | ---: | ---: |
| mixed-language | 6.1781e-6 | 4.4840e-5 | 135 ms | 12,276 ms |
| literal mask / reversed candidates | 1.2017e-5 | 4.6827e-5 | 93 ms | 10,658 ms |
| long multilingual (408 tokens) | 2.2149e-6 | 2.3450e-5 | 180 ms | 9,998 ms |

All three tensor inputs matched the frozen reference exactly; choices and pending/proposed routing matched. Numerical deltas were below 1e-4. The official probability reference includes its output rounding; these numbers are not accuracy measurements.

Profiles reported one fused DirectML kernel per graph and no CPU-provider kernel events for these shape-specialized runs. Host tokenization, validation, copies and readback still consume CPU. Profile event counts/durations are not total GPU utilization or whole-request latency.

CPU controls took about 6.8–8.5 seconds end-to-end (389–944 ms in their run portions). The GPU calls were therefore **slower overall with the current cold-load-per-preview architecture**. CPU-process peak working sets during the GPU runs were about 1.47–1.78 GiB; GPU VRAM peak was **not measured** and must not be inferred from those figures.

## Decision and remaining gates

No automatic GPU selection or GPU UI option is enabled by this experiment. Remaining work includes artifact/runtime packaging, shape coverage, invalid-input semantics, cancellation/device failure, VRAM/resource limits and end-to-end performance with reusable sessions. A three-case experimental transform is not the full P4 quality corpus or release acceptance. CPU service remains available.

Evidence: `artifacts/p4-gpu/result.json`, `reshape-transform.json`, `source.json`, DXGI adapter inventory, per-case result/profiling files and the retained failure logs. The scratch generator records the production engine source hash; it changes only execution-provider/profiling configuration and measurement around the original arithmetic.
