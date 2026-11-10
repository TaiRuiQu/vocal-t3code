# Windows voice orchestrator eval

This eval runs the companion's actual local Codex or Claude Code driver against its embedded orchestrator instructions, `VoiceTools.Catalog`, `VoiceTools`, and loopback `LocalMcpServer`. Only remote environments are fake: authored Acme projects supply a small tool catalog and record the destination and task actually dispatched. No real cloud host, project, repository, or conversation is changed.

The entry point is `apps/voice-windows/T3.Voice.Tests/VoiceEval.cs`. It lives in the native test executable because the component under test is .NET/Windows, uses Windows SQLite and DPAPI, and must exercise the real CLI transport. It does not require the TypeScript eval package's Node runtime or dependencies.

Run on Windows with .NET 10 and an installed, signed-in Codex or Claude Code CLI. Use an executable path with `--executable` if it is absent from PATH; batch files are unsupported. The harness reuses CLI authentication and does not request an API key.

```powershell
dotnet run --project apps/voice-windows/T3.Voice.Tests -- --eval --dry-run
dotnet run --project apps/voice-windows/T3.Voice.Tests -- --eval --provider codex --model <installed-model-id> --case dispatch-and-return --samples 1 --out $env:TEMP/t3-voice-eval-smoke
dotnet run --project apps/voice-windows/T3.Voice.Tests -- --eval --provider claude --model <available-model-id> --samples 3 --out $env:TEMP/t3-voice-eval-claude
```

`--out` is required for a real run and must not already contain `results.jsonl`. Keep results outside the worktree or in the gitignored `evals/runs/` directory. `--dry-run` imports and hashes the real instructions and tool catalog, prints bounded schema counts and case summaries, and starts neither a CLI nor a model. Omitting `--model` uses that CLI's configured default; recorded results distinguish this from an explicitly requested model. The driver does not expose the executed model ID, so the harness never invents one.

Real runs consume the selected CLI account's subscription quota or configured billing. The harness cannot measure monetary cost and records it as unknown. Start with one case and one sample. Do not describe real CLI runs as free merely because no API key is needed.

| Case | Source | Behavior checked |
| --- | --- | --- |
| `ambiguous-project` | Authored control from this feature's requirements | Asks which Acme project; does not dispatch while the target is unclear. |
| `new-task-preserves-question` | Authored regression case for persistent questions | Dispatches an explicit unrelated task while preserving the unanswered colour question. |
| `dispatch-and-return` | Authored ordinary-work control | Selects the named machine and project, supplies the goal and test requirement, dispatches once, and returns without waiting or polling. |
| `completion-report` | Authored completion control | Reports the supplied health-check outcome and PR, without starting or waiting for more work. |

`--case` accepts comma-separated IDs. `--samples` defaults to three and accepts 1–10. `--timeout-seconds` bounds each trajectory, defaults to 120, and accepts 20–600. Results contain behavioral checks, bounded final text and observed fake-remote calls; they exclude CLI session IDs, bearer credentials and the temporary workspace path. Native provider errors are sanitized again before writing. The harness starts each sample in a separate temporary database and local CLI conversation.

`results.jsonl` records the requested provider/model, harness version, production instruction/catalog hashes and each sample's outcome. `summary.json` aggregates results; a case fails when half or more samples fail. `--baseline <previous-output-directory-or-results.jsonl>` compares pass counts and requires the same provider and requested model. Compare instruction changes against a baseline on that same model; the initial authored cases alone do not prove improvement over an earlier prompt.

Peak memory is sampled every 200 ms using the companion's `ResourceUsage` implementation. It includes the native test harness and its child process tree, and excludes the WinUI 3 UI and unrelated applications. It is a measured trajectory peak, not a guarantee that the shipped application stays below its memory budget.

Add `--with-ui --ui-executable <published T3.Voice.exe path>` to launch the published WinUI 3 companion preview during the run. The native core test assembly contains no UI and cannot be used as the companion executable. The fixture starts the supplied executable directly with `--demo`, an isolated temporary database and `--ready-file`; it waits for the ready signal before starting any model call. It never captures audio or configures startup, and stops the UI by its captured process handle afterward.

```powershell
dotnet run --project apps/voice-windows/T3.Voice.Tests -- --eval --provider codex --model <installed-model-id> --case dispatch-and-return --samples 1 --with-ui --ui-executable C:/path/to/publish/T3.Voice.exe --out $env:TEMP/t3-voice-eval-winui
```

Memory then includes the WinUI 3 companion, real CLI and eval process together. `companionAndCliPeakBytes` subtracts the eval process itself from each sample, and the report records the supplied executable's filename and hash. A UI startup failure aborts before model calls. If the ready UI exits during a trajectory, that row's resource values are invalidated, later samples are skipped, and the harness exits with code 2; behavioral checks are retained separately. WPF-era measurements do not establish the WinUI 3 build's footprint. Visual and game-focus verification still require inspecting the actual Windows client.

Add `--ui-compact` to measure tray and popup with Settings closed, as during
gameplay. Reports record this state explicitly; do not compare its footprint
to a Settings-open run without noting the difference.

Add cases to `Cases` in `VoiceEval.cs` with fictional data and a clear requirement or regression source, then add behavioral checks in `Grade`. The local instructions and tool definitions must remain imported from the production assembly. The fake remote catalog is deliberately an authored, reduced fixture; do not copy production T3 schemas into it or treat this eval as remote contract verification. `RemoteTests` and `ToolsTests` cover those deterministic protocol and persistence boundaries.
