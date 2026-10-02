# Claude subscription usage credits

Claude subscription usage credits fund extra usage after plan quotas are reached. They are distinct from the monthly spending cap and from Anthropic Console/API prepaid credits.

## Connecting

Open Settings > Anthropic > Accounts and choose **Connect usage credits** beside an account. Sign into that account on Claude in the embedded browser, then select **Connect**. Close Accounts and Settings to refresh the overlay. Each login has an isolated WebView2 profile under `%LOCALAPPDATA%\SethsAIUsageMonitor\ClaudeBillingWebView2`.

Connection verifies `/api/oauth/profile` using that account's Claude Code credentials, compares its account UUID with `/api/account` on `claude.ai`, and requires the OAuth organization UUID in `/api/organizations`. It then reads `/api/organizations/{orgId}/prepaid/credits`. Labels and emails are never sufficient to authorize a match. No billing writes, purchases, or changes to extra-usage settings are performed.

The optional `ProviderAccount.ClaudeBilling` record holds a connection/profile ID, account and organization UUIDs, Windows CurrentUser-DPAPI-protected initial cookie snapshot, browser user agent, status, and check/retry timestamps. The live website session resides in the isolated WebView2 profile. Website requests run inside that browser with its current cookies; the initial cookie snapshot is never replayed by a separate HTTP client or restored over rotated browser cookies. OAuth bearer tokens go only to `api.anthropic.com`. Raw sessions and responses are not logged.

`IClaudeBillingBrowser`/`IClaudeBillingWebSession` separate browser transport from identity verification and balance parsing. Connect borrows the visible sign-in WebView. Refresh opens an invisible native WebView2 controller with that connection's saved profile on a dedicated STA dispatcher, then disposes it after the read. It never activates a desktop window. Both paths use same-origin, read-only browser `fetch` for the three identity/balance routes. CDP `Runtime.evaluate` with `awaitPromise` waits for the fetch and response body; ordinary `ExecuteScriptAsync` alone does not await a returned Promise. Redirects, unexpected routes, and cross-origin requests are rejected. Replaying cookies via .NET HttpClient can trigger a browser challenge even when the embedded Billing page works, which is why website reads must stay inside Chromium.

**Reconnect credits** saves a replacement connection only after successful verification. Cancelled or failed attempts leave the old connection intact. **Disconnect credits** removes the protected session and isolated browser profile; account removal performs the same cleanup. Profile deletion invalidates pending browser opens, cancels active reads, and waits for controllers to close before deleting files. WebView2 file cleanup retries briefly after disposal. A cleanup failure is reported without restoring credentials.

## Refresh and account ownership

`IClaudeBillingReader` separates billing authentication from the quota collector. `UsageAggregatorService` yields quota results first, then runs optional billing in a worker and yields a second result under the same `ProviderAccount.DisplayKey`. The ordinary card update/grouping path puts the balance only on the main account card.

Billing verifies both identities again on each fetch and checks that the authenticating token did not change. The aggregator also checks its refresh generation, credential fingerprint, current account identity/configuration, enabled state, display key, and connection ID before saving status or publishing a result. Removal, disconnect, replacement login, CLI slot changes, or a newer refresh invalidate pending results. A managed account currently backed by the CLI slot uses that slot's credentials for verification. When an outgoing default login is adopted into a managed account, its matching billing connection follows it.

An existing non-null OAuth balance takes precedence and skips the web request. OAuth `spend.balance` can be null even when the billing page shows purchased funds; null is not zero. The web endpoint's explicit money amount and currency are used; neither monthly headroom nor amount spent is a balance. Reported zero and negative balances are retained without changing the subscription's quota status.

Billing reads have a 30-second deadline. Browser shutdown keeps its message pump alive for up to ten more seconds to receive `BrowserProcessExited` and release profile files. Authentication/identity failures require reconnecting; transient/network/schema/browser-challenge failures back off for ten minutes (manual refresh bypasses that delay). Failed balances are hidden, with a message in Accounts; quota results stay available. Connection status writes and account mutations use serialized settings updates, preserving concurrent account additions and billing changes.

All balance rows are amber. The mini strip presents a separate provider/account label and right-aligned amount; the Anthropic API card says **Anthropic API** and omits the redundant word “credits” from its small dollar label to leave room for the provider name.

## Verification

`ClaudeBillingServiceTests` and `ClaudeBillingAggregationTests` use fake credentials, mock OAuth HTTP handlers and browser sessions, temporary settings, and controlled delayed tasks. They cover identity/org mismatches, encryption, currency and null handling, session errors, cancellation, cleanup, settings preservation, delayed updates, generation changes, and CLI slot changes. Service tests reject any website request sent to HttpClient. `ClaudeBillingBrowserTests` also exercise the real installed WebView2 runtime with invisible controllers and local interception of every website request: asynchronous fetch completion, persistent cookie reuse, profile isolation, cancellation, and disconnect during a read. They use only disposable fake profiles and never read the developer's login or contact provider APIs.

Run the full test project and fake layout sweep documented in AGENTS.md. The account-dialog screenshot includes a fake connected account. For live acceptance, the user signs into the new connection and compares the reported balance with Claude Billing for that same account; never infer success from fixture screenshots. Website sessions and the endpoint are undocumented and may require reconnecting or parser updates. A single-login replacement remains desirable, but no reliable alternative has been verified for accounts whose OAuth balance is null.
