# AIUsageMonitor Project Instructions

# AGENTS.md

Project operating instructions for AI assistants working in this repository.

## Shared Project Memory

- At the start of each new task or thread involving this repository, read this file before inspecting files, running commands, making a plan, or taking any other project action.
- Treat follow-up replies in the same continuous task as part of that task. Do not reread this file unless the repository or working directory changes, this file is modified, or its instructions are no longer available in context.
- Treat this file as the shared project memory for AI assistants.
- Do not rely on vendor-specific, proprietary, or hidden memory systems for project facts, preferences, or operating instructions. (except to remember to ALWAYS read this file first before doing anything.  Remember that.)
- Update this file with important repo-specific information learned during work, including build commands, test commands, conventions, decisions, pitfalls, and current project preferences.
- Keep this file accurate and current. Remove or correct stale, misleading, or incorrect information when discovered.
- If information is temporary or uncertain, label it clearly rather than presenting it as permanent fact.

Scope policy: this file holds cross-cutting rules, workflows, and gotchas that most sessions need, plus a feature index. Keep it around 30 KB. Feature deep-dives live in `docs/<topic>.md`: before working on a feature listed in the index, read its doc; when finishing feature work, update that doc and keep the index entry here to one or two lines (where it lives + the non-obvious constraint). Cross-cutting rules and new gotchas still land here directly. When a change makes anything stale, here or in a linked doc, update it in the same change.

Also open and read agents_local.md - this should be used for useful info that pertains to this specific checkout/developer, like info on which systems it can ssh into and use for building or whatever.  This should not be checked in to git.

## Testing

- When possible, design automated tests for new features and bug fixes.
- Run relevant automated tests after finishing changes to guard against regressions.
- If tests cannot be run or do not exist, state that clearly in the handoff and describe any manual verification performed.


## Security

- Never commit sensitive data, including credentials, tokens, passwords, private keys, cookies, customer data, personal data, or machine-specific authentication material.
- If an AI assistant needs authentication data or other secrets for local work, use `agents_secret.md` for those notes.
- `agents_secret.md` must stay ignored by git and must not be committed.
- Do not put secrets in commit messages, logs, issue text, pull request descriptions, generated docs, or other tracked files.
- Before committing, review staged changes for accidental secrets.

## Safety

**Destructive deletes.** 
No recursive or wildcard delete (`rm -rf`, `rmdir /s`, `del /s`, `Remove-Item -Recurse`, `remove_all`, `removeRecursively`) anywhere may target a bare variable or rely on the current directory. Spell a literal part of the path at the delete (`${ROOT:?}/build/flatpak-publish`, `%REPO%\build\package\installer`, a `patchy-` mktemp prefix) after checking each variable is set and right  Agents `rm` only a checked full literal path.

## Computer Control
- MCP server operations within the user's requested task do not require an
  additional permission question, including using an application's MCP to
  inspect, create, edit, validate, or export its content.
- Taking screenshots of the desktop or controlling the desktop still requires
  express permission in the current request. This includes Computer Use,
  desktop UI automation, SendInput, clicking, and typing. Using MCP as a wrapper
  for those desktop actions does not bypass this requirement.
- Permission to complete a task, inspect an app, compare behavior, or proceed
  autonomously does not imply permission for desktop screenshots or control.
- Driving applications through their own scripting interfaces (for example Adobe
  Photoshop via COM `DoJavaScript`) is always allowed; it does not see or control
  the desktop. Only desktop screenshots and desktop input control need permission.

## Git

- Create a local git repo and use it.  Commit features/etc as needed.
- Never add OpenAI/Codex/Claude etc as a co-author on git commits.
- NEVER `git push` unless explicitly told to push. "Commit" means commit locally only; committing is not permission to push.


