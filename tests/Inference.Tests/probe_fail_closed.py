"""Fail-closed protocol and artifact smoke test for the P1 published CPU worker."""
import json
import os
import shutil
import struct
import subprocess
import tempfile
from pathlib import Path

root = Path(__file__).resolve().parents[2]
bundle = root / "artifacts/model/exported-multilingual"
worker = root / "artifacts/publish/inference-win-x64/DeskNest.Inference.exe"
request = json.loads((root / "tests/Inference.Tests/fixtures/multilingual-request.json").read_text(encoding="utf-8"))["request"]
payload = json.dumps(request, ensure_ascii=False).encode("utf-8")
frame = struct.pack("<I", len(payload)) + payload


def invoke(folder, data):
    proc = subprocess.run([str(worker), "--inference-worker", str(folder)], input=data,
                          capture_output=True, timeout=60)
    assert proc.returncode != 0 and not proc.stdout, (proc.returncode, proc.stdout[:80])
    return proc.stderr.decode("utf-8", errors="replace")


def hardlink_bundle(folder):
    for name in json.loads((bundle / "manifest.json").read_text(encoding="utf-8"))["files"]:
        os.link(bundle / name, folder / name)
    shutil.copyfile(bundle / "manifest.json", folder / "manifest.json")


assert "Invalid frame size" in invoke(bundle, struct.pack("<i", 1024 * 1024 + 1))
print("PASS oversized frame rejected without a response", flush=True)
with tempfile.TemporaryDirectory(dir=root / "artifacts/model") as temporary:
    folder = Path(temporary)
    hardlink_bundle(folder)
    manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    manifest["files"]["encoder.onnx"] = "0" * 64
    (folder / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    assert "Model hash mismatch: encoder.onnx" in invoke(folder, frame)
    print("PASS tampered graph hash rejected before inference", flush=True)
with tempfile.TemporaryDirectory(dir=root / "artifacts/model") as temporary:
    folder = Path(temporary)
    hardlink_bundle(folder)
    (folder / "encoder.onnx.data").unlink()
    assert "encoder.onnx.data" in invoke(folder, frame)
    print("PASS missing external-data rejected before inference", flush=True)
