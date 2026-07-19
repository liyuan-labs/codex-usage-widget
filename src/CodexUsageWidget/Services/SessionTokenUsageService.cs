using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CodexUsageWidget.Models;

namespace CodexUsageWidget.Services;

public sealed class SessionTokenUsageService
{
    private const int ReadBufferSize = 256 * 1024;
    private const int MaxCandidateLineSize = 256 * 1024;

    private readonly IReadOnlyList<string> _sessionRoots;
    private readonly TokenCostEstimator _costEstimator;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedRollout> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public SessionTokenUsageService(
        string? codexHome = null,
        TokenCostEstimator? costEstimator = null)
    {
        var home = codexHome ?? CodexExecutableLocator.GetCodexHome();
        _sessionRoots =
        [
            Path.Combine(home, "sessions"),
            Path.Combine(home, "archived_sessions")
        ];
        _costEstimator = costEstimator ?? new TokenCostEstimator();
    }

    public Task<TokenUsageSnapshot> GetSnapshotAsync(
        TokenUsageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => ReadSnapshot(query, cancellationToken), cancellationToken);
    }

    private TokenUsageSnapshot ReadSnapshot(
        TokenUsageQuery query,
        CancellationToken cancellationToken)
    {
        var now = TimeZoneInfo.ConvertTime(query.Now ?? DateTimeOffset.Now, TimeZoneInfo.Local);
        var today = DateOnly.FromDateTime(now.Date);
        var todayStart = AtLocalMidnight(today);
        var todayEnd = AtLocalMidnight(today.AddDays(1));
        var (accumulatedStart, accumulatedEnd) = ResolveAccumulatedBounds(query, now);
        var earliestStart = query.IncludeAccumulated && query.Period == TokenUsagePeriod.All
            ? DateTimeOffset.MinValue
            : query.IncludeAccumulated && accumulatedStart < todayStart
                ? accumulatedStart
                : todayStart;

        var todayAccumulator = new PeriodAccumulator();
        var accumulatedAccumulator = new PeriodAccumulator();
        DateOnly? earliestBucketDate = null;
        var unreadableFiles = 0;

        foreach (var file in EnumerateDistinctRollouts(ref unreadableFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (earliestStart != DateTimeOffset.MinValue &&
                    file.LastWriteTimeUtc < earliestStart.UtcDateTime)
                {
                    continue;
                }

                foreach (var bucket in ReadRolloutCached(file, cancellationToken))
                {
                    if (bucket.LocalDate == today)
                    {
                        todayAccumulator.Add(bucket);
                    }

                    if (!query.IncludeAccumulated ||
                        !IsDateInRange(bucket.LocalDate, accumulatedStart, accumulatedEnd))
                    {
                        continue;
                    }

                    accumulatedAccumulator.Add(bucket);
                    if (earliestBucketDate is null || bucket.LocalDate < earliestBucketDate)
                    {
                        earliestBucketDate = bucket.LocalDate;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A concurrently moved, partially written, or legacy file is optional.
                unreadableFiles++;
            }
        }

        if (query.IncludeAccumulated && query.Period == TokenUsagePeriod.All &&
            earliestBucketDate is { } firstDate)
        {
            accumulatedStart = AtLocalMidnight(firstDate);
        }

        var warning = unreadableFiles > 0
            ? $"有 {unreadableFiles} 个本地会话日志暂时无法读取，统计可能不完整。"
            : null;

        return new TokenUsageSnapshot(
            CreatePeriodSnapshot(todayStart, todayEnd, todayAccumulator),
            CreatePeriodSnapshot(accumulatedStart, accumulatedEnd, accumulatedAccumulator),
            now,
            "本地日志估算",
            warning);
    }

    private IReadOnlyList<FileInfo> EnumerateDistinctRollouts(ref int unreadableFiles)
    {
        var distinct = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _sessionRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             root,
                             "*.jsonl",
                             SearchOption.AllDirectories))
                {
                    var file = new FileInfo(path);
                    var key = ExtractOwnerId(path) ?? Path.GetFullPath(path);
                    if (!distinct.TryGetValue(key, out var existing) ||
                        file.Length > existing.Length ||
                        file.Length == existing.Length && file.LastWriteTimeUtc > existing.LastWriteTimeUtc)
                    {
                        distinct[key] = file;
                    }
                }
            }
            catch
            {
                unreadableFiles++;
            }
        }

        return distinct.Values.ToArray();
    }

    private IReadOnlyList<DailyUsageBucket> ReadRolloutCached(
        FileInfo file,
        CancellationToken cancellationToken)
    {
        var length = file.Length;
        var lastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(file.FullName, out var cached) &&
                cached.Length == length &&
                cached.LastWriteUtcTicks == lastWriteUtcTicks)
            {
                return cached.Buckets;
            }
        }

        var buckets = ReadRollout(file.FullName, cancellationToken);
        lock (_cacheLock)
        {
            _cache[file.FullName] = new CachedRollout(length, lastWriteUtcTicks, buckets);
        }

        return buckets;
    }

    private IReadOnlyList<DailyUsageBucket> ReadRollout(
        string path,
        CancellationToken cancellationToken)
    {
        var state = new RolloutParsingState(ExtractOwnerId(path));
        ScanCandidateLines(
            path,
            line => ProcessCandidateLine(line, state),
            prefix => ProcessOversizedControlPrefix(prefix, state),
            cancellationToken);

        return state.Accepted.Values
            .OrderBy(bucket => bucket.LocalDate)
            .Select(bucket => bucket.ToSnapshot())
            .ToArray();
    }

    private static void ProcessOversizedControlPrefix(
        ReadOnlyMemory<byte> utf8Prefix,
        RolloutParsingState state)
    {
        try
        {
            var reader = new Utf8JsonReader(
                utf8Prefix.Span,
                isFinalBlock: false,
                state: default);
            string? rootType = null;
            string? metaId = null;
            string? model = null;

            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var propertyName = reader.GetString();
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                {
                    continue;
                }

                if (rootType is null && string.Equals(propertyName, "type", StringComparison.Ordinal))
                {
                    rootType = reader.GetString();
                }
                else if (metaId is null && string.Equals(propertyName, "id", StringComparison.Ordinal))
                {
                    metaId = reader.GetString();
                }
                else if (model is null && string.Equals(propertyName, "model", StringComparison.Ordinal))
                {
                    model = reader.GetString();
                }
            }

            if (string.Equals(rootType, "turn_context", StringComparison.Ordinal))
            {
                state.CurrentModel = model ?? state.CurrentModel;
                state.AcceptUsage = true;
                return;
            }

            if (!string.Equals(rootType, "session_meta", StringComparison.Ordinal))
            {
                return;
            }

            state.OwnerId ??= metaId;
            if (!string.IsNullOrWhiteSpace(metaId) &&
                !string.IsNullOrWhiteSpace(state.OwnerId) &&
                !string.Equals(metaId, state.OwnerId, StringComparison.OrdinalIgnoreCase))
            {
                state.Accepted.Clear();
                state.AcceptUsage = false;
                state.CurrentModel = null;
            }
        }
        catch (JsonException)
        {
            // A partial prefix may end in the middle of a JSON token.
        }
    }

    private void ProcessCandidateLine(
        ReadOnlyMemory<byte> utf8Line,
        RolloutParsingState state)
    {
        try
        {
            if (utf8Line.Length >= 3 &&
                utf8Line.Span[0] == 0xEF &&
                utf8Line.Span[1] == 0xBB &&
                utf8Line.Span[2] == 0xBF)
            {
                utf8Line = utf8Line[3..];
            }

            using var document = JsonDocument.Parse(utf8Line);
            var root = document.RootElement;
            var rootType = GetString(root, "type");
            if (!root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (string.Equals(rootType, "session_meta", StringComparison.Ordinal))
            {
                var metaId = GetString(payload, "id");
                state.OwnerId ??= metaId;
                if (!string.IsNullOrWhiteSpace(metaId) &&
                    !string.IsNullOrWhiteSpace(state.OwnerId) &&
                    !string.Equals(metaId, state.OwnerId, StringComparison.OrdinalIgnoreCase))
                {
                    // Forked agents replay inherited history under a foreign owner. The
                    // cumulative baseline must survive, but every provisional contribution
                    // before the final real tail must be removed.
                    state.Accepted.Clear();
                    state.AcceptUsage = false;
                    state.CurrentModel = null;
                }

                return;
            }

            if (string.Equals(rootType, "turn_context", StringComparison.Ordinal))
            {
                state.CurrentModel = GetString(payload, "model") ?? state.CurrentModel;
                state.AcceptUsage = true;
                return;
            }

            if (string.Equals(rootType, "event_msg", StringComparison.Ordinal) &&
                string.Equals(GetString(payload, "type"), "thread_settings_applied", StringComparison.Ordinal) &&
                payload.TryGetProperty("thread_settings", out var threadSettings) &&
                threadSettings.ValueKind == JsonValueKind.Object)
            {
                state.CurrentModel = GetString(threadSettings, "model") ?? state.CurrentModel;
                return;
            }

            if (!string.Equals(rootType, "event_msg", StringComparison.Ordinal) ||
                !string.Equals(GetString(payload, "type"), "token_count", StringComparison.Ordinal) ||
                !payload.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var total = TryReadUsage(info, "total_token_usage");
            var last = TryReadUsage(info, "last_token_usage");
            TokenUsageTotals? delta = null;

            if (total is { } totalReading)
            {
                var normalizedTotal = totalReading.Totals.Normalize();
                var normalizedLast = last?.Totals.Normalize();
                if (state.PreviousTotal is null)
                {
                    delta = normalizedTotal;
                }
                else if (totalReading.Fields.HasFlag(UsageFields.Input) &&
                         totalReading.Fields.HasFlag(UsageFields.Output) &&
                         state.PreviousTotalFields.HasFlag(UsageFields.Input) &&
                         state.PreviousTotalFields.HasFlag(UsageFields.Output) &&
                         normalizedTotal.HasNondecreasingPrimaryCounters(state.PreviousTotal))
                {
                    delta = CreateContinuousDelta(
                        normalizedTotal,
                        totalReading.Fields,
                        state.PreviousTotal,
                        state.PreviousTotalFields,
                        last);
                }
                else if (normalizedLast is { TotalTokens: > 0 })
                {
                    // A primary cumulative counter reset or malformed total is best
                    // represented by the explicitly reported per-turn usage.
                    delta = normalizedLast;
                }
                else
                {
                    // Both primary counters moved backwards, or a primary field is
                    // absent. Treat the available total as a new baseline.
                    delta = normalizedTotal;
                }

                state.PreviousTotal = normalizedTotal;
                state.PreviousTotalFields = totalReading.Fields;
            }
            else if (last is { } lastReading)
            {
                delta = lastReading.Totals.Normalize();
                state.PreviousTotal = state.PreviousTotal is null
                    ? delta
                    : state.PreviousTotal + delta;
                state.PreviousTotalFields |= lastReading.Fields;
            }

            if (!state.AcceptUsage || delta is null || delta.TotalTokens == 0 ||
                !TryReadTimestamp(root, out var timestamp))
            {
                return;
            }

            var localDate = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local).Date);
            if (!state.Accepted.TryGetValue(localDate, out var bucket))
            {
                bucket = new DailyUsageAccumulator(localDate);
                state.Accepted.Add(localDate, bucket);
            }

            bucket.Add(
                new TokenUsageSample(timestamp, state.CurrentModel, delta),
                _costEstimator);
        }
        catch (JsonException)
        {
            // Ignore a malformed legacy or concurrently written line and continue.
        }
    }

    private static void ScanCandidateLines(
        string path,
        Action<ReadOnlyMemory<byte>> processLine,
        Action<ReadOnlyMemory<byte>> processOversizedControlPrefix,
        CancellationToken cancellationToken)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var lineBuffer = ArrayPool<byte>.Shared.Rent(MaxCandidateLineSize);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferSize,
                FileOptions.SequentialScan);

            var lineLength = 0;
            var oversized = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = stream.Read(readBuffer, 0, readBuffer.Length);
                if (bytesRead == 0)
                {
                    break;
                }

                var chunk = readBuffer.AsSpan(0, bytesRead);
                var position = 0;
                while (position < chunk.Length)
                {
                    var newlineIndex = chunk[position..].IndexOf((byte)'\n');
                    var segmentLength = newlineIndex >= 0
                        ? newlineIndex
                        : chunk.Length - position;

                    if (!oversized && segmentLength > 0)
                    {
                        if (lineLength + segmentLength <= MaxCandidateLineSize)
                        {
                            chunk.Slice(position, segmentLength)
                                .CopyTo(lineBuffer.AsSpan(lineLength));
                            lineLength += segmentLength;
                        }
                        else
                        {
                            var available = MaxCandidateLineSize - lineLength;
                            if (available > 0)
                            {
                                chunk.Slice(position, available)
                                    .CopyTo(lineBuffer.AsSpan(lineLength));
                                lineLength += available;
                            }

                            oversized = true;
                        }
                    }

                    position += segmentLength;
                    if (newlineIndex < 0)
                    {
                        break;
                    }

                    ProcessBufferedLine(
                        lineBuffer,
                        lineLength,
                        oversized,
                        processLine,
                        processOversizedControlPrefix);
                    lineLength = 0;
                    oversized = false;
                    position++;
                }
            }

            if (lineLength > 0)
            {
                ProcessBufferedLine(
                    lineBuffer,
                    lineLength,
                    oversized,
                    processLine,
                    processOversizedControlPrefix);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(lineBuffer);
        }
    }

    private static void ProcessBufferedLine(
        byte[] buffer,
        int length,
        bool oversized,
        Action<ReadOnlyMemory<byte>> processLine,
        Action<ReadOnlyMemory<byte>> processOversizedControlPrefix)
    {
        if (length == 0)
        {
            return;
        }

        if (oversized)
        {
            var prefix = buffer.AsSpan(0, length);
            if (prefix.IndexOf("turn_context"u8) >= 0 ||
                prefix.IndexOf("session_meta"u8) >= 0)
            {
                processOversizedControlPrefix(buffer.AsMemory(0, length));
            }

            return;
        }

        var span = buffer.AsSpan(0, length);
        if (span.IndexOf("token_count"u8) < 0 &&
            span.IndexOf("turn_context"u8) < 0 &&
            span.IndexOf("session_meta"u8) < 0 &&
            span.IndexOf("thread_settings_applied"u8) < 0)
        {
            return;
        }

        processLine(buffer.AsMemory(0, length));
    }

    private static TokenUsagePeriodSnapshot CreatePeriodSnapshot(
        DateTimeOffset start,
        DateTimeOffset endExclusive,
        PeriodAccumulator accumulator) => new(
        start,
        endExclusive,
        accumulator.Totals,
        new TokenCostEstimate(
            decimal.Round(accumulator.EstimatedUsd, 6, MidpointRounding.AwayFromZero),
            accumulator.UnpricedTokens,
            accumulator.UnpricedTokens > 0,
            accumulator.UnknownModels
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray()));

    private static bool IsDateInRange(
        DateOnly date,
        DateTimeOffset start,
        DateTimeOffset endExclusive)
    {
        var startDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(start, TimeZoneInfo.Local).Date);
        var inclusiveEnd = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(endExclusive.AddTicks(-1), TimeZoneInfo.Local).Date);
        return date >= startDate && date <= inclusiveEnd;
    }

    private static (DateTimeOffset Start, DateTimeOffset EndExclusive) ResolveAccumulatedBounds(
        TokenUsageQuery query,
        DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.Date);
        var tomorrow = AtLocalMidnight(today.AddDays(1));
        return query.Period switch
        {
            TokenUsagePeriod.SevenDays => (AtLocalMidnight(today.AddDays(-6)), tomorrow),
            TokenUsagePeriod.ThirtyDays => (AtLocalMidnight(today.AddDays(-29)), tomorrow),
            TokenUsagePeriod.NinetyDays => (AtLocalMidnight(today.AddDays(-89)), tomorrow),
            TokenUsagePeriod.All => (DateTimeOffset.MinValue, tomorrow),
            TokenUsagePeriod.Custom => ResolveCustomBounds(query),
            _ => (AtLocalMidnight(today.AddDays(-29)), tomorrow)
        };
    }

    private static (DateTimeOffset Start, DateTimeOffset EndExclusive) ResolveCustomBounds(
        TokenUsageQuery query)
    {
        if (query.CustomStart is not { } start || query.CustomEnd is not { } end)
        {
            throw new ArgumentException("自定义周期需要开始和结束日期。", nameof(query));
        }

        if (end < start)
        {
            throw new ArgumentException("自定义周期的结束日期不能早于开始日期。", nameof(query));
        }

        return (AtLocalMidnight(start), AtLocalMidnight(end.AddDays(1)));
    }

    private static DateTimeOffset AtLocalMidnight(DateOnly date)
    {
        var localDateTime = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
    }

    private static TokenUsageTotals CreateContinuousDelta(
        TokenUsageTotals current,
        UsageFields currentFields,
        TokenUsageTotals previous,
        UsageFields previousFields,
        ParsedUsage? last)
    {
        var input = current.InputTokens - previous.InputTokens;
        var output = current.OutputTokens - previous.OutputTokens;
        var cached = ResolveSubsetDelta(
            UsageFields.CachedInput,
            current.CachedInputTokens,
            previous.CachedInputTokens,
            currentFields,
            previousFields,
            last,
            usage => usage.CachedInputTokens);
        var cacheWrite = ResolveSubsetDelta(
            UsageFields.CacheWriteInput,
            current.CacheWriteInputTokens,
            previous.CacheWriteInputTokens,
            currentFields,
            previousFields,
            last,
            usage => usage.CacheWriteInputTokens);
        var reasoning = ResolveSubsetDelta(
            UsageFields.ReasoningOutput,
            current.ReasoningOutputTokens,
            previous.ReasoningOutputTokens,
            currentFields,
            previousFields,
            last,
            usage => usage.ReasoningOutputTokens);

        return new TokenUsageTotals(input, cached, cacheWrite, output, reasoning).Normalize();
    }

    private static long ResolveSubsetDelta(
        UsageFields field,
        long current,
        long previous,
        UsageFields currentFields,
        UsageFields previousFields,
        ParsedUsage? last,
        Func<TokenUsageTotals, long> select)
    {
        var currentPresent = currentFields.HasFlag(field);
        var previousPresent = previousFields.HasFlag(field);
        if (currentPresent && previousPresent && current >= previous)
        {
            return current - previous;
        }

        if (last is { } lastReading && lastReading.Fields.HasFlag(field))
        {
            return select(lastReading.Totals.Normalize());
        }

        // A missing field starts a new baseline for that subset. Do not turn a
        // later restored cumulative value into a fabricated current-turn spike.
        return currentPresent && previousPresent ? current : 0;
    }

    private static ParsedUsage? TryReadUsage(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fields = UsageFields.None;
        long Read(string name, UsageFields field)
        {
            if (usage.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var parsed))
            {
                fields |= field;
                return Math.Max(0, parsed);
            }

            return 0;
        }

        return new ParsedUsage(
            new TokenUsageTotals(
                Read("input_tokens", UsageFields.Input),
                Read("cached_input_tokens", UsageFields.CachedInput),
                Read("cache_write_input_tokens", UsageFields.CacheWriteInput),
                Read("output_tokens", UsageFields.Output),
                Read("reasoning_output_tokens", UsageFields.ReasoningOutput)),
            fields);
    }

    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var value) &&
               value.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(
                   value.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                   out timestamp);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ExtractOwnerId(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length < 36)
        {
            return null;
        }

        var candidate = name[^36..];
        return Guid.TryParse(candidate, out _) ? candidate : null;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class RolloutParsingState(string? ownerId)
    {
        public string? OwnerId { get; set; } = ownerId;
        public TokenUsageTotals? PreviousTotal { get; set; }
        public UsageFields PreviousTotalFields { get; set; }
        public string? CurrentModel { get; set; }
        public bool AcceptUsage { get; set; } = true;
        public Dictionary<DateOnly, DailyUsageAccumulator> Accepted { get; } = [];
    }

    [Flags]
    private enum UsageFields
    {
        None = 0,
        Input = 1,
        CachedInput = 2,
        CacheWriteInput = 4,
        Output = 8,
        ReasoningOutput = 16
    }

    private readonly record struct ParsedUsage(TokenUsageTotals Totals, UsageFields Fields);

    private sealed class DailyUsageAccumulator(DateOnly localDate)
    {
        public DateOnly LocalDate { get; } = localDate;
        public TokenUsageTotals Totals { get; private set; } = TokenUsageTotals.Zero;
        public decimal EstimatedUsd { get; private set; }
        public long UnpricedTokens { get; private set; }
        public HashSet<string> UnknownModels { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(TokenUsageSample sample, TokenCostEstimator estimator)
        {
            Totals += sample.Totals;
            if (estimator.TryEstimate(sample, out var estimatedUsd))
            {
                EstimatedUsd += estimatedUsd;
                return;
            }

            UnpricedTokens = SaturatingAdd(UnpricedTokens, sample.Totals.TotalTokens);
            UnknownModels.Add(string.IsNullOrWhiteSpace(sample.Model) ? "unknown" : sample.Model.Trim());
        }

        public DailyUsageBucket ToSnapshot() => new(
            LocalDate,
            Totals,
            EstimatedUsd,
            UnpricedTokens,
            UnknownModels.ToArray());
    }

    private sealed record DailyUsageBucket(
        DateOnly LocalDate,
        TokenUsageTotals Totals,
        decimal EstimatedUsd,
        long UnpricedTokens,
        IReadOnlyList<string> UnknownModels);

    private sealed class PeriodAccumulator
    {
        public TokenUsageTotals Totals { get; private set; } = TokenUsageTotals.Zero;
        public decimal EstimatedUsd { get; private set; }
        public long UnpricedTokens { get; private set; }
        public HashSet<string> UnknownModels { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(DailyUsageBucket bucket)
        {
            Totals += bucket.Totals;
            EstimatedUsd += bucket.EstimatedUsd;
            UnpricedTokens = SaturatingAdd(UnpricedTokens, bucket.UnpricedTokens);
            foreach (var model in bucket.UnknownModels)
            {
                UnknownModels.Add(model);
            }
        }
    }

    private sealed record CachedRollout(
        long Length,
        long LastWriteUtcTicks,
        IReadOnlyList<DailyUsageBucket> Buckets);
}