- This is a Windows-only .NET 10 WPF/WinForms tray app. The root project is `AIUsageMonitor.csproj` and targets `net10.0-windows`.
- The app monitors AI usage across Anthropic, OpenAI/Codex, Antigravity, Gemini, and Cursor. Some live collectors may run local CLIs, use saved credentials, hit provider APIs, or consume a small amount of quota, so use fake diagnostics/tests unless live behavior is explicitly needed.
- Provider refreshes are expected to run independently. Do not reintroduce all-or-nothing refresh behavior where one slow collector keeps every provider card stuck on `checking`.
- Keep collector work off the WPF dispatcher/UI thread. Marshal only view-model/UI updates back to the dispatcher.
- Root `dotnet build` is intended to build only the app project. Test and diagnostics projects live under `tests/` and `tools/`, and the app project excludes those folders from its compile items.
- Automated refresh regression tests live in `tests/AIUsageMonitor.Tests`. Run them with `dotnet test .\tests\AIUsageMonitor.Tests\AIUsageMonitor.Tests.csproj` when changing refresh aggregation, provider state, cancellation, backoff, view-model card update, or Claude account/slot-identity behavior. `MultiAccountAggregationTests` drives account resolution from a fake home dir, so those tests never depend on which account the developer's own `~/.claude` happens to hold; keep it that way.
- Command-line diagnostics live in `tools/AIUsageMonitor.Diagnostics`. Useful fake checks:
  - `dotnet run --project .\tools\AIUsageMonitor.Diagnostics -- refresh --fake --scenario staggered --assert-independent`
  - `dotnet run --project .\tools\AIUsageMonitor.Diagnostics -- refresh --fake --scenario failure`
  - `dotnet run --project .\tools\AIUsageMonitor.Diagnostics -- refresh --fake --scenario cancel --cancel-after-ms 500`
- Diagnostics `provider-result` events include `planName`; pass `--force-refresh` to exercise the same collector path used by the manual tray refresh button, and `--provider ProviderName` to run one live provider. `--provider` matches the base provider so all of its accounts run; add `--account <id-or-label>` to narrow to one account.
- Multiple Anthropic/Claude and OpenAI accounts are supported (other providers are still single-account). Claude key facts:
  - `Models\ProviderAccount.cs` is the account entity stored in `AppSettings.ProviderAccounts`. `AppSettings.Normalize()` synthesizes the implicit default account (`anthropic-default`, the user's normal `~/.claude` login) so legacy settings migrate with zero behavior change.
  - The per-account provider key is `ProviderAccount.DisplayKey` (default account: `Anthropic`; managed: `Anthropic - <Label>`). That key IS the provider name throughout the aggregator, failure/backoff states, checking placeholders, and card upserts. Never compute it anywhere else; mismatched key strings make `SetChecking` clear all cards each refresh.
  - Managed accounts live in per-account CLAUDE_CONFIG_DIRs under `%LOCALAPPDATA%\SethsAIUsageMonitor\accounts\anthropic\<slug>`. Login runs the official `claude` CLI in a visible cmd window with `CLAUDE_CONFIG_DIR` set (`Services\AnthropicAccountManagerService.cs`); the app then reads and refreshes that dir's `.credentials.json` itself.
  - `Services\AnthropicOAuthTokenRefresher.cs` refreshes managed-account OAuth tokens via `https://console.anthropic.com/v1/oauth/token` with Claude Code's public client id `9d1c250a-e61b-44d9-88ed-5944d1962f5e` (same piggyback pattern as the Gemini collector's Google token refresh) and atomically writes rotated tokens back. The default `~/.claude` account is NOT refreshed by the app; the CLI owns it.
  - `UsageAggregatorService` expands the default Anthropic collector into one `Collectors\AccountScopedUsageCollector.cs` per enabled account and caches those instances per account id; rebuilding them each refresh would drop per-instance backoff state. Managed-account collectors run with `allowCliRefresh: false`.
  - "Switch CLI to this account" (`Services\ClaudeAccountSwitchService.cs`) swaps the account's credentials into `~/.claude/.credentials.json` with sync-back to the previously active managed account, timestamped backups (newest 3 kept), and deletion of the stale `~/.claude` usage/profile caches. If the outgoing login matches no managed account it is ADOPTED: a new managed account is created holding its credentials so it stays monitored and can be switched back to. It affects the claude CLI only, never the Claude Desktop app (separate login, no safe external switch).
  - CRITICAL: Claude Code stores identity in TWO files that must always be swapped together: the OAuth token in `.credentials.json` (what authenticates and gets billed) and the cached `oauthAccount` block in `.claude.json` (what `claude /status` displays). For the default setup `.claude.json` is at `~/.claude.json` (profile root); for a CLAUDE_CONFIG_DIR it is inside that dir. Swapping only the token leaves the CLI split-brained (billing one account while /status shows another). The switch service swaps/sync-backs/adopts both; never add a code path that moves one without the other. The app's "active account" detection uses the live `/api/oauth/profile` endpoint (token-based, authoritative), not the cached block.
  - The default entry is the `~/.claude` SLOT, not an identity. `AppSettings.GetMonitoringAccounts` hides that implicit entry from collection, the accounts dialog, and the Settings count whenever any saved Claude accounts exist (including disabled accounts). With no saved Claude accounts, legacy default-slot monitoring remains. Keep the hidden entry in settings for switching/identity metadata. When a managed account's `AccountUuid` matches the slot's live identity, its collector may still use the slot's freshest tokens under that account's DisplayKey, without adding another card.
  - The Claude Accounts active badge uses only the currently detected identity. An empty live identity must never fall back to the stored default uuid; clear/rebuild rows when no login is found so the header and badges agree. Only verified identities may trigger identity-cache repair.
  - A slot login requires a nonblank readable access token, not just an existing `.credentials.json`. Claude can leave empty tokens plus a stale `oauthAccount` block and another account's usage cache behind. Such a slot must not suppress any saved account's collector. `ClaudeStatusFileUsageCollector` returns unavailable before reading usage/profile caches or status exports when credentials have no access token (managed accounts may first recover through their token refresher). Never restore cached-usage fallback for empty credentials; it can display Corp's old numbers under Seth's name.
  - CRITICAL: the slot's identity is resolved LIVE every refresh by `Services\ClaudeSlotIdentityService.cs`, never from the `AccountUuid` stored in settings. The user can re-login `~/.claude` from the claude CLI, the VS Code extension, or Claude Code at any time without telling the app; trusting the stored uuid made the slot card keep the previous account's NAME while showing the new account's NUMBERS, and skipped the account that actually moved (two cards, identical data, one account never collected). Resolution order: the verified profile identity while it still belongs to the token now in the slot, then the `oauthAccount` block in `~/.claude.json` (free, offline, rewritten by every login), then the stored uuid, and nothing at all when `~/.claude/.credentials.json` is missing (a logged-out slot means no managed account is active). `UsageAggregatorService` persists the resolved identity back into the default entry, so settings follow the slot instead of driving it.
  - `/api/oauth/profile` is the authority (the token is what gets billed) and settles split-brain cases, but it is only called when the slot's access token changed or the previous answer aged out, so ordinary ticks cost nothing extra. Anything derived from the cached `.claude.json` block is `IsVerified: false` and must never be used to write into an account dir.
  - `ClaudeSlotIdentityService.TryMirrorSlotCredentials` copies the slot's rotating tokens (and the identity block) back into the matching managed account dir each refresh, but only for a verified identity and only when `~/.claude`'s copy is newer. Without this, an account logged out of the slot from outside the app is left holding tokens the CLI has since rotated away, so its card dies with "login expired" and it cannot be switched back to.
  - `Services\ClaudeIdentityFiles.cs` holds the shared `.claude.json` / `.credentials.json` read+atomic-write helpers; `ClaudeAccountSwitchService` and `ClaudeSlotIdentityService` both go through it. Never hand-roll another writer for those files.
  - `TrayIconService` caches an `AppSettings` for the app's whole lifetime, so every save it makes goes through `SaveSettingsPreservingAccounts()`, which re-reads `ProviderAccounts` from disk first. Saving that cached copy directly (a window move alone triggers one) rolls the account list and the detected slot identity back to whatever they were at startup.
  - Two collectors must never share a DisplayKey (their cards would clobber each other); `ResolveAnthropicAccountCollectors` enforces that with a yielded-key set on top of the DisplayKey dedupe in `AppSettings.Normalize()`.
  - Each enabled account adds its own `/api/oauth/usage` call per refresh tick; per-instance rate-limit backoff contains this.
