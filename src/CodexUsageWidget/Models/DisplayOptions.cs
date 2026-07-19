namespace CodexUsageWidget.Models;

public enum WidgetViewStyle
{
    Card,
    Ring
}

public enum TokenUsageScope
{
    Today,
    Cumulative
}

public enum TokenUsagePeriod
{
    SevenDays,
    ThirtyDays,
    NinetyDays,
    All,
    Custom
}

public static class DisplayOptionParser
{
    public static WidgetViewStyle ParseViewStyle(string? value) =>
        Enum.TryParse<WidgetViewStyle>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            ? parsed
            : WidgetViewStyle.Ring;

    public static TokenUsageScope ParseTokenScope(string? value) =>
        Enum.TryParse<TokenUsageScope>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            ? parsed
            : TokenUsageScope.Today;

    public static TokenUsagePeriod ParseTokenPeriod(string? value) => value switch
    {
        "7Days" or "SevenDays" => TokenUsagePeriod.SevenDays,
        "30Days" or "ThirtyDays" => TokenUsagePeriod.ThirtyDays,
        "90Days" or "NinetyDays" => TokenUsagePeriod.NinetyDays,
        "All" => TokenUsagePeriod.All,
        "Custom" => TokenUsagePeriod.Custom,
        _ => TokenUsagePeriod.ThirtyDays
    };

    public static string ToSettingValue(this TokenUsagePeriod value) => value switch
    {
        TokenUsagePeriod.SevenDays => "7Days",
        TokenUsagePeriod.ThirtyDays => "30Days",
        TokenUsagePeriod.NinetyDays => "90Days",
        TokenUsagePeriod.All => "All",
        TokenUsagePeriod.Custom => "Custom",
        _ => "30Days"
    };
}
