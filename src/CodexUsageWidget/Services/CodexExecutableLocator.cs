using System.Diagnostics;
using System.IO;

namespace CodexUsageWidget.Services;

public static class CodexExecutableLocator
{
    public static string? Find()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (IsUsable(explicitPath))
        {
            return Path.GetFullPath(explicitPath!);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");

        foreach (var process in Process.GetProcessesByName("codex"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (IsUsable(path) && IsUnder(path!, installRoot))
                {
                    return path;
                }
            }
            catch
            {
                // Some processes do not allow MainModule inspection. Continue with disk discovery.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (Directory.Exists(installRoot))
        {
            var installed = Directory
                .EnumerateFiles(installRoot, "codex.exe", SearchOption.AllDirectories)
                .Where(IsUsable)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();

            if (installed is not null)
            {
                return installed.FullName;
            }
        }

        var pluginFallback = Path.Combine(
            GetCodexHome(), "plugins", ".plugin-appserver", "codex.exe");
        return IsUsable(pluginFallback) ? pluginFallback : null;
    }

    public static string GetCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
    }

    private static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith("codex.exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    private static bool IsUnder(string path, string parent)
    {
        var fullPath = Path.GetFullPath(path);
        var fullParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }
}