- OpenAI saved-account architecture and validation: see `docs/openai-accounts.md`. Saved accounts use isolated Codex app-server homes; never read the shared sessions or switch the normal Codex login for these accounts. `OpenAiUseSavedAccounts` stays true after the last account is disabled/removed; preserve it together with `ProviderAccounts` when saving cached settings.
- Purchased ChatGPT usage credits: both OpenAI collectors pass the rate-limit `credits` block through `Collectors\CodexCredits.cs`, which adds a "Credits" `IsBalance` row (credit count, not dollars) only when the account holds credits. Keep balance rows out of quota-card headline percentages. Every balance row, including standalone Anthropic API credits, is amber (`UsageWindowDisplay.IsHighlightedBalance`, `#FBBF24`). The mini strip keeps the provider/account label and displays `ProviderUsageCard.SecondaryBalanceText` at the right, hidden only when unavailable. Anthropic API credits use the short label "Anthropic API" and just the dollar amount on the mini strip; other sizes include "credits". Fake data covers both OpenAI and Claude balances.
- Claude subscription usage credits: `Collectors\ClaudeUsageCredits.cs` reads the explicit OAuth/status-export `spend.balance` money object (`amount_minor`, `currency`, `exponent`) into an ungrouped "Usage credits" `IsBalance` row on that account's main card. It uses the same yellow secondary balance display as OpenAI; `usage.fake.json` includes a sample on "Anthropic - Seth". Never derive the prepaid balance from `monthly_limit - used_credits`: those describe the separate monthly spending cap, not purchased funds. Console/API credits remain a separate login/card. Missing or malformed balance data is omitted; reported zero balances do not affect quota status. Both Pro and Max fresh local exports check OAuth when the balance is absent, subject to existing rate-limit backoff. Cached supplemental data includes balances and merges only missing rows; successful responses replace the cache even when they omit previously reported balances.
- Claude website billing connections: see `docs/claude-usage-credits.md`. `ProviderAccount.ClaudeBilling` stores an optional protected session; connect and refresh verify account AND organization against the authenticating OAuth profile. OAuth can report `spend.balance: null` despite purchased funds; null never means zero. Billing yields a second update after quota through `IClaudeBillingReader`, guarded by refresh generation, current account/connection, and credential fingerprint. Never await billing before publishing quotas or attach a balance by label/email. Account mutations and Console cache/setup saves use `AppSettingsService.Update` so billing updates cannot overwrite other settings. Default-slot billing follows its verified identity when adopted into a managed account.
  - Website billing requests must use `ClaudeBillingBrowser` and the connection's WebView2 profile, not replay cookies with HttpClient (Claude can challenge HttpClient while the browser works). Connect borrows the visible browser; refresh uses an invisible controller on its own STA dispatcher. Browser fetches await their Promise before returning. Profile deletion cancels readers and prevents older refreshes recreating it. `ClaudeBillingBrowserTests` exercise the installed WebView2 runtime with local fake responses and disposable profiles, without provider traffic or desktop automation.
