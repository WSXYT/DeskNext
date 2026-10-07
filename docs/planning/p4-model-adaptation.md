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

The checkpoint `ee5fc045834ca9d6822a03778b1d59b7e4cda024d232168d62777c37dc93e0d7` is **not adopted or exported into the application**. A one-case gain cannot justify the fallback regression.

### Conservative scorer-only comparison — no classification gain

The fixed `--scorer-only` comparison restarted from original weights, froze the two head Transformer layers and type embedding as well as the encoder/action head, and trained only 592,897 scorer parameters. Forty updates at 1e-4 accumulated gradients over all twenty training items per update; the final checkpoint was fixed in advance. Training loss decreased **1.327526 → 1.164982**, but agreement remained **12/20 training and 6/10 development checks**, with all check-set choices unchanged. The two uncovered recipes still selected ambiguity. Peak CUDA allocation/reservation were **95.75/116 MiB**, elapsed **18.484 seconds**. Evidence: `pilot-trained-scorer-v1/report.json`; unaccepted head SHA-256 `cd6580d79e73adaf3ae4dd71ad2440a48ee7891ad50344cd2547f6e1aef5df16`. This model is not adopted: lower training loss alone is not useful classification improvement.

### Accumulated-head comparison — completed, not adopted

The `--accumulated-head` control retained 40 updates, 1e-4 rate, twenty-item gradient accumulation and original weights, while training Transformer/type/scorer parameters. Two starts were terminated after epoch 39 and 34 by host-memory pressure (176 and 60 MiB available); neither saved a final model. Their loss logs are retained, not accepted runs.

The trainer then gained optional local continuation checkpoints every five completed epochs, including model/optimizer, shuffle and Torch/CUDA RNG state, losses, and the exact data/features/protocol/code identity. A create-new checksum receipt is written only after each checkpoint completes; restore uses restricted `weights_only=True` and rejects a changed contract or bytes. A tiny CUDA regression proved the next optimizer update and tensors exactly match after restore. This is local trusted training-artifact continuation, not an imported-model or filesystem-transaction protocol. A cooperative stop flag permits checkpointing at an epoch boundary; hard emergency termination remains a last resort.

`pilot-checkpoint-1/report.json` completed all forty epochs from original weights without needing a resume. The monitor requested stopping near shutdown, after training/report had already completed, so the successful report must not be portrayed as a resumed full training validation. Loss decreased **1.327526 → 0.583076**, training agreement **12/20 → 15/20**, but development agreement regressed **6/10 → 5/10** (Chinese Logo became categories-insufficient). Peak CUDA allocation remained **367.43 MiB**; script time **46.688 seconds** includes checkpoint writes. Final head SHA-256 `3756cecc30e8c26bd5aa194a35bb04597a0368cf01c0154ee83c679c3c1a996d` is not adopted/exported. Checkpoints are for continuation only, not retrospective selection of an earlier favorable epoch.

The small optimizer comparisons are closed. All inspected examples remain development data, not independent P4 acceptance.

### Fixed category-set contrastive data

`prepare_contrastive_head_pilot.py` defines a separate **44-training / 20-development-check** pilot before model execution. Training uses English/Chinese invoice, logo, authentication, soup, itinerary and laboratory-result subjects, each with the relevant category present, absent and with ordinary candidate order reversed, plus unclear-name/hint pairs. Check subjects use receipts, posters, database schemas, cakes and different generic names. Semantic groups and complete request inputs do not overlap between those two partitions. Labels follow the explicitly authored subject and provided category set; filenames/hints are data, and expected labels are never sent to the encoder.

The taxonomy varies four of six categories (Finance/Design/Software/Cooking/Travel/Science) plus both fixed reserved outcomes. Ordinary option IDs still use the production c0/c1 mapping for the current order. This tests category applicability rather than only a fixed filename→position shortcut. The data remains AI-assisted synthetic, not independent human acceptance. It does not include the public filename-reference corpus or private user files.

Frozen preparation SHA-256: `5ee1beede9d1a00946ff56f00995dcb95fd7a6271b888fc293e76b0d455a9a42`; protocol: accumulated-head, 40 updates at 1e-4, batch size one with 44-example gradient accumulation, original checkpoint, encoder/action head frozen, no calibration fitting or intermediate-checkpoint selection. The two small preparation tests pass, including absent-category labels and label-ID remapping after reversal. The first serial NVIDIA extraction completed 23 inputs before emergency memory cancellation at index 23. Those successful records were reused unchanged, not rerun. A temporary pre-tokenization script initially omitted the space after the option-ID colon; strict comparison to the production output caught that discrepancy before any new features were used. The first continuation exited with no work because its required preparation marker was absent. Neither failure is claimed as passed.

After correcting only that temporary script, all 23 prior token tensors matched exactly; the remaining 41 inputs then completed with precomputed tensors on DirectML. `contrastive-features-tensor-continuation-v2/extraction.json` reports 64 complete cases, of which 23 are reused. Original request/label digest remains unchanged. This is complete feature extraction, not a trained/accepted classifier or a new CPU-inference baseline.

The first contrastive training attempt was terminated during CUDA feature loading, before the first epoch or checkpoint (`contrastive-checkpoint-1-status.json`, available system RAM 94 MiB). Its attempted source/progress and the extracted features remain intact. One bounded local monitor waits for stable host headroom to run the identical fixed protocol; no training outcome is claimed until its final report exists.

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
