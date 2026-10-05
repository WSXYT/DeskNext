# Laya multilingual FP32 — DeskNext model preview

**English** · [简体中文](#简体中文)

This release contains the **optional local classification model**, not the DeskNext application. It is a fixed multilingual FP32 ONNX package for the app's Laya CPU preview. No Python environment or cloud key is required at runtime.

## Download

| Asset | Purpose |
| --- | --- |
| `desknext-laya-multilingual-fp32-v1.zip` | Complete model bundle, **812,781,615 bytes** (about 813 MB / 775 MiB) |
| `SHA256SUMS` | Archive integrity checksum |

```text
bd2464a8b63f195fd1aed579b355b37d3ef6f45f1e796116b041846a7de3f6fc
```

In DeskNext, open **Settings → Laya → Install model package**, select the ZIP, wait for verification and installation, then **Save settings** to activate the resulting model folder. Alternatively, extract the complete package and select its directory; keep both `.onnx.data` files beside their graphs. Model installation does not move your classification files.

## Included and traceable

- Split `encoder.onnx` and `head.onnx` graphs plus external data, tokenizer and calibration configuration; ONNX opset 18.
- Pinned Laya source: `970dc8c5f63d7b886a68409493f37d569424f933`.
- Multilingual checkpoint: `55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851`.
- Ten archive entries, including the original Apache-2.0 license, modification notice and export patch.
- The application pins archive and manifest hashes and verifies model files before activation.

The package contains no user files, credentials, application binaries or training dataset. Earlier fixed-input CPU equivalence and local preview checks are documented in the repository; they do not establish general classification accuracy, GPU support, all-platform compatibility or automatic-move safety. This remains a **model prerelease**, not a signed application release. A checksum pins bytes; it is not a publisher signature.

See the [project overview](https://github.com/WSXYT/DeskNext#readme), [中文说明](https://github.com/WSXYT/DeskNext/blob/main/README.zh-CN.md) and [deployment status](https://github.com/WSXYT/DeskNext/blob/main/docs/planning/p4-report.md).

---

## 简体中文

本预发布包含**可选的本地分类模型**，不是 DeskNext 应用安装包。它是供 Laya CPU 预览使用的固定多语言 FP32 ONNX 模型，运行时无需 Python 或云端密钥。

### 下载与使用

- `desknext-laya-multilingual-fp32-v1.zip`：完整模型包，**812,781,615 字节**，约 813 MB / 775 MiB。
- `SHA256SUMS`：压缩包完整性校验文件，SHA-256 见上方。
- 在 DeskNext 中打开**设置 → Laya → 安装模型包**，选择 ZIP，等待校验与安装，再点击**保存设置**启用新模型目录。也可完整解压后选择目录；不要拆散图文件与对应的 `.onnx.data` 文件。
- 模型安装不会移动你的分类文件，也不会隐式授权自动整理。

### 来源与边界

模型包包含 encoder/head 双图、外部数据、分词器、校准配置，以及 Apache-2.0 许可证、修改声明和导出补丁；共十个文件。固定上游源码和 checkpoint 版本见上方。应用会校验压缩包、清单及模型文件。

包内没有用户文件、密钥、应用二进制或训练数据。既有 CPU 固定输入等价检查与本地预览证据，不等于分类质量、GPU、所有平台或自动移动验收。本条目仍为**模型预发布**，不是已签名的应用正式版；校验和也不等于发布者签名。
