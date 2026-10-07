# P4 frozen-encoder adaptation — development only

## Decision and environment

On 2026-10-07 the user selected **continue model adaptation**, retaining the original P4 quality gate rather than moving it to a later phase. Live Jev calls remain disallowed. No new P5–P7 features are authorized by this adaptation work.

The current host has an RTX 2050 with 4 GiB VRAM and NVIDIA driver 517.00 (CUDA 11.7). The established export environment remains `artifacts/py-venv`, PyTorch **2.7.1+cpu**. DirectML inference does not provide PyTorch gradient training. A separate `artifacts/p4-training/cuda117` environment uses PyTorch **2.0.1+cu117**, NumPy 1.26.4 and safetensors 0.5.3. Its wheel was obtained from the official PyTorch CUDA 11.7 index and checked against SHA-256 `a77ba4f4b13c8b6c2c863b84a98dde2ddf1feaad5f25700d41cf3236e11d2ee8`. The first pip download timed out; a retained curl transfer subsequently completed and verified before local installation. Logs preserve the failed attempt. Temporary files and caches were placed on D:, not the nearly-full system drive. No drivers, system settings or prior environments were modified.

This older CUDA runtime is a **local training experiment**, not an application dependency or a proposed runtime upgrade/downgrade. The exact environment is recorded in `artifacts/p4-training/training-environment.txt`. A small real CUDA matrix operation/backward check confirmed the named RTX GPU and finite gradients. No CPU fallback is allowed.

## Why freeze the encoder

The pinned checkpoint contains about 29.94 MB of non-encoder FP16 weights. Loading only the decision head avoids the full multilingual encoder and its large token table. The head is still the upstream `DecisionModel` architecture: two transformer layers, type embedding, scorer and action head. The encoder is replaced only in the development trainer by an immutable feature-returning module with **zero trainable parameters**. The action head is frozen: no action labels or automatic-file policy are trained.

The existing isolated NVIDIA runner accepts an explicit diagnostic `--export-features` flag. After GPU encoder execution it writes `encoder-features.f32` and bounded input/shape metadata alongside the ordinary response and profiles, then still runs the original head. This is opt-in only, never set for the App/versioned worker. Feature files can encode supplied text, so they belong in private ignored artifacts, not release assets. Altering a filename, hint, candidate list or order requires fresh encoder features because the encoder is bidirectional over the entire request.

`tests/Inference.Tests/train_frozen_head.py` loads the explicitly supplied trusted local `laya/common.py`, records its SHA-256, constructs that head, and reads only non-encoder tensors from the pinned safetensors checkpoint. It verifies the untrained CUDA-head logits against the prior DirectML output before any update. The script accepts one short feature matrix and at most 16 updates; this is a capability check, not a training campaign or automatic tuning system.

## First actual training capability result

Evidence: `artifacts/p4-training/head-capability-v1/report.json` and `head-capability-v1-status.json`.

- Input: previously inspected `zh-CN:invoice` from the AI-assisted development corpus, explicit training target `c0` (Finance). **Not held out**.
- Frozen features: shape `[1,125,768]`, 384,000 bytes; SHA-256 `d17ce07d19e00569620696b0bd8db9777e8c945f4b0f945908cb9b5d432bb337`.
- Original checkpoint SHA-256: `9d628fd971b700382ac6f65920a86f149777b2e748e0c955fb3b19695aa8f204`, unchanged after training.
- Upstream architecture SHA-256: `84cd569620b75415d2b10e7b29e278269a82d97d5716f81fec098708c1c0dc6e`.
- CUDA/DirectML initial head-logit difference: **9.5367e-7** (threshold 1e-4).
- Trainable parameters: **14,770,945**, all on CUDA; encoder parameters loaded into Torch: **0**.
- Eight deterministic dropout-disabled AdamW updates at learning rate 1e-5; gradients finite and scorer parameters changed.
- Training-example loss: **2.971875 → 2.850521**. Final choice remains **filename-ambiguous**, not the target.
- Peak CUDA allocation **356.72 MiB**, allocator reservation **412 MiB**, total script time **12.937 seconds**. These are PyTorch allocator figures, not whole-adapter/host peak measurements.
- Saved `unaccepted-head.safetensors` only inside this experiment, SHA-256 `07c40196f75d85a23628aa9dd5eddc3dff7ce5d7d63e88bded3d7af4908ac802`.

This demonstrates genuine discrete-GPU backpropagation and an update to the existing head, **not** useful classification generalization, fitted calibration, a new accepted model or P4 completion. The output is not exported into the application, and no model manifest/pins/defaults were changed to adopt it.

