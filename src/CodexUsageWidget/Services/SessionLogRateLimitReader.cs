using System.IO;
using System.Text;
using CodexUsageWidget.Models;

namespace CodexUsageWidget.Services;

public sealed class SessionLogRateLimitReader
{
    private const int MaxFilesToInspect = 32;
    private const int MaxTailBytes = 2 * 1024 * 1024;
    private readonly string _codexHome;

    public SessionLogRateLimitReader(string? codexHome = null)
    {
        _codexHome = codexHome ?? CodexExecutableLocator.GetCodexHome();
    }

    public CodexQuotaSnapshot? TryReadLatest()
    {
        try
        {
            foreach (var file in EnumerateRecentSessionFiles())
            {
                foreach (var line in ReadTailLines(file.FullName).Reverse())
                {
                    try
                    {
                        var snapshot = RateLimitResponseParser.ParseSessionLogLine(
                            line,
                            new DateTimeOffset(file.LastWriteTime));
                        if (snapshot is not null)
                        {
                            return snapshot;
                        }
                    }
                    catch
                    {
                        // A concurrently written or legacy line may not be valid JSON.
                    }
                }
            }
        }
        catch
        {
            // Fallback data is optional; callers will present the primary error if it is unavailable.
        }

        return null;
    }

    private IEnumerable<FileInfo> EnumerateRecentSessionFiles()
    {
        var roots = new[]
        {
            Path.Combine(_codexHome, "sessions"),
            Path.Combine(_codexHome, "archived_sessions")
        };

        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxFilesToInspect);
    }

    private static IReadOnlyList<string> ReadTailLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var bytesToRead = (int)Math.Min(stream.Length, MaxTailBytes);
        if (bytesToRead <= 0)
        {
            return Array.Empty<string>();
        }

        var startsMidFile = stream.Length > bytesToRead;
        stream.Seek(-bytesToRead, SeekOrigin.End);
        var buffer = new byte[bytesToRead];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, offset);
        if (startsMidFile)
        {
            var firstNewline = text.IndexOf('\n');
            text = firstNewline >= 0 ? text[(firstNewline + 1)..] : string.Empty;
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
