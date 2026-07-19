using System.Text;
using System.Text.Json;
using CodexUsageWidget;
using CodexUsageWidget.Models;
using CodexUsageWidget.Services;

var failures = new List<string>();

void Check(string name, bool condition)
{
    if (condition)
    {
        Console.WriteLine($"PASS  {name}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"FAIL  {name}");
    }
}

const string appServerFixture = """
{
  "rateLimits": {
    "limitId": "legacy",
    "primary": { "usedPercent": 99, "windowDurationMins": 60, "resetsAt": 1893456000 }
  },
  "rateLimitsByLimitId": {
    "other": {
      "limitId": "other",
      "primary": { "usedPercent": 0, "windowDurationMins": 300, "resetsAt": 1893456000 }
    },
    "codex": {
      "limitId": "codex",
      "primary": { "usedPercent": 27, "windowDurationMins": 300, "resetsAt": 1893456000 },
      "secondary": { "usedPercent": 8, "windowDurationMins": 10080, "resetsAt": 1894060800 },
      "credits": { "hasCredits": true, "unlimited": false, "balance": "12.5" },
      "planType": "pro"
    }
  },
  "rateLimitResetCredits": { "availableCount": 3, "credits": [] }
}
""";

using (var document = JsonDocument.Parse(appServerFixture))
{
    var parsed = RateLimitResponseParser.ParseAppServerResult(document.RootElement);
    Check("selects codex bucket", parsed.LimitId == "codex");
    Check("converts primary used to remaining", parsed.Primary?.RemainingPercent == 73);
    Check("converts secondary used to remaining", parsed.Secondary?.RemainingPercent == 92);
    Check("keeps window duration", parsed.Primary?.WindowDurationMinutes == 300);
    Check("parses credits without detail leakage", parsed.Credits?.Balance == "12.5");
    Check("parses reset-credit count", parsed.ResetCreditsAvailable == 3);
}

const string clampFixture = """
{
  "rateLimits": {
    "primary": { "usedPercent": 140, "windowDurationMins": 300 },
    "secondary": { "usedPercent": -5, "windowDurationMins": 10080 }
  }
}
""";

using (var document = JsonDocument.Parse(clampFixture))
{
    var parsed = RateLimitResponseParser.ParseAppServerResult(document.RootElement);
    Check("clamps exhausted window to zero", parsed.Primary?.RemainingPercent == 0);
    Check("clamps overfull remaining to one hundred", parsed.Secondary?.RemainingPercent == 100);
}

Check(
    "normalizes custom colors",
    MainWindow.TryNormalizeColor("3b82f6", out var normalizedColor) && normalizedColor == "#3B82F6");
Check(
    "rejects malformed custom colors",
    !MainWindow.TryNormalizeColor("#12XYZ9", out _));

var legacySettings = JsonSerializer.Deserialize<WidgetSettings>(
    "{\"Left\":12,\"Top\":34,\"Topmost\":false}");
Check("loads legacy position settings", legacySettings is { Left: 12, Top: 34, Topmost: false });
Check("defaults missing legacy width to half-size", legacySettings?.Width == 230);
Check("defaults missing legacy height to half-size", legacySettings?.Height == 244);
Check("defaults missing legacy opacity", legacySettings?.Opacity == 1.0);
Check("defaults missing legacy colors", legacySettings?.AccentColor == "#10A37F");
Check("defaults missing legacy view to ring", legacySettings?.ViewStyle == "Ring");
Check("defaults missing legacy token panel to visible", legacySettings?.ShowTokenUsage == true);

var customSettings = new WidgetSettings
{
    Left = 80,
    Top = 90,
    Width = 520,
    Height = 360,
    Opacity = 0.75,
    AccentColor = "#8B5CF6",
    BackgroundColor = "#E8EDF5",
    ViewStyle = "Card",
    ShowTokenUsage = false,
    TokenScope = "Cumulative",
    TokenPeriod = "90Days",
    CustomStartDate = new DateTime(2030, 1, 1),
    CustomEndDate = new DateTime(2030, 1, 31)
};
var roundTrippedSettings = JsonSerializer.Deserialize<WidgetSettings>(
    JsonSerializer.Serialize(customSettings));
Check("round-trips adjustable size", roundTrippedSettings is { Width: 520, Height: 360 });
Check("round-trips opacity and colors", roundTrippedSettings is
{
    Opacity: 0.75,
    AccentColor: "#8B5CF6",
    BackgroundColor: "#E8EDF5"
});
Check("round-trips display options", roundTrippedSettings is
{
    ViewStyle: "Card",
    ShowTokenUsage: false,
    TokenScope: "Cumulative",
    TokenPeriod: "90Days",
    CustomStartDate: not null,
    CustomEndDate: not null
});
Check(
    "falls back to supported display options",
    DisplayOptionParser.ParseViewStyle("not-a-style") == WidgetViewStyle.Ring &&
    DisplayOptionParser.ParseViewStyle("999") == WidgetViewStyle.Ring &&
    DisplayOptionParser.ParseTokenScope("999") == TokenUsageScope.Today &&
    DisplayOptionParser.ParseTokenPeriod("not-a-period") == TokenUsagePeriod.ThirtyDays);
Check("uses an absolute settings path", Path.IsPathRooted(WidgetSettingsStore.SettingsPath));

var settingsTestDirectory = Path.Combine(
    Path.GetTempPath(),
    $"codex-widget-settings-test-{Guid.NewGuid():N}");
var settingsTestPath = Path.Combine(settingsTestDirectory, "settings.json");
try
{
    Check("writes appearance settings", WidgetSettingsStore.Save(customSettings, settingsTestPath));
    var loadedCustomSettings = WidgetSettingsStore.Load(settingsTestPath);
    Check("loads persisted appearance settings", loadedCustomSettings is
    {
        Width: 520,
        Height: 360,
        Opacity: 0.75,
        AccentColor: "#8B5CF6",
        BackgroundColor: "#E8EDF5"
    });
    Check("loads persisted display options", loadedCustomSettings is
    {
        ViewStyle: "Card",
        ShowTokenUsage: false,
        TokenScope: "Cumulative",
        TokenPeriod: "90Days"
    });
}
finally
{
    if (Directory.Exists(settingsTestDirectory))
    {
        Directory.Delete(settingsTestDirectory, recursive: true);
    }
}

var tempHome = Path.Combine(Path.GetTempPath(), $"codex-widget-test-{Guid.NewGuid():N}");
try
{
    var sessionDirectory = Path.Combine(tempHome, "sessions", "2030", "01", "01");
    Directory.CreateDirectory(sessionDirectory);
    var sessionPath = Path.Combine(sessionDirectory, "rollout-test.jsonl");
    var fixtureLine = """
    {"timestamp":"2030-01-01T01:02:03Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","primary":{"used_percent":40,"window_minutes":300,"resets_at":1893456000},"secondary":{"used_percent":15,"window_minutes":10080,"resets_at":1894060800},"credits":{"has_credits":false,"unlimited":false,"balance":"0"},"plan_type":"plus"}}}
    """;
    File.WriteAllText(sessionPath, fixtureLine + Environment.NewLine, new UTF8Encoding(false));

    var cached = new SessionLogRateLimitReader(tempHome).TryReadLatest();
    Check("reads session-log fallback", cached is not null);
    Check("marks fallback as cache", cached?.Source == "缓存");
    Check("parses snake_case primary", cached?.Primary?.RemainingPercent == 60);
    Check("parses snake_case secondary", cached?.Secondary?.RemainingPercent == 85);
}
finally
{
    if (Directory.Exists(tempHome))
    {
        Directory.Delete(tempHome, recursive: true);
    }
}

var estimator = new TokenCostEstimator();
var knownCost = estimator.Estimate(new[]
{
    new TokenUsageSample(
        DateTimeOffset.UtcNow,
        "gpt-5.6-sol",
        new TokenUsageTotals(1_000, 400, 100, 300, 100))
});
Check("estimates known-model Standard API equivalent", knownCost.EstimatedUsd == 0.012325m);
Check("does not double-count reasoning output", knownCost.UnpricedTokens == 0);

var partialCost = estimator.Estimate(new[]
{
    new TokenUsageSample(
        DateTimeOffset.UtcNow,
        "gpt-5.6-luna",
        new TokenUsageTotals(1_000, 0, 0, 100, 20)),
    new TokenUsageSample(
        DateTimeOffset.UtcNow,
        "codex-auto-review",
        new TokenUsageTotals(100, 0, 0, 10, 0))
});
Check("marks unknown model cost as partial", partialCost is
{
    IsPartialEstimate: true,
    UnpricedTokens: 110
});
Check("does not guess unknown model price", partialCost.UnknownModels.Contains("codex-auto-review"));

var tokenHome = Path.Combine(Path.GetTempPath(), $"codex-widget-token-test-{Guid.NewGuid():N}");
try
{
    var localMidday = DateTime.Today.AddHours(12);
    var now = new DateTimeOffset(localMidday, TimeZoneInfo.Local.GetUtcOffset(localMidday));
    var sessionDirectory = Path.Combine(
        tokenHome,
        "sessions",
        now.Year.ToString("0000"),
        now.Month.ToString("00"),
        now.Day.ToString("00"));
    Directory.CreateDirectory(sessionDirectory);

    var rootId = Guid.NewGuid().ToString();
    var rootPath = Path.Combine(sessionDirectory, $"rollout-{rootId}.jsonl");
    var rootLines = new[]
    {
        JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-20).ToUniversalTime().ToString("O"),
            type = "session_meta",
            payload = new { id = rootId }
        }),
        JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-19).ToUniversalTime().ToString("O"),
            type = "turn_context",
            payload = new { model = "gpt-5.6-terra" }
        }),
        TokenCountLine(now.AddMinutes(-18), 100, 40, 50, 10, cacheWrite: 20),
        TokenCountLine(
            now.AddMinutes(-17),
            160,
            60,
            80,
            20,
            last: new TokenUsageTotals(60, 20, 0, 30, 10)),
        TokenCountLine(now.AddMinutes(-16), 160, 60, 80, 20),
        JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-15).ToUniversalTime().ToString("O"),
            type = "response_item",
            payload = new { text = "fake turn_context token_count should not affect totals" }
        })
    };
    File.WriteAllLines(rootPath, rootLines, new UTF8Encoding(false));

    var childId = Guid.NewGuid().ToString();
    var childPath = Path.Combine(sessionDirectory, $"rollout-{childId}.jsonl");
    var childLines = new[]
    {
        JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-14).ToUniversalTime().ToString("O"),
            type = "session_meta",
            payload = new { id = childId }
        }),
        JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-13).ToUniversalTime().ToString("O"),
            type = "turn_context",
            payload = new { model = "gpt-5.6-terra" }
        }),
        TokenCountLine(now.AddMinutes(-12), 160, 60, 80, 20),
        JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-11).ToUniversalTime().ToString("O"),
            type = "session_meta",
            payload = new { id = rootId, replay_padding = new string('x', 300_000) }
        }),
        TokenCountLine(now.AddMinutes(-10), 200, 70, 90, 23),
        JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-9).ToUniversalTime().ToString("O"),
            type = "turn_context",
            payload = new { model = "gpt-5.6-luna", context_padding = new string('y', 300_000) }
        }),
        TokenCountLine(now.AddMinutes(-8), 250, 80, 100, 25)
    };
    File.WriteAllLines(childPath, childLines, new UTF8Encoding(false));

    var archivedDirectory = Path.Combine(tokenHome, "archived_sessions");
    Directory.CreateDirectory(archivedDirectory);
    File.Copy(childPath, Path.Combine(archivedDirectory, $"rollout-{childId}.jsonl"));

    var tokenService = new SessionTokenUsageService(tokenHome);
    var todayOnly = await tokenService.GetSnapshotAsync(new TokenUsageQuery(
        TokenUsagePeriod.ThirtyDays,
        Now: now,
        IncludeAccumulated: false));
    Check("aggregates total-token positive deltas", todayOnly.Today.Totals is
    {
        InputTokens: 210,
        CachedInputTokens: 70,
        CacheWriteInputTokens: 20,
        OutputTokens: 90,
        ReasoningOutputTokens: 22,
        TotalTokens: 300
    });
    Check("keeps four displayed token categories mutually exclusive",
        todayOnly.Today.Totals.RegularInputTokens == 140 &&
        todayOnly.Today.Totals.VisibleOutputTokens == 68 &&
        todayOnly.Today.Totals.RegularInputTokens +
        todayOnly.Today.Totals.CachedInputTokens +
        todayOnly.Today.Totals.VisibleOutputTokens +
        todayOnly.Today.Totals.ReasoningOutputTokens == 300);
    Check("removes inherited child-agent prefix", todayOnly.Today.Totals.TotalTokens == 300);
    Check("deduplicates active and archived rollout copies", todayOnly.Today.Totals.TotalTokens == 300);
    Check("handles oversized fork control lines", todayOnly.Today.Totals.InputTokens == 210);
    Check("skips unrequested cumulative scan", todayOnly.Accumulated.Totals.TotalTokens == 0);

    var cumulative = await tokenService.GetSnapshotAsync(new TokenUsageQuery(
        TokenUsagePeriod.SevenDays,
        Now: now));
    Check("aggregates selected cumulative period", cumulative.Accumulated.Totals.TotalTokens == 300);
    Check("returns stable results from unchanged-file cache", cumulative.Today.Totals.TotalTokens == 300);
}
finally
{
    if (Directory.Exists(tokenHome))
    {
        Directory.Delete(tokenHome, recursive: true);
    }
}

