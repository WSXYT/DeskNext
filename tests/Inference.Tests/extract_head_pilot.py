"""Extract only the explicitly prepared small pilot inputs, serially on NVIDIA. No training or CPU fallback."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from development_corpus import canonical, write_json


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("prepared", type=Path)
    p.add_argument("model", type=Path)
    p.add_argument("output", type=Path)
    p.add_argument("--adapter", type=int, required=True)
    args = p.parse_args()
    data = json.loads((args.prepared / "pilot.json").read_text(encoding="utf-8"))
    if data.get("datasetId") != "desknext-frozen-head-pilot-v1" or len(data["cases"]) != 30:
        raise ValueError("Expected the explicitly prepared 30-case pilot")
    args.output.mkdir(exist_ok=False)
    write_json(args.output / "source.json", {"pilotSha256": hashlib.sha256((args.prepared / "pilot.json").read_bytes()).hexdigest(),
                                           "scope": __doc__, "sourceSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()})
    completed = []
    try:
        for index, row in enumerate(data["cases"]):
            path = args.prepared / f"{index:02d}-request.json"
            actual = json.loads(path.read_text(encoding="utf-8"))["request"]
            digest = hashlib.sha256(canonical({k: actual[k] for k in ("state", "instructions", "candidates")})).hexdigest()
            if actual != row["request"] or digest != row["requestSha256"]:
                raise ValueError("Prepared request changed")
            with (args.output / f"{index:02d}-driver.log").open("xb") as log:
                run = subprocess.run([sys.executable, str(Path(__file__).with_name("run_directml_check.py")),
                    str(args.model), str(path), str(args.output / f"{index:02d}"), "--adapter", str(args.adapter), "--export-features"],
                    stdout=log, stderr=subprocess.STDOUT, timeout=110)
            if run.returncode != 0:
                raise RuntimeError(f"Extraction failed at {index}; stop without CPU fallback or discarding earlier evidence")
            completed.append({"index": index, "id": row["id"], "split": row["split"]})
            print(json.dumps(completed[-1]), flush=True)
    finally:
        write_json(args.output / "extraction.json", {"completed": completed, "complete": len(completed) == 30, "formalAcceptance": False})


if __name__ == "__main__":
    main()
