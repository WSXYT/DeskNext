# AI-assisted development diagnostic — not acceptance

## Scope and provenance

On 2026-10-07 the user asked the assistant to prepare/prelabel samples and declined live Jev calls. `tests/Inference.Tests/fixtures/ai-development-corpus.json` contains **120 assistant-authored synthetic cases: 12 languages × 10 scenario families**. Expected labels and their rationales were fixed before inference. Translations belong to the same development groups; they are not 120 independent real-world cases. All are explicitly `ai-assisted-synthetic`, `heldOut=false`. No private filenames, file contents or credentials were collected.

The three categories are Finance, Design and Software, with fixed English descriptions, plus `filename-ambiguous` and `categories-insufficient`. This deliberately tests non-English filenames against an English taxonomy; it does **not** evaluate every localized preset. Cases cover invoices, budgets, logos, wireframes, SQL migration, authentication code, unclear names/folders, an uncovered recipe topic and explicit user hints. The assistant's labels are provisional judgments, not human ground truth.

## Reproduce or inspect

```bash
# Prepare the names, provisional labels/rationales and frozen requests without inference.
python tests/Inference.Tests/development_corpus.py artifacts/quality/prepared --prepare-only

# Run only the explicit synthetic metadata through a local App-hosted CPU worker.
python tests/Inference.Tests/development_corpus.py artifacts/quality/development-run \
  --worker /absolute/path/to/DeskNest.App.exe \
  --model /absolute/path/to/verified/model

# An exact-case recheck is a separate subset, never merged into the original report.
python tests/Inference.Tests/development_corpus.py artifacts/quality/recheck \
  --worker /absolute/path/to/DeskNest.App.exe --model /absolute/path/to/verified/model \
  --case es-ES:generic-folder
```

Use the executable without `.exe` on Unix. Python is developer-only and uses its standard library; it is not a DeskNext runtime dependency. Choose a fresh output directory: the tool never overwrites prior results. The request builder mirrors the current compact category aliases/Unicode JSON state/instructions used by the App, but this diagnostic does **not** exercise the GUI or installed-package path. No labels or rationales are included in model requests. Request IDs, protocol version, manifest digest and distributions are checked; the existing offline scorer maps `c0`–`c2` back to canonical category IDs.

Outputs include `labels.json`, `requests.json`, `predictions.json`, raw replies, stderr, flushed per-request progress, the exact runner/fixture and `disagreements.json`. Failed requests remain in the denominator. There are no automatic-operation receipts. The independent-human gate and automatic-error confidence bound therefore cannot pass, regardless of sample count.

## First actual CPU run

Evidence: `artifacts/p4-quality/development-v1-run1/` (local Windows, original pinned CPU bundle).

- 120 requests attempted; 119 responses; **1 protocol/runtime failure**. The runner exits nonzero rather than calling the batch successful.
- Provisional-label agreement: **60/120 (50.0%)**; ordinary categories **39/84 (46.4%)**.
- Ambiguous-name recall: **21/24 (87.5%)**; precision **21/68 (30.9%)**. The model often uses ambiguity for names that the provisional annotations consider clear.
- Categories-insufficient: **0/12** uncovered-recipe cases selected it. Eleven selected ambiguous and one selected Software.
- No files moved and no automatic observations: the one-sided automatic-error bound is **unknown**, not 0%.

| Filename language | Agreement / 10 | Runtime failures |
| --- | ---: | ---: |
| en-US | 9 | 0 |
| zh-CN | 5 | 0 |
| zh-TW | 3 | 0 |
| ja-JP | 7 | 0 |
| de-DE | 8 | 0 |
| fr-FR | 6 | 0 |
| es-ES | 4 | 1 |
| pt-BR | 3 | 0 |
| ru-RU | 6 | 0 |
| ar-SA | 3 | 0 |
| hi-IN | 3 | 0 |
| bn-BD | 3 | 0 |

