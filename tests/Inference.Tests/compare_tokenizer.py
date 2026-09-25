"""Development-only P1 preprocessing probe against pinned Laya build_sequence.

Requires pinned Laya source and Transformers in an isolated development venv,
the revision-pinned multilingual tokenizer, and a built C# worker.
This does NOT prove model logits or full A–D equivalence.
"""
import copy
import json
import pathlib
import subprocess

from laya.common import build_sequence
from transformers import AutoTokenizer

root = pathlib.Path(__file__).resolve().parents[2]
model = root / "artifacts/model/multilingual"
fixture = root / "tests/Inference.Tests/fixtures/multilingual-request.json"
worker = root / "src/DeskNest.Inference/bin/Release/net10.0/DeskNest.Inference.dll"
base = json.loads(fixture.read_text(encoding="utf-8"))
config = json.loads((model / "rl_agent_config.json").read_text(encoding="utf-8"))
tok = AutoTokenizer.from_pretrained(model, local_files_only=True)


def compare(name, request):
    q = request["request"]
    question = {
        "t": "choice",
        "ins": q["instructions"],
        "crit": {candidate["id"]: candidate["description"] for candidate in q["candidates"]},
    }
    ids, markers = build_sequence(
        tok, q["state"], question,
        max_len=config["max_len"], head_max_len=config["head_max_len"],
    )
    assert len(markers) == len(q["candidates"]), "oracle dropped a marker"
    expected = {
        "inputIds": [ids],
        "attentionMask": [[1] * len(ids)],
        "markerPos": [markers],
        "markerMask": [[True] * len(markers)],
        "qtype": [0],
    }
    completed = subprocess.run(
        ["dotnet", str(worker), "--encode-probe", str(model)],
        input=json.dumps(request, ensure_ascii=False), text=True, encoding="utf-8",
        capture_output=True, timeout=30, check=True,
    )
    actual = json.loads(completed.stdout)
    for field, value in expected.items():
        assert actual[field] == value, f"{name}: {field} differs"
    print(f"{name}: {len(ids)} token IDs, {len(markers)} markers matched")


compare("mixed-language", base)
for name, updates in [
    ("empty-state", {"state": ""}),
    ("literal-mask", {"state": "memo <mask> 2026", "instructions": "Choose <mask> the best category"}),
    ("ordered-candidates", {"candidates": list(reversed(base["request"]["candidates"]))}),
    ("long-state", {"state": "a " * 790}),
]:
    case = copy.deepcopy(base)
    case["request"].update(updates)
    compare(name, case)

