"""Local training continuation only. Never load downloaded pickle objects or an unverified partial checkpoint."""
import hashlib
import json
from pathlib import Path


def digest(path):
    with Path(path).open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def save(torch, folder, model, optimizer, rng, losses, contract):
    folder = Path(folder)
    path = folder / f"epoch-{len(losses):03d}.pt"
    checkpoint = {"contract": contract, "epoch": len(losses), "losses": list(losses),
                  "model": model.state_dict(), "optimizer": optimizer.state_dict(),
                  "shuffleRng": rng.getstate(), "torchRng": torch.get_rng_state(),
                  "cudaRng": torch.cuda.get_rng_state_all()}
    # Stream directly; don't clone the entire optimizer to CPU in a second in-memory dictionary.
    with path.open("xb") as file:
        torch.save(checkpoint, file)
    receipt = {"sha256": digest(path), "epoch": len(losses), "contract": contract}
    with path.with_suffix(".json").open("x", encoding="utf-8") as file:
        json.dump(receipt, file, indent=2)
    return str(path)


def restore(torch, path, model, optimizer, rng, contract):
    path = Path(path)
    receipt = json.loads(path.with_suffix(".json").read_text(encoding="utf-8"))
    if receipt["contract"] != contract or digest(path) != receipt["sha256"]:
        raise ValueError("Training continuation identity or checksum mismatch")
    # weights_only restricts loading to tensors/basic containers. The caller still supplies trusted local artifacts.
    state = torch.load(path, map_location="cpu", weights_only=True)
    if state["contract"] != contract or state["epoch"] != receipt["epoch"] or state["epoch"] != len(state["losses"]):
        raise ValueError("Inconsistent training checkpoint")
    model.load_state_dict(state["model"], strict=True)
    optimizer.load_state_dict(state["optimizer"])
    rng.setstate(state["shuffleRng"])
    torch.set_rng_state(state["torchRng"])
    torch.cuda.set_rng_state_all(state["cudaRng"])
    return list(state["losses"])
