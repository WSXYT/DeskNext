"""Developer-only DirectML decomposition of the pinned Laya CPU bundle; never run by the app.
Requires the export environment's onnx/numpy, not user Python. No training/quantization.
"""
import argparse
import hashlib
import json
import shutil
from pathlib import Path

import numpy as np
import onnx
from onnx import external_data_helper, helper, numpy_helper

BASE_MANIFEST = "3a65f3fb45166e0e7e2c044802740712ae48770cc26a11a715984d7b3aa81f52"


def sha(path):
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def patch_reshape(model):
    nodes = {out: node for node in model.graph.node for out in node.output}
    constants = {t.name: numpy_helper.to_array(t) for t in model.graph.initializer if t.data_location != onnx.TensorProto.EXTERNAL}
    for node in model.graph.node:
        if node.op_type == "Constant":
            for attr in node.attribute:
                if attr.name == "value" and attr.type == onnx.AttributeProto.TENSOR:
                    constants[node.output[0]] = numpy_helper.to_array(attr.t)

    def has_minus_one(value, depth=0):
        if depth > 16:
            return False
        if value in constants:
            a = constants[value]
            return a.dtype.kind == "i" and bool(np.any(a == -1))
        node = nodes.get(value)
        if node is None:
            return False
        if node.op_type == "Concat" and any(a.name == "axis" and a.i == 0 for a in node.attribute):
            return any(has_minus_one(v, depth + 1) for v in node.input)
        return node.op_type in ("Unsqueeze", "Squeeze", "Identity") and has_minus_one(node.input[0], depth + 1)

    changed = []
    for node in model.graph.node:
        if node.op_type == "Reshape" and has_minus_one(node.input[1]):
            for attr in node.attribute:
                if attr.name == "allowzero" and attr.i == 1:
                    attr.i = 0
                    changed.append(node.name)
    return changed


def derive(source, target):
    if sha(source / "manifest.json") != BASE_MANIFEST:
        raise ValueError("Not the pinned CPU model manifest")
    manifest = json.loads((source / "manifest.json").read_text(encoding="utf-8"))
    for name, digest in manifest["files"].items():
        if Path(name).name != name or sha(source / name) != digest:
            raise ValueError("Source asset mismatch: " + name)
    target.mkdir(parents=True, exist_ok=False)
    changed = {}
    for name in ("head.onnx.data", "rl_agent_config.json", "tokenizer.json"):
        shutil.copyfile(source / name, target / name)
    # Separate output bytes: altering/removing this local experiment cannot alter the CPU bundle.
    shutil.copyfile(source / "encoder.onnx.data", target / "token-embedding-source.bin")
    for name, expected_count in (("encoder.onnx", 44), ("head.onnx", 1)):
        model = onnx.load(str(source / name), load_external_data=False)
        changed[name] = patch_reshape(model)
        if len(changed[name]) != expected_count:
            raise ValueError("Unexpected graph transformation coverage")
        if name == "encoder.onnx":
            weight = next(t for t in model.graph.initializer if t.name == "encoder.embeddings.tok_embeddings.weight")
            consumers = [n for n in model.graph.node if weight.name in n.input]
            if (len(consumers) != 1 or consumers[0].op_type != "Gather" or list(consumers[0].input) != [weight.name, "input_ids"] or
                    list(consumers[0].output) != ["embedding"] or next(a.i for a in consumers[0].attribute if a.name == "axis") != 0 or
                    weight.data_type != onnx.TensorProto.FLOAT or list(weight.dims) != [256000, 768]):
                raise ValueError("Unexpected token embedding consumer")
            entry = {e.key: e.value for e in weight.external_data}
            if entry["location"] != "encoder.onnx.data":
                raise ValueError("Unexpected embedding storage")
            layout = {"file": "token-embedding-source.bin", "offset": int(entry["offset"]), "rows": 256000, "width": 768,
                      "input": "embedding", "format": "little-endian-float32", "sourceTensor": weight.name}
            (target / "embedding-layout.json").write_text(json.dumps(layout, indent=2), encoding="utf-8")
            model.graph.input.append(helper.make_tensor_value_info("embedding", onnx.TensorProto.FLOAT, ["batch", "seq", 768]))
            model.graph.node.remove(consumers[0])
            model.graph.initializer.remove(weight)
            with (source / "encoder.onnx.data").open("rb") as old, (target / "encoder.onnx.data").open("xb") as new:
                for tensor in model.graph.initializer:
                    if tensor.data_location != onnx.TensorProto.EXTERNAL:
                        continue
                    ext = {e.key: e.value for e in tensor.external_data}
                    if ext["location"] != "encoder.onnx.data":
                        raise ValueError("Unexpected external weight file")
                    remaining, offset = int(ext["length"]), new.tell()
                    old.seek(int(ext["offset"]))
                    while remaining:
                        data = old.read(min(1024 * 1024, remaining))
                        if not data:
                            raise EOFError("Incomplete external tensor")
                        new.write(data)
                        remaining -= len(data)
                    tensor.raw_data = b""
                    external_data_helper.set_external_data(tensor, "encoder.onnx.data", offset, int(ext["length"]))
                    tensor.ClearField("raw_data")
            manifest["graphIO"][name]["inputs"].append({"dtype": "FLOAT", "name": "embedding", "shape": ["batch", "seq", 768]})
        onnx.save(model, str(target / name))
        onnx.checker.check_model(str(target / name))
    for name in ("encoder.onnx", "encoder.onnx.data", "head.onnx", "token-embedding-source.bin", "embedding-layout.json"):
        manifest["files"][name] = sha(target / name)
    manifest["experimentalVariant"] = "directml-reshape-row-lookup"
    manifest["baseManifestSha256"] = BASE_MANIFEST
    manifest["derivation"] = {"reshapeNodes": changed, "tokenLookup": "Exact host FP32 Gather rows; neural graphs on GPU.",
                              "scriptSha256": sha(Path(__file__)), "onnxVersion": onnx.__version__}
    (target / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"directory": str(target), "manifestSha256": sha(target / "manifest.json"),
                      "encoderGraphWeightsBytes": (target / "encoder.onnx.data").stat().st_size,
                      "sourceModified": False, "productionEnabled": False}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("target", type=Path)
    args = parser.parse_args()
    derive(args.source.resolve(strict=True), args.target.resolve())
