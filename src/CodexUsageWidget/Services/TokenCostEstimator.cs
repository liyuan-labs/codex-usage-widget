using CodexUsageWidget.Models;

namespace CodexUsageWidget.Services;

public sealed class TokenCostEstimator
{
    public const string PricingVerifiedDate = "2026-07-18";
    public const string PricingSource = "https://developers.openai.com/api/docs/pricing";

    private const long LongContextThreshold = 272_000;
    private const decimal OneMillion = 1_000_000m;

    private static readonly IReadOnlyDictionary<string, ModelPricing> PricingByModel =
        new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = new(5m, 0.5m, 6.25m, 30m, ModelFamily.Gpt56),
            ["gpt-5.6"] = new(5m, 0.5m, 6.25m, 30m, ModelFamily.Gpt56),
            ["gpt-5.6-terra"] = new(2.5m, 0.25m, 3.125m, 15m, ModelFamily.Gpt56),
            ["gpt-5.6-luna"] = new(1m, 0.1m, 1.25m, 6m, ModelFamily.Gpt56),
            ["gpt-5.5"] = new(5m, 0.5m, 5m, 30m, ModelFamily.Gpt55),
            ["gpt-5.5-2026-04-23"] = new(5m, 0.5m, 5m, 30m, ModelFamily.Gpt55),
            ["gpt-5.4"] = new(2.5m, 0.25m, 2.5m, 15m, ModelFamily.Gpt54),
            ["gpt-5.4-2026-03-05"] = new(2.5m, 0.25m, 2.5m, 15m, ModelFamily.Gpt54),
            ["gpt-5.4-mini"] = new(0.75m, 0.075m, 0.75m, 4.5m, ModelFamily.Fixed),
            ["gpt-5.4-mini-2026-03-17"] = new(0.75m, 0.075m, 0.75m, 4.5m, ModelFamily.Fixed),
            ["gpt-5.3-codex"] = new(1.75m, 0.175m, 1.75m, 14m, ModelFamily.Fixed)
        };

    public TokenCostEstimate Estimate(IEnumerable<TokenUsageSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var estimated = 0m;
        var unpriced = 0L;
        var unknownModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in samples)
        {
            var usage = sample.Totals.Normalize();
            if (usage.TotalTokens == 0)
            {
                continue;
            }

            var model = sample.Model?.Trim();
            if (string.IsNullOrEmpty(model) || !PricingByModel.TryGetValue(model, out var pricing))
            {
                unpriced = SaturatingAdd(unpriced, usage.TotalTokens);
                unknownModels.Add(string.IsNullOrEmpty(model) ? "unknown" : model);
                continue;
            }

            estimated += EstimateKnownUsage(usage, pricing);
        }

        return new TokenCostEstimate(
            decimal.Round(estimated, 6, MidpointRounding.AwayFromZero),
            unpriced,
            unpriced > 0,
            unknownModels.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public bool TryEstimate(TokenUsageSample sample, out decimal estimatedUsd)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var model = sample.Model?.Trim();
        if (string.IsNullOrEmpty(model) || !PricingByModel.TryGetValue(model, out var pricing))
        {
            estimatedUsd = 0m;
            return false;
        }

        estimatedUsd = EstimateKnownUsage(sample.Totals.Normalize(), pricing);
        return true;
    }

    private static decimal EstimateKnownUsage(TokenUsageTotals usage, ModelPricing pricing)
    {
        var cached = Math.Min(usage.CachedInputTokens, usage.InputTokens);
        var cacheWrite = Math.Min(
            usage.CacheWriteInputTokens,
            Math.Max(0, usage.InputTokens - cached));
        var uncached = Math.Max(0, usage.InputTokens - cached - cacheWrite);

        var inputRate = pricing.Input;
        var cachedRate = pricing.CachedInput;
        var cacheWriteRate = pricing.CacheWriteInput;
        var outputRate = pricing.Output;

        if (usage.InputTokens > LongContextThreshold && pricing.Family != ModelFamily.Fixed)
        {
            inputRate *= 2m;
            cachedRate *= 2m;
            outputRate *= 1.5m;
            if (pricing.Family == ModelFamily.Gpt56)
            {
                cacheWriteRate = inputRate * 1.25m;
            }
            else
            {
                cacheWriteRate = inputRate;
            }
        }

        // OutputTokens already includes ReasoningOutputTokens, so reasoning is not added twice.
        var millionTokenUnits =
            uncached * inputRate +
            cached * cachedRate +
            cacheWrite * cacheWriteRate +
            usage.OutputTokens * outputRate;
        return millionTokenUnits / OneMillion;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        return left + right;
    }

    private sealed record ModelPricing(
        decimal Input,
        decimal CachedInput,
        decimal CacheWriteInput,
        decimal Output,
        ModelFamily Family);

    private enum ModelFamily
    {
        Gpt56,
        Gpt55,
        Gpt54,
        Fixed
    }
}
