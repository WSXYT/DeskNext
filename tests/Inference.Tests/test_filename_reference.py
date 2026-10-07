import unittest

from filename_reference import prepare


class FilenameReferenceTests(unittest.TestCase):
    def test_only_unambiguous_published_label_mappings_are_evaluated(self):
        rows = [
            {"id": 1, "filename": "guide.pdf", "category": "guide", "indicative": True},
            {"id": 2, "filename": "guide.pdf", "category": "guide", "indicative": True},
            {"id": 3, "filename": "000.pdf", "category": "form", "indicative": False},
            {"id": 4, "filename": "outside.pdf", "category": "oos", "indicative": None},
            {"id": 5, "filename": "conflict.pdf", "category": "guide", "indicative": True},
            {"id": 6, "filename": "conflict.pdf", "category": "oos", "indicative": None},
        ]
        labels, requests, selection = prepare(rows)
        self.assertEqual(selection["selected"], 2)
        self.assertEqual(selection["excluded"], {"conflictingFilenameRows": 2,
            "duplicateFilenameRows": 1, "outOfScopeWithoutFilenameAmbiguityLabel": 1})
        self.assertEqual({s["expected"] for s in labels["samples"]}, {"guide", "filename-ambiguous"})
        self.assertEqual(len({s["requestSha256"] for s in labels["samples"]}), 2)
        self.assertEqual(len(requests[0]["candidates"]), 17)
        self.assertFalse(labels["provenance"]["heldOut"])
        self.assertTrue(all("category" not in r["state"] and "http" not in r["state"] for r in requests))
        with self.assertRaises(ValueError):
            prepare([dict(rows[0], indicative="false")])


if __name__ == "__main__":
    unittest.main()
