using System.IO;
using System.Text;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

// Issue #2: a power loss left monitor.settings.json zero-filled and every later start failed.
[TestClass]
public sealed class AppSettingsServiceRecoveryTests
{
    private string _root = null!;
    private AppLogService _log = null!;
    private AppSettingsService _settings = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _log = new AppLogService(_root);
        _settings = new AppSettingsService(_root, _log);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    public static IEnumerable<object[]> MalformedFiles =>
    [
        ["zero-filled (power loss)", new byte[3302]],
        ["empty", Array.Empty<byte>()],
        ["whitespace only", Encoding.UTF8.GetBytes("  \r\n\t ")],
        ["BOM only", new byte[] { 0xEF, 0xBB, 0xBF }],
        ["truncated object", Encoding.UTF8.GetBytes("""{ "UiScalePercent": 120, "ProviderAccounts": [ { "Id": "x" """)],
        ["plain text", Encoding.UTF8.GetBytes("this is not json")],
        ["literal null", Encoding.UTF8.GetBytes("null")],
        ["array instead of object", Encoding.UTF8.GetBytes("[1, 2, 3]")],
        ["string instead of object", Encoding.UTF8.GetBytes("\"settings\"")],
        ["wrong property type", Encoding.UTF8.GetBytes("""{ "UiScalePercent": "large" }""")],
        ["number overflow", Encoding.UTF8.GetBytes("""{ "UiScalePercent": 99999999999999999999 }""")],
        ["accounts is a string", Encoding.UTF8.GetBytes("""{ "ProviderAccounts": "none" }""")],
        ["invalid UTF-8 bytes", new byte[] { 0xFF, 0xFE, 0x00, 0xC3, 0x28, 0x7B, 0x22 }],
        ["valid JSON then NUL tail", WithNulTail()],
        ["NUL head then valid JSON", WithNulHead()],
        ["unterminated string", Encoding.UTF8.GetBytes("""{ "OverlayTheme": "dark""")],
        ["duplicate closing brace", Encoding.UTF8.GetBytes("""{ "UiScalePercent": 120 }}""")],
    ];

    private static byte[] WithNulTail() => [.. Encoding.UTF8.GetBytes("""{ "UiScalePercent": 120 }"""), .. new byte[64]];

    private static byte[] WithNulHead() => [.. new byte[64], .. Encoding.UTF8.GetBytes("""{ "UiScalePercent": 120 }""")];

    public static string MalformedName(System.Reflection.MethodInfo _, object?[]? data) => $"Malformed: {data?[0]}";

    [TestMethod]
    [DynamicData(nameof(MalformedFiles), DynamicDataDisplayName = nameof(MalformedName))]
    public void MalformedFileFallsBackToDefaultsWithoutThrowing(string name, byte[] contents)
    {
        File.WriteAllBytes(_settings.SettingsPath, contents);

        var settings = _settings.Load();

        Assert.IsNotNull(settings, name);
        Assert.AreEqual(AppSettings.DefaultUiScalePercent, settings.UiScalePercent, name);
        AssertMainFileIsValid();
        var quarantined = CorruptFiles();
        Assert.HasCount(1, quarantined, name);
        CollectionAssert.AreEqual(contents, File.ReadAllBytes(quarantined[0]), $"{name}: the bad file must be kept for inspection");
        StringAssert.Contains(File.ReadAllText(_log.LogPath), "starting with default settings", name);
    }

    [TestMethod]
    [DynamicData(nameof(MalformedFiles), DynamicDataDisplayName = nameof(MalformedName))]
    public void MalformedFileDoesNotBreakUpdateOrSave(string name, byte[] contents)
    {
        File.WriteAllBytes(_settings.SettingsPath, contents);
        _settings.Update(settings => settings.UiScalePercent = 130);
        Assert.AreEqual(130, _settings.Load().UiScalePercent, name);

        File.WriteAllBytes(_settings.SettingsPath, contents);
        _settings.SavePreservingAccounts(new AppSettings { UiScalePercent = 140 });
        Assert.AreEqual(140, _settings.Load().UiScalePercent, name);
        AssertMainFileIsValid();
    }

