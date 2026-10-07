import unittest

from prepare_contrastive_head_pilot import prepare as prepare_contrastive
from prepare_head_pilot import prepare_pilot


class HeadPilotTests(unittest.TestCase):
    def test_contrastive_labels_follow_category_presence_not_position(self):
        data = prepare_contrastive()
        rows = data["cases"]
        self.assertEqual((sum(r["split"] == "train" for r in rows), sum(r["split"] == "development-check" for r in rows)), (44, 20))
        self.assertEqual(len({r["requestSha256"] for r in rows}), 64)
        self.assertFalse({r["group"] for r in rows if r["split"] == "train"} & {r["group"] for r in rows if r["split"] == "development-check"})
        for row in rows:
            if row["variant"] == "absent":
                self.assertEqual(row["targetId"], "categories-insufficient")
            if row["variant"] == "reordered":
                original = next(r for r in rows if r["split"] == row["split"] and r["group"] == row["group"] and r["language"] == row["language"] and r["variant"] == "present")
                self.assertEqual(row["expected"], original["expected"])
                self.assertNotEqual(row["targetId"], original["targetId"])
                self.assertEqual(row["request"]["state"], original["request"]["state"])
        self.assertFalse(data["provenance"]["heldOut"])

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