var tokenCounterEdgeHome = Path.Combine(
    Path.GetTempPath(),
    $"codex-widget-token-counter-edge-{Guid.NewGuid():N}");
try
{
    var localMidday = DateTime.Today.AddHours(12);
    var now = new DateTimeOffset(localMidday, TimeZoneInfo.Local.GetUtcOffset(localMidday));
    var ownerId = Guid.NewGuid().ToString();
    var directory = Path.Combine(
        tokenCounterEdgeHome,
        "sessions",
        now.Year.ToString("0000"),
        now.Month.ToString("00"),
        now.Day.ToString("00"));
    Directory.CreateDirectory(directory);
    var lines = new[]
    {
        JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-10).ToUniversalTime().ToString("O"),
            type = "session_meta",
            payload = new { id = ownerId }
        }),
        JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-9).ToUniversalTime().ToString("O"),
            type = "turn_context",
            payload = new { model = "gpt-5.6-terra" }
        }),
        TokenCountLine(now.AddMinutes(-8), 100, 40, 50, 10, cacheWrite: 20),
        LastOnlyTokenCountLine(
            now.AddMinutes(-7),
            new TokenUsageTotals(10, 4, 1, 5, 2)),
        TokenCountLine(now.AddMinutes(-6), 110, 44, 55, 12, cacheWrite: 21),
        TokenCountLineWithNullableSubsets(
            now.AddMinutes(-5), 120, null, null, 60, null),
        TokenCountLine(
            now.AddMinutes(-4),
            130,
            48,
            65,
            14,
            cacheWrite: 22,
            last: new TokenUsageTotals(10, 4, 1, 5, 2)),
        TokenCountLineWithNullableSubsets(
            now.AddMinutes(-3), 140, null, null, 70, null),
        TokenCountLine(now.AddMinutes(-2), 150, 50, 75, 15, cacheWrite: 23)
    };
    File.WriteAllLines(
        Path.Combine(directory, $"rollout-{ownerId}.jsonl"),
        lines,
        new UTF8Encoding(false));

    var service = new SessionTokenUsageService(tokenCounterEdgeHome);
    var snapshot = await service.GetSnapshotAsync(new TokenUsageQuery(
        TokenUsagePeriod.ThirtyDays,
        Now: now,
        IncludeAccumulated: false));
    Check("last-only usage advances the cumulative baseline", snapshot.Today.Totals is
    {
        InputTokens: 150,
        OutputTokens: 75,
        TotalTokens: 225
    });
    Check("missing subset fields do not create restored cumulative spikes", snapshot.Today.Totals is
    {
        CachedInputTokens: 48,
        CacheWriteInputTokens: 22,
        ReasoningOutputTokens: 14
    });
}
finally
{
    if (Directory.Exists(tokenCounterEdgeHome))
    {
        Directory.Delete(tokenCounterEdgeHome, recursive: true);
    }
}

