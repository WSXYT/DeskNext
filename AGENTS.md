<!-- pi-agents-md:begin version=1 scope=. -->
## Implementation boundaries
- Product: 栖格 · DeskNest. `PLAN.md` is the approved P0–P7 sequence; immediately mark each truly completed step through Plannotator. `docs/planning/feature-tracker.md` tracks F01–F13 without conflating targets with working features. `docs/upstream.md` pins inputs; append imported file/hash/notices when copying source.
- P0 skeleton: `DeskNest.sln`, .NET 10 projects `src/DeskNest.Core`, `DeskNest.Platform`, `DeskNest.Inference`, `DeskNest.App`; Avalonia app is intentionally a library until the UI agent writes an entrypoint. No real file actions/model runtime yet.
- UI code/design goes to a subagent whose task names Gemini-3.8-flash-high. Non-UI work may use SubAgent where helpful on the session default model. Parent owns contracts, integration, safety and evidence; one writer per worktree/area, independent review for safety-critical work. No mockup or subagent success report counts as tested functionality.
- Core is the sole durable state and file-operation authority. See `docs/planning/source-review.md` S01–S16 before porting DeskBox/Pogget; never auto-move desktop files before fault-injection and recovery gates.
- End-user inference must not depend on Python; development-only Python exports compare A–D raw request, tokenizer, tensors, probabilities, decisions and actual file actions (`docs/planning/inference-validation.md`).
- Use localized resource keys for all new UI strings, with 12-language/RTL/keyboard checks. Product label is confirmed; tagline remains a draft. GPL-3.0-only is the approved plan and permits paid redistribution; preserve upstream notices and corresponding source.
<!-- pi-agents-md:end -->
