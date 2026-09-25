# PoggetCore source attribution (P1 native probe)

Source: https://github.com/EnderMo/PoggetCore at commit `0b9d19f37a4f6dfc0e85fdd5e5d409f9b22dcce9` (tree `a5e3a32c11007351837d7cb29ec6ec3c1001eb12`), Apache-2.0; original license copied unmodified as `LICENSE`. Paths and SHA-256 hashes below describe **original upstream bytes**, before local changes.

| Imported path (relative to this directory) | Original SHA-256 | Local change |
| --- | --- | --- |
| `PoggetCoreManager.cpp` | `6a38fd41662f65a593c4039eea10eee88e71469d0209d2bca9f7fedbd27e0071` | none; real layout computation |
| `PoggetCoreManager.hpp` | `b956743dc7735d96b175a23c8d66d397d25f626bd7f9a4b2377cbaf4e2718e9b` | removed Meta/Vina/item-manager includes and singleton accessors, retained layout DTO and computation declaration |
| `Flow/FlowDataflow.hpp` | `75aa28c45518c0b31bf244011f16461fd15882fd4d912da025b9b4aa28d55f11` | none |
| `Flow/FlowModules.hpp` | `55ae3359cba04ff44b3d9171b743d38871cd391b994f84757bacd2b3b42e9c50` | none |
| `Flow/FlowRuntime.hpp` | `8dd2428d21d0380c463ca7d30fc233b2e120a05f6560d5d8d94738a4660976cc` | publish Core definition snapshots instead of scanning Vina catalog; manual runs use approved document snapshots; move/rename use host callback, never HistoryFileSystem; cancellation/stop (portable `std::thread` + atomic stop in place of unavailable Apple `std::jthread`); UTF-8 conversion; overflow retains baseline |
| `Storage/FlowJson.hpp` | `12844d172283636d61dce5f393551d6155d9cc2471092f966a39788874f91b87` | Apple libc++ lacks floating `from_chars`/`to_chars`; use locale-classic iostream conversion for finite JSON doubles with max_digits10 round-trip; leave integer and other-platform paths unchanged |
| `Storage/FlowStorage.hpp` | `0a69c57b703b0e39c5a0888fe16dede96cc4b276536373833935a08cc469625b` | none; parser/serializer retained; storage writer not instantiated |
| `LICENSE` | `1eb85fc97224598dad1852b5d6483bbcf0aa8608790dcc657a5a2a761ae9c8c6` | none |

The bridge and native tests are original DeskNest code. This local attribution must be carried into the central `docs/upstream.md` ledger by the integration owner (outside this worker's file ownership). The imported `FlowStorage.hpp` contains upstream persistence helpers, but this probe only calls parse/serialize; the product must keep Core as sole durable writer.