- `ProviderUsage.AccountLabel` is presentation metadata: carry it through wrappers and grouped-card copies. Keep all provider/card identity keys sourced from `ProviderAccount.DisplayKey`.
- `AppSettingsService.Update` serializes settings updates; cached saves use `SavePreservingAccounts`. Claude slot identity updates must update only the current slot fields so they cannot roll back newly saved OpenAI accounts.
- Settings durability (issue #2, a power loss left `monitor.settings.json` zero-filled and the app stuck with no tray icon): `SaveCore` flushes each temp file to disk (`Flush(true)`) before the rename and writes the same JSON to `monitor.settings.json.bak`. An unparseable file (empty, NULs, bad JSON, wrong types) is moved to `.corrupt-<timestamp>`, logged, and replaced from `.bak`, then the newest parseable leftover `.tmp`, then defaults. IO errors (locked file) still throw and never quarantine; a missing file still means defaults so deleting it resets. `App.OnStartup` wraps service construction so a startup failure shows a message and shuts down instead of leaving a tray-less process holding the single-instance lock. Tests: `AppSettingsServiceRecoveryTests`.
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
- OpenAI Accounts README screenshot uses temporary fake settings and never starts Codex or reads credentials: `dotnet run --project .\AIUsageMonitor.csproj -- --screenshot-openai-accounts .\docs\screenshot-openai-accounts.png`.
- Responsive overlay layout regressions can be checked with the permanent fake-data sweep. It renders 544 supported size/scale/content combinations without reading settings, credentials, or provider APIs, then writes a JSON report, contact sheets, and worst-case PNGs under the requested output directory. Hard geometry, missing-card-content, or excessive-internal-gap failures return a nonzero exit code:
  - `dotnet run --project .\AIUsageMonitor.csproj -- --layout-sweep .\artifacts\layout-sweep`
- When the user says to bump the version, increment it by 0.01, for example `V1.02` becomes `V1.03`. Update both `Services\AppMetadata.cs` and the README download section's current version and latest updated date.
- `package-release.bat` publishes a self-contained win-x64 single-file build, signs it, verifies the signature, copies `build\SethsAIUsageMonitor.exe`, and creates `artifacts\SethsAIUsageMonitor-win-x64.zip`.
- `package-release.bat` kills any running app instance before publishing/signing. After a successful package run has copied `build\SethsAIUsageMonitor.exe`, restart that copied build executable so the tray app is running again for the user.
  - From an agent command session, use `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\restart-app.ps1` after packaging. Plain `Start-Process` can leave the app inside the command runner's kill-on-close Windows job, so it disappears when that session closes without a crash event. The helper validates the copied executable's signature, launches it with `CREATE_BREAKAWAY_FROM_JOB`, checks startup, and verifies `IN_LAUNCHER_JOB=False`. Windows can retain a desktop-wide parent job, so testing membership in any job is too broad. Check its PID again from a subsequent command before reporting success. It launches only the app, with no visible shell or desktop automation; if breakaway is denied, report the failure instead of claiming a lasting restart.
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
