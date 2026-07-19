using System.IO;
using System.Text.Json;
using CodexUsageWidget.Models;

namespace CodexUsageWidget.Services;

public static class RateLimitResponseParser
{
    public static CodexQuotaSnapshot ParseAppServerResult(
        JsonElement result,
        DateTimeOffset? updatedAt = null)
    {
        var rateLimits = SelectCodexBucket(result);
        var primary = ParseWindow(rateLimits, "primary", camelCase: true);
        var secondary = ParseWindow(rateLimits, "secondary", camelCase: true);

        if (primary is null && secondary is null)
        {
            throw new InvalidDataException("Codex 未返回可显示的额度窗口。");
        }

        return new CodexQuotaSnapshot(
            GetNullableString(rateLimits, "limitId"),
            GetNullableString(rateLimits, "limitName"),
            GetNullableString(rateLimits, "planType"),
            primary,
            secondary,
            ParseCredits(rateLimits, camelCase: true),
            ParseIndividualLimit(rateLimits, camelCase: true),
            ParseResetCreditsCount(result),
            "实时",
            updatedAt ?? DateTimeOffset.Now);
    }

    public static CodexQuotaSnapshot? ParseSessionLogLine(
        string line,
        DateTimeOffset? fallbackTimestamp = null)
    {
        if (string.IsNullOrWhiteSpace(line) ||
            !line.Contains("rate_limits", StringComparison.Ordinal))
        {
            return null;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("rate_limits", out var rateLimits) ||
            rateLimits.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var primary = ParseWindow(rateLimits, "primary", camelCase: false);
        var secondary = ParseWindow(rateLimits, "secondary", camelCase: false);
        if (primary is null && secondary is null)
        {
            return null;
        }

        var timestamp = fallbackTimestamp ?? DateTimeOffset.Now;
        if (root.TryGetProperty("timestamp", out var timestampElement) &&
            timestampElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(timestampElement.GetString(), out var parsedTimestamp))
        {
            timestamp = parsedTimestamp.ToLocalTime();
        }

        return new CodexQuotaSnapshot(
            GetNullableString(rateLimits, "limit_id"),
            GetNullableString(rateLimits, "limit_name"),
            GetNullableString(rateLimits, "plan_type"),
            primary,
            secondary,
            ParseCredits(rateLimits, camelCase: false),
            ParseIndividualLimit(rateLimits, camelCase: false),
            0,
            "缓存",
            timestamp,
            "实时接口暂不可用，显示最近一次本地快照");
    }

    private static JsonElement SelectCodexBucket(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Object &&
            buckets.TryGetProperty("codex", out var codexBucket) &&
            codexBucket.ValueKind == JsonValueKind.Object)
        {
            return codexBucket;
        }

        if (result.TryGetProperty("rateLimits", out var rateLimits) &&
            rateLimits.ValueKind == JsonValueKind.Object)
        {
            return rateLimits;
        }

        throw new InvalidDataException("Codex 额度响应缺少 rateLimits。");
    }

    private static RateLimitWindowSnapshot? ParseWindow(
        JsonElement snapshot,
        string propertyName,
        bool camelCase)
    {
        if (!snapshot.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usedName = camelCase ? "usedPercent" : "used_percent";
        if (!TryGetInt32(window, usedName, out var usedPercent))
        {
            return null;
        }

        var durationName = camelCase ? "windowDurationMins" : "window_minutes";
        var resetName = camelCase ? "resetsAt" : "resets_at";
        var duration = TryGetInt64(window, durationName, out long durationValue)
            ? (long?)durationValue
            : null;
        DateTimeOffset? resetsAt = TryGetInt64(window, resetName, out long resetValue)
            ? DateTimeOffset.FromUnixTimeSeconds(resetValue).ToLocalTime()
            : null;

        return new RateLimitWindowSnapshot(usedPercent, duration, resetsAt);
    }

    private static CreditsSnapshot? ParseCredits(JsonElement snapshot, bool camelCase)
    {
        if (!snapshot.TryGetProperty("credits", out var credits) ||
            credits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hasName = camelCase ? "hasCredits" : "has_credits";
        var hasCredits = TryGetBoolean(credits, hasName, out var hasValue) && hasValue;
        var unlimited = TryGetBoolean(credits, "unlimited", out var unlimitedValue) && unlimitedValue;
        return new CreditsSnapshot(hasCredits, unlimited, GetNullableString(credits, "balance"));
    }

    private static IndividualLimitSnapshot? ParseIndividualLimit(JsonElement snapshot, bool camelCase)
    {
        var propertyName = camelCase ? "individualLimit" : "individual_limit";
        if (!snapshot.TryGetProperty(propertyName, out var limit) ||
            limit.ValueKind != JsonValueKind.Object ||
            !TryGetInt32(limit, camelCase ? "remainingPercent" : "remaining_percent", out var remaining) ||
            !TryGetInt64(limit, camelCase ? "resetsAt" : "resets_at", out var resetsAt))
        {
            return null;
        }

        return new IndividualLimitSnapshot(
            GetNullableString(limit, "limit") ?? string.Empty,
            GetNullableString(limit, "used") ?? string.Empty,
            Math.Clamp(remaining, 0, 100),
            DateTimeOffset.FromUnixTimeSeconds(resetsAt).ToLocalTime());
    }

    private static int ParseResetCreditsCount(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitResetCredits", out var resetCredits) &&
            resetCredits.ValueKind == JsonValueKind.Object &&
            TryGetInt32(resetCredits, "availableCount", out var count))
        {
            return Math.Max(0, count);
        }

        return 0;
    }

    private static string? GetNullableString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = default;
        if (!element.TryGetProperty(name, out var property) ||
            (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }
}
