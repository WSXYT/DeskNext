"""Bounded CUDA-only capability check for Laya's frozen-encoder decision head.

This deliberately overfits ONE already-seen development example. It is not a
quality evaluation, new accepted model, calibration fit, or automatic-file policy.
The full encoder is never loaded into Torch; inputs come from the explicit
DirectML --export-features developer path. No downloads or CPU fallback.
"""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import sys
import time
from pathlib import Path


def sha(path):
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def load_frozen_head(torch, common_path, checkpoint_path, hidden):
    """Load only upstream non-encoder weights; explicitly supplied source is trusted developer code."""
    from safetensors import safe_open
    from types import SimpleNamespace

    spec = importlib.util.spec_from_file_location("desknext_upstream_common", common_path)
    common = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(common)

    class FrozenFeatures(torch.nn.Module):
        def __init__(self, features):
            super().__init__()
            self.config = SimpleNamespace(hidden_size=768)
            self.hidden = features

        def forward(self, input_ids, attention_mask):
            return SimpleNamespace(last_hidden_state=self.hidden)

    model = common.DecisionModel(FrozenFeatures(hidden), head_layers=2, n_act=2).float()
    with safe_open(str(checkpoint_path), framework="pt", device="cpu") as weights:
        state = {k: weights.get_tensor(k).float() for k in weights.keys() if not k.startswith("encoder.")}  # noqa: SIM118 - safe_open is not a mapping
    model.load_state_dict(state, strict=True)
    model.cuda().eval()
    model.act_head.requires_grad_(False)
    return model


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("features", type=Path, help="Successful DirectML output directory with stdout.json and profiles/feature-input.json")
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("upstream_common", type=Path, help="Explicit trusted local laya/common.py")
    parser.add_argument("output", type=Path)
    parser.add_argument("--target", required=True, help="Explicit development training target ID, not a model-generated label")
    parser.add_argument("--steps", type=int, default=8)
    args = parser.parse_args()
    if not 1 <= args.steps <= 16:
        parser.error("Capability check is bounded to 1..16 updates, not a training campaign")
    args.output.mkdir(parents=True, exist_ok=False)
    report = {"success": False, "formalAcceptance": False, "scope": __doc__,
              "target": args.target, "updatesRequested": args.steps, "sourceWeightsModified": False}
    started = time.monotonic()
    try:
        if sys.platform == "win32":
            # Below-normal priority only for this process; never touch other applications or the driver.
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.GetCurrentProcess.restype = ctypes.c_void_p
            kernel.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
            if not kernel.SetPriorityClass(kernel.GetCurrentProcess(), 0x4000):
                raise OSError("Cannot lower training process priority")
        import numpy as np
        import torch
        from safetensors.torch import save_file

        if not torch.cuda.is_available():
            raise RuntimeError("CUDA training unavailable; CPU fallback is forbidden")
        device = torch.cuda.get_device_name(0)
        if "RTX 2050" not in device:
            raise RuntimeError("This bounded capability check expects the explicitly selected RTX 2050")
        torch.set_num_threads(1)
        torch.set_num_interop_threads(1)
        torch.manual_seed(104729)
        torch.cuda.manual_seed_all(104729)
        torch.backends.cuda.matmul.allow_tf32 = False
        torch.backends.cudnn.allow_tf32 = False
        torch.backends.cuda.enable_flash_sdp(False)
        torch.backends.cuda.enable_mem_efficient_sdp(False)
        torch.backends.cuda.enable_math_sdp(True)
        report.update(torch=torch.__version__, cuda=torch.version.cuda, device=device,
                      architectureSourceSha256=sha(args.upstream_common))
        checkpoint_hash = sha(args.checkpoint)
        if checkpoint_hash != "9d628fd971b700382ac6f65920a86f149777b2e748e0c955fb3b19695aa8f204":
            raise ValueError("Not the original pinned Laya checkpoint")
        report["checkpointSha256"] = checkpoint_hash
        raw_path = args.features / "profiles/encoder-features.f32"
        metadata = json.loads((args.features / "profiles/feature-input.json").read_text(encoding="utf-8"))
        measured = json.loads((args.features / "stdout.json").read_text(encoding="utf-8"))
        checked = json.loads((args.features / "summary.json").read_text(encoding="utf-8"))
        if checked["success"] is not True or measured["success"] is not True or measured.get("featuresExported") is not True:
            raise ValueError("Feature extraction has no successful GPU receipt")
        shape = metadata["shape"]
        if len(shape) != 3 or shape[0] != 1 or not 1 <= shape[1] <= 256 or shape[2] != 768 or metadata["dtype"] != "float32-le":
            raise ValueError("Capability input must be one <=256-token frozen feature matrix")
        if raw_path.stat().st_size != 4 * shape[1] * 768:
            raise ValueError("Incomplete encoder feature bytes")
        candidates = [c["id"] for c in metadata["request"]["candidates"]]
        if args.target not in candidates or len(set(candidates)) != len(candidates):
            raise ValueError("Target is absent or duplicated")
        tensors = metadata["tensors"]
        if tensors != measured["result"]["tensors"]:
            raise ValueError("Feature/result tensor identity mismatch")
        report.update(requestId=metadata["request"]["requestId"], featureSha256=sha(raw_path),
                      featureShape=shape, modelManifestSha256=measured["modelManifestSha256"],
                      featureReceiptSha256=sha(args.features / "stdout.json"))
        hidden = torch.from_numpy(np.fromfile(raw_path, dtype="<f4").reshape(shape)).cuda()
        if not bool(torch.isfinite(hidden).all()):
            raise ValueError("Non-finite frozen encoder features")
        model = load_frozen_head(torch, args.upstream_common, args.checkpoint, hidden)
        args_gpu = [torch.tensor(tensors[key], device="cuda", dtype=dtype) for key, dtype in
                    (("inputIds", torch.long), ("attentionMask", torch.long), ("markerPos", torch.long),
                     ("markerMask", torch.bool), ("qtype", torch.long))]
        expected = torch.tensor(measured["result"]["logits"], device="cuda")
        with torch.no_grad():
            original, _ = model(*args_gpu)
        initial_delta = (original[0] - expected).abs().max().item()
        report["initialHeadVsDirectMlLogitDelta"] = initial_delta
        if not np.isfinite(initial_delta) or initial_delta > 1e-4:
            raise ValueError("Original CUDA head does not match the frozen DirectML reference")
        parameters = [p for p in model.parameters() if p.requires_grad]
        if any(p.device.type != "cuda" for p in parameters):
            raise RuntimeError("A trainable parameter is not on the discrete GPU")
        optimizer = torch.optim.AdamW(parameters, lr=1e-5, weight_decay=0.0)
        target = torch.tensor([candidates.index(args.target)], device="cuda")
        report["trainableParameters"] = sum(p.numel() for p in parameters)
        report["encoderParametersLoaded"] = sum(p.numel() for p in model.encoder.parameters())
        losses = []
        scorer_before = model.scorer[-1].weight.detach().clone()
        torch.cuda.reset_peak_memory_stats()
        # eval mode keeps dropout off: test a deterministic gradient update, not production training quality.
        for _ in range(args.steps):
            if time.monotonic() - started > 90:
                raise TimeoutError("Capability check exceeded its short budget")
            optimizer.zero_grad(set_to_none=True)
            logits, _ = model(*args_gpu)
            loss = torch.nn.functional.cross_entropy(logits, target)
            if not torch.isfinite(loss):
                raise ValueError("Non-finite CUDA loss")
            loss.backward()
            if not all(p.grad is not None and bool(torch.isfinite(p.grad).all()) for p in parameters):
                raise ValueError("Missing or non-finite trainable-head gradient")
            torch.nn.utils.clip_grad_norm_(parameters, 1.0)
            optimizer.step()
            losses.append(loss.item())
        with torch.no_grad():
            final, _ = model(*args_gpu)
            final_loss = torch.nn.functional.cross_entropy(final, target).item()
        torch.cuda.synchronize()
        changed = not torch.equal(scorer_before, model.scorer[-1].weight)
        report.update(lossBefore=losses[0], losses=losses, lossAfter=final_loss, headParameterChanged=changed,
                      peakCudaAllocatedMiB=round(torch.cuda.max_memory_allocated() / 1048576, 2),
                      peakCudaReservedMiB=round(torch.cuda.max_memory_reserved() / 1048576, 2),
                      updatesCompleted=len(losses), cudaGradientFinite=True,
                      finalTrainingChoice=candidates[final[0].argmax().item()])
        if not changed or final_loss >= losses[0]:
            raise ValueError("Bounded update did not reduce training-example loss")
        # Unaccepted experiment only; this file is never installed or substituted for the base model.
        tail = {k: v.detach().cpu().contiguous() for k, v in model.state_dict().items() if not k.startswith("encoder.")}
        save_file(tail, str(args.output / "unaccepted-head.safetensors"), metadata={"scope": "one-example CUDA capability; not a released or calibrated model"})
        report["outputHeadSha256"] = sha(args.output / "unaccepted-head.safetensors")
        if sha(args.checkpoint) != checkpoint_hash:
            raise ValueError("Original checkpoint changed during the experiment")
        report["success"] = True
    except BaseException as error:
        report["error"] = type(error).__name__ + ": " + str(error)
    finally:
        report["elapsedSeconds"] = round(time.monotonic() - started, 3)
        (args.output / "report.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
        (args.output / "source-runner.py").write_bytes(Path(__file__).read_bytes())
        print(json.dumps(report, indent=2, allow_nan=False), flush=True)
    if not report["success"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
