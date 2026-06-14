# AIUsageMonitor Project Instructions

- This is a Windows-only .NET 10 WPF/WinForms tray app. The root project is `AIUsageMonitor.csproj` and targets `net10.0-windows`.
- The app monitors AI usage across Anthropic, OpenAI/Codex, Antigravity, Gemini, and Cursor. Some live collectors may run local CLIs, use saved credentials, hit provider APIs, or consume a small amount of quota, so use fake diagnostics/tests unless live behavior is explicitly needed.
- Provider refreshes are expected to run independently. Do not reintroduce all-or-nothing refresh behavior where one slow collector keeps every provider card stuck on `checking`.
- Keep collector work off the WPF dispatcher/UI thread. Marshal only view-model/UI updates back to the dispatcher.
- Root `dotnet build` is intended to build only the app project. Test and diagnostics projects live under `tests/` and `tools/`, and the app project excludes those folders from its compile items.
- Automated refresh regression tests live in `tests/AIUsageMonitor.Tests`. Run them with `dotnet test .\tests\AIUsageMonitor.Tests\AIUsageMonitor.Tests.csproj` when changing refresh aggregation, provider state, cancellation, backoff, or view-model card update behavior.
- Command-line diagnostics live in `tools/AIUsageMonitor.Diagnostics`. Useful fake checks:
  - `dotnet run --project .\tools\AIUsageMonitor.Diagnostics -- refresh --fake --scenario staggered --assert-independent`
  - `dotnet run --project .\tools\AIUsageMonitor.Diagnostics -- refresh --fake --scenario failure`
  - `dotnet run --project .\tools\AIUsageMonitor.Diagnostics -- refresh --fake --scenario cancel --cancel-after-ms 500`
- Optional live diagnostics are opt-in only: `dotnet run --project .\tools\AIUsageMonitor.Diagnostics -- refresh --live --timeout-seconds 120`.
- `package-release.bat` publishes a self-contained win-x64 single-file build, signs it, verifies the signature, copies `build\SethsAIUsageMonitor.exe`, and creates `artifacts\SethsAIUsageMonitor-win-x64.zip`.
- Keep this `AGENTS.md` file updated whenever project workflow, architecture, test commands, diagnostics, packaging, signing, or release expectations change.
- If the user says `commit`, stage only the correct task-related files and use a very brief commit message, one or two lines at most, that names each included feature or fix.
- After finishing any task in this repository, run `dotnet build`.
- If `dotnet build` succeeds, run `.\package-release.bat`.
- In the final response, report whether the release package, signing verification, and copied build executable succeeded.
- Do not treat the task as complete until the release batch has finished or its failure has been reported.
