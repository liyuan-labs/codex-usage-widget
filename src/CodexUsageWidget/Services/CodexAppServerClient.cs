using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexUsageWidget.Services;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Process? _process;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCancellation;
    private Task? _readerTask;
    private long _nextRequestId;
    private bool _disposed;
    private string? _lastError;

    public event EventHandler? RateLimitsUpdated;

    public async Task<JsonElement> ReadRateLimitsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await SendRequestCoreAsync(
            "account/rateLimits/read",
            parameters: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning())
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning())
            {
                return;
            }

            await StopProcessAsync().ConfigureAwait(false);
            var executable = CodexExecutableLocator.Find()
                ?? throw new FileNotFoundException(
                    "未找到 Codex CLI。请先安装或启动 Codex 桌面版。");

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    _lastError = args.Data;
                }
            };

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动 Codex app-server。");
            }

            process.BeginErrorReadLine();
            _process = process;
            _writer = process.StandardInput;
            _writer.AutoFlush = true;
            _readCancellation = new CancellationTokenSource();
            _readerTask = ReadLoopAsync(process.StandardOutput, _readCancellation.Token);

            try
            {
                var initializeParams = new
                {
                    clientInfo = new
                    {
                        name = "codex-quota-widget",
                        title = "Codex Quota Widget",
                        version = "1.3.0"
                    }
                };

                await SendRequestCoreAsync(
                    "initialize",
                    initializeParams,
                    cancellationToken).ConfigureAwait(false);
                await SendNotificationAsync("initialized", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopProcessAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private bool IsRunning()
    {
        try
        {
            return _process is { HasExited: false } && _writer is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task<JsonElement> SendRequestCoreAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new InvalidOperationException("Codex app-server 尚未启动。");
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;

        var message = new Dictionary<string, object?>
        {
            ["id"] = requestId,
            ["method"] = method,
            ["params"] = parameters
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await WriteLineAsync(writer, JsonSerializer.Serialize(message), timeout.Token)
                .ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task SendNotificationAsync(string method, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new InvalidOperationException("Codex app-server 尚未启动。");
        var message = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["method"] = method
        });
        await WriteLineAsync(writer, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(
        StreamWriter writer,
        string line,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                JsonDocument? document = null;
                try
                {
                    document = JsonDocument.Parse(line);
                    var root = document.RootElement;

                    if (root.TryGetProperty("id", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.Number &&
                        idElement.TryGetInt64(out var requestId) &&
                        _pending.TryGetValue(requestId, out var completion))
                    {
                        if (root.TryGetProperty("result", out var result))
                        {
                            completion.TrySetResult(result.Clone());
                        }
                        else if (root.TryGetProperty("error", out var error))
                        {
                            completion.TrySetException(new InvalidOperationException(
                                GetErrorMessage(error)));
                        }
                    }
                    else if (root.TryGetProperty("method", out var methodElement) &&
                             methodElement.ValueKind == JsonValueKind.String &&
                             string.Equals(
                                 methodElement.GetString(),
                                 "account/rateLimits/updated",
                                 StringComparison.Ordinal))
                    {
                        RateLimitsUpdated?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (JsonException)
                {
                    // Ignore non-protocol output and keep the connection alive.
                }
                finally
                {
                    document?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            terminalError = exception;
        }
        finally
        {
            var message = terminalError?.Message ?? _lastError ?? "Codex app-server 已断开。";
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new IOException(message, terminalError));
            }
        }
    }

    private static string GetErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? "Codex app-server 返回错误。";
        }

        return "Codex app-server 返回错误。";
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        var writer = _writer;
        var cancellation = _readCancellation;
        var readerTask = _readerTask;

        _process = null;
        _writer = null;
        _readCancellation = null;
        _readerTask = null;

        try
        {
            writer?.Close();
        }
        catch
        {
            // Best-effort shutdown.
        }

        cancellation?.Cancel();
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort shutdown.
            }
        }

        if (readerTask is not null)
        {
            try
            {
                await readerTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // The process has already been terminated.
            }
        }

        cancellation?.Dispose();
        process?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
            _startGate.Dispose();
            _writeGate.Dispose();
        }
    }
}
