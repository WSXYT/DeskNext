# P2 workspace metadata boundary (first usable UI)

This is **not** the P3 file executor or P4 live inference. `DeskNest.Core.Workspace` owns one versioned metadata snapshot. The Avalonia app holds read-only copies and sends explicit updates through `WorkspaceStore.UpdateAsync`; native Pogget, model worker, and views never write `workspace.json` themselves.

## API

- `await WorkspaceStore.OpenAsync()` uses platform application data; tests may pass a temporary directory. It acquires a process-exclusive `.lock`. A corrupt primary recovers from a valid `.bak`; corrupt primary **and** backup writes a persistent `.recovery-required` marker and stops startup (including subsequent restarts). A future schema also stops startup without rewriting the source.
- `store.Snapshot` is a detached copy. `await store.UpdateAsync(state => state with { ... })` serializes updates, validates references, atomically saves by the imported `ResilientJsonStore`, increments `Revision`, and publishes the snapshot only after the save succeeds. Dispose with `await using` when the UI exits.
- `WorkspaceState` schema v1: `Revision`, `OnboardingStep` 0–5, `OnboardingComplete`, `Preset` (`office`, `development`, `creative`, `custom`), `Settings`, `Spaces`, `Files`, `Pending`, `Operations`. Stable IDs are GUIDs; user-supplied space name/description are not translated after creation.
- `WorkspaceSettings`: `Language`, `Theme` (`System`, `Dark`, `Light`), selected `InferenceProvider` (`Laya`/`Jev`), optional `ModelCacheDirectory`, `ManagedRoot`, monitored/excluded folders and `WantsMonitoring`. **These are preferences, not running features.** Jev API keys are not stored in P2; never put a secret in this JSON.
- `WorkspaceSpace`: stable ID, name/description, `Managed`/`Mapped`, physical folder. `WorkspaceFile`: metadata for a real user-selected path and space. `PendingFile`: source metadata + ambiguity/insufficiency/near-tie and optional real suggestion. `ProposedOperation`: *only* proposed or awaiting a user; no executed/moved/undo state exists yet.
- No automatic watcher, directory creation under managed root, model download, live Jev call, file moving, or real desktop takeover in P2. OOBE may save a preferred monitoring range but must show **not active** until P5. Model setup may be selected and pending; manual organization works without model weights.

## UI handoff

First launch with `OnboardingComplete == false` opens a resumable five-step wizard: language/theme; Jev-vs-Laya choice and optional local cache path; presets and editable categories; managed root and optional scan/whitelist preferences; review and complete. Persist each step without duplicating categories on restart. Do not pretend API-key testing, downloading, baseline capture, or physical file action happened.

After completion, the main studio shows the **persisted** spaces (not the P1 four fake example entries), two independently accessible managed/mapped space surfaces and a small drop-capsule entrypoint, an honest pending drawer, and a settings surface. Empty states explain why no files appear. A choice of suggested real space or newly created name/description updates **metadata**, but actual relocation/undo is disabled until P3; retain a pending item if not physically reconciled. Use existing 12 embedded dictionaries, extend all in lockstep with resource keys; Arabic RTL and left-to-right paths remain isolated. Large file collections must use a virtualizing `ListBox`, not `ItemsControl` over 10k rows. Synthetic 10k items live only in test code, never in the shipping UI.

Verification: restart OOBE at every step; settings/space/pending persistence; empty startup; corrupt-primary/backup and simultaneous-owner failures; 12-language key equality and RTL; 10k test metadata with viewport-bounded visuals; headless smoke and real window startup on supported OS. Record UI screenshots/actual measurements separately; don't claim a screenshot was taken if only a headless view existed.
