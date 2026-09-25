"""Development-only bridge for PyTorch 2.7 / onnxscript 0.5 ONNX export.

The exporter may retain referenced function definitions after inlining.
Remove only definitions proven unused. When graph opset already equals the
requested opset, conversion is a no-op; retain referenced functions and rely
on upstream ORT verification plus an independent ONNX checker. Never convert
a different opset implicitly or skip numerical checks.
"""
import runpy
import sys
from pathlib import Path

from onnx_ir.passes.common import RemoveUnusedFunctionsPass
from torch.onnx._internal.exporter import _compat

original_convert = _compat.onnxscript_apis.convert_version


def checked_convert(model, target_version):
    RemoveUnusedFunctionsPass()(model)
    if model.functions:
        names = [(fn.domain, fn.name) for fn in model.functions.values()]
        if model.graph.opset_imports.get("") != target_version:
            raise RuntimeError(f"Cannot convert referenced ONNX functions: {names}")
        print(f"Retaining referenced functions at opset {target_version}: {names}", file=sys.stderr)
        return model
    return original_convert(model, target_version)


_compat.onnxscript_apis.convert_version = checked_convert
upstream = Path(__file__).resolve().parents[2] / "artifacts/laya-source/laya-ts/scripts/export_onnx.py"
sys.argv = [str(upstream), *sys.argv[1:]]
runpy.run_path(str(upstream), run_name="__main__")
