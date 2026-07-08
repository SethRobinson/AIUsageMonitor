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
- Diagnostics `provider-result` events include `planName`; pass `--force-refresh` to exercise the same collector path used by the manual tray refresh button, and `--provider ProviderName` to run one live provider. `--provider` matches the base provider so all of its accounts run; add `--account <id-or-label>` to narrow to one account.
- Multiple Anthropic/Claude accounts are supported (other providers are still single-account). Key facts:
  - `Models\ProviderAccount.cs` is the account entity stored in `AppSettings.ProviderAccounts`. `AppSettings.Normalize()` synthesizes the implicit default account (`anthropic-default`, the user's normal `~/.claude` login) so legacy settings migrate with zero behavior change.
  - The per-account provider key is `ProviderAccount.DisplayKey` (default account: `Anthropic`; managed: `Anthropic - <Label>`). That key IS the provider name throughout the aggregator, failure/backoff states, checking placeholders, and card upserts. Never compute it anywhere else; mismatched key strings make `SetChecking` clear all cards each refresh.
  - Managed accounts live in per-account CLAUDE_CONFIG_DIRs under `%LOCALAPPDATA%\SethsAIUsageMonitor\accounts\anthropic\<slug>`. Login runs the official `claude` CLI in a visible cmd window with `CLAUDE_CONFIG_DIR` set (`Services\AnthropicAccountManagerService.cs`); the app then reads and refreshes that dir's `.credentials.json` itself.
  - `Services\AnthropicOAuthTokenRefresher.cs` refreshes managed-account OAuth tokens via `https://console.anthropic.com/v1/oauth/token` with Claude Code's public client id `9d1c250a-e61b-44d9-88ed-5944d1962f5e` (same piggyback pattern as the Gemini collector's Google token refresh) and atomically writes rotated tokens back. The default `~/.claude` account is NOT refreshed by the app; the CLI owns it.
  - `UsageAggregatorService` expands the default Anthropic collector into one `Collectors\AccountScopedUsageCollector.cs` per enabled account and caches those instances per account id; rebuilding them each refresh would drop per-instance backoff state. Managed-account collectors run with `allowCliRefresh: false`.
  - "Switch CLI to this account" (`Services\ClaudeAccountSwitchService.cs`) swaps the account's credentials into `~/.claude/.credentials.json` with sync-back to the previously active managed account, timestamped backups (newest 3 kept), and deletion of the stale `~/.claude` usage/profile caches. If the outgoing login matches no managed account it is ADOPTED: a new managed account is created holding its credentials so it stays monitored and can be switched back to. It affects the claude CLI only, never the Claude Desktop app (separate login, no safe external switch).
  - CRITICAL: Claude Code stores identity in TWO files that must always be swapped together: the OAuth token in `.credentials.json` (what authenticates and gets billed) and the cached `oauthAccount` block in `.claude.json` (what `claude /status` displays). For the default setup `.claude.json` is at `~/.claude.json` (profile root); for a CLAUDE_CONFIG_DIR it is inside that dir. Swapping only the token leaves the CLI split-brained (billing one account while /status shows another). The switch service swaps/sync-backs/adopts both; never add a code path that moves one without the other. The app's "active account" detection uses the live `/api/oauth/profile` endpoint (token-based, authoritative), not the cached block.
  - The default entry is the `~/.claude` SLOT, not an identity. When a managed account's `AccountUuid` matches the default entry's, the aggregator gives the slot collector that account's DisplayKey and skips the managed dir's own collector (no duplicate card), and the accounts window hides the Default row. The default entry's identity fields are kept current by the accounts window (`DetectActiveAccountAsync`) and by switches.
  - Each enabled account adds its own `/api/oauth/usage` call per refresh tick; per-instance rate-limit backoff contains this.
- Optional live diagnostics are opt-in only: `dotnet run --project .\tools\AIUsageMonitor.Diagnostics -- refresh --live --timeout-seconds 120`.
  - Manual Anthropic refresh live check: `dotnet run --project .\tools\AIUsageMonitor.Diagnostics -- refresh --live --provider Anthropic --force-refresh --timeout-seconds 120`.
- README screenshots are generated from `usage.fake.json` with the app's built-in screenshot mode, which forces 100% UI scale and rebases fake reset times relative to the capture time. Regenerate them with:
  - `dotnet run --project .\AIUsageMonitor.csproj -- --screenshot .\docs\screenshot-full.png --screenshot-size 1440x740`
  - `dotnet run --project .\AIUsageMonitor.csproj -- --screenshot .\docs\screenshot-compact.png --screenshot-size 1440x136`
  - `dotnet run --project .\AIUsageMonitor.csproj -- --screenshot .\docs\screenshot-mini-strip.png --screenshot-size 1440x60`
  - `Copy-Item .\docs\screenshot-full.png .\docs\screenshot.png -Force`
