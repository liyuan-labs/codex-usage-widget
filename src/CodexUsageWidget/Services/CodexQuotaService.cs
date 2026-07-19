using CodexUsageWidget.Models;

namespace CodexUsageWidget.Services;

public sealed class CodexQuotaService : IAsyncDisposable
{
    private readonly SessionLogRateLimitReader _fallbackReader;
    private CodexAppServerClient? _client;
    private CodexQuotaSnapshot? _lastGood;

    public CodexQuotaService(SessionLogRateLimitReader? fallbackReader = null)
    {
        _fallbackReader = fallbackReader ?? new SessionLogRateLimitReader();
    }

    public event EventHandler? RateLimitsUpdated;

    public async Task<CodexQuotaSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            _client ??= CreateClient();
            var result = await _client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = RateLimitResponseParser.ParseAppServerResult(result);
            _lastGood = snapshot;
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ResetClientAsync().ConfigureAwait(false);

            var cached = _fallbackReader.TryReadLatest();
            if (cached is not null)
            {
                _lastGood = cached;
                return cached;
            }

            if (_lastGood is not null)
            {
                return _lastGood with
                {
                    Source = "缓存",
                    Warning = "实时连接中断，保留上次成功结果"
                };
            }

            throw new InvalidOperationException(
                "无法读取 Codex 额度。请确认 Codex 已登录并重试。",
                exception);
        }
    }

    private CodexAppServerClient CreateClient()
    {
        var client = new CodexAppServerClient();
        client.RateLimitsUpdated += OnRateLimitsUpdated;
        return client;
    }

    private void OnRateLimitsUpdated(object? sender, EventArgs args) =>
        RateLimitsUpdated?.Invoke(this, EventArgs.Empty);

    private async Task ResetClientAsync()
    {
        var client = _client;
        _client = null;
        if (client is null)
        {
            return;
        }

        client.RateLimitsUpdated -= OnRateLimitsUpdated;
        await client.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await ResetClientAsync().ConfigureAwait(false);
    }
}
