"""Prepare a fixed small synthetic adaptation pilot; neither split is independent quality acceptance.
Twenty NEW training names; ten already-inspected original development checks.
No external human-reference data, private files or cloud service is used.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path

from development_corpus import FIXTURE, canonical, prepare, write_json

TRAIN = {
    "en": [
        ("Vendor invoice for replacement monitors.pdf", "finance"),
        ("Marketing department quarterly budget.xlsx", "finance"),
        ("Bluebird brand logo final.svg", "design"),
        ("Signup screen wireframe.fig", "design"),
        ("Password reset endpoint implementation.py", "code"),
        ("Add customer table migration.sql", "code"),
        ("file_v7.txt", "filename-ambiguous"),
        ("New document 9.txt", "filename-ambiguous"),
        ("Broccoli omelette cooking instructions.md", "categories-insufficient"),
        ("Banana muffin recipe.txt", "categories-insufficient"),
    ],
    "zh": [
        ("打印机采购增值税发票.pdf", "finance"),
        ("市场部第四季度预算.xlsx", "finance"),
        ("蓝鸟品牌商标定稿.svg", "design"),
        ("注册界面线框设计.fig", "design"),
        ("重置密码接口实现.py", "code"),
        ("创建客户表迁移脚本.sql", "code"),
        ("文档第九版.txt", "filename-ambiguous"),
        ("新建文本文档8.txt", "filename-ambiguous"),
        ("西兰花煎蛋制作步骤.md", "categories-insufficient"),
        ("香蕉松饼食谱.txt", "categories-insufficient"),
    ],
}


def prepare_pilot():
    fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
    labels, requests = prepare(fixture)
    originals = {r["requestId"]: r for r in requests}
    label_by_id = {s["id"]: s for s in labels["samples"]}
    alias = dict(zip(labels["samples"][0]["candidates"], [c["id"] for c in requests[0]["candidates"]], strict=True))
    rows = []
    for language, examples in TRAIN.items():
        for index, (name, expected) in enumerate(examples):
            request = copy.deepcopy(requests[0])
            request["requestId"] = f"train:{language}:{index}"
            request["state"] = json.dumps({"name": name, "directory": False}, ensure_ascii=False, separators=(",", ":"))
            rows.append({"split": "train", "id": request["requestId"], "expected": expected, "targetId": alias[expected], "request": request})
    for language in ("en-US", "zh-CN"):
        for scenario in ("invoice", "logo", "authentication", "generic-name", "recipe"):
            identity = language + ":" + scenario
            request = originals[identity]
            expected = label_by_id[identity]["expected"]
            rows.append({"split": "development-check", "id": identity, "expected": expected, "targetId": alias[expected], "request": request})
    digests = [hashlib.sha256(canonical({k: r["request"][k] for k in ("state", "instructions", "candidates")})).hexdigest() for r in rows]
    if len(set(digests)) != len(rows):
        raise ValueError("Train/check model inputs must not duplicate")
    for row, digest in zip(rows, digests, strict=True):
        row["requestSha256"] = digest
    return {"schemaVersion": 1, "datasetId": "desknext-frozen-head-pilot-v1", "provenance": {"labeling": "ai-assisted-synthetic",
        "heldOut": False, "scope": __doc__}, "training": {"epochs": 6, "learningRate": 0.0003, "seed": 104729,
        "batchSize": 1, "encoderFrozen": True, "actionHeadFrozen": True, "fitTemperature": False},
        "cases": rows}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    document = prepare_pilot()
    write_json(args.output / "pilot.json", document)
    for index, row in enumerate(document["cases"]):
        write_json(args.output / f"{index:02d}-request.json", {"request": row["request"]})
    (args.output / "source-prepare.py").write_bytes(Path(__file__).read_bytes())
    print(json.dumps({"train": 20, "developmentChecks": 10, "independentAcceptance": False,
                      "pilotSha256": hashlib.sha256((args.output / "pilot.json").read_bytes()).hexdigest()}))
