"""Local evaluation of published filename annotations; not P4 acceptance.

The third-party JSON is NOT distributed here. Supply the pinned upstream file.
No downloads, document/URL fetching, cloud calls, training or file operations.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

from development_corpus import INSTRUCTIONS, SPECIAL, canonical, run, write_json

SOURCE_COMMIT = "21e9f05ff744558a9ad520f253e2ad1bb6f1d69a"
SOURCE_SHA256 = "7f289a4e232c854b33ad8634b866ec7a84ac38161499abb6852128abf4356203"
SOURCE_URL = ("https://github.com/frank7li/Document_Classification_using_File_Names/blob/"
              + SOURCE_COMMIT + "/datasets/common_crawl_dataset.json")
PAPER = "https://arxiv.org/html/2410.01166v2#S5.SS2"
CATEGORIES = ["bill_act_law", "course_syllabus", "form", "guide", "job_posting", "letter", "map",
              "meeting_minutes", "newsletter", "policy", "press_release", "price_list", "restaurant_menu",
              "resume", "specification"]


def prepare(rows):
    """Deduplicate complete filename inputs; never reinterpret unannotated OOS ambiguity."""
    groups = defaultdict(list)
    seen_ids = set()
    for row in rows:
        if (not isinstance(row, dict) or not isinstance(row.get("filename"), str) or not row["filename"] or
                row.get("category") not in CATEGORIES + ["oos"] or
                (row["category"] != "oos" and type(row.get("indicative")) is not bool)):
            raise ValueError("Unexpected upstream filename annotation")
        if str(row["id"]) in seen_ids:
            raise ValueError("Duplicate upstream row ID")
        seen_ids.add(str(row["id"]))
        groups[row["filename"]].append(row)

    def label(row):
        if row["category"] == "oos":
            return "oos"  # Published OOS rows do not annotate whether the filename is informative.
        return row["category"] if row["indicative"] else SPECIAL[0]

    candidates = [{"id": f"c{i}", "description": name.replace("_", " ")} for i, name in enumerate(CATEGORIES)] + [
        {"id": SPECIAL[0], "description": "The filename does not identify its subject."},
        {"id": SPECIAL[1], "description": "None of the available categories fits."}]
    samples, requests, excluded = [], [], Counter()
    for name, values in sorted(groups.items()):
        labels = {label(row) for row in values}
        if len(labels) != 1:
            excluded["conflictingFilenameRows"] += len(values)
            continue
        if "oos" in labels:
            excluded["outOfScopeWithoutFilenameAmbiguityLabel"] += len(values)
            continue
        excluded["duplicateFilenameRows"] += len(values) - 1
        row = min(values, key=lambda value: str(value["id"]))
        request_id = "cc:" + str(row["id"])
        request = {"requestId": request_id, "revision": 0,
                   "state": json.dumps({"name": name, "directory": False}, ensure_ascii=False, separators=(",", ":")),
                   "instructions": INSTRUCTIONS, "candidates": candidates}
        input_hash = hashlib.sha256(canonical({k: request[k] for k in ("state", "instructions", "candidates")})).hexdigest()
        samples.append({"id": request_id, "group": hashlib.sha256(name.encode("utf-8")).hexdigest(),
                        "language": "en", "scenario": "indicative" if row["indicative"] else "ambiguous",
                        "requestSha256": input_hash, "candidates": CATEGORIES + list(SPECIAL),
                        "expected": label(row), "name": name, "directory": False, "hint": "",
                        "rationale": "Published category and indicative flag; no assistant relabeling.",
                        "riskAssessment": "not assessed"})
        requests.append(request)
    labels = {"schemaVersion": 1, "datasetId": "published-filename-reference-21e9f05-v1",
              "provenance": {"labeling": "upstream-human-reported", "heldOut": False,
                             "source": SOURCE_URL, "annotationEvidence": PAPER,
                             "limitations": "English only; Laya training overlap unknown; no insufficient-category, high-risk or automatic-operation acceptance; no dataset redistribution license located."},
              "samples": samples}
    selection = {"sourceRows": len(rows), "uniqueFilenames": len(groups), "selected": len(samples),
                 "excluded": dict(excluded), "expectedCounts": dict(Counter(s["expected"] for s in samples))}
    return labels, requests, selection


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--worker", type=Path)
    parser.add_argument("--model", type=Path)
    args = parser.parse_args()
    if not args.prepare_only and (args.worker is None or args.model is None):
        parser.error("Actual inference requires --worker and --model")
    with args.source.open("rb") as file:
        raw = file.read(1024 * 1024 + 1)
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
        raise ValueError("Source does not match the pinned published annotations")
    labels, requests, selection = prepare(json.loads(raw))
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "labels.json", labels)
    write_json(args.output / "requests.json", requests)
    write_json(args.output / "selection.json", selection)
    write_json(args.output / "source.json", {"commit": SOURCE_COMMIT, "sourceSha256": SOURCE_SHA256,
        "sourceUrl": SOURCE_URL, "paper": PAPER, "formalAcceptance": False,
        "requestSetSha256": hashlib.sha256(canonical(requests)).hexdigest(),
        "runnerSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()})
    (args.output / "source-runner.py").write_bytes(Path(__file__).read_bytes())
    (args.output / "source-worker-runner.py").write_bytes(Path(__file__).with_name("development_corpus.py").read_bytes())
    print(json.dumps(selection), flush=True)
    if not args.prepare_only:
        run(args.worker.resolve(strict=True), args.model.resolve(strict=True), args.output, labels, requests)


if __name__ == "__main__":
    main()
