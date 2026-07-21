using CodexUsageWidget.Models;

namespace CodexUsageWidget.Services;

public sealed class TokenCostEstimator
{
    public const string PricingVerifiedDate = "2026-07-21";
    public const string PricingSource = "https://developers.openai.com/api/docs/pricing";
    public const string DefaultFallbackModel = "gpt-5.6-terra";

    private const long LongContextThreshold = 272_000;
    private const decimal OneMillion = 1_000_000m;

    private static readonly IReadOnlyDictionary<string, ModelPricing> StandardPricingByModel =
        new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = new(5m, 0.5m, 6.25m, 30m, ModelFamily.Gpt56),
            ["gpt-5.6"] = new(5m, 0.5m, 6.25m, 30m, ModelFamily.Gpt56),
            ["gpt-5.6-terra"] = new(2.5m, 0.25m, 3.125m, 15m, ModelFamily.Gpt56),
            ["gpt-5.6-luna"] = new(1m, 0.1m, 1.25m, 6m, ModelFamily.Gpt56),
            ["gpt-5.5"] = new(5m, 0.5m, null, 30m, ModelFamily.Gpt55),
            ["gpt-5.5-2026-04-23"] = new(5m, 0.5m, null, 30m, ModelFamily.Gpt55),
            ["gpt-5.4"] = new(2.5m, 0.25m, null, 15m, ModelFamily.Gpt54),
            ["gpt-5.4-2026-03-05"] = new(2.5m, 0.25m, null, 15m, ModelFamily.Gpt54),
            ["gpt-5.4-mini"] = new(0.75m, 0.075m, null, 4.5m, ModelFamily.Fixed),
            ["gpt-5.4-mini-2026-03-17"] = new(0.75m, 0.075m, null, 4.5m, ModelFamily.Fixed),
            ["gpt-5.3-codex"] = new(1.75m, 0.175m, null, 14m, ModelFamily.Fixed)
        };

    private static readonly IReadOnlyDictionary<string, ModelPricing> PriorityPricingByModel =
        new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = new(10m, 1m, 12.5m, 60m, ModelFamily.Gpt56),
            ["gpt-5.6"] = new(10m, 1m, 12.5m, 60m, ModelFamily.Gpt56),
            ["gpt-5.6-terra"] = new(5m, 0.5m, 6.25m, 30m, ModelFamily.Gpt56),
            ["gpt-5.6-luna"] = new(2m, 0.2m, 2.5m, 12m, ModelFamily.Gpt56),
            ["gpt-5.5"] = new(12.5m, 1.25m, null, 75m, ModelFamily.Gpt55),
            ["gpt-5.5-2026-04-23"] = new(12.5m, 1.25m, null, 75m, ModelFamily.Gpt55),
            ["gpt-5.4"] = new(5m, 0.5m, null, 30m, ModelFamily.Gpt54),
            ["gpt-5.4-2026-03-05"] = new(5m, 0.5m, null, 30m, ModelFamily.Gpt54),
            ["gpt-5.4-mini"] = new(1.5m, 0.15m, null, 9m, ModelFamily.Fixed),
            ["gpt-5.4-mini-2026-03-17"] = new(1.5m, 0.15m, null, 9m, ModelFamily.Fixed)
        };

    private static readonly IReadOnlyDictionary<string, ModelPricing> FlexPricingByModel =
        new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = new(2.5m, 0.25m, 3.125m, 15m, ModelFamily.Gpt56),
            ["gpt-5.6"] = new(2.5m, 0.25m, 3.125m, 15m, ModelFamily.Gpt56),
            ["gpt-5.6-terra"] = new(1.25m, 0.125m, 1.5625m, 7.5m, ModelFamily.Gpt56),
            ["gpt-5.6-luna"] = new(0.5m, 0.05m, 0.625m, 3m, ModelFamily.Gpt56),
            ["gpt-5.5"] = new(2.5m, 0.25m, null, 15m, ModelFamily.Gpt55),
            ["gpt-5.5-2026-04-23"] = new(2.5m, 0.25m, null, 15m, ModelFamily.Gpt55),
            ["gpt-5.4"] = new(1.25m, 0.13m, null, 7.5m, ModelFamily.Gpt54),
            ["gpt-5.4-2026-03-05"] = new(1.25m, 0.13m, null, 7.5m, ModelFamily.Gpt54),
            ["gpt-5.4-mini"] = new(0.375m, 0.0375m, null, 2.25m, ModelFamily.Fixed),
            ["gpt-5.4-mini-2026-03-17"] = new(0.375m, 0.0375m, null, 2.25m, ModelFamily.Fixed)
        };

    private readonly string _fallbackModel;

    public TokenCostEstimator(string? fallbackModel = null)
    {
        _fallbackModel = IsPublishedStandardModel(fallbackModel)
            ? fallbackModel!.Trim()
            : DefaultFallbackModel;
    }

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
            if (string.IsNullOrEmpty(model) ||
                !StandardPricingByModel.TryGetValue(model, out var pricing) ||
                !CanEstimateUsage(usage, pricing))
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
        var usage = sample.Totals.Normalize();
        if (string.IsNullOrEmpty(model) ||
            !StandardPricingByModel.TryGetValue(model, out var pricing) ||
            !CanEstimateUsage(usage, pricing))
        {
            estimatedUsd = 0m;
            return false;
        }

        estimatedUsd = EstimateKnownUsage(usage, pricing);
        return true;
    }

    public bool TryEstimateServiceTier(
        TokenUsageSample sample,
        out decimal estimatedUsd,
        out string? normalizedTier)
    {
        ArgumentNullException.ThrowIfNull(sample);
        normalizedTier = NormalizeServiceTier(sample.ServiceTier);
        var model = sample.Model?.Trim();
        if (normalizedTier is null ||
            string.IsNullOrEmpty(model) ||
            !TryGetServiceTierPricing(normalizedTier, model, out var pricing) ||
            !CanEstimateUsage(sample.Totals.Normalize(), pricing))
        {
            estimatedUsd = 0m;
            return false;
        }

        estimatedUsd = EstimateKnownUsage(sample.Totals.Normalize(), pricing);
        return true;
    }

    public bool TryEstimateServiceTierFullCoverage(
        TokenUsageSample sample,
        out decimal estimatedUsd,
        out string normalizedTier,
        out bool usedInference,
        out string? inferredModel,
        out string? inferredServiceTier,
        out string? referenceModel)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var usage = sample.Totals.Normalize();
        var observedTier = NormalizeServiceTier(sample.ServiceTier);
        normalizedTier = observedTier ?? "standard";
        inferredServiceTier = observedTier is null
            ? string.IsNullOrWhiteSpace(sample.ServiceTier) ? "unknown" : sample.ServiceTier.Trim()
            : null;

        var model = sample.Model?.Trim();
        if (!string.IsNullOrEmpty(model) &&
            TryGetServiceTierPricing(normalizedTier, model, out var pricing) &&
            CanEstimateUsage(usage, pricing))
        {
            estimatedUsd = EstimateKnownUsage(usage, pricing);
            usedInference = inferredServiceTier is not null;
            inferredModel = null;
            referenceModel = null;
            return true;
        }

        var fallbackModel = ResolveFallbackModel(normalizedTier);
        if (!TryGetServiceTierPricing(normalizedTier, fallbackModel, out var fallbackPricing) ||
            !CanEstimateUsage(usage, fallbackPricing))
        {
            estimatedUsd = 0m;
            usedInference = false;
            inferredModel = null;
            referenceModel = null;
            return false;
        }

        estimatedUsd = EstimateKnownUsage(usage, fallbackPricing);
        usedInference = true;
        inferredModel = string.IsNullOrWhiteSpace(model) ? "unknown" : model;
        referenceModel = fallbackModel;
        return true;
    }

    public static string? NormalizeServiceTier(string? serviceTier) =>
        serviceTier?.Trim().ToLowerInvariant() switch
        {
            "default" or "standard" => "standard",
            "priority" or "fast" => "priority",
            "flex" => "flex",
            _ => null
        };

    private string ResolveFallbackModel(string normalizedTier)
    {
        if (TryGetServiceTierPricing(normalizedTier, _fallbackModel, out _))
        {
            return _fallbackModel;
        }

        return DefaultFallbackModel;
    }

    private static bool IsPublishedStandardModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        StandardPricingByModel.ContainsKey(model.Trim());

    private static bool TryGetServiceTierPricing(
        string normalizedTier,
        string model,
        out ModelPricing pricing)
    {
        var catalog = normalizedTier switch
        {
            "standard" => StandardPricingByModel,
            "priority" => PriorityPricingByModel,
            "flex" => FlexPricingByModel,
            _ => null
        };
        if (catalog is not null && catalog.TryGetValue(model, out var matchedPricing))
        {
            pricing = matchedPricing;
            return true;
        }

        pricing = null!;
        return false;
    }

    private static bool CanEstimateUsage(TokenUsageTotals usage, ModelPricing pricing) =>
        usage.CacheWriteInputTokens == 0 || pricing.CacheWriteInput.HasValue;

    private static decimal EstimateKnownUsage(TokenUsageTotals usage, ModelPricing pricing)
    {
        var cached = Math.Min(usage.CachedInputTokens, usage.InputTokens);
        var cacheWrite = Math.Min(
            usage.CacheWriteInputTokens,
            Math.Max(0, usage.InputTokens - cached));
        var uncached = Math.Max(0, usage.InputTokens - cached - cacheWrite);

        var inputRate = pricing.Input;
        var cachedRate = pricing.CachedInput;
        var cacheWriteRate = pricing.CacheWriteInput.GetValueOrDefault();
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
            else if (pricing.CacheWriteInput.HasValue)
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
        decimal? CacheWriteInput,
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
