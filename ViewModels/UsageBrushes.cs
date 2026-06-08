using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace AIUsageMonitor.ViewModels;

internal static class UsageBrushes
{
    private static readonly MediaBrush AnthropicAccent = FrozenBrush("#F59E0B");
    private static readonly MediaBrush OpenAiAccent = FrozenBrush("#10B981");
    private static readonly MediaBrush AntigravityAccent = FrozenBrush("#F472B6");
    private static readonly MediaBrush GeminiAccent = FrozenBrush("#60A5FA");
    private static readonly MediaBrush CursorAccent = FrozenBrush("#A78BFA");
    private static readonly MediaBrush DefaultAccent = FrozenBrush("#E5E7EB");

    public static MediaBrush ProviderAccent(string providerName)
    {
        var normalized = providerName.Trim().ToLowerInvariant();

        if (normalized.StartsWith("anthropic"))
        {
            return AnthropicAccent;
        }

        if (normalized.StartsWith("openai"))
        {
            return OpenAiAccent;
        }

        if (normalized.StartsWith("antigravity"))
        {
            return AntigravityAccent;
        }

        if (normalized.StartsWith("gemini"))
        {
            return GeminiAccent;
        }

        return normalized.StartsWith("cursor") ? CursorAccent : DefaultAccent;
    }

    public static MediaBrush FrozenBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