var executable = CodexExecutableLocator.Find();
Check("locates a real Codex executable", executable is not null && File.Exists(executable));

if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        await using var service = new CodexQuotaService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var live = await service.GetSnapshotAsync(timeout.Token);
        Check("live read returns a primary window", live.Primary is not null);
        Check("live remaining is in range", live.Primary is { RemainingPercent: >= 0 and <= 100 });
        Console.WriteLine("INFO  live app-server integration passed (account metrics suppressed).");
    }
    catch (Exception)
    {
        failures.Add("live app-server integration");
        Console.WriteLine("FAIL  live app-server integration (details suppressed for privacy).");
    }
}

if (args.Contains("--token-integration", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var liveTokenService = new SessionTokenUsageService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var liveUsage = await liveTokenService.GetSnapshotAsync(
            new TokenUsageQuery(TokenUsagePeriod.ThirtyDays, IncludeAccumulated: false),
            timeout.Token);
        Check("live token aggregation returns non-negative totals", liveUsage.Today.Totals.TotalTokens >= 0);
        Console.WriteLine("INFO  live daily token integration passed (usage metrics suppressed).");
    }
    catch (Exception)
    {
        failures.Add("live token integration");
        Console.WriteLine("FAIL  live token integration (details suppressed for privacy).");
    }
}

