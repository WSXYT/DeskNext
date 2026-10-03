# DeskNext Laya multilingual FP32 model — preview distribution

This separate model asset contains public Laya weights converted to split ONNX
for DeskNext. It contains no user files, credentials or desktop metadata.

Upstream: Convai Innovations, Laya, licensed under Apache License 2.0.
See LICENSE-Laya.txt and https://huggingface.co/convaiinnovations/laya.
Checkpoint: 55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851, multilingual subdirectory.
Source: https://github.com/convaiinnovations/laya/tree/970dc8c5f63d7b886a68409493f37d569424f933

## Modifications

DeskNext converted the checkpoint to encoder.onnx / encoder.onnx.data and
head.onnx / head.onnx.data (FP32, ONNX opset 18). The exporter was modified for
strict weight loading and PyTorch 2.7 dynamic export; export-strict.patch is
included. Tokenizer and agent configuration are retained. manifest.json records
all file hashes, upstream identifiers, export-patch hash and toolchain versions.

Small fixed-input CPU parity checks and real local previews have passed.
This is not a classification-quality guarantee, GPU qualification, publisher
signature or a completed DeskNext release. The model is used for explicit
suggestions; it does not authorize automatic file movements.

Keep all files together in one directory. The DeskNext downloader pins the
archive hash independently of its contents. Manual users must obtain the hash
from a trusted application/source release, not trust a replaced archive's own
manifest. No Python installation is required to use this exported model.
