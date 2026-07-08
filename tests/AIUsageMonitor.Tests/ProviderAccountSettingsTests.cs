using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class ProviderAccountSettingsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [TestMethod]
    public void NormalizeSynthesizesDefaultAnthropicAccountForLegacySettings()
    {
        var legacyJson = """
            {
              "UpdateIntervalMinutes": 20,
              "EnabledProviders": { "Anthropic": true, "Cursor": false }
            }
            """;
        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson, JsonOptions)!;

        settings.Normalize();

        var accounts = settings.GetAccounts(KnownProviders.Anthropic);
        Assert.AreEqual(1, accounts.Count);
        Assert.AreEqual(ProviderAccount.DefaultAnthropicAccountId, accounts[0].Id);
        Assert.IsTrue(accounts[0].IsDefault);
        Assert.IsTrue(accounts[0].Enabled);
        Assert.AreEqual(KnownProviders.Anthropic, accounts[0].DisplayKey);
        Assert.IsTrue(settings.IsProviderEnabled(KnownProviders.Anthropic));
        Assert.IsFalse(settings.IsProviderEnabled(KnownProviders.Cursor));
    }

    [TestMethod]
    public void ProviderAccountsRoundTripThroughJson()
    {
        var settings = new AppSettings();
        settings.ProviderAccounts.Add(new ProviderAccount
        {
            Id = "anthropic-work-1a2b",
            ProviderName = KnownProviders.Anthropic,
            Label = "Work",
            Enabled = true,
            ConfigDir = @"C:\accounts\anthropic\work-1a2b",
            Email = "seth@company.com",
            AccountUuid = "uuid-1"
        });
        settings.Normalize();

        var json = JsonSerializer.Serialize(settings);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)!;
        reloaded.Normalize();

        var accounts = reloaded.GetAccounts(KnownProviders.Anthropic);
        Assert.AreEqual(2, accounts.Count);
        Assert.IsTrue(accounts[0].IsDefault);
        var managed = accounts[1];
        Assert.AreEqual("anthropic-work-1a2b", managed.Id);
        Assert.AreEqual("Work", managed.Label);
        Assert.AreEqual(@"C:\accounts\anthropic\work-1a2b", managed.ConfigDir);
        Assert.AreEqual("seth@company.com", managed.Email);
        Assert.AreEqual("uuid-1", managed.AccountUuid);
        Assert.AreEqual("Anthropic - Work", managed.DisplayKey);
    }

    [TestMethod]
    public void NormalizeDropsBlankAndDuplicateAccountIds()
    {
        var settings = new AppSettings
        {
            ProviderAccounts =
            [
                new ProviderAccount { Id = "", ProviderName = KnownProviders.Anthropic, Label = "Blank" },
                new ProviderAccount { Id = "dup", ProviderName = KnownProviders.Anthropic, Label = "First" },
                new ProviderAccount { Id = "dup", ProviderName = KnownProviders.Anthropic, Label = "Second" }
            ]
        };

        settings.Normalize();

        var accounts = settings.GetAccounts(KnownProviders.Anthropic);
        Assert.AreEqual(2, accounts.Count);
        Assert.IsTrue(accounts[0].IsDefault);
        Assert.AreEqual("First", accounts[1].Label);
    }

    [TestMethod]
    public void NormalizeDemotesSecondDefaultAccount()
    {
        var settings = new AppSettings
        {
            ProviderAccounts =
            [
                new ProviderAccount { Id = "a", ProviderName = KnownProviders.Anthropic, IsDefault = true, ConfigDir = @"C:\ignored" },
                new ProviderAccount { Id = "b", ProviderName = KnownProviders.Anthropic, IsDefault = true, Label = "Second" }
            ]
        };

        settings.Normalize();

        var accounts = settings.ProviderAccounts;
        Assert.AreEqual(2, accounts.Count);
        Assert.IsTrue(accounts[0].IsDefault);
        Assert.AreEqual(string.Empty, accounts[0].ConfigDir, "default account never keeps a config dir");
        Assert.IsFalse(accounts[1].IsDefault);
    }

    [TestMethod]
    public void NormalizeMakesDuplicateDisplayKeysUnique()
    {
        var settings = new AppSettings
        {
            ProviderAccounts =
            [
                new ProviderAccount { Id = "w1", ProviderName = KnownProviders.Anthropic, Label = "Work" },
                new ProviderAccount { Id = "w2", ProviderName = KnownProviders.Anthropic, Label = "Work" }
            ]
        };

        settings.Normalize();

        var keys = settings.GetAccounts(KnownProviders.Anthropic)
            .Select(account => account.DisplayKey)
            .ToList();
        Assert.AreEqual(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [TestMethod]
    public void CloneDeepCopiesProviderAccounts()
    {
        var settings = new AppSettings
        {
            ProviderAccounts =
            [
                new ProviderAccount { Id = "work", ProviderName = KnownProviders.Anthropic, Label = "Work" }
            ]
        };
        settings.Normalize();

        var clone = settings.Clone();
        clone.ProviderAccounts.First(account => account.Id == "work").Label = "Changed";

        Assert.AreEqual("Work", settings.ProviderAccounts.First(account => account.Id == "work").Label);
    }

    [TestMethod]
    public void ManagedAccountWithBlankLabelKeysById()
    {
        var account = new ProviderAccount
        {
            Id = "anthropic-x-1a2b",
            ProviderName = KnownProviders.Anthropic,
            Label = ""
        };

        Assert.AreEqual("Anthropic - anthropic-x-1a2b", account.DisplayKey);
    }
}
