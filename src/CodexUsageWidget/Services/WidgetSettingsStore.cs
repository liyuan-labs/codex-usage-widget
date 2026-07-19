using System.IO;
using System.Text.Json;

namespace CodexUsageWidget.Services;

public sealed class WidgetSettings
{
    public double? Left { get; init; }
    public double? Top { get; init; }
    public bool Topmost { get; init; } = true;
    public double Width { get; init; } = 230;
    public double Height { get; init; } = 244;
    public double Opacity { get; init; } = 1.0;
    public string AccentColor { get; init; } = "#10A37F";
    public string BackgroundColor { get; init; } = "#191C23";
    public string ViewStyle { get; init; } = "Ring";
    public bool ShowTokenUsage { get; init; } = true;
    public string TokenScope { get; init; } = "Today";
    public string TokenPeriod { get; init; } = "30Days";
    public DateTime? CustomStartDate { get; init; }
    public DateTime? CustomEndDate { get; init; }
}

public static class WidgetSettingsStore
{
    public static string SettingsPath { get; } = Path.Combine(
        ResolveLocalApplicationData(),
        "CodexQuotaWidget",
        "settings.json");

    public static WidgetSettings Load(string? path = null)
    {
        path ??= SettingsPath;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(path))
                       ?? new WidgetSettings();
            }
        }
        catch
        {
            // Invalid settings should never prevent the widget from starting.
        }

        return new WidgetSettings();
    }

    public static bool Save(WidgetSettings settings, string? path = null)
    {
        path ??= SettingsPath;
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            // Position persistence is optional.
            return false;
        }
    }

    private static string ResolveLocalApplicationData()
    {
        var environmentPath = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(environmentPath) && Path.IsPathRooted(environmentPath))
        {
            return Environment.ExpandEnvironmentVariables(environmentPath);
        }

        var knownFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(knownFolderPath) && Path.IsPathRooted(knownFolderPath))
        {
            return knownFolderPath;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, "AppData", "Local");
        }

        return AppContext.BaseDirectory;
    }
}
