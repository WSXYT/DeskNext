# Offline classification scoring / 离线分类评分

This developer tool scores **explicitly supplied** labels and recorded predictions. It does not collect files, call a model/API, move files, produce human labels, enable automation or grant P4/P7 acceptance. Python is a developer-only requirement, not an application runtime dependency.

```bash
python tests/Inference.Tests/quality_report.py labels.json predictions.json artifacts/quality/run-001.json
python -m unittest discover -s tests/Inference.Tests -p test_quality_report.py -v
```

Inputs are bounded to 16 MiB each. The output uses create-new mode, so an earlier report is not overwritten. Keep private datasets and reports outside Git or under ignored `artifacts/`; do not publish private filenames, credentials or unredacted operation receipts.

## Labels

The following is **one synthetic format example, not evaluation evidence**:

```json
{
  "schemaVersion": 1,
  "datasetId": "format-example-only",
  "provenance": {
    "labeling": "synthetic",
    "heldOut": false,
    "source": "Documentation example, not human annotations"
  },
  "developmentGroups": [],
  "samples": [{
    "id": "example-1",
    "group": "example-project",
    "language": "zh-CN",
    "scenario": "document-name",
    "requestSha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "candidates": ["documents", "filename-ambiguous", "categories-insufficient"],
    "expected": "documents",
    "highRisk": false
  }]
}
```

For actual evaluation, humans label the allowed/redacted metadata **without viewing model suggestions**. Record origin/permission and use `labeling: "independent-human", heldOut: true` only when those declarations are true. The tool cannot verify that assertion. `developmentGroups` must contain all development/tuning group IDs; variants/renames of the same file or project share a group and must not leak across splits. Missing development-group evidence is not treated as proven independence.

`requestSha256` identifies the frozen model input: canonical UTF-8 JSON of `{state, instructions, candidates}`, sorted object keys, no insignificant serialization whitespace, preserving array order and the exact state/instruction strings. Exclude volatile request IDs/revisions. Do not include expected labels or label rationales in a model request. Freeze and retain the input-generation source version separately.

`candidates` uses stable **canonical category IDs** in probability order, plus the two reserved special IDs. If the runtime uses `c0`/`c1` aliases, map them through that request's frozen space snapshot before scoring. Do not aggregate an alias whose meaning changes between samples as if it were one category. `protocol-failure` is reserved for the report. Duplicate sample IDs or duplicate input hashes are refused rather than counted as independent samples.

## Predictions

Use one provider/model per report. A local report requires the model-manifest digest. A model failure is an `error` record (or a missing result), not a fabricated ambiguous prediction.

```json
{
  "schemaVersion": 1,
  "datasetId": "format-example-only",
  "provider": "laya",
  "modelId": "example-only",
  "manifestSha256": "0000000000000000000000000000000000000000000000000000000000000000",
  "samples": [{
    "id": "example-1",
    "requestSha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "choice": "documents",
    "probabilities": [0.9, 0.05, 0.05]
  }]
}
```

Only a genuinely recorded **automatic execution** may add `automaticOperation: {"id": "<unique operation UUID>", "target": "<canonical target ID>"}`. A proposed suggestion or manually confirmed move does not count. Current manual-only workflows normally have no such records: their automatic denominator and confidence bound remain unknown, not zero-error evidence. Receipt authenticity still requires independent review; a UUID alone is not proof an operation occurred.

## Interpretation

The report includes confusion counts, ordinary accuracy, precision/recall for each special option, language/scenario/candidate-size strata, Brier score and ten-bin ECE. Missing/invalid model results stay in the total accuracy/coverage denominator and are counted separately as protocol failures. Brier/ECE are explicitly limited to valid distributions with their sample count.

Automatic error bounds use the exact one-sided 95% Clopper–Pearson interval. No automatic observations yields `null`, not 0%. `numericGateMet` checks at least 500 samples, no protocol failures, the reported automatic-error upper bound ≤1%, and no reported high-risk wrong automatic action. It does not certify sampling coverage or truth of supplied evidence. `readyForManualEvidenceReview` additionally checks declared human/held-out provenance and reported group separation. **`formalAcceptance` is always false**: provenance, actual execution, taxonomy mapping, coverage and policy must be reviewed before any phase can be closed.

### 中文说明

该入口只评分明确提供的标签与结果，不扫描私人文件、不调用付费 API，也不生成“人工标签”。标签应由人盲标，保留来源授权、项目分组及冻结输入；同一文件/项目的改名变体不能跨开发集与验收集。请求哈希不包含答案，运行时短 ID 须先按冻结快照还原为稳定分类 ID。

缺失响应和非法概率不会被伪装为“文件名模糊”。人工确认的移动、仅建议的目标不得填入自动执行回执；没有实际自动样本时，自动错误率上界保持未知。输出不会覆盖旧报告。文档样例及单元测试中的 500 条生成记录只测试算术与拒绝规则，**不能用于满足 P4 的 500 条独立人工标注要求**。

数值条件通过也不等于正式验收。工具始终输出 `formalAcceptance: false`；必须另行审阅数据独立性、覆盖范围、真实操作回执和冻结策略。
