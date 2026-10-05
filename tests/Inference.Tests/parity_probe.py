"""Four independent P1 CPU inference paths for the pinned Laya multilingual checkpoint.

Run stages A, B, C, D in separate processes so Torch, Python ORT, and .NET
ORT sessions never occupy the memory-limited development host together.
Generated evidence is under ignored artifacts/model/; Python is not shipped.
"""
import argparse
import copy
import json
from pathlib import Path
import struct
import subprocess

root = Path(__file__).resolve().parents[2]
model = root / "artifacts/model/multilingual"
bundle = root / "artifacts/model/exported-multilingual"
fixture = root / "tests/Inference.Tests/fixtures/multilingual-request.json"
worker = root / "src/DeskNest.Inference/bin/Release/net10.0/DeskNest.Inference.dll"
published_worker = root / "artifacts/publish/inference-win-x64/DeskNest.Inference.exe"
output = root / "artifacts/model"


def cases():
    base = json.loads(fixture.read_text(encoding="utf-8"))["request"]
    result = [("mixed-language", base)]
    reverse = copy.deepcopy(base)
    reverse["requestId"] = "p1-reordered-002"
    reverse["state"] = "memo <mask> 2026: quarterly invoice and budget"
    reverse["candidates"].reverse()
    result.append(("literal-mask-reversed", reverse))
    long = copy.deepcopy(base)
    long["requestId"] = "p1-long-003"
    long["state"] = "设计、需求、اجتماع،code review, " * 32
    result.append(("long-multilingual", long))
    return result


def stage_a():
    import torch
    from laya.agent import Agent
    from laya.common import collate_items

    torch.set_num_threads(1)
    agent = Agent(str(model), device="cpu")
    result = []
    for name, request in cases():
        qdef = {
            "type": "choice", "instructions": request["instructions"],
            "criteria": {c["id"]: c["description"] for c in request["candidates"]},
        }
        answer = agent.system_one(request["state"], {"category": qdef})
        internal = {"category": agent._to_internal(qdef)}
        items = agent._encode_state(request["state"], ["category"], internal)
        b = collate_items([items], agent.tok.pad_token_id)
        with torch.inference_mode():
            logits, acts = agent._infer(b)
        tensors = {
            "inputIds": b["input_ids"].tolist(),
            "attentionMask": b["attention_mask"].tolist(),
            "markerPos": b["marker_pos"].tolist(),
            "markerMask": b["marker_mask"].tolist(),
            "qtype": b["qtype"].reshape(-1).tolist(),
        }
        n = len(request["candidates"])
        item = {"case": name, "request": request, "tensors": tensors,
                "logits": logits[0, :n].tolist(), "actLogits": acts[0].tolist(),
                "official": answer}
        result.append(item)
        print("A", name, len(tensors["inputIds"][0]), "tokens", "choice=", answer["answers"]["category"]["choice"], flush=True)
    (output / "p1-parity-A.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")


def stage_b():
    import numpy as np
    import onnxruntime as ort

    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    enc = ort.InferenceSession(str(bundle / "encoder.onnx"), options, providers=["CPUExecutionProvider"])
    head = ort.InferenceSession(str(bundle / "head.onnx"), options, providers=["CPUExecutionProvider"])
    expected = json.loads((output / "p1-parity-A.json").read_text(encoding="utf-8"))
    result = []
    for row in expected:
        t = row["tensors"]
        ids = np.asarray(t["inputIds"], dtype=np.int64)
        att = np.asarray(t["attentionMask"], dtype=np.int64)
        hidden = enc.run(["last_hidden_state"], {"input_ids": ids, "attention_mask": att})[0]
        logits, acts = head.run(["logits", "act_logits"], {
            "hidden_states": hidden,
            "marker_pos": np.asarray(t["markerPos"], dtype=np.int64),
            "marker_mask": np.asarray(t["markerMask"], dtype=np.bool_),
            "qtype": np.asarray(t["qtype"], dtype=np.int64).reshape(-1, 1),
            "attention_mask": att,
        })
        n = len(row["request"]["candidates"])
        delta = float(np.max(np.abs(logits[0, :n] - np.asarray(row["logits"]))))
        assert delta < 1e-4, (row["case"], "Torch/ORT logits", delta)
        result.append({"case": row["case"], "logits": logits[0, :n].tolist(), "actLogits": acts[0].tolist()})
        print("B", row["case"], "maxTorchLogitDelta=", delta, flush=True)
    (output / "p1-parity-B.json").write_text(json.dumps(result, indent=2), encoding="utf-8")


