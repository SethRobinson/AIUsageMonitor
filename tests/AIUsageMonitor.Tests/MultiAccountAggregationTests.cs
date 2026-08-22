using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class MultiAccountAggregationTests
{
    [TestMethod]
    public void ProviderNamesIncludeEachEnabledAnthropicAccount()
    {
        using var context = AggregatorContext.CreateWithDefaultCollectors(settings =>
        {
            settings.ProviderAccounts.Add(CreateManagedAccount("work", "Work"));
        });

        var providerNames = context.Aggregator.ProviderNames;

        CollectionAssert.Contains(providerNames.ToList(), KnownProviders.Anthropic);
        CollectionAssert.Contains(providerNames.ToList(), "Anthropic - Work");
    }

    [TestMethod]
    public void DisabledManagedAccountIsExcludedFromProviderNames()
    {
        using var context = AggregatorContext.CreateWithDefaultCollectors(settings =>
        {
            var account = CreateManagedAccount("work", "Work");
            account.Enabled = false;
            settings.ProviderAccounts.Add(account);
        });

        var providerNames = context.Aggregator.ProviderNames.ToList();

        CollectionAssert.Contains(providerNames, KnownProviders.Anthropic);
        CollectionAssert.DoesNotContain(providerNames, "Anthropic - Work");
    }

    [TestMethod]
    public void DisablingBaseProviderRemovesAllAccountKeys()
    {
        using var context = AggregatorContext.CreateWithDefaultCollectors(settings =>
        {
            settings.ProviderAccounts.Add(CreateManagedAccount("work", "Work"));
            settings.SetProviderEnabled(KnownProviders.Anthropic, false);
        });

        var providerNames = context.Aggregator.ProviderNames.ToList();

        CollectionAssert.DoesNotContain(providerNames, KnownProviders.Anthropic);
        CollectionAssert.DoesNotContain(providerNames, "Anthropic - Work");
    }

    [TestMethod]
    public void ActiveManagedAccountTakesOverTheSlotCardAndIsNotDuplicated()
    {
        using var context = AggregatorContext.CreateWithDefaultCollectors(
            settings =>
            {
                settings.ProviderAccounts.Add(CreateDefaultAccount("uuid-work"));
                var work = CreateManagedAccount("work", "Work");
                work.AccountUuid = "uuid-work";
                settings.ProviderAccounts.Add(work);
            },
            home => WriteSlotLogin(home, "uuid-work", "work@example.com"));

        var providerNames = context.Aggregator.ProviderNames.ToList();

        Assert.AreEqual(1, providerNames.Count(name =>
            string.Equals(name, "Anthropic - Work", StringComparison.OrdinalIgnoreCase)),
            "the active managed account must appear exactly once (via the ~/.claude slot)");
        CollectionAssert.DoesNotContain(providerNames, KnownProviders.Anthropic);
    }

    [TestMethod]
    public void SlotLoginChangedOutsideTheAppRekeysCardsInsteadOfDuplicatingThem()
    {
        // Settings still say the slot holds Work (the last value the app saw) while the
        // user has since logged ~/.claude into Personal from the VS Code extension.
        using var context = AggregatorContext.CreateWithDefaultCollectors(
            settings =>
            {
                settings.ProviderAccounts.Add(CreateDefaultAccount("uuid-work"));
                var work = CreateManagedAccount("work", "Work");
                work.AccountUuid = "uuid-work";
                settings.ProviderAccounts.Add(work);
                var personal = CreateManagedAccount("personal", "Personal");
                personal.AccountUuid = "uuid-personal";
                settings.ProviderAccounts.Add(personal);
            },
            home => WriteSlotLogin(home, "uuid-personal", "personal@example.com"));

        var providerNames = context.Aggregator.ProviderNames.ToList();

        Assert.AreEqual(1, providerNames.Count(name => name == "Anthropic - Personal"),
            "the account now in the slot owns the slot card");
        Assert.AreEqual(1, providerNames.Count(name => name == "Anthropic - Work"),
            "the account that moved out of the slot must still be collected from its own dir");
        CollectionAssert.DoesNotContain(providerNames, KnownProviders.Anthropic);
    }

    [TestMethod]
    public void LoggedOutSlotCollectsEveryManagedAccountFromItsOwnDir()
    {
        using var context = AggregatorContext.CreateWithDefaultCollectors(settings =>
        {
            settings.ProviderAccounts.Add(CreateDefaultAccount("uuid-work"));
            var work = CreateManagedAccount("work", "Work");
            work.AccountUuid = "uuid-work";
            settings.ProviderAccounts.Add(work);
        });

        var providerNames = context.Aggregator.ProviderNames.ToList();

        CollectionAssert.Contains(providerNames, KnownProviders.Anthropic);
        Assert.AreEqual(1, providerNames.Count(name => name == "Anthropic - Work"),
            "a stale stored uuid must not hide an account when ~/.claude has no login at all");
    }

    [TestMethod]
    public void EveryAnthropicProviderKeyIsUnique()
    {
        using var context = AggregatorContext.CreateWithDefaultCollectors(
            settings =>
            {
                settings.ProviderAccounts.Add(CreateDefaultAccount("uuid-work"));
                var work = CreateManagedAccount("work", "Work");
                work.AccountUuid = "uuid-work";
                settings.ProviderAccounts.Add(work);
                // The same login saved twice under two labels still gets two distinct card
                // keys, and one key is never handed to two collectors.
                var duplicate = CreateManagedAccount("work-again", "Work Again");
                duplicate.AccountUuid = "uuid-work";
                settings.ProviderAccounts.Add(duplicate);
            },
            home => WriteSlotLogin(home, "uuid-work", "work@example.com"));

        var providerNames = context.Aggregator.ProviderNames.ToList();

        CollectionAssert.AllItemsAreUnique(providerNames);
    }

    [TestMethod]
    public void RenamedDefaultAccountUsesLabeledDisplayKey()
    {
        var account = ProviderAccount.CreateDefaultAnthropic();

        account.Label = "Personal";
        Assert.AreEqual("Anthropic - Personal", account.DisplayKey);

        account.Label = ProviderAccount.DefaultAccountLabel;
        Assert.AreEqual(KnownProviders.Anthropic, account.DisplayKey);

        account.Label = string.Empty;
        Assert.AreEqual(KnownProviders.Anthropic, account.DisplayKey);
    }

    [TestMethod]
    public async Task FailureBackoffIsIsolatedPerAccount()
    {
        var failing = new AccountScopedUsageCollector(
            new ScriptedCollector("Anthropic", fail: true),
            "Anthropic - Work",
            KnownProviders.Anthropic);
        var healthy = new AccountScopedUsageCollector(
            new ScriptedCollector("Anthropic", fail: false),
            "Anthropic",
            KnownProviders.Anthropic);
        using var context = AggregatorContext.CreateWithCollectors(failing, healthy);

        var firstResults = await CollectAsync(context.Aggregator);
        var secondResults = await CollectAsync(context.Aggregator);

        var failedFirst = firstResults.Single(provider => provider.Name == "Anthropic - Work");
        Assert.IsTrue(failedFirst.IsUnavailable);
        StringAssert.Contains(failedFirst.StatusMessage, "Collection failed");

        var pausedSecond = secondResults.Single(provider => provider.Name == "Anthropic - Work");
        StringAssert.Contains(pausedSecond.StatusMessage, "Collection paused");

        var healthySecond = secondResults.Single(provider => provider.Name == "Anthropic");
        Assert.IsFalse(healthySecond.IsUnavailable, "healthy account must not inherit the failing account's backoff");
    }

    [TestMethod]
    public async Task AccountScopedCollectorStampsNameAndSourceProviderName()
    {
        var collector = new AccountScopedUsageCollector(
            new ScriptedCollector("Anthropic", fail: false),
            "Anthropic - Work",
            KnownProviders.Anthropic);

        var usage = await collector.CollectAsync(CancellationToken.None);

        Assert.AreEqual("Anthropic - Work", usage.Name);
        Assert.AreEqual("Anthropic - Work", usage.SourceProviderName);
        Assert.AreEqual(1, usage.Windows.Count);
    }

    [TestMethod]
    public void CardsForTwoAccountsUpsertIndependently()
    {
        var viewModel = new UsageOverlayViewModel();
        viewModel.SetChecking(["Anthropic", "Anthropic - Work"]);

        viewModel.ApplyProvider(BuildUsage("Anthropic", 10));
        viewModel.ApplyProvider(BuildUsage("Anthropic - Work", 55));
        viewModel.ApplyProvider(BuildUsage("Anthropic - Work", 60));

        Assert.AreEqual(2, viewModel.Providers.Count);
        var defaultCard = viewModel.Providers.Single(card => card.SourceProviderName == "Anthropic");
        var workCard = viewModel.Providers.Single(card => card.SourceProviderName == "Anthropic - Work");
        Assert.IsFalse(defaultCard.IsChecking);
        Assert.IsFalse(workCard.IsChecking);
    }

    [TestMethod]
    public void GroupedWindowsOnManagedAccountKeepAccountSourceKey()
    {
        var viewModel = new UsageOverlayViewModel();
        var usage = BuildUsage("Anthropic - Work", 20);
        usage.Windows.Add(new UsageWindow
        {
            Title = "Sonnet weekly",
            DisplayGroupName = "Sonnet",
            Limit = 100,
            Used = 30,
            Remaining = 70,
            ResetAt = DateTimeOffset.Now.AddDays(3)
        });

        viewModel.ApplyProvider(usage);

        Assert.AreEqual(2, viewModel.Providers.Count);
        Assert.IsTrue(viewModel.Providers.All(card => card.SourceProviderName == "Anthropic - Work"));
        Assert.IsTrue(viewModel.Providers.Any(card => card.ShortName == "Anthropic - Work Sonnet"));

        viewModel.ApplyProvider(BuildUsage("Anthropic - Work", 25));

        Assert.AreEqual(1, viewModel.Providers.Count, "re-applying the account must replace all of its cards");
    }

    private static ProviderAccount CreateDefaultAccount(string accountUuid)
    {
        return new ProviderAccount
        {
            Id = ProviderAccount.DefaultAnthropicAccountId,
            ProviderName = KnownProviders.Anthropic,
            Label = ProviderAccount.DefaultAccountLabel,
            IsDefault = true,
            AccountUuid = accountUuid
        };
    }

    // Writes the two files a real ~/.claude login leaves behind: the token, and the
    // oauthAccount block the CLI caches next to it.
    private static void WriteSlotLogin(string homeDirectory, string accountUuid, string email)
    {
        var claudeDirectory = Path.Combine(homeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);

        var credentials = new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = $"token-{accountUuid}",
                ["refreshToken"] = $"refresh-{accountUuid}",
                ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeMilliseconds()
            }
        };
        File.WriteAllText(Path.Combine(claudeDirectory, ".credentials.json"), credentials.ToJsonString());

        var claudeJson = new JsonObject
        {
            ["oauthAccount"] = new JsonObject
            {
                ["accountUuid"] = accountUuid,
                ["emailAddress"] = email
            }
        };
        File.WriteAllText(Path.Combine(homeDirectory, ".claude.json"), claudeJson.ToJsonString());
    }

    private static ProviderAccount CreateManagedAccount(string idSuffix, string label)
    {
        return new ProviderAccount
        {
            Id = $"anthropic-{idSuffix}",
            ProviderName = KnownProviders.Anthropic,
            Label = label,
            Enabled = true,
            ConfigDir = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", $"account-{idSuffix}")
        };
    }

    private static async Task<List<ProviderUsage>> CollectAsync(UsageAggregatorService aggregator)
    {
        var results = new List<ProviderUsage>();
        await foreach (var provider in aggregator.CollectIncrementalAsync(CancellationToken.None))
        {
            results.Add(provider);
        }

        return results;
    }

    private static ProviderUsage BuildUsage(string providerName, double usedPercent)
    {
        return new ProviderUsage
        {
            Name = providerName,
            SourceProviderName = providerName,
            Source = "Test",
            StatusMessage = "Test usage.",
            LastCheckedAt = DateTimeOffset.Now,
            Windows =
            [
                new UsageWindow
                {
                    Title = "Session",
                    Limit = 100,
                    Used = usedPercent,
                    Remaining = Math.Max(0, 100 - usedPercent),
                    ResetAt = DateTimeOffset.Now.AddHours(1)
                }
            ]
        };
    }

    private sealed class ScriptedCollector(string providerName, bool fail) : IUsageCollector
    {
        public string ProviderName { get; } = providerName;

        public Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
        {
            if (fail)
            {
                throw new HttpRequestException("Scripted account failure.");
            }

            return Task.FromResult(BuildUsage(ProviderName, 10));
        }
    }

    private sealed class AggregatorContext : IDisposable
    {
        private readonly string _tempDirectory;

        private AggregatorContext(string tempDirectory, UsageAggregatorService aggregator)
        {
            _tempDirectory = tempDirectory;
            Aggregator = aggregator;
        }

        public UsageAggregatorService Aggregator { get; }

        public static AggregatorContext CreateWithDefaultCollectors(
            Action<AppSettings> configureSettings,
            Action<string>? configureHomeDirectory = null)
        {
            var tempDirectory = CreateTempDirectory();
            var settingsService = new AppSettingsService(tempDirectory);
            var settings = new AppSettings();
            configureSettings(settings);
            settingsService.Save(settings);

            // A fake home dir keeps the slot identity (and so which account owns the slot
            // card) deterministic instead of depending on the developer's own login.
            var homeDirectory = Path.Combine(tempDirectory, "home");
            Directory.CreateDirectory(homeDirectory);
            configureHomeDirectory?.Invoke(homeDirectory);

            var logService = new AppLogService(tempDirectory);
            var aggregator = new UsageAggregatorService(
                logService,
                settingsService,
                collectors: null,
                new ClaudeSlotIdentityService(logService, accountManager: null, homeDirectory));
            return new AggregatorContext(tempDirectory, aggregator);
        }

        public static AggregatorContext CreateWithCollectors(params IUsageCollector[] collectors)
        {
            var tempDirectory = CreateTempDirectory();
            var settingsService = new AppSettingsService(tempDirectory);
            settingsService.Save(new AppSettings());

            var aggregator = new UsageAggregatorService(
                new AppLogService(tempDirectory),
                settingsService,
                collectors.ToList());
            return new AggregatorContext(tempDirectory, aggregator);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string CreateTempDirectory()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            return tempDirectory;
        }
    }
}
