# P6 desktop utilities — partial implementation

## Native tray entry (2026-10-03)

Normal desktop startup now registers an Avalonia tray icon using the existing DeskNext brand geometry. Its localized menu restores/activates the existing workbench, opens the existing borrowed capsule window, or requests normal application shutdown. The capsule item is unavailable before the Studio is ready. This does not hide the workbench on close, enable background startup, or start monitoring. Unsupported shell integration leaves the visible workbench usable.

`Services/DesktopTray.cs` owns the icon and releases its application registration and view-model/localization subscriptions when the workbench closes. Native smoke modes do not install the tray implicitly.

Bounded checks: App Release build with zero warnings/errors; the existing headless workflow constructs the production tray/menu, restores the existing minimized window, checks localized labels and the shutdown callback, and verifies disposal removes its registration (`artifacts/p3-publication-tests/headless-desktop-tray.log`, terminal Success/ManagedClipboardWorkflowVerified true, 15,102 ms). These are command/lifecycle checks, not native taskbar-click or multi-platform tray-display acceptance. No full-suite, CI or package rerun.

Still open: global summon shortcut, desktop attachment, tray-to-background lifetime preferences, platform-specific tray display/interaction, and the remaining F03/F06–F11 utilities. No P6 gate is marked passed.
