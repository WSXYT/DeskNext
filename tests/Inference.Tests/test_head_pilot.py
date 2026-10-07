import unittest

from prepare_head_pilot import prepare_pilot


class HeadPilotTests(unittest.TestCase):
    def test_fixed_small_splits_are_disjoint_and_both_keep_fallbacks(self):
        data = prepare_pilot()
        train = [r for r in data["cases"] if r["split"] == "train"]
        checks = [r for r in data["cases"] if r["split"] == "development-check"]
        self.assertEqual((len(train), len(checks)), (20, 10))
        self.assertEqual(len({r["requestSha256"] for r in data["cases"]}), 30)
        self.assertFalse(data["provenance"]["heldOut"])
        self.assertEqual({r["expected"] for r in train}, {r["expected"] for r in checks})
        for row in data["cases"]:
            candidates = {c["id"] for c in row["request"]["candidates"]}
            self.assertIn("filename-ambiguous", candidates)
            self.assertIn("categories-insufficient", candidates)
            self.assertIn(row["targetId"], candidates)
            self.assertNotIn("expected", row["request"]["state"])


if __name__ == "__main__":
    unittest.main()
