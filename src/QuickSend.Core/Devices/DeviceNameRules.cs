using System.Globalization;
using System.Text;

namespace Eslee.QuickSend.Core.Devices;

/// <summary>
/// Normalizes the QuickSend display name a user types for their own device.
/// The display name never participates in identity: the stable device id, the
/// certificate and its fingerprint stay untouched when the name changes.
/// </summary>
public static class DeviceNameRules
{
    public const int MaxLength = 32;

    public static bool TryNormalize(string? candidate, out string normalized, out string? error)
    {
        normalized = string.Empty;
        if (candidate is null)
        {
            error = "기기 이름을 입력하세요.";
            return false;
        }

        var builder = new StringBuilder(candidate.Length);
        var pendingSpace = false;
        foreach (var rune in candidate.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            // Tabs and newlines are control characters but still separate words, so they
            // collapse to a single space instead of being dropped outright.
            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                if (builder.Length > 0) pendingSpace = true;
                continue;
            }
            if (Rune.IsControl(rune)) continue;
            if (category is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse) continue;
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(rune.ToString());
        }

        var value = builder.ToString();
        if (value.Length == 0)
        {
            error = "기기 이름은 공백만으로 지정할 수 없습니다.";
            return false;
        }

        var elements = new StringInfo(value);
        if (elements.LengthInTextElements > MaxLength)
        {
            error = $"기기 이름은 {MaxLength}자 이하로 입력하세요.";
            return false;
        }

        normalized = value;
        error = null;
        return true;
    }

    /// <summary>Returns the stored name when it is still valid, otherwise the platform fallback.</summary>
    public static string NormalizeOrFallback(string? candidate, string fallback) =>
        TryNormalize(candidate, out var normalized, out _) ? normalized
            : TryNormalize(fallback, out var fallbackName, out _) ? fallbackName
            : "QuickSend";
}
