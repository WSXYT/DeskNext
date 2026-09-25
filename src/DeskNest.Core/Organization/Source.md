# DeskBox P1(a) attribution

Extracted from Tianyu199509/DeskBox commit `44e7a0d48769f8dbfb6d107715e0508918fc7d87` (GPL-3.0-only; upstream LICENSE SHA-256 `230184f60bae2feaf244f10a8bac053c8ff33a183bcc365b4d8b876d2b7f4809`; project root LICENSE contains GPL-3.0-only). Original paths and SHA-256:

- `src/DeskBox/Services/DesktopAutoOrganizationStateMachine.cs`: `0cfda4bef1fc2bcf158a85850bd1abc19546b39e692cffc847ec3770e532a59c` → `Organization/DesktopAutoOrganizationStateMachine.cs`: namespace changed, OS-aware path comparer instead of Windows-only case folding, exclusion enum relocated.
- `src/DeskBox/Services/DesktopOrganizationRuleResolver.cs`: `4f8ccc5d6feb1be65b030e96a1b76b3bad460beffe27d2801a8a72a56e44b92b` → `Organization/DesktopOrganizationRuleResolver.cs`: namespace and classifier helper reference changed.
- `src/DeskBox/Services/ResilientJsonStore.cs`: `dd3b144983d96850c6016ccad8cfcd3fb9bbfcaf40cd8f7c858b529e7840c0d2` → `Storage/ResilientJsonStore.cs`: namespace changed, App.Log replaced with Trace.TraceWarning; load result/source and store publicly accessible to Core consumers.
- `src/DeskBox/Models/DesktopOrganizationModels.cs`: `18cd44d75b0d8496592ccd6c27d1a91f4782608dd7226f17f5f6f52fdd4d141b` → `Organization/DesktopOrganizationModels.cs`: only routing rule, classification identifiers/snapshot/exclusion/scope extracted; no transaction or file executor.
- `src/DeskBox/Models/WidgetConfig.cs`: `9f0f6213e5979ff7ec168d63dd2f33404bb756632c9e6dc6b3d4f5ef199e1dff` → `Organization/DesktopOrganizationModels.cs`: routing-only projection (`Id`, `WidgetKind`, `IsDisabled`, `MappedFolderPath`), enum values retained, no WinUI/layout.
- `src/DeskBox/Services/DesktopOrganizationClassifier.cs`: `16b79cd3bb65f1b345d8b7e6fcf9fb96a9fa5c249a6ea5076a780347ae78edab` → `Organization/DesktopOrganizationExtensions.cs`: exact `NormalizeExtension` method only.

The probe exposes no watcher, transaction, migration or automatic file movement. Recovery callers must inspect `ResilientJsonLoadResult.Source` before ever treating a default as a new empty journal. Source quarantine alone does not persist a failure marker across subsequent restarts; that is a later safety gate, not a P1(a) claim.