if (args.Contains("--token-cumulative-integration", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var liveTokenService = new SessionTokenUsageService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var liveUsage = await liveTokenService.GetSnapshotAsync(
            new TokenUsageQuery(TokenUsagePeriod.SevenDays),
            timeout.Token);
        Check("live seven-day aggregation returns non-negative totals",
            liveUsage.Accumulated.Totals.TotalTokens >= liveUsage.Today.Totals.TotalTokens);
        Console.WriteLine("INFO  live cumulative token integration passed (usage metrics suppressed).");
    }
    catch (Exception)
    {
        failures.Add("live seven-day token integration");
        Console.WriteLine("FAIL  live cumulative token integration (details suppressed for privacy).");
    }
}

if (args.Contains("--settings-probe", StringComparer.OrdinalIgnoreCase))
{
    var probeDirectory = Path.Combine(
        Path.GetTempPath(),
        $"codex-widget-settings-probe-{Guid.NewGuid():N}");
    var probePath = Path.Combine(probeDirectory, "settings.json");
    Check("writes isolated settings probe", WidgetSettingsStore.Save(new WidgetSettings(), probePath));
    if (Directory.Exists(probeDirectory))
    {
        Directory.Delete(probeDirectory, recursive: true);
    }

    Console.WriteLine("INFO  isolated settings probe completed (local path suppressed).");
}

