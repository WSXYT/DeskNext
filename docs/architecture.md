# DeskNest P0 contracts and layout

The codebase is **only an initial buildable project skeleton**. No desktop UI, model runtime, file mover or upstream source has been imported yet. The application name is **栖格 · DeskNest**; internal assembly prefix is `DeskNest`. Provisional cross-platform application identifier: `app.desknest.desktop` (verify ownership/collision before store publication). The user-facing slogan is not fixed.

| Project | Responsibility | Owns user files? |
| --- | --- | --- |
| `src/DeskNest.Core/` | spaces, rules, file transaction, monitoring, Flow definitions, persistence, i18n | Sole durable writer / transaction authority |
| `src/DeskNest.Platform/` | Windows/macOS/Linux volume identity, Shell, drag-drop, key store | No independent transaction state |
| `src/DeskNest.Inference/` | Jev adapter and native ONNX worker protocol; no Avalonia dependency | No file-operation capability |
| `src/DeskNest.App/` | Avalonia UI and early worker entrypoint, owned by the UI delegate for views | UI only, calls Core |
| `native/pogget/` (P1) | Selected Pogget layouts and sequential Flow logic, mediated through bounded ABI | No independent user-file executor |

Default **managed user files**: `~/DeskNest/Spaces` (user selectable, never silently delete on uninstall). Configuration and operation recovery records use the OS application-data directory for `app.desknest.desktop`: Windows `%LOCALAPPDATA%/DeskNest`, macOS `~/Library/Application Support/DeskNest`, Linux `${XDG_DATA_HOME:-~/.local/share}/desknest`. Model cache is separately selectable; defaults to Windows `%LOCALAPPDATA%/DeskNest/Models`, macOS `~/Library/Caches/DeskNest/Models`, Linux `${XDG_CACHE_HOME:-~/.cache}/desknest/models`. These paths are a P0 design contract, **not yet implemented or verified**. Resolve home/XDG safely and check nesting/symlinks before monitoring; no automatic action on existing desktop files at first launch.

P1 must prove a headless testable Core extract, native ABI and CPU A–D inference before enabling automated file operations. When adapting upstream data formats, preserve recovery-journal compatibility until pending operations have been resolved. The immutable upstream references are recorded in [upstream.md](upstream.md); source/code import and build provenance must be added when it occurs.
