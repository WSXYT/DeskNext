"""Freeze a P1 diagnostic manifest for the pinned multilingual ONNX bundle.

This is a reproducibility inventory, not a production installer signature.
"""
import hashlib
import json
from importlib.metadata import version
from pathlib import Path

import onnx

root = Path(__file__).resolve().parents[2]
checkpoint = root / "artifacts/model/multilingual"
bundle = root / "artifacts/model/exported-multilingual"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as data:
        for chunk in iter(lambda: data.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


weights_sha = sha256(checkpoint / "model.safetensors")
assert weights_sha == "9d628fd971b700382ac6f65920a86f149777b2e748e0c955fb3b19695aa8f204"
required = ["encoder.onnx", "head.onnx", "tokenizer.json", "rl_agent_config.json"]
for name in required:
    assert (bundle / name).is_file(), f"missing export {name}"
files = {p.name: sha256(p) for p in sorted(bundle.iterdir()) if p.is_file() and p.name != "manifest.json"}


def graph_io(name):
    graph = onnx.load(str(bundle / name), load_external_data=False)
    def describe(values):
        return [{"name": item.name, "dtype": onnx.TensorProto.DataType.Name(item.type.tensor_type.elem_type),
                 "shape": [dim.dim_value if dim.HasField("dim_value") else dim.dim_param
                           for dim in item.type.tensor_type.shape.dim]} for item in values]
    assert next(x.version for x in graph.opset_import if x.domain == "") == 18
    return {"inputs": describe(graph.graph.input), "outputs": describe(graph.graph.output)}


manifest = {
    "sourceCommit": "970dc8c5f63d7b886a68409493f37d569424f933",
    "checkpointRevision": "55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851",
    "weightsSha256": weights_sha,
    "exportPatchSha256": sha256(root / "tests/Inference.Tests/export-strict.patch"),
    "toolchain": {name: version(name) for name in ("torch", "transformers", "onnx", "onnxruntime", "tokenizers")},
    "opset": 18,
    "graphIO": {name: graph_io(name) for name in ("encoder.onnx", "head.onnx")},
    "files": files,
}
(bundle / "manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
for name in files:
    print(name, files[name])