## Reproduce the capability check

Use a new output directory each time. The CUDA training environment is developer-only; no end-user Python requirement is introduced.

```sh
# Extract one explicit development request with the already verified GPU runner.
python tests/Inference.Tests/run_directml_check.py /absolute/derived-model request.json \
  artifacts/training/features --adapter 1 --export-features --reference prior-result.json

# Run the small head-only CUDA capability check using an explicitly chosen development label.
artifacts/p4-training/cuda117/Scripts/python.exe tests/Inference.Tests/train_frozen_head.py \
  artifacts/training/features artifacts/model/multilingual/model.safetensors \
  artifacts/laya-source/laya/common.py artifacts/training/head-capability --target c0 --steps 8
```

## Fixed 20/10 development pilot

`prepare_head_pilot.py` fixes twenty newly authored English/Chinese training names and ten already-inspected original development checks across Finance, Design, Software, ambiguous and insufficient categories. No request input is duplicated across the two partitions; the checks are development-only because their prior outcomes were already inspected. The public English human-reported filename set is not used for this training. Fixed protocol: six epochs, batch size one, seed 104729, AdamW learning rate 0.0003, no fitted temperature, frozen encoder/action head, deterministic dropout-disabled updates. Final weights are selected by the fixed update count, never by the ten-case score.

The preparation digest is `2fba69b42e04df71711cf498a9f79ec1aa77c2d9e336c1eac7fe4bd91aef796f`. All thirty raw requests completed through the explicit NVIDIA feature exporter (`artifacts/p4-training/pilot-features-v1/`, `extraction.json complete=true`). It retains per-case model/feature/result evidence. The small split-contract test passed; it does not evaluate model quality.

Two training starts were terminated by the external memory monitor before the first epoch completed: `pilot-training-v1-status.json` records available RAM 144 MiB; the `CUDA_MODULE_LOADING=LAZY` follow-up records 188 MiB. The latter's stage journal places it at first frozen-feature CUDA transfer, before decision-head loading or optimizer steps. Neither has a training result or accepted checkpoint. Lazy loading did not eliminate host pressure. Source/progress are now written before loading so a hard interruption retains the exact attempted procedure. No user processes/drivers/settings were changed and no CPU fallback was taken. A single bounded admission monitor (`pilot-admission-status.json`) later admitted the unchanged protocol with 2,140 MiB available system memory. That run completed; it does not erase the two earlier failures or establish that lazy loading repaired memory pressure.

### Fixed final result — not adopted

`artifacts/p4-training/pilot-trained-v1-headroom/report.json` records 120 actual CUDA updates in six fixed epochs, with initial CUDA-head/DirectML maximum logit difference **1.4194e-6**, peak allocation **367.43 MiB** and reservation **424 MiB**. The script completed in **18.032 seconds**, original checkpoint unchanged. Training agreement moved **12/20 → 14/20**; the separate but already-inspected development checks moved **6/10 → 7/10**. Chinese authentication improved, but the Chinese uncovered recipe moved from ambiguous to the wrong concrete Software category; neither recipe selected categories-insufficient. Epoch-average training loss fluctuated (1.507, 1.946, 2.077, 1.914, 2.080, 1.663).

The checkpoint `ee5fc045834ca9d6822a03778b1d59b7e4cda024d232168d62777c37dc93e0d7` is **not adopted or exported into the application**. A one-case gain cannot justify the fallback regression. A follow-up may compare a more constrained scorer-only update, but all inspected examples remain development data; no result here closes P4's independent quality requirement.

```sh
python tests/Inference.Tests/prepare_head_pilot.py artifacts/training/pilot
python tests/Inference.Tests/extract_head_pilot.py artifacts/training/pilot \
  /absolute/derived-model artifacts/training/pilot-features --adapter 1
artifacts/p4-training/cuda117/Scripts/python.exe tests/Inference.Tests/adapt_frozen_head.py \
  artifacts/training/pilot artifacts/training/pilot-features \
  artifacts/model/multilingual/model.safetensors artifacts/laya-source/laya/common.py \
  artifacts/training/pilot-result
```

Keep GPU extraction and training serial, not concurrently resident. Runtime requests are not reused as training labels. The 120-case AI-assisted development corpus and public English filename reference keep their existing reports; no favorable training loss replaces them. The fixed small split and result above are development work; an export/inference-equivalence check is appropriate **only if** a later adaptation is worth retaining. Independent human-held-out quality, fallback behavior, multilingual/long-tail coverage and automatic-operation safety remain required and unpassed.