- The Settings-dialog README screenshot is generated from DEFAULT (fake) `AppSettings` — never the user's real `monitor.settings.json` — and auto-sizes its height so every option shows with no scrollbar. Regenerate it with:
  - `dotnet run --project .\AIUsageMonitor.csproj -- --screenshot-settings .\docs\screenshot-settings.png` (pass `--screenshot-size WxH` to force an explicit size instead of auto-fit).
- The Claude Accounts dialog README screenshot renders from FAKE demo accounts (work/personal in a temp settings dir; no real settings, ~/.claude reads, or network). Regenerate it with:
  - `dotnet run --project .\AIUsageMonitor.csproj -- --screenshot-accounts .\docs\screenshot-accounts.png`
- When the user says to bump the version, increment it by 0.01, for example `V1.02` becomes `V1.03`. Update both `Services\AppMetadata.cs` and the README download section's current version and latest updated date.
- `package-release.bat` publishes a self-contained win-x64 single-file build, signs it, verifies the signature, copies `build\SethsAIUsageMonitor.exe`, and creates `artifacts\SethsAIUsageMonitor-win-x64.zip`.
- `package-release.bat` kills any running app instance before publishing/signing. After a successful package run has copied `build\SethsAIUsageMonitor.exe`, restart that copied build executable so the tray app is running again for the user.
- Release uploads must use `upload-to-rtsoft.bat` after a successful signed package build. Do not manually upload or copy the zip to the release host outside that batch file. The upload batch ends with `pause`, so when running it headlessly from PowerShell, invoke it by absolute path with `Start-Process` and redirect empty stdin the same way as `package-release.bat`.
- The release must ALWAYS be code-signed. Never add a skip-signing/unsigned path, never distribute an unsigned build, and don't "work around" signing problems by disabling it. Signing is done by `%RT_PROJECTS%\Signing\sign.bat` and is non-interactive (SmartCard token + PIN) as long as the token is plugged in.
- IMPORTANT — running it headlessly (e.g. from an agent/non-interactive shell): `sign.bat` ends with a `pause` that waits for a keypress and will hang any run with no interactive console. Do NOT remove the `pause` and do NOT skip signing. Instead feed empty stdin so the `pause` falls through while signing still completes. Also avoid piping output through buffering filters (e.g. `Select-Object`) if you want live progress.
  - In a `cmd` or `bash` shell the simple form works: `cmd /c "package-release.bat" < nul`.
  - From the PowerShell tool that form does NOT work, for two reasons: PowerShell has no `<` stdin-redirection operator, and the agent shell runs with `NoDefaultCurrentDirectoryInExePath` set, so cmd refuses to execute a bare/relative batch name from the current directory — it reports `'package-release.bat' is not recognized` even though the file is right there (read-only `dir package-release.bat` still finds it, which makes it look like a directory problem when it is not). Invoke the batch by ABSOLUTE path via `Start-Process`, feed an empty file as stdin, and run the PowerShell tool with `dangerouslyDisableSandbox: true` (signing needs the real SmartCard token; the batch also does `taskkill`/file writes) and a ~600000 ms timeout:

    ```powershell
    $bat = 'D:\projects\AI\AIUsageMonitor\package-release.bat'
    $in  = Join-Path $env:TEMP 'pkgrel_in.txt'; Set-Content -LiteralPath $in -Value '' -NoNewline
    $o   = Join-Path $env:TEMP 'pkgrel_out.txt'; $e = Join-Path $env:TEMP 'pkgrel_err.txt'
    $p = Start-Process cmd.exe -ArgumentList '/c',"`"$bat`"" -WorkingDirectory (Split-Path $bat) `
      -RedirectStandardInput $in -RedirectStandardOutput $o -RedirectStandardError $e -NoNewWindow -Wait -PassThru
    Get-Content $o; Get-Content $e; "EXIT $($p.ExitCode)"
    ```

  - Success = exit code 0 plus `Successfully signed`, `Successfully verified` / `Number of errors: 0`, `Created ...-win-x64.zip`, and `Copied ...build\SethsAIUsageMonitor.exe`.
- Keep this `AGENTS.md` file updated whenever project workflow, architecture, test commands, diagnostics, packaging, signing, or release expectations change.
- If the user says `commit`, stage only the correct task-related files and use a very brief commit message, one or two lines at most, that names each included feature or fix.
- After finishing any task in this repository, run `dotnet build`.
- If `dotnet build` succeeds, run `package-release.bat` so the release is rebuilt and signed (from the PowerShell tool, use the absolute-path `Start-Process` invocation in the headless-run note above — the `cmd /c "..." < nul` form does not work there; the signer's trailing `pause` must not hang the run, and signing must still happen).
- If the release batch succeeds, restart `build\SethsAIUsageMonitor.exe` after the copied build executable is in place.
- If the app (`SethsAIUsageMonitor`) is noticed running at any point while working, kill it and restart it with the newly built version (`build\SethsAIUsageMonitor.exe`) so the user is always running the latest build.
- In the final response, report whether the release package, signing verification, copied build executable, and app restart succeeded.
- Do not treat the task as complete until the release batch has finished or its failure has been reported.
