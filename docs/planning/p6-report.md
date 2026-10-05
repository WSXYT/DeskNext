# P6 desktop utilities — partial implementation

## Native tray entry (2026-10-03)

Normal desktop startup now registers an Avalonia tray icon using the existing DeskNext brand geometry. Its localized menu restores/activates the existing workbench, opens the existing borrowed capsule window, or requests normal application shutdown. The capsule item is unavailable before the Studio is ready. This does not hide the workbench on close, enable background startup, or start monitoring. Unsupported shell integration leaves the visible workbench usable.

`Services/DesktopTray.cs` owns the icon and releases its application registration and view-model/localization subscriptions when the workbench closes. Native smoke modes do not install the tray implicitly.

Bounded checks: App Release build with zero warnings/errors; the existing headless workflow constructs the production tray/menu, restores the existing minimized window, checks localized labels and the shutdown callback, and verifies disposal removes its registration (`artifacts/p3-publication-tests/headless-desktop-tray.log`, terminal Success/ManagedClipboardWorkflowVerified true, 15,102 ms). These are command/lifecycle checks, not native taskbar-click or multi-platform tray-display acceptance. No full-suite, CI or package rerun.

## Cross-space catalog search (2026-10-03)

The sidebar Search entry and in-workbench Ctrl/Cmd+K search registered file/folder names across every space, independently of the managed/mapped sidebar filter. Results show the owning space, keep paths in LTR tooltips, and are capped at 100 with a refinement notice. Enter, double-click or Show in space selects the live catalog item in its space so the existing open/reveal/preview/file commands remain the sole action implementation. No disk scan, external index, metadata save or file operation is performed by searching/navigation. Blank/no-match queries clear the action target; snapshot refresh recomputes the results.

Bounded checks: zero-warning/error App Release build and the existing headless workflow (`artifacts/p3-publication-tests/headless-workspace-search.log`, Success/DictionaryParityVerified/ManagedClipboardWorkflowVerified true, 12,031 ms). It injects Ctrl/Cmd+K from another tab, searches matching names across spaces while a sidebar filter is active, follows the production location button, verifies unchanged revision/source content and clears a no-match target. Four keys were added across all twelve locales. LSP reports only redundant-using warnings in the view and an inconclusive new partial-file check; compiler/runtime evidence is authoritative. No full-suite, CI or package rerun.

## Space appearance and automatic accents (2026-10-04)

Space windows now offer List, Grid and Details arrangements from their view menu. List/Details stay virtualized; Grid uses pages of at most 96 items rather than an unbounded WrapPanel. Windows file-type icons come from SHGetFileInfo with USEFILEATTRIBUTES, not document execution or thumbnail extraction. Spaces/items can choose a local icon from the space-window menu; the capsule has its own context-menu choice/reset. Custom images affect DeskNext only, are decoded off the UI thread with bounded dimensions, and remain at the user-selected path (missing images fall back). No file associations or desktop.ini are changed.

Settings offers a full opaque color ring plus hex/presets/reset, or Windows-accent/current-wallpaper sampling. Automatic modes preserve the saved manual color, update on platform color notifications and window activation, and fall back to the Windows/manual accent when the wallpaper is unavailable. Sampling is a local thumbnail-based dominant-color approximation; it never changes system wallpaper/settings and is not per-monitor slideshow support. Space/capsule windows explicitly default to non-topmost; actual desktop-layer attachment is still open.

Bounded evidence: App Release build zero warnings/errors; the existing headless workflow passed (`artifacts/ui-review/headless-system-icons-layout.log`, terminal Success/DictionaryParityVerified/ManagedClipboardWorkflowVerified, 17,078 ms), covering arrangement switching, icon set/reset with no file-operation history, capsule synchronization, Windows type icon retrieval, and manual/automatic accent settings. WorkspaceStore tests passed 15/15 with reopen persistence of source mode, manual color, view mode and space/item/capsule icon paths. Native Chinese Grid capture: `artifacts/ui-review/native-20261004-201918-3a9317/` (real OS pixels, isolated fixtures, one display at 250%; capture-only topmost). No full CI/package repetition and no physical picker, all-file-type or multi-monitor acceptance claim.

Still open: global OS summon shortcut, non-catalog search providers/Everything, desktop attachment, tray-to-background lifetime preferences, platform-specific tray display/interaction, and the remaining F03/F06–F11 utilities. No P6 gate is marked passed.