if (failures.Count == 0)
{
    Console.WriteLine("All smoke tests passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} smoke test(s) failed: {string.Join(", ", failures)}");
return 1;

static string TokenCountLine(
    DateTimeOffset timestamp,
    long input,
    long cached,
    long output,
    long reasoning,
    long cacheWrite = 0,
    TokenUsageTotals? last = null)
{
    object? lastUsage = last is null
        ? null
        : new
        {
            input_tokens = last.InputTokens,
            cached_input_tokens = last.CachedInputTokens,
            cache_write_input_tokens = last.CacheWriteInputTokens,
            output_tokens = last.OutputTokens,
            reasoning_output_tokens = last.ReasoningOutputTokens,
            total_tokens = last.TotalTokens
        };
    return JsonSerializer.Serialize(new
    {
        timestamp = timestamp.ToUniversalTime().ToString("O"),
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new
            {
                total_token_usage = new
                {
                    input_tokens = input,
                    cached_input_tokens = cached,
                    cache_write_input_tokens = cacheWrite,
                    output_tokens = output,
                    reasoning_output_tokens = reasoning,
                    total_tokens = input + output
                },
                last_token_usage = lastUsage
            }
        }
    });
}

static string LastOnlyTokenCountLine(
    DateTimeOffset timestamp,
    TokenUsageTotals last) => JsonSerializer.Serialize(new
{
    timestamp = timestamp.ToUniversalTime().ToString("O"),
    type = "event_msg",
    payload = new
    {
        type = "token_count",
        info = new
        {
            last_token_usage = new
            {
                input_tokens = last.InputTokens,
                cached_input_tokens = last.CachedInputTokens,
                cache_write_input_tokens = last.CacheWriteInputTokens,
                output_tokens = last.OutputTokens,
                reasoning_output_tokens = last.ReasoningOutputTokens,
                total_tokens = last.TotalTokens
            }
        }
    }
});

static string TokenCountLineWithNullableSubsets(
    DateTimeOffset timestamp,
    long input,
    long? cached,
    long? cacheWrite,
    long output,
    long? reasoning) => JsonSerializer.Serialize(new
{
    timestamp = timestamp.ToUniversalTime().ToString("O"),
    type = "event_msg",
    payload = new
    {
        type = "token_count",
        info = new
        {
            total_token_usage = new
            {
                input_tokens = input,
                cached_input_tokens = cached,
                cache_write_input_tokens = cacheWrite,
                output_tokens = output,
                reasoning_output_tokens = reasoning,
                total_tokens = input + output
            }
        }
    }
});
