import copy
import hashlib
import unittest
import uuid
from quality_report import score, upper95, SPECIAL


def fixture(count=3):
    samples = [dict(id=str(i), group=f"group-{i}", language="zh-CN", scenario="unit-fixture", requestSha256=hashlib.sha256(str(i).encode()).hexdigest(),
                    candidates=["documents", *SPECIAL], expected="documents") for i in range(count)]
    labels = dict(schemaVersion=1, datasetId="synthetic-unit-fixture", provenance=dict(labeling="synthetic", heldOut=False, source="unit test"),
                  developmentGroups=[], samples=samples)
    predictions = dict(schemaVersion=1, datasetId=labels["datasetId"], provider="laya", modelId="fixture-only", manifestSha256="a"*64,
                       samples=[dict(id=s["id"],requestSha256=s["requestSha256"],choice="documents",probabilities=[0.9,0.05,0.05]) for s in samples])
    return labels, predictions


class QualityReportTests(unittest.TestCase):
    def test_exact_upper_bound_and_no_observations(self):
        self.assertIsNone(upper95(0, 0))
        self.assertGreater(upper95(0, 298), 0.01)
        self.assertLess(upper95(0, 299), 0.01)
        self.assertTrue(0.009 < upper95(1, 500) < 0.01)
        self.assertEqual(upper95(500, 500), 1)

    def test_failures_stay_in_denominator_and_do_not_become_uncertainty(self):
        labels, predictions = fixture()
        labels["samples"][1]["expected"] = SPECIAL[0]
        labels["samples"][2]["expected"] = SPECIAL[1]
        predictions["samples"][1] = dict(id="1", requestSha256=labels["samples"][1]["requestSha256"], error="timeout")
        predictions["samples"].pop()
        result = score(labels, predictions)
        self.assertEqual(result["protocolFailures"], 2)
        self.assertEqual(result["accuracy"], 1/3)
        self.assertEqual(result["special"][SPECIAL[0]]["recall"], 0)
        self.assertAlmostEqual(result["brierOnValidResponses"], 0.015)
        self.assertAlmostEqual(result["eceOnValidResponses"], 0.1)
        self.assertIsNone(result["oneSided95ErrorUpperBound"])
        self.assertFalse(result["numericGateMet"])

    def test_duplicate_extra_and_mismatched_inputs_are_refused(self):
        labels, predictions = fixture()
        damaged = copy.deepcopy(labels)
        damaged["samples"][1]["requestSha256"] = damaged["samples"][0]["requestSha256"]
        with self.assertRaises(ValueError): score(damaged, predictions)
        damaged = copy.deepcopy(predictions)
        damaged["samples"][0]["requestSha256"] = "0"*64
        with self.assertRaises(ValueError): score(labels, damaged)
        damaged = copy.deepcopy(predictions)
        damaged["samples"].append(dict(id="extra"))
        with self.assertRaises(ValueError): score(labels, damaged)
        predictions["samples"][0]["probabilities"] = [float("nan"),0,0]
        self.assertEqual(score(labels, predictions)["protocolFailures"], 1)

    def test_synthetic_samples_and_declarations_never_grant_formal_acceptance(self):
        labels, predictions = fixture(500)  # Generated for unit arithmetic only; NEVER a quality corpus.
        for i, record in enumerate(predictions["samples"]):
            record["automaticOperation"] = dict(id=str(uuid.UUID(int=i+1)), target="documents")
        result = score(labels, predictions)
        self.assertTrue(result["numericGateMet"])
        self.assertFalse(result["readyForManualEvidenceReview"])
        self.assertFalse(result["formalAcceptance"])
        labels["provenance"] = dict(labeling="independent-human", heldOut=True, source="unverified unit declaration")
        labels["developmentGroups"] = ["group-0"]
        self.assertFalse(score(labels, predictions)["readyForManualEvidenceReview"])
        labels["developmentGroups"] = []
        labels["samples"][0].update(expected=SPECIAL[0], highRisk=True)
        result = score(labels, predictions)
        self.assertEqual(result["highRiskWrongAutomaticOperations"], 1)
        self.assertFalse(result["numericGateMet"])
        self.assertFalse(result["formalAcceptance"])


if __name__ == "__main__":
    unittest.main()