def stage_dotnet(stage):
    import numpy as np

    expected = json.loads((output / "p1-parity-A.json").read_text(encoding="utf-8"))
    previous = json.loads((output / "p1-parity-B.json").read_text(encoding="utf-8"))
    results = []
    for a, b in zip(expected, previous, strict=True):
        assert a["case"] == b["case"]
        raw = {"request": a["request"]}
        if stage == "C":
            raw["tensors"] = a["tensors"]
            proc = subprocess.run(
                ["dotnet", str(worker), "--tensor-probe", str(bundle)],
                input=json.dumps(raw, ensure_ascii=False), capture_output=True,
                text=True, encoding="utf-8", timeout=240,
            )
            assert proc.returncode == 0, (a["case"], proc.stderr[-3000:])
            actual = json.loads(proc.stdout)
        else:
            payload = json.dumps(a["request"], ensure_ascii=False).encode("utf-8")
            framed = struct.pack("<I", len(payload)) + payload
            assert published_worker.is_file(), "publish the self-contained win-x64 worker before stage D"
            proc = subprocess.run(
                [str(published_worker), "--inference-worker", str(bundle)],
                input=framed, capture_output=True, timeout=240,
            )
            assert proc.returncode == 0, (a["case"], proc.stderr.decode("utf-8", errors="replace")[-3000:])
            assert len(proc.stdout) >= 4, (a["case"], "missing binary response")
            length = struct.unpack("<I", proc.stdout[:4])[0]
            assert len(proc.stdout) == 4 + length, (a["case"], "invalid framing")
            actual = json.loads(proc.stdout[4:].decode("utf-8"))
        assert actual["requestId"] == a["request"]["requestId"]
        assert actual["revision"] == a["request"]["revision"]
        assert actual["tensors"] == a["tensors"], (a["case"], "input tensors differ")
        torch_delta = float(np.max(np.abs(np.asarray(actual["logits"]) - np.asarray(a["logits"]))))
        ort_delta = float(np.max(np.abs(np.asarray(actual["logits"]) - np.asarray(b["logits"]))))
        assert torch_delta < 1e-4 and ort_delta < 1e-4, (a["case"], torch_delta, ort_delta)
        t = actual["temperature"]
        z = np.asarray(a["logits"], dtype=np.float64) / t
        probs = np.exp(z - z.max())
        probs /= probs.sum()
        prob_delta = float(np.max(np.abs(probs - np.asarray(actual["probabilities"]))))
        assert prob_delta < 1e-4, (a["case"], "probabilities", prob_delta)
        official = a["official"]["answers"]["category"]
        assert actual["choice"] == official["choice"], (a["case"], "decision differs")
        for i, c in enumerate(a["request"]["candidates"]):
            assert abs(actual["probabilities"][i] - official["probabilities"][c["id"]]) <= 1.1e-4, (a["case"], c["id"])
        assert abs(actual["confidence"] - official["confidence"]) < 1.1e-4
        assert abs(actual["answerConfidence"] - official["answer_confidence"]) < 1.1e-4
        assert abs(actual["actProbability"] - official["action"]["act_probability"]) < 1.1e-4
        assert actual["action"] == ("pending" if actual["choice"] in ("filename-ambiguous", "categories-insufficient") or
            sorted(actual["probabilities"], reverse=True)[0] - sorted(actual["probabilities"], reverse=True)[1] <= 0.0002
            else "proposed")
        results.append({"case": a["case"], "result": actual, "torchLogitDelta": torch_delta,
                        "pythonOrtLogitDelta": ort_delta, "probabilityDelta": prob_delta})
        print(stage, a["case"], "torchLogitDelta=", torch_delta, "pythonOrtLogitDelta=", ort_delta,
              "probabilityDelta=", prob_delta, "choice=", actual["choice"], "action=", actual["action"], flush=True)
    (output / f"p1-parity-{stage}.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    if stage == "D":
        c = json.loads((output / "p1-parity-C.json").read_text(encoding="utf-8"))
        for left, right in zip(c, results, strict=True):
            assert left["case"] == right["case"] and left["result"] == right["result"]
        print("A-D: all raw inputs, tokens, logits, probabilities and actions matched", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("stage", choices=["A", "B", "C", "D"])
    parser.add_argument("--bundle", type=Path, default=bundle)
    parser.add_argument("--worker", type=Path, default=published_worker,
                        help="Actual installed application or inference worker for stage D")
    parser.add_argument("--diagnostic-worker", type=Path, default=worker)
    parser.add_argument("--output", type=Path, default=output,
                        help="Use a fresh directory to preserve previous stage evidence")
    args = parser.parse_args()
    bundle = args.bundle.resolve()
    published_worker = args.worker.resolve()
    worker = args.diagnostic_worker.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    {"A": stage_a, "B": stage_b, "C": lambda: stage_dotnet("C"),
     "D": lambda: stage_dotnet("D")}[args.stage]()
