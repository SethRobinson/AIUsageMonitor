using System.Text;
using System.IO;
using AIUsageMonitor.Collectors;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class AntigravityUsageCollectorTests
{
    [TestMethod]
    public void CachedModelConfigProtoBuildsUsageWindow()
    {
        var now = new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);
        var resetAt = now.AddHours(6);
        var modelConfig = BuildModelConfig("Gemini 3 Flash", 0.25f, resetAt);

        var usage = AntigravityUsageCollector.TryBuildCachedUsageFromModelConfigBytesForTests(
            [modelConfig],
            "Google AI Pro",
            now.AddMinutes(-5),
            now);

        Assert.IsNotNull(usage);
        Assert.IsFalse(usage.IsUnavailable);
        Assert.AreEqual(KnownProviders.Antigravity, usage.Name);
        Assert.AreEqual("Google AI Pro", usage.PlanName);
        Assert.AreEqual("Gemini Flash", usage.Windows[0].Title);
        Assert.AreEqual(75, usage.Windows[0].UsedPercent, 0.01);
        Assert.AreEqual(resetAt, usage.Windows[0].ResetAt);
        StringAssert.Contains(usage.StatusMessage, "Cached Antigravity model quota");
    }

    private static byte[] BuildModelConfig(string modelName, float remainingFraction, DateTimeOffset resetAt)
    {
        using var stream = new MemoryStream();
        WriteString(stream, 1, modelName);

        using var quota = new MemoryStream();
        WriteFixed32(quota, 1, BitConverter.SingleToUInt32Bits(remainingFraction));

        using var timestamp = new MemoryStream();
        WriteVarint(timestamp, 1, (ulong)resetAt.ToUnixTimeSeconds());
        WriteBytes(quota, 2, timestamp.ToArray());

        WriteBytes(stream, 15, quota.ToArray());
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, int fieldNumber, string value)
    {
        WriteBytes(stream, fieldNumber, Encoding.UTF8.GetBytes(value));
    }

    private static void WriteBytes(Stream stream, int fieldNumber, byte[] value)
    {
        WriteRawVarint(stream, ((ulong)fieldNumber << 3) | 2);
        WriteRawVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    private static void WriteFixed32(Stream stream, int fieldNumber, uint value)
    {
        WriteRawVarint(stream, ((ulong)fieldNumber << 3) | 5);
        Span<byte> buffer = stackalloc byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteVarint(Stream stream, int fieldNumber, ulong value)
    {
        WriteRawVarint(stream, (ulong)fieldNumber << 3);
        WriteRawVarint(stream, value);
    }

    private static void WriteRawVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }
}
