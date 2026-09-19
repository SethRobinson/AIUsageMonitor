# Saved OpenAI accounts

`AppSettings.OpenAiUseSavedAccounts` defaults to false for existing installations. The first successful managed login enables it. In saved mode, the aggregator replaces the automatic `CodexLogUsageCollector` with one `AccountScopedUsageCollector` per enabled, non-default OpenAI account. Removing or disabling all saved accounts does not restore automatic monitoring.

Accounts use `ProviderAccount` and its canonical `DisplayKey`. `AccountUuid` stores a SHA-256 identity key combining the OAuth user and workspace; email is display metadata only. `ProviderUsage.AccountLabel` is carried through account wrappers and display-group expansion so shortened grouped cards keep the account label without changing their source/update key.

`OpenAiAccountManagerService` stages each browser login in a fresh monitor-owned directory. It validates the identity, rejects duplicates and wrong-identity re-logins, then atomically installs the CLI-owned `auth.json`. Account directories are immediate children of `%LOCALAPPDATA%\SethsAIUsageMonitor\accounts\openai`. Deletion rejects outside paths and reparse points. Per-directory gates serialize credential replacement, removal, and collection.

`CodexAccountClient` starts a hidden `codex app-server --listen stdio://` child with an isolated `CODEX_HOME` and file credential storage. It initializes the JSONL connection and uses only account login/read/rate-limit methods. There are no turns or model probes. Codex owns refresh-token rotation; the monitor never implements its own OpenAI OAuth refresh. RPC payloads, browser login URLs, and subprocess stderr must not be logged. The process tree is terminated on completion or cancellation. Login has a five-minute deadline; each collection has a 45-second deadline and a five-minute per-account failure backoff, bypassed by manual refresh.

`OpenAiAccountUsageCollector` validates the stored identity before and after collection. It prefers the `codex` bucket in `rateLimitsByLimitId`, falling back to `rateLimits`. Only numeric, present quota windows become usage bars; absent quota does not mean zero used. The automatic collector remains available only in automatic mode.

Both OpenAI collectors also read the `credits` block that Codex returns next to the quota windows (`hasCredits`/`unlimited`/`balance` from the app-server, `has_credits` etc. in session logs). `Collectors\CodexCredits.cs` parses either spelling and, when the account actually holds credits, adds a "Credits" balance row (`IsBalance`, no progress bar, no reset) after the percentage windows. The balance is a credit count, not dollars, so it is shown as "2,389 credits"; `has_credits: false` with a zero balance adds nothing. The row never changes the card headline percent.

`AppSettingsService.Update` serializes read-modify-write operations by settings path, and writes settings atomically. `SavePreservingAccounts` and `AppSettings.CopyAccountSettingsFrom` preserve both providers' account list and the saved-OpenAI mode across stale Settings/tray copies.

## Validation

- `dotnet test .\tests\AIUsageMonitor.Tests\AIUsageMonitor.Tests.csproj`
- Fake staggered, failure, and cancellation diagnostics documented in AGENTS.md.
- `dotnet run --project .\AIUsageMonitor.csproj -- --layout-sweep .\artifacts\layout-sweep` now covers 544 combinations, including real grouped account metadata and multiple OpenAI accounts.
- `dotnet run --project .\AIUsageMonitor.csproj -- --screenshot-openai-accounts .\docs\screenshot-openai-accounts.png` uses only temporary fake settings and demo emails; it does not start Codex or read credentials.

`OpenAiAccountTests` uses fake auth files and RPC responses. Its Windows subprocess test runs a fake local npm-style shim and verifies stdio, isolated environment, cancellation, and child-process cleanup; it does not contact OpenAI.

Opt-in live diagnostic: `dotnet run --project .\tools\AIUsageMonitor.Diagnostics -- refresh --live --provider OpenAI --account Work --force-refresh --timeout-seconds 120`. Never run live checks without an explicit need/opt-in.

Protocol reference: https://learn.chatgpt.com/docs/app-server