Examples: `五月供应商发票.pdf` selected ambiguous (90.7%); `新建文件夹` selected Finance (39.0%); `Vegetable soup recipe.md` selected ambiguous (81.9%) despite a clearly uncovered topic. No thresholds, probabilities or labels were changed to make these results look better.

`es-ES:generic-folder` ended before its reply; worker stderr reported insufficient system resources while accessing `tokenizer.json`. The next request used a fresh worker (two starts total), and the final worker exited cleanly. A **separate one-case recheck** (`development-v1-recheck-es-folder/`) returned ambiguous with clean process exit. That is evidence the individual request can execute, not a diagnosis/fix of the original resource failure; the 120-row report remains unchanged and failed.

Identifiers: model manifest SHA-256 `3a65f3fb45166e0e7e2c044802740712ae48770cc26a11a715984d7b3aa81f52`; App host SHA-256 `d80ba1a8a61743d04ad3ff0adbc0a108ed2d9bf8a0587891474a564049b802a2`. Label file SHA-256 `8720952e659c60282f9867dd212d9eab4e0179bed17cd135cfc6fc7a08a0c4d0`; prediction file SHA-256 `f9706de1573d0c0b9d42e0c7625aa1cce4c83fd12fa17b11ce02584d87d8770b`.

## Rejected wording experiment

A bounded follow-up changed only the two special-option descriptions to distinguish an unclear subject from a clear but uncovered subject. Twelve cases (invoice/generic name/recipe in English, Simplified Chinese, Japanese and German) were selected after inspecting the first run, so this is explicitly tuning data. The actual CPU worker returned all twelve and exited cleanly, but agreement fell from **6/12 to 5/12**; no recipe improved, and the German invoice regressed from Finance to ambiguous. The experiment was **not adopted**. Production descriptions, model weights, labels and thresholds remain unchanged. Raw comparison: `artifacts/p4-quality/wording-experiment1/`; local experiment script: `artifacts/p4-quality/compare-wording.py`.

### Rejected raw-filename state experiment

A second twelve-case comparison kept the original special descriptions but replaced the inner JSON state with the raw filename. Agreement rose from 6/12 to 8/12, recovering Chinese/Japanese invoices, but those languages' uncovered recipes changed from ambiguity to **wrong concrete categories** (Software/Finance). It was also **not adopted**: aggregate agreement alone is not evidence of a safer fallback policy. Outputs remain under `artifacts/p4-quality/plain-state-experiment/`; production JSON state stays unchanged.

## Frozen-corpus rerun after token-map reuse

`artifacts/p4-quality/development-v1-token-cache-run2/` reran the same 120 frozen requests after removing repeated vocabulary parsing. It returned **120 valid responses, zero protocol failures, one worker start and a clean exit**. All **119 previously valid complete versioned replies are identical** (including tensors, logits, probabilities and choices); the formerly failed Spanish folder request returned ambiguous. Agreement is **61/120 (50.8%)**, ordinary agreement remains **39/84**, and insufficient-category recall remains **0/12**. No inputs, labels, thresholds or model assets changed. The original failed run is retained; this successful rerun is not proof that resource exhaustion cannot recur, and it does not satisfy the independent quality gate.

## What this changes

This gives P4 an actual, reproducible development signal instead of another arithmetic-only scorer. It shows why model suggestions must remain reviewable, especially multilingual inputs and the two fallback options. Future prompt/model changes can compare against these fixed cases, but doing so makes them development data, not independent validation. A blind human-labeled held-out corpus, real service evidence and the other P4 requirements still remain open; the user declined Jev calls, so none were made.

## 中文摘要

已经由助手准备并预标注了120条不涉及私人文件的多语言样本，也真正跑了本地模型。首轮仅60条与预标注一致，且有一次资源不足导致的请求失败；错分清单可直接查看，不需要先学习数据格式。此数据供开发定位问题，不能冒充500条独立人工验收数据。单独复查成功未覆盖原失败记录。当前不应启用无人确认的自动移动，P4未通过；后续阶段按最新要求保持等待。
