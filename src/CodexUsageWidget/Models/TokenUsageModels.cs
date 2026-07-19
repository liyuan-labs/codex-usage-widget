namespace CodexUsageWidget.Models;

public sealed record TokenUsageQuery(
    TokenUsagePeriod Period,
    DateOnly? CustomStart = null,
    DateOnly? CustomEnd = null,
    DateTimeOffset? Now = null,
    bool IncludeAccumulated = true);

public sealed record TokenUsageTotals(
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens)
{
    public static TokenUsageTotals Zero { get; } = new(0, 0, 0, 0, 0);

    // The Codex log reports cached/cache-write tokens as subsets of input,
    // and reasoning tokens as a subset of output.
    public long RegularInputTokens => Math.Max(0, InputTokens - CachedInputTokens);

    public long VisibleOutputTokens => Math.Max(0, OutputTokens - ReasoningOutputTokens);

    public long TotalTokens => SaturatingAdd(InputTokens, OutputTokens);

    public TokenUsageTotals Normalize()
    {
        var input = Math.Max(0, InputTokens);
        var cached = Math.Clamp(CachedInputTokens, 0, input);
        var cacheWrite = Math.Clamp(CacheWriteInputTokens, 0, input - cached);
        var output = Math.Max(0, OutputTokens);
        var reasoning = Math.Clamp(ReasoningOutputTokens, 0, output);
        return new TokenUsageTotals(input, cached, cacheWrite, output, reasoning);
    }

    public static TokenUsageTotals operator +(TokenUsageTotals left, TokenUsageTotals right) =>
        new(
            SaturatingAdd(left.InputTokens, right.InputTokens),
            SaturatingAdd(left.CachedInputTokens, right.CachedInputTokens),
            SaturatingAdd(left.CacheWriteInputTokens, right.CacheWriteInputTokens),
            SaturatingAdd(left.OutputTokens, right.OutputTokens),
            SaturatingAdd(left.ReasoningOutputTokens, right.ReasoningOutputTokens));

    internal bool HasNondecreasingPrimaryCounters(TokenUsageTotals previous) =>
        InputTokens >= previous.InputTokens && OutputTokens >= previous.OutputTokens;

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        return left + right;
    }
}

public sealed record TokenUsageSample(
    DateTimeOffset Timestamp,
    string? Model,
    TokenUsageTotals Totals);

public sealed record TokenCostEstimate(
    decimal EstimatedUsd,
    long UnpricedTokens,
    bool IsPartialEstimate,
    IReadOnlyList<string> UnknownModels)
{
    public static TokenCostEstimate Empty { get; } = new(0m, 0, false, Array.Empty<string>());
}

public sealed record TokenUsagePeriodSnapshot(
    DateTimeOffset Start,
    DateTimeOffset EndExclusive,
    TokenUsageTotals Totals,
    TokenCostEstimate Cost);

public sealed record TokenUsageSnapshot(
    TokenUsagePeriodSnapshot Today,
    TokenUsagePeriodSnapshot Accumulated,
    DateTimeOffset UpdatedAt,
    string Source,
    string? Warning = null);
