# Fixed upstream source references

Pinned on 2026-09-25 using the GitHub commits API and checked against each LICENSE. These are **inputs**, not an assertion that code was imported or tested. See [reuse matrix](planning/reuse-matrix.md) and [source review](planning/source-review.md) for candidate files and required adaptations.

| Component | Repository | Pinned commit | Tree | License at pin | Current use |
| --- | --- | --- | --- | --- | --- |
| DeskBox | https://github.com/Tianyu199509/DeskBox | `44e7a0d48769f8dbfb6d107715e0508918fc7d87` | `9d93c751ec83b20559a05e80ddeaf0c7c39a4c42` | GPL-3.0-only | Not imported |
| PoggetCore | https://github.com/EnderMo/PoggetCore | `0b9d19f37a4f6dfc0e85fdd5e5d409f9b22dcce9` | `a5e3a32c11007351837d7cb29ec6ec3c1001eb12` | Apache-2.0 | Not imported |
| Laya | https://github.com/NandhaKishorM/laya | `970dc8c5f63d7b886a68409493f37d569424f933` | `747a1179790fd36ddc915faafdfdb8272353e3f8` | Apache-2.0 (repository) | Not imported |
| Laya multilingual checkpoint | https://huggingface.co/convaiinnovations/laya/tree/55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851/multilingual | `55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851` | HF repo revision (includes `multilingual/model.safetensors`, `multilingual/tokenizer/tokenizer.json` and configs) | Apache-2.0 model card | Not downloaded/exported; file hashes and tokenizer equivalence pending P1 |

When importing: record file paths, hashes, upstream notices and modifications in this ledger; do not automatically treat model checkpoint files as repository source. CI/export Python remains separate from the end-user package.