    [TestMethod]
    public void NullAccountEntryIsDroppedRatherThanQuarantiningTheFile()
    {
        File.WriteAllText(_settings.SettingsPath, """{ "UiScalePercent": 120, "ProviderAccounts": [ null ] }""");

        var settings = _settings.Load();

        Assert.AreEqual(120, settings.UiScalePercent);
        Assert.IsTrue(settings.ProviderAccounts.All(account => account is not null));
        Assert.IsEmpty(CorruptFiles());
    }

    [TestMethod]
    public void CorruptFileRecoversFromBackup()
    {
        _settings.Save(new AppSettings { UiScalePercent = 125 });
        File.WriteAllBytes(_settings.SettingsPath, new byte[3302]);

        var settings = _settings.Load();

        Assert.AreEqual(125, settings.UiScalePercent);
        Assert.AreEqual(125, new AppSettingsService(_root).Load().UiScalePercent, "the recovered settings must be written back");
        Assert.HasCount(1, CorruptFiles());
        StringAssert.Contains(File.ReadAllText(_log.LogPath), "Recovered settings from");
    }

    [TestMethod]
    public void CorruptFileAndBackupRecoverFromNewestLeftoverTempFile()
    {
        File.WriteAllBytes(_settings.SettingsPath, new byte[3302]);
        File.WriteAllBytes(_settings.BackupPath, new byte[3302]);
        var older = _settings.SettingsPath + ".older.tmp";
        var newer = _settings.SettingsPath + ".newer.tmp";
        var garbage = _settings.SettingsPath + ".garbage.tmp";
        File.WriteAllText(older, """{ "UiScalePercent": 110 }""");
        File.WriteAllText(newer, """{ "UiScalePercent": 115 }""");
        File.WriteAllText(garbage, "{ broken");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(garbage, DateTime.UtcNow);

        Assert.AreEqual(115, _settings.Load().UiScalePercent);
        AssertMainFileIsValid();
    }

    [TestMethod]
    public void RecoveredFileIsNotQuarantinedAgain()
    {
        File.WriteAllText(_settings.SettingsPath, "garbage");

        _settings.Load();
        _settings.Load();
        new AppSettingsService(_root, _log).Load();

        Assert.HasCount(1, CorruptFiles());
    }

    [TestMethod]
    public void RepeatedCorruptionKeepsEachQuarantinedFile()
    {
        File.WriteAllText(_settings.SettingsPath, "first");
        _settings.Load();
        File.WriteAllText(_settings.SettingsPath, "second");
        _settings.Load();

        var contents = CorruptFiles().Select(File.ReadAllText).Order().ToList();
        CollectionAssert.AreEqual(new[] { "first", "second" }, contents);
    }

    [TestMethod]
    public void SaveWritesMainAndBackupWithoutLeavingTempFiles()
    {
        _settings.Save(new AppSettings { UiScalePercent = 135 });

        Assert.AreEqual(File.ReadAllText(_settings.SettingsPath), File.ReadAllText(_settings.BackupPath));
        Assert.AreEqual(135, _settings.Load().UiScalePercent);
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
    }

    [TestMethod]
    public void DeletedSettingsFileStillResetsToDefaults()
    {
        _settings.Save(new AppSettings { UiScalePercent = 135 });
        File.Delete(_settings.SettingsPath);

        Assert.AreEqual(AppSettings.DefaultUiScalePercent, _settings.Load().UiScalePercent);
        Assert.IsEmpty(CorruptFiles());
    }

    [TestMethod]
    public void LockedSettingsFileIsNotQuarantined()
    {
        _settings.Save(new AppSettings { UiScalePercent = 135 });

        using (new FileStream(_settings.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsExactly<IOException>(() => _settings.Load());
        }

        Assert.IsEmpty(CorruptFiles());
        Assert.AreEqual(135, _settings.Load().UiScalePercent);
    }

    private string[] CorruptFiles() => Directory.GetFiles(_root, Path.GetFileName(_settings.SettingsPath) + ".corrupt-*");

    private void AssertMainFileIsValid()
    {
        var json = File.ReadAllText(_settings.SettingsPath);
        Assert.IsNotNull(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json));
    }
}
