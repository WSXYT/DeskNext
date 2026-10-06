"""Offline scoring only: no model/API calls, file collection, or file-operation authority."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import uuid

SPECIAL = ("filename-ambiguous", "categories-insufficient")


def upper95(errors, count):
    """Exact one-sided Clopper-Pearson upper bound; no observations means unknown."""
    if not count:
        return None
    if errors == count:
        return 1.0
    if errors == 0:
        return -math.expm1(math.log(0.05) / count)
    lo, hi = errors / count, 1.0
    coefficients = [math.lgamma(count + 1) - math.lgamma(i + 1) - math.lgamma(count - i + 1)
                    for i in range(errors + 1)]
    for _ in range(64):
        p = (lo + hi) / 2
        terms = [c + i * math.log(p) + (count - i) * math.log1p(-p) for i, c in enumerate(coefficients)]
        largest = max(terms)
        log_cdf = largest + math.log(sum(math.exp(v - largest) for v in terms))
        if log_cdf > math.log(0.05):
            lo = p
        else:
            hi = p
    return hi


def text(value, maximum=128):
    return isinstance(value, str) and 0 < len(value) <= maximum and not any(ord(c) < 32 for c in value)


def digest(value):
    return isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdefABCDEF" for c in value)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def score(labels, predictions):
    require(type(labels.get("schemaVersion")) is int and labels["schemaVersion"] == 1 and
            type(predictions.get("schemaVersion")) is int and predictions["schemaVersion"] == 1, "Unsupported schema")
    require(text(labels.get("datasetId")) and predictions.get("datasetId") == labels["datasetId"], "Dataset mismatch")
    require(predictions.get("provider") in ("laya", "jev") and text(predictions.get("modelId"), 256), "One provider/model per report is required")
    require(digest(predictions.get("manifestSha256")) if predictions["provider"] == "laya" else True, "Local model manifest digest is required")
    samples = labels.get("samples")
    records = predictions.get("samples")
    require(isinstance(samples, list) and 0 < len(samples) <= 100_000 and isinstance(records, list) and len(records) <= 100_000, "Invalid sample collection")
    ids, hashes, by_id = set(), set(), {}
    for sample in samples:
        require(isinstance(sample, dict) and text(sample.get("id")) and text(sample.get("group")) and text(sample.get("language"), 32) and text(sample.get("scenario"), 64), "Invalid label identity")
        candidates = sample.get("candidates")
        if not isinstance(candidates, list):
            raise ValueError("Invalid candidates")
        require(3 <= len(candidates) <= (255 if predictions["provider"] == "jev" else 256) and all(text(c) for c in candidates), "Invalid candidates")
        require(len(set(candidates)) == len(candidates) and "protocol-failure" not in candidates and all(s in candidates for s in SPECIAL) and sample.get("expected") in candidates, "Missing, duplicate or unknown label")
        require(digest(sample.get("requestSha256")), "Missing input digest")
        require(sample["id"] not in ids and sample["requestSha256"].lower() not in hashes, "Duplicate ID or model input")
        require(type(sample.get("highRisk", False)) is bool, "highRisk must be boolean")
        ids.add(sample["id"]); hashes.add(sample["requestSha256"].lower())
    for record in records:
        require(isinstance(record, dict) and record.get("id") in ids and record["id"] not in by_id, "Extra or duplicate prediction")
        by_id[record["id"]] = record

    confusion = defaultdict(Counter)
    strata = defaultdict(Counter)
    calibration = [dict(count=0, confidence=0.0, correct=0) for _ in range(10)]
    failures, automatic, wrong_auto, high_risk_wrong = 0, 0, 0, 0
    correct, ordinary_correct, ordinary_count, valid, brier = 0, 0, 0, 0, 0.0
    operation_ids = set()
    for sample in samples:
        candidates, expected = sample["candidates"], sample["expected"]
        record = by_id.get(sample["id"], {})
        if record:
            require(digest(record.get("requestSha256")) and record["requestSha256"].lower() == sample["requestSha256"].lower(), "Prediction/input digest mismatch")
        raw_probabilities = record.get("probabilities")
        probabilities = raw_probabilities if isinstance(raw_probabilities, list) else []
        valid_response = (bool(record) and not record.get("error")
            and len(probabilities) == len(candidates)
            and all(type(p) in (float, int) and math.isfinite(p) and 0 <= p <= 1 for p in probabilities)
            and abs(sum(probabilities) - 1) <= 1e-6)
        if valid_response:
            winner = min(range(len(candidates)), key=lambda i: (-probabilities[i], candidates[i]))
            valid_response = record.get("choice") == candidates[winner]
        choice = record["choice"] if valid_response else "protocol-failure"
        match = valid_response and choice == expected
        correct += match
        ordinary_count += expected not in SPECIAL
        ordinary_correct += match and expected not in SPECIAL
        confusion[expected][choice] += 1
        bucket = "3-5" if len(candidates) <= 5 else "6-10" if len(candidates) <= 10 else "11-20" if len(candidates) <= 20 else "21-50" if len(candidates) <= 50 else "51-256"
        for key in ("language:" + sample["language"], "scenario:" + sample["scenario"], "candidates:" + bucket):
            strata[key]["count"] += 1
            strata[key]["correct"] += match
            strata[key]["protocolFailures"] += not valid_response
        if not valid_response:
            failures += 1
        else:
            valid += 1
            brier += sum((p - int(c == expected)) ** 2 for c, p in zip(candidates, probabilities))
            confidence = max(probabilities)
            bin_ = calibration[min(9, int(confidence * 10))]
            bin_["count"] += 1; bin_["confidence"] += confidence; bin_["correct"] += match
        # Only explicitly supplied execution receipts count. Pending/proposed/manual confirmations are NOT automatic moves.
        operation = record.get("automaticOperation") if record else None
        if operation is not None:
            require(isinstance(operation, dict) and operation.get("target") in candidates, "Invalid reported automatic target")
            require(text(operation.get("id"), 36), "Invalid automatic receipt ID")
            receipt = str(uuid.UUID(operation["id"]))
            require(receipt != str(uuid.UUID(int=0)) and receipt not in operation_ids, "Missing or duplicate automatic receipt")
            operation_ids.add(receipt)
            automatic += 1
            error = not valid_response or operation["target"] != expected or operation["target"] in SPECIAL or operation["target"] != choice
            wrong_auto += error
            high_risk_wrong += error and sample.get("highRisk", False)
    special = {}
    for label in SPECIAL:
        true_positive = confusion[label][label]
        predicted = sum(row[label] for row in confusion.values())
        actual = sum(confusion[label].values())
        special[label] = {"precision": true_positive / predicted if predicted else None,
                          "recall": true_positive / actual if actual else None, "actual": actual, "predicted": predicted}
    groups = {s["group"] for s in samples}
    development = labels.get("developmentGroups")
    require(development is None or isinstance(development, list) and all(text(g) for g in development), "Invalid development groups")
    overlap = sorted(groups.intersection(development or []))
    provenance = labels.get("provenance", {})
    require(isinstance(provenance, dict), "Invalid provenance declaration")
    declared_independent = provenance.get("labeling") == "independent-human" and provenance.get("heldOut") is True and text(provenance.get("source"), 2048)
    upper = upper95(wrong_auto, automatic)
    numeric_gate = len(samples) >= 500 and failures == 0 and upper is not None and upper <= 0.01 and high_risk_wrong == 0
    return dict(schemaVersion=1, datasetId=labels["datasetId"], provider=predictions["provider"], modelId=predictions["modelId"],
        modelManifestSha256=predictions.get("manifestSha256"),
        samples=len(samples), validResponses=valid, protocolFailures=failures, accuracy=correct / len(samples),
        ordinaryAccuracy=ordinary_correct / ordinary_count if ordinary_count else None, special=special,
        confusion={k:dict(v) for k,v in confusion.items()}, strata={k:dict(v) for k,v in strata.items()},
        brierOnValidResponses=brier / valid if valid else None,
        eceOnValidResponses=sum(abs(b["confidence"] - b["correct"]) for b in calibration) / valid if valid else None,
        calibrationBins=calibration, reportedAutomaticOperations=automatic, automaticCoverage=automatic / len(samples),
        wrongAutomaticOperations=wrong_auto, highRiskWrongAutomaticOperations=high_risk_wrong, oneSided95ErrorUpperBound=upper,
        numericGateMet=numeric_gate, declaredIndependentHumanHeldOut=declared_independent,
        developmentGroupsChecked=development is not None, overlappingGroups=overlap,
        readyForManualEvidenceReview=numeric_gate and declared_independent and development is not None and not overlap,
        formalAcceptance=False, scope="Offline scoring of supplied evidence only. Label independence, execution receipts and provenance require separate review; no P4/P7 acceptance is granted.")


def load(path):
    with Path(path).open("rb") as file:
        raw = file.read(16 * 1024 * 1024 + 1)
    require(len(raw) <= 16 * 1024 * 1024, "Input exceeds 16 MiB")
    value = json.loads(raw)
    require(isinstance(value, dict), "Input must be a JSON object")
    return value, hashlib.sha256(raw).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("labels")
    parser.add_argument("predictions")
    parser.add_argument("output")
    args = parser.parse_args()
    labels, labels_hash = load(args.labels)
    predictions, predictions_hash = load(args.predictions)
    result = score(labels, predictions)
    result.update(labelsFileSha256=labels_hash, predictionsFileSha256=predictions_hash)
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as file:
        json.dump(result, file, ensure_ascii=False, indent=2, allow_nan=False)
    print(json.dumps({k:result[k] for k in ("samples", "protocolFailures", "numericGateMet", "readyForManualEvidenceReview", "formalAcceptance")}))


if __name__ == "__main__":
    main()
