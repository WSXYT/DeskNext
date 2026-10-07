"""One fixed small frozen-head training pilot. AI-assisted development data, never formal acceptance.
No downloads, full encoder, CPU fallback, automatic model activation or threshold fitting.
"""
import argparse
import ctypes
import hashlib
import json
import os
import random
import time
from pathlib import Path

import head_training_checkpoint as continuation
from development_corpus import canonical
from train_frozen_head import load_frozen_head, sha


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("prepared", type=Path)
    p.add_argument("features", type=Path)
    p.add_argument("checkpoint", type=Path)
    p.add_argument("upstream_common", type=Path)
    p.add_argument("output", type=Path)
    mode = p.add_mutually_exclusive_group()
    mode.add_argument("--scorer-only", action="store_true", help="Fixed comparison: freeze transformers/type embedding, 40 accumulated scorer updates at 1e-4")
    mode.add_argument("--accumulated-head", action="store_true", help="Fixed comparison: train decision-head transformers/type embedding/scorer, 40 accumulated updates at 1e-4")
    p.add_argument("--resume", type=Path, help="Verified local epoch checkpoint; output must still be a new directory")
    p.add_argument("--stop-file", type=Path, help="Local monitor request to checkpoint at the next completed epoch and exit")
    args = p.parse_args()
    args.output.mkdir(exist_ok=False)
    report = {"success": False, "formalAcceptance": False, "scope": __doc__}
    started = time.monotonic()
    (args.output / "source-trainer.py").write_bytes(Path(__file__).read_bytes())
    (args.output / "source-head-loader.py").write_bytes(Path(__file__).with_name("train_frozen_head.py").read_bytes())

    def stage(name):
        report["lastStage"] = name
        (args.output / "progress.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps({"stage": name, "elapsedSeconds": round(time.monotonic() - started, 3)}), flush=True)

    try:
        # Load CUDA kernels on first use instead of all GPU libraries eagerly on this small host.
        os.environ.setdefault("CUDA_MODULE_LOADING", "LAZY")
        report["cudaModuleLoading"] = os.environ["CUDA_MODULE_LOADING"]
        stage("import-runtime")
        import numpy as np
        import torch
        from safetensors.torch import save_file
        if not torch.cuda.is_available() or "RTX 2050" not in torch.cuda.get_device_name(0):
            raise RuntimeError("The explicit RTX 2050 CUDA training device is required; no CPU fallback")
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.GetCurrentProcess.restype = ctypes.c_void_p
        kernel.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        if not kernel.SetPriorityClass(kernel.GetCurrentProcess(), 0x4000):
            raise OSError("Cannot set below-normal priority")
        torch.set_num_threads(1)
        torch.set_num_interop_threads(1)
        torch.backends.cuda.matmul.allow_tf32 = False
        torch.backends.cudnn.allow_tf32 = False
        torch.backends.cuda.enable_flash_sdp(False)
        torch.backends.cuda.enable_mem_efficient_sdp(False)
        torch.backends.cuda.enable_math_sdp(True)
        stage("validate-inputs")
        data = json.loads((args.prepared / "pilot.json").read_text(encoding="utf-8"))
        contrastive = data.get("datasetId") == "desknext-contrastive-head-pilot-v1"
        if contrastive and not args.accumulated_head:
            raise ValueError("Contrastive pilot requires its fixed accumulated-head protocol")
        expected_training = ({"epochs": 40, "learningRate": 0.0001, "seed": 104729, "batchSize": 1,
                              "encoderFrozen": True, "actionHeadFrozen": True, "fitTemperature": False,
                              "mode": "accumulated-head", "gradientAccumulation": 44,
                              "headTransformerFrozen": False, "typeEmbeddingFrozen": False} if contrastive else
                             {"epochs": 6, "learningRate": 0.0003, "seed": 104729, "batchSize": 1,
                              "encoderFrozen": True, "actionHeadFrozen": True, "fitTemperature": False})
        if data["training"] != expected_training or data["provenance"]["heldOut"] is not False:
            raise ValueError("Pilot protocol changed")
        extraction = json.loads((args.features / "extraction.json").read_text())
        if extraction["complete"] is not True or len(data["cases"]) != (64 if contrastive else 30):
            raise ValueError("Incomplete feature extraction")
        source = json.loads((args.features / "source.json").read_text())
        if source["pilotSha256"] != sha(args.prepared / "pilot.json"):
            raise ValueError("Feature preparation/dataset mismatch")
        original_hash = sha(args.checkpoint)
        if original_hash != "9d628fd971b700382ac6f65920a86f149777b2e748e0c955fb3b19695aa8f204":
            raise ValueError("Not the original pinned checkpoint")
        report.update(protocol=data["training"], pilotSha256=source["pilotSha256"], checkpointSha256=original_hash,
                      commonSha256=sha(args.upstream_common), torch=torch.__version__, device=torch.cuda.get_device_name(0))
        torch.manual_seed(104729)
        torch.cuda.manual_seed_all(104729)
        rng = random.Random(104729)
        rows = []
        feature_hashes = {}
        digests = set()
        stage("load-frozen-features")
        for index, row in enumerate(data["cases"]):
            folder = args.features / f"{index:02d}"
            summary = json.loads((folder / "summary.json").read_text())
            metadata = json.loads((folder / "profiles/feature-input.json").read_text())
            result = json.loads((folder / "stdout.json").read_text())["result"]
            digest = hashlib.sha256(canonical({k: metadata["request"][k] for k in ("state", "instructions", "candidates")})).hexdigest()
            if summary["success"] is not True or metadata["request"] != row["request"] or digest != row["requestSha256"] or digest in digests:
                raise ValueError("Invalid or duplicate frozen input")
            digests.add(digest)
            shape = metadata["shape"]
            if shape[0] != 1 or not 1 <= shape[1] <= 256 or shape[2] != 768:
                raise ValueError("Unsupported pilot feature shape")
            tensor = metadata["tensors"]
            if tensor != result["tensors"]:
                raise ValueError("Feature/result tensor mismatch")
            file = folder / "profiles/encoder-features.f32"
            feature_hashes[row["id"]] = sha(file)
            hidden = torch.from_numpy(np.fromfile(file, dtype="<f4").reshape(shape)).cuda()
            if not bool(torch.isfinite(hidden).all()):
                raise ValueError("Non-finite frozen features")
            inputs = [torch.tensor(tensor[key], device="cuda", dtype=dtype) for key, dtype in
                      (("inputIds", torch.long), ("attentionMask", torch.long), ("markerPos", torch.long),
                       ("markerMask", torch.bool), ("qtype", torch.long))]
            candidates = [c["id"] for c in row["request"]["candidates"]]
            rows.append({**row, "hidden": hidden, "inputs": inputs, "target": candidates.index(row["targetId"]),
                         "candidates": candidates, "reference": result})
        stage("load-decision-head")
        model = load_frozen_head(torch, args.upstream_common, args.checkpoint, rows[0]["hidden"])
        accumulated = args.scorer_only or args.accumulated_head
        if args.scorer_only:
            model.head.requires_grad_(False)
            model.type_emb.requires_grad_(False)
        if accumulated:
            report["protocol"] = {**expected_training, "mode": "scorer-only" if args.scorer_only else "accumulated-head",
                                  "epochs": 40, "learningRate": 0.0001, "gradientAccumulation": 44 if contrastive else 20,
                                  "headTransformerFrozen": args.scorer_only, "typeEmbeddingFrozen": args.scorer_only}
        train = [r for r in rows if r["split"] == "train"]
        checks = [r for r in rows if r["split"] == "development-check"]
        if (len(train), len(checks)) != ((44, 20) if contrastive else (20, 10)):
            raise ValueError("Wrong fixed split sizes")

        def evaluate(items):
            answers = []
            with torch.no_grad():
                for row in items:
                    model.encoder.hidden = row["hidden"]
                    logits, _ = model(*row["inputs"])
                    probabilities = logits[0].softmax(-1).cpu().tolist()
                    choice = row["candidates"][logits[0].argmax().item()]
                    answers.append({"id": row["id"], "targetId": row["targetId"], "choice": choice,
                                    "probabilities": probabilities, "logits": logits[0].cpu().tolist(), "correct": choice == row["targetId"]})
            return answers

        stage("compare-original-head")
        before = evaluate(rows)
        delta = max(abs(a - b) for row, answer in zip(rows, before, strict=True)
                    for a, b in zip(row["reference"]["logits"], answer["logits"], strict=True))
        if delta > 1e-4:
            raise ValueError("Initial CUDA head/DirectML frozen-feature logits differ")
        report["initialHeadVsDirectMlMaxLogitDelta"] = delta
        parameters = [p for p in model.parameters() if p.requires_grad]
        report["trainableParameters"] = sum(p.numel() for p in parameters)
        optimizer = torch.optim.AdamW(parameters, lr=0.0001 if accumulated else 0.0003, weight_decay=0)
        epochs = 40 if accumulated else 6
        losses = []
        contract = {"pilot": source["pilotSha256"], "baseCheckpoint": original_hash,
                    "architecture": report["commonSha256"], "features": feature_hashes,
                    "protocol": report["protocol"], "torch": str(torch.__version__),
                    "trainer": sha(Path(__file__)), "loader": sha(Path(__file__).with_name("train_frozen_head.py"))}
        if args.resume:
            losses = continuation.restore(torch, args.resume, model, optimizer, rng, contract)
            if not 0 <= len(losses) <= epochs:
                raise ValueError("Checkpoint is beyond the fixed training schedule")
            report["resumedFrom"] = {"path": str(args.resume), "sha256": sha(args.resume), "completedEpochs": len(losses)}
        torch.cuda.reset_peak_memory_stats()
        # Fixed order shuffle/epochs, dropout disabled; never choose a checkpoint by check-set score.
        stage("train-fixed-protocol")
        for epoch in range(len(losses), epochs):
            order = list(train)
            rng.shuffle(order)
            total = 0.0
            if accumulated:
                optimizer.zero_grad(set_to_none=True)
            for row in order:
                if time.monotonic() - started > 180:
                    raise TimeoutError("Bounded training budget exceeded")
                model.encoder.hidden = row["hidden"]
                if not accumulated:
                    optimizer.zero_grad(set_to_none=True)
                logits, _ = model(*row["inputs"])
                loss = torch.nn.functional.cross_entropy(logits, torch.tensor([row["target"]], device="cuda"))
                if not bool(torch.isfinite(loss)):
                    raise ValueError("Non-finite loss")
                (loss / len(train) if accumulated else loss).backward()
                if not accumulated:
                    torch.nn.utils.clip_grad_norm_(parameters, 1.0, error_if_nonfinite=True)
                    optimizer.step()
                total += loss.item()
            if accumulated:
                torch.nn.utils.clip_grad_norm_(parameters, 1.0, error_if_nonfinite=True)
                optimizer.step()
            losses.append(total / len(train))
            print(json.dumps({"epoch": epoch + 1, "trainLoss": losses[-1]}), flush=True)
            stop_requested = args.stop_file is not None and args.stop_file.exists()
            if len(losses) % 5 == 0 or len(losses) == epochs or stop_requested:
                report["lastCheckpoint"] = continuation.save(torch, args.output, model, optimizer, rng, losses, contract)
                report["completedEpochs"] = len(losses)
                stage("checkpoint-saved")
            if stop_requested:
                raise InterruptedError("Local monitor requested a checkpointed stop; resume the remaining fixed epochs")
        stage("evaluate-fixed-final-head")
        after = evaluate(rows)
        report.update(success=True, epochs=epochs, updates=epochs if accumulated else epochs * len(train), trainLossByEpoch=losses,
                      trainAgreementBefore=sum(x["correct"] for r, x in zip(rows, before, strict=True) if r["split"] == "train"),
                      trainAgreementAfter=sum(x["correct"] for r, x in zip(rows, after, strict=True) if r["split"] == "train"),
                      checkAgreementBefore=sum(x["correct"] for r, x in zip(rows, before, strict=True) if r["split"] == "development-check"),
                      checkAgreementAfter=sum(x["correct"] for r, x in zip(rows, after, strict=True) if r["split"] == "development-check"),
                      peakCudaAllocatedMiB=round(torch.cuda.max_memory_allocated()/1048576,2),
                      peakCudaReservedMiB=round(torch.cuda.max_memory_reserved()/1048576,2),
                      featureHashes=feature_hashes, before=before, after=after)
        weights = {k: v.detach().cpu().contiguous() for k, v in model.state_dict().items() if not k.startswith("encoder.")}
        save_file(weights, str(args.output / "unaccepted-head.safetensors"), metadata={"scope": "fixed synthetic development pilot; not accepted/calibrated"})
        report["headSha256"] = sha(args.output / "unaccepted-head.safetensors")
        if sha(args.checkpoint) != original_hash:
            raise ValueError("Original checkpoint changed")
    except BaseException as error:
        report["success"] = False
        report["error"] = type(error).__name__ + ": " + str(error)
    finally:
        report["elapsedSeconds"] = round(time.monotonic() - started, 3)
        (args.output / "report.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
        (args.output / "source-trainer.py").write_bytes(Path(__file__).read_bytes())
        (args.output / "source-head-loader.py").write_bytes(Path(__file__).with_name("train_frozen_head.py").read_bytes())
        print(json.dumps({k:v for k,v in report.items() if k not in ("before", "after", "featureHashes", "scope")},indent=2),flush=True)
    if not report["success"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
