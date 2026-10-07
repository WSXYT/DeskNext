"""Evaluate one fixed adapted head against an existing frozen-feature development set.
No optimization, model selection, CPU inference, cloud calls or acceptance claim.
"""
import argparse
import ctypes
import hashlib
import json
import os
import time
from pathlib import Path

from development_corpus import canonical
from train_frozen_head import load_frozen_head, sha


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("prepared", type=Path)
    p.add_argument("features", type=Path)
    p.add_argument("base_checkpoint", type=Path)
    p.add_argument("adapted_head", type=Path)
    p.add_argument("training_report", type=Path)
    p.add_argument("upstream_common", type=Path)
    p.add_argument("output", type=Path)
    args = p.parse_args()
    args.output.mkdir(exist_ok=False)
    report = {"success": False, "formalAcceptance": False, "scope": __doc__}
    started = time.monotonic()
    try:
        os.environ.setdefault("CUDA_MODULE_LOADING", "LAZY")
        import numpy as np
        import torch
        from safetensors.torch import load_file
        if not torch.cuda.is_available() or "RTX 2050" not in torch.cuda.get_device_name(0):
            raise RuntimeError("Explicit NVIDIA CUDA device required; no CPU fallback")
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.GetCurrentProcess.restype = ctypes.c_void_p
        kernel.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        if not kernel.SetPriorityClass(kernel.GetCurrentProcess(), 0x4000):
            raise OSError("Cannot lower priority")
        torch.set_num_threads(1)
        torch.set_num_interop_threads(1)
        torch.backends.cuda.matmul.allow_tf32 = False
        torch.backends.cudnn.allow_tf32 = False
        torch.backends.cuda.enable_flash_sdp(False)
        torch.backends.cuda.enable_mem_efficient_sdp(False)
        torch.backends.cuda.enable_math_sdp(True)
        training = json.loads(args.training_report.read_text(encoding="utf-8"))
        if training["success"] is not True or training["headSha256"] != sha(args.adapted_head) or training["checkpointSha256"] != sha(args.base_checkpoint) or training["commonSha256"] != sha(args.upstream_common):
            raise ValueError("Fixed adapted head or provenance mismatch")
        data = json.loads((args.prepared / "pilot.json").read_text(encoding="utf-8"))
        if not 1 <= len(data["cases"]) <= 100 or data["provenance"]["heldOut"] is not False:
            raise ValueError("Expected a bounded development set")
        source = json.loads((args.features / "source.json").read_text(encoding="utf-8"))
        if source["pilotSha256"] != sha(args.prepared / "pilot.json"):
            raise ValueError("Mismatched feature preparation")
        rows = []
        for i, row in enumerate(data["cases"]):
            folder = args.features / f"{i:02d}"
            metadata = json.loads((folder / "profiles/feature-input.json").read_text(encoding="utf-8"))
            result = json.loads((folder / "stdout.json").read_text(encoding="utf-8"))
            if metadata["request"] != row["request"] or metadata["tensors"] != result["result"]["tensors"] or result["success"] is not True:
                raise ValueError("Invalid frozen-feature evidence")
            if hashlib.sha256(canonical({k: row["request"][k] for k in ("state", "instructions", "candidates")})).hexdigest() != row["requestSha256"]:
                raise ValueError("Input digest mismatch")
            shape = metadata["shape"]
            if len(shape) != 3 or shape[0] != 1 or not 1 <= shape[1] <= 256 or shape[2] != 768:
                raise ValueError("Unsupported feature shape")
            file = folder / "profiles/encoder-features.f32"
            h = torch.from_numpy(np.fromfile(file, dtype="<f4").reshape(shape)).cuda()
            t = metadata["tensors"]
            inputs = [torch.tensor(t[k], dtype=d, device="cuda") for k, d in
                      (("inputIds", torch.long), ("attentionMask", torch.long), ("markerPos", torch.long), ("markerMask", torch.bool), ("qtype", torch.long))]
            rows.append({"row": row, "h": h, "inputs": inputs, "reference": result["result"], "featureSha256": sha(file)})
        model = load_frozen_head(torch, args.upstream_common, args.base_checkpoint, rows[0]["h"])
        def evaluate():
            outputs = []
            with torch.no_grad():
                for item in rows:
                    model.encoder.hidden = item["h"]
                    logits, _ = model(*item["inputs"])
                    if not bool(torch.isfinite(logits).all()):
                        raise ValueError("Non-finite head output")
                    outputs.append(logits[0].cpu().tolist())
            return outputs
        before = evaluate()
        delta = max(abs(a - b) for item, values in zip(rows, before, strict=True) for a, b in zip(item["reference"]["logits"], values, strict=True))
        if delta > 1e-4:
            raise ValueError("Base head no longer matches its feature reference")
        model.load_state_dict(load_file(str(args.adapted_head), device="cpu"), strict=True)
        after = evaluate()
        cases = []
        for item, b, a in zip(rows, before, after, strict=True):
            row = item["row"]
            ids = [c["id"] for c in row["request"]["candidates"]]
            before_choice = ids[min(range(len(ids)), key=lambda i: (-b[i], ids[i]))]
            after_choice = ids[min(range(len(ids)), key=lambda i: (-a[i], ids[i]))]
            cases.append({"id": row["id"], "originSplit": row["split"], "targetId": row["targetId"],
                          "before": before_choice, "after": after_choice, "beforeCorrect": before_choice == row["targetId"],
                          "afterCorrect": after_choice == row["targetId"], "featureSha256": item["featureSha256"],
                          "beforeLogits": b, "afterLogits": a})
        report.update(success=True, datasetId=data["datasetId"], inputSha256=source["pilotSha256"],
                      adaptedHeadSha256=training["headSha256"], trainingReportSha256=sha(args.training_report),
                      baseHeadLogitDelta=delta, count=len(cases), beforeCorrect=sum(c["beforeCorrect"] for c in cases),
                      afterCorrect=sum(c["afterCorrect"] for c in cases),
                      regressions=[c["id"] for c in cases if c["beforeCorrect"] and not c["afterCorrect"]],
                      wrongConcreteForSpecial=[c["id"] for c in cases if c["targetId"] in ("filename-ambiguous", "categories-insufficient") and c["after"] not in ("filename-ambiguous", "categories-insufficient")],
                      cases=cases, device=torch.cuda.get_device_name(0), peakCudaAllocatedMiB=round(torch.cuda.max_memory_allocated()/1048576, 2))
    except BaseException as error:
        report["error"] = type(error).__name__ + ": " + str(error)
    finally:
        report["elapsedSeconds"] = round(time.monotonic() - started, 3)
        (args.output / "report.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
        (args.output / "source-evaluator.py").write_bytes(Path(__file__).read_bytes())
        print(json.dumps({k: v for k, v in report.items() if k not in ("scope", "cases")}, indent=2), flush=True)
    if not report["success"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
