# Published filename reference — evaluation in progress

## Source and scope

[Li, Larson and Leach, *Document Classification using File Names*](https://arxiv.org/html/2410.01166v2), section 5.2, reports two-person manual annotation of document categories and filename ambiguity. After excluding invalid URLs, non-English documents and annotator disagreements, its Common Crawl subset contains 1,198 rows: 284 indicative in-scope filenames, 264 ambiguous in-scope filenames and 650 out-of-scope documents. The appendix links the [authors' dataset repository](https://github.com/frank7li/Document_Classification_using_File_Names).

This is a useful external reference, **not a complete DeskNext acceptance corpus**. The input is English PDF names, with fifteen fixed document categories rather than arbitrary personal spaces. The labels are reported by the authors; they are not newly collected by DeskNext or relabeled by the assistant. Overlap with Laya's training data is unknown. File/folder variety, other languages, high-risk cases and automatic-operation receipts are not covered. The source repository has no dataset license identified in this inspection: the data, derived labels and model outputs stay under ignored `artifacts/`, not in this repository or release assets. This document and the adapter contain no copied dataset rows.

Pinned input:

- Commit: `21e9f05ff744558a9ad520f253e2ad1bb6f1d69a`.
- File: `datasets/common_crawl_dataset.json` (286,627 bytes).
- SHA-256: `7f289a4e232c854b33ad8634b866ec7a84ac38161499abb6852128abf4356203`.
- Fields used: `id`, `filename`, `category`, `indicative`. The `url` field is neither fetched nor sent to a model. No PDF contents are downloaded.

## Frozen selection and mapping

`tests/Inference.Tests/filename_reference.py` prepares the requests before inference:

1. Group exact filename strings (1,145 unique inputs). Exclude every group with conflicting mapped labels: 26 rows in three groups.
2. Exclude the remaining 635 out-of-scope rows. They describe document-level out-of-scope status but have no filename ambiguity annotation, so they cannot honestly be forced into DeskNext's `categories-insufficient` rather than `filename-ambiguous` outcome.
3. Collapse one repeated, consistently labeled in-scope filename.
4. Retain **536 unique inputs: 284 indicative and 252 ambiguous**. Preserve the published category for indicative filenames; map the explicit false `indicative` flag to `filename-ambiguous`.

Requests use the existing Unicode JSON state with filename and `directory=false`, the unchanged classification instruction, fifteen compact request-local IDs (`c0`–`c14`) with category names as descriptions, and both reserved fallback options. Expected labels, source URLs and rationales never enter the request. This reference taxonomy is not a test of every localized OOBE preset. The longest filename by character count passed the native tokenizer preflight (17 markers, 540 sequence tokens); that is not a blanket assertion that every possible input fits.

The complete frozen request set has canonical SHA-256 `d8f3d0a68860ff4487bb2aa643aeb9d924b3e31ed6b54faadeca0486c23ec9a8`. Selection tests use only tiny assistant-authored fixtures and cover duplicates, conflicts, out-of-scope exclusion and malformed annotation refusal. They do not establish classifier accuracy.

## Reproduction

Obtain the pinned JSON separately from the authors, retaining its attribution and checking their usage terms. Then use the developer-only Python standard-library adapter:

```bash
python tests/Inference.Tests/filename_reference.py /absolute/path/common_crawl_dataset.json \
  artifacts/quality/reference-prepared --prepare-only

python tests/Inference.Tests/filename_reference.py /absolute/path/common_crawl_dataset.json \
  artifacts/quality/reference-run \
  --worker /absolute/path/DeskNest.App.exe --model /absolute/path/verified-model

python -m unittest discover -s tests/Inference.Tests -p test_filename_reference.py
```

Use the executable without `.exe` on Unix. Output directories must be new. The adapter checks the source digest, writes the exact selection/requests and delegates to the existing local versioned-worker driver. It makes no network, Jev or user-file organization calls. Full replies, distributions, request/manifest identity and process termination are evaluated separately from label agreement. The scoring tool always sets `formalAcceptance=false`; absent automatic execution receipts do not mean zero automatic errors.

## Interrupted first run and continuation

`artifacts/p4-quality/filename-reference-run1/` recorded 135 complete progress lines before the enclosing command was aborted. There was no final report or observed clean exit, and the old driver had not yet written its in-memory full replies. These are **partial distribution records**, not a successful 536-case run or tensor-parity evidence. No old files were overwritten.

A local-only continuation verifies the original frozen-request digest and the 135-record prefix's IDs/input digests, then runs only the remaining **401 requests** in a fresh process under `filename-reference-run1-continuation/`. Its status is at `artifacts/p4-quality/filename-continuation-status.json`; a running process is not acceptance. The suffix will have its own dataset identity and report. The generic driver now writes `execution.json` before inference and flushes `raw-replies.jsonl` before each progress record, so later interruptions do not discard all previous tensor replies. This changes evidence retention, not model inputs or outputs.

No aggregate result or clean-run claim is available yet. A future combined distribution report must retain the interruption and unavailable-prefix-raw-reply limitations rather than presenting two processes as a single uninterrupted run. No thresholds, weights, candidate text or labels are tuned during this evaluation. P4 remains open; GPU and unattended moves remain disabled, and the user declined live Jev requests.
