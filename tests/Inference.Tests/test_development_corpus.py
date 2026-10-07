"""Small provenance/request-contract checks; the synthetic fixture is not a quality gate."""
import json
import unittest

from development_corpus import FIXTURE, prepare
from quality_report import score


class DevelopmentCorpusTests(unittest.TestCase):
    def test_generated_inputs_exclude_labels_and_remain_development_only(self):
        labels, requests = prepare(json.loads(FIXTURE.read_text(encoding="utf-8")))
        self.assertEqual(120, len(requests))
        self.assertEqual(12, len({row["language"] for row in labels["samples"]}))
        self.assertEqual(120, len({row["requestSha256"] for row in labels["samples"]}))
        self.assertEqual(labels["provenance"]["labeling"], "ai-assisted-synthetic")
        self.assertFalse(labels["provenance"]["heldOut"])
        for label, request in zip(labels["samples"], requests, strict=True):
            self.assertEqual(set(request), {"requestId", "revision", "state", "instructions", "candidates"})
            state = json.loads(request["state"])
            self.assertLessEqual(set(state), {"name", "directory", "hint"})
            self.assertEqual(label["name"], state["name"])
            self.assertEqual(["c0", "c1", "c2", "filename-ambiguous", "categories-insufficient"],
                             [c["id"] for c in request["candidates"]])
        report = score(labels, {"schemaVersion": 1, "datasetId": labels["datasetId"], "provider": "laya",
                                "modelId": "fixture-not-a-model-run", "manifestSha256": "0" * 64, "samples": []})
        self.assertEqual(120, report["protocolFailures"])
        self.assertEqual(0, report["reportedAutomaticOperations"])
        self.assertIsNone(report["oneSided95ErrorUpperBound"])
        self.assertFalse(report["declaredIndependentHumanHeldOut"])
        self.assertFalse(report["readyForManualEvidenceReview"])
        self.assertFalse(report["formalAcceptance"])

    def test_duplicate_input_cannot_inflate_the_sample_count(self):
        fixture = json.loads(FIXTURE.read_text(encoding="utf-8"))
        fixture["locales"]["en-US"]["names"][1] = fixture["locales"]["en-US"]["names"][0]
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            prepare(fixture)


if __name__ == "__main__":
    unittest.main()
