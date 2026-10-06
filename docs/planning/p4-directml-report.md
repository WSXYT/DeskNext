# DirectML feasibility — not enabled (2026-10-06)

## Scope

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
