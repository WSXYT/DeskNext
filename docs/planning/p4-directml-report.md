# DirectML feasibility — developer-only execution, not an App backend

## Isolated workbench-to-GPU workflow (2026-10-07)

`DeskNest.App --directml-preview-smoke --gpu-worker=<absolute-exe> --local-model=<derived-model> --gpu-adapter=1` now connects the actual Main/Studio classification callbacks to the separate self-contained NVIDIA worker. An internal constructor injection supplies only the worker start factory; normal constructors retain the original CPU factory. No backend path comes from saved metadata, and nothing changes the public CPU bundle/default. A twelve-language experimental NVIDIA label distinguishes the local result and provider label from CPU. The explicit external worker is trusted developer code, not an arbitrary downloaded executable authenticated by this mode.

The focused workflow reuses the existing file/pending-model fixture rather than running the whole regression matrix. It verifies model-file checks, file preview, session reuse for pending preview, transient hint editing and stale-result discard, cancellation, newly created category selection, and model suggestion followed by **separate manual confirmation**, journaled import and undo. No model directly moves a file. Four completed previews produced eight retained graph profiles, all DirectML and no CPU graph kernels. The workflow ended with no retained local worker; two worker starts reflect explicit cancellation then a fresh later request, not a hidden CPU retry.

`artifacts/p4-gpu/workbench-preview.log` reports `Success=true`, `DirectMlPreviewVerified=true`, `LocalClassificationPreviewVerified=true`, `LocalWorkerReuseVerified=true`, 8 graph profiles, 47,312 ms for the full workflow. The temporary fixture root is recorded in that result. App Release build: zero warnings/errors; strict probe-driver checks: 11/11; twelve dictionaries: identical keys and localized experimental label. No live Jev call, large CPU evaluation, full P3 test/CI/package cycle, native desktop compositing or quality claim. LSP confirmed two changed files clean and two inconclusive, not four clean.

This is a diagnostic workbench integration, **not** normal user GPU selection or distribution acceptance. That distinction remains visible in flags, labels and documentation.

## Existing-client / versioned GPU worker integration (2026-10-07)

The separate developer executable now accepts `--adapter=N --profiles=<new-root> --inference-worker <model> --protocol=1`. It uses the existing 1 MiB `Probe.WorkerRequest`/`WorkerReply` contract, checks version/request/model digest before model execution, rehashes the pinned assets per request and checks the manifest afterward. Stdout is frames only. The same `RunVerified` method serves both standalone JSON and worker modes; no second math or tokenizer implementation was added. Each request retains sequential encoder/head loading and releases both graph sessions; process reuse is **not** warm session caching.

The unmodified production `LocalPreviewSession` supplies model-hash framing, accepts the responses, reuses one worker process and owns cancellation/disposal. Three fixed **raw requests** (not supplied tensors) completed on the same RTX 2050 process, with exact token tensors and matching choices/routing. CPU references were reused, not recomputed. Per-case request times were 9,895 / 9,542 / 9,613 ms; maximum logit differences from saved CPU results were 6.4373e-6 / 3.8147e-6 / 5.1260e-6, probability differences at most 1.0208e-6. Six retained profiles show one DirectML kernel each and no CPU-provider kernels. Host tokenization/embedding reads/postprocessing are still CPU work.

Cancelling the following real request retired the process and cleared the client PID without restarting it; it does not prove device removal or interruption at every GPU instruction. Oversized frame, bad protocol and wrong manifest requests were rejected before graph profiles were created. `artifacts/p4-gpu/versioned-worker-followup/directml-worker-followup.trx` records **2 executed/passed, 0 failed/skipped**. The preceding check in `versioned-worker/directml-worker.trx` failed via memory-pressure cancellation, remains retained, and is not reclassified as a pass. The host's available memory subsequently increased; no user process or machine setting was changed. Source diagnostics still show stale missing NuGet types while the separate build and actual tests compile/execute; no clean-LSP claim.

This closes a bounded existing-client-to-GPU-protocol path only. App backend selection, released/installed GPU runtime and model artifacts, graceful UI cancellation, broad shape/adapter coverage and classifier quality remain open. The App and public model bundle stay CPU-only; no automatic GPU selector is exposed.

### Self-contained developer directory

Source `f137b70` was self-contained-published for Windows x64 into `artifacts/p4-gpu/runtime-f137b70/`, separate from all App/CPU outputs. It includes DirectML/ORT, .NET runtime and the actual tokenizer native filename `tokenizers_proto.dll` (not `tokenizers.dll`). No model weights or third-party dataset are included. A direct executable invocation from that published directory processed the long multilingual raw request with production tokenization and DirectML graph profiles: exit 0, matching tensor/choice/pending result, CPU-reference logit delta 5.12e-6 and probability delta 1.0208e-6, 9,323 ms, 676 MiB process peak (`published-worker-check/summary.json`). It performed no CPU inference fallback. The host still performs tokenization, row reads and postprocessing. This is **not** an installed App test, signed artifact, clean machine or peak-VRAM measurement. No public release was created or replaced.

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
