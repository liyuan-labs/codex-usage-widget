using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexUsageWidget.Models;
using CodexUsageWidget.Services;

namespace CodexUsageWidget;

public partial class MainWindow : Window
{
    private readonly CodexQuotaService _quotaService = new();
    private readonly SessionTokenUsageService _tokenUsageService = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private CodexQuotaSnapshot? _snapshot;
    private TokenUsageSnapshot? _tokenUsageSnapshot;
    private AppearanceWindow? _appearanceWindow;
    private bool _refreshInProgress;
    private bool _tokenRefreshInProgress;
    private bool _tokenRefreshPending;
    private bool _tokenRefreshPendingBusyState;
    private long _tokenQueryVersion;
    private bool _settingsReady;
    private bool? _lastSettingsSaveSucceeded;
    private DateTimeOffset _lastPushRefresh = DateTimeOffset.MinValue;
    private Color _accentColor = Color.FromRgb(0x10, 0xA3, 0x7F);
    private Color _displayAccent = Color.FromRgb(0x10, 0xA3, 0x7F);
    private Color _backgroundColor = Color.FromRgb(0x19, 0x1C, 0x23);
    private WidgetViewStyle _viewStyle = WidgetViewStyle.Ring;
    private bool _showTokenUsage = true;
    private TokenUsageScope _tokenScope = TokenUsageScope.Today;
    private TokenUsagePeriod _tokenPeriod = TokenUsagePeriod.ThirtyDays;
    private DateTime? _customStartDate;
    private DateTime? _customEndDate;
    private bool _displayOptionsReady;
    private bool _synchronizingDisplayOptions;

    public string AccentColorHex { get; private set; } = "#10A37F";
    public string BackgroundColorHex { get; private set; } = "#191C23";
    public WidgetViewStyle ViewStyle => _viewStyle;
    public bool ShowTokenUsage => _showTokenUsage;
    public TokenUsageScope TokenScope => _tokenScope;
    public TokenUsagePeriod TokenPeriod => _tokenPeriod;
    public DateTime? CustomStartDate => _customStartDate;
    public DateTime? CustomEndDate => _customEndDate;

    public MainWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(2)
        };
        _refreshTimer.Tick += async (_, _) => await Task.WhenAll(
            RefreshAsync(showBusyState: false),
            RefreshTokenUsageAsync());

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) => UpdateTimeLabels();

        _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveCurrentSettings();
        };

        SizeChanged += (_, _) => ScheduleSettingsSave();
        LocationChanged += (_, _) => ScheduleSettingsSave();

        TodayScopeButton.Click += TodayScopeButton_Click;
        CumulativePeriodComboBox.SelectionChanged += CumulativePeriodComboBox_SelectionChanged;
        CumulativePeriodComboBox.DropDownOpened += CumulativePeriodComboBox_DropDownOpened;
        HideTokenUsageButton.Click += HideTokenUsageButton_Click;
        ShowTokenMenuItem.Click += ShowTokenMenuItem_Click;

        _quotaService.RateLimitsUpdated += QuotaService_RateLimitsUpdated;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySavedPlacement();
        _refreshTimer.Start();
        _clockTimer.Start();
        await Task.WhenAll(
            RefreshAsync(showBusyState: true),
            RefreshTokenUsageAsync(showBusyState: true));
    }

    private void ApplySavedPlacement()
    {
        var settings = WidgetSettingsStore.Load();
        ApplyAppearance(
            NormalizeFinite(settings.Width, 230, MinWidth, MaxWidth),
            NormalizeFinite(settings.Height, 244, MinHeight, MaxHeight),
            NormalizeFinite(settings.Opacity, 1.0, 0.50, 1.0),
            settings.AccentColor,
            settings.BackgroundColor);
        ApplyDisplayOptions(
            DisplayOptionParser.ParseViewStyle(settings.ViewStyle),
            settings.ShowTokenUsage,
            DisplayOptionParser.ParseTokenScope(settings.TokenScope),
            DisplayOptionParser.ParseTokenPeriod(settings.TokenPeriod),
            settings.CustomStartDate,
            settings.CustomEndDate,
            resizeWindow: false);
        Topmost = settings.Topmost;
        TopmostMenuItem.IsChecked = Topmost;

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        var savedPositionIsVisible = settings.Left is { } left &&
                                     settings.Top is { } top &&
                                     double.IsFinite(left) &&
                                     double.IsFinite(top) &&
                                     left + 48 >= virtualLeft &&
                                     top + 48 >= virtualTop &&
                                     left <= virtualRight - 48 &&
                                     top <= virtualBottom - 48;

        if (savedPositionIsVisible)
        {
            Left = settings.Left!.Value;
            Top = settings.Top!.Value;
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 18;
            Top = workArea.Top + 18;
        }

        _displayOptionsReady = true;
        _settingsReady = true;
        SaveCurrentSettings();
    }

    private async Task RefreshAsync(bool showBusyState)
    {
        if (_refreshInProgress || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _refreshInProgress = true;
        RefreshButton.IsEnabled = false;
        if (showBusyState && _snapshot is null)
        {
            StatusText.Text = "正在连接 Codex…";
            StatusDot.Fill = CreateBrush("#FFF59E0B");
        }

        try
        {
            var snapshot = await _quotaService.GetSnapshotAsync(_lifetime.Token);
            _snapshot = snapshot;
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _refreshInProgress = false;
        }
    }

    private async Task RefreshTokenUsageAsync(bool showBusyState = false)
    {
        if (_lifetime.IsCancellationRequested || !_showTokenUsage)
        {
            return;
        }

        if (_tokenRefreshInProgress)
        {
            if (showBusyState)
            {
                ShowTokenUsageBusyState();
            }

            _tokenRefreshPending = true;
            _tokenRefreshPendingBusyState |= showBusyState;
            return;
        }

        _tokenRefreshInProgress = true;
        var queryVersion = _tokenQueryVersion;
        var query = new TokenUsageQuery(
            _tokenPeriod,
            _customStartDate is { } start ? DateOnly.FromDateTime(start) : null,
            _customEndDate is { } end ? DateOnly.FromDateTime(end) : null,
            IncludeAccumulated: _tokenScope == TokenUsageScope.Cumulative);
        if (showBusyState || _tokenUsageSnapshot is null)
        {
            ShowTokenUsageBusyState();
        }

        try
        {
            var snapshot = await _tokenUsageService.GetSnapshotAsync(query, _lifetime.Token);
            if (queryVersion == _tokenQueryVersion && _showTokenUsage)
            {
                _tokenUsageSnapshot = snapshot;
                ApplyTokenUsageSnapshot(snapshot);
            }
            else if (_showTokenUsage)
            {
                _tokenRefreshPending = true;
                _tokenRefreshPendingBusyState = true;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch
        {
            if (queryVersion == _tokenQueryVersion && _showTokenUsage)
            {
                TokenTotalText.Text = "—";
                EstimatedCostText.Text = "US$—";
                TokenEstimateNoteText.Text = "本地 Token 统计暂不可用";
                TokenUsagePanel.ToolTip = "未能读取本地会话计数；额度环仍可正常使用。";
            }
        }
        finally
        {
            _tokenRefreshInProgress = false;
            if (_tokenRefreshPending && !_lifetime.IsCancellationRequested && _showTokenUsage)
            {
                var pendingBusyState = _tokenRefreshPendingBusyState;
                _tokenRefreshPending = false;
                _tokenRefreshPendingBusyState = false;
                _ = RefreshTokenUsageAsync(pendingBusyState);
            }
            else if (!_showTokenUsage)
            {
                _tokenRefreshPending = false;
                _tokenRefreshPendingBusyState = false;
            }
        }
    }

    private void ShowTokenUsageBusyState()
    {
        TokenTotalText.Text = "…";
        EstimatedCostText.Text = "正在估算";
        TokenEstimateNoteText.Text = "正在读取本地 Codex Token 计数…";
        RegularInputTokenText.Text = "…";
        CachedInputTokenText.Text = "…";
        VisibleOutputTokenText.Text = "…";
        ReasoningOutputTokenText.Text = "…";
        SetTokenSegmentWeights(0, 0, 0, 0);
        TokenUsagePanel.ToolTip = "正在读取当前所选范围的本地 Token 计数。";
    }

    private void ApplySnapshot(CodexQuotaSnapshot snapshot)
    {
        PlanText.Text = string.IsNullOrWhiteSpace(snapshot.PlanType)
            ? "CODEX"
            : snapshot.PlanType.ToUpperInvariant();

        ApplyWindow(
            snapshot.Primary,
            PrimaryTitle,
            PrimaryPercentRun,
            PrimaryProgress,
            PrimaryResetText);
        ApplyWindow(
            snapshot.Secondary,
            SecondaryTitle,
            SecondaryPercentRun,
            SecondaryProgress,
            SecondaryResetText);
        ApplyRingWindow(
            snapshot.Primary,
            PrimaryRingTitle,
            PrimaryRingPercentRun,
            PrimaryRing,
            PrimaryRingUsedText);
        ApplyRingWindow(
            snapshot.Secondary,
            SecondaryRingTitle,
            SecondaryRingPercentRun,
            SecondaryRing,
            SecondaryRingUsedText);

        var isLive = string.Equals(snapshot.Source, "实时", StringComparison.Ordinal);
        StatusDot.Fill = isLive ? CreateBrush(_displayAccent) : CreateBrush("#FFF59E0B");
        StatusText.Text = $"{snapshot.Source} · {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss}";
        var sourceDescription = snapshot.Warning ??
            "通过 Codex app-server 读取，不访问 auth.json 或登录令牌。";
        StatusText.ToolTip = $"{sourceDescription}\n设置：{WidgetSettingsStore.SettingsPath}\n" +
                             $"最近保存：{(_lastSettingsSaveSucceeded == true ? "成功" : "尚未保存")}";

        var badges = new List<string>();
        if (snapshot.Credits is { Unlimited: true })
        {
            badges.Add("Credits ∞");
        }
        else if (snapshot.Credits is { HasCredits: true, Balance: { Length: > 0 } balance })
        {
            badges.Add($"Credits {balance}");
        }

        if (snapshot.ResetCreditsAvailable > 0)
        {
            badges.Add($"重置卡 {snapshot.ResetCreditsAvailable}");
        }

        if (snapshot.IndividualLimit is { } individualLimit)
        {
            badges.Add($"个人 {individualLimit.RemainingPercent}%");
        }

        BadgeText.Text = string.Join(" · ", badges);
        BadgeBorder.Visibility = badges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BadgeBorder.ToolTip = "额外 Credits、可用额度重置卡或个人额度";
        UpdateTimeLabels();
    }

    private void ApplyTokenUsageSnapshot(TokenUsageSnapshot snapshot)
    {
        var selected = _tokenScope == TokenUsageScope.Today
            ? snapshot.Today
            : snapshot.Accumulated;
        var totals = selected.Totals.Normalize();
        var cost = selected.Cost;

        TokenTotalText.Text = FormatTokenCount(totals.TotalTokens);
        RegularInputTokenText.Text = FormatTokenCount(totals.RegularInputTokens);
        CachedInputTokenText.Text = FormatTokenCount(totals.CachedInputTokens);
        VisibleOutputTokenText.Text = FormatTokenCount(totals.VisibleOutputTokens);
        ReasoningOutputTokenText.Text = FormatTokenCount(totals.ReasoningOutputTokens);

        SetTokenSegmentWeights(
            totals.RegularInputTokens,
            totals.CachedInputTokens,
            totals.VisibleOutputTokens,
            totals.ReasoningOutputTokens);

        if (cost.UnpricedTokens > 0 && cost.EstimatedUsd == 0)
        {
            EstimatedCostText.Text = "US$—";
        }
        else
        {
            var partialMarker = cost.IsPartialEstimate ? "*" : string.Empty;
            EstimatedCostText.Text = $"≈ US${FormatUsd(cost.EstimatedUsd)}{partialMarker}";
        }

        var rangeLabel = _tokenScope == TokenUsageScope.Today
            ? "当日"
            : FormatPeriodLabel(_tokenPeriod, selected.Start, selected.EndExclusive);
        if (cost.IsPartialEstimate)
        {
            TokenEstimateNoteText.Text =
                $"仅含已公布单价模型；另有 {FormatTokenCount(cost.UnpricedTokens)} Token 未定价";
        }
        else
        {
            TokenEstimateNoteText.Text = "Standard API 等价估算，不代表订阅实际扣款";
        }

        var unknownModels = cost.UnknownModels.Count > 0
            ? $"\n无公开单价：{string.Join("、", cost.UnknownModels.Take(4))}"
            : string.Empty;
        var cacheWriteNote = totals.CacheWriteInputTokens > 0
            ? $"\n普通输入中含缓存写入 {FormatTokenCount(totals.CacheWriteInputTokens)}，金额按对应写入价计算。"
            : string.Empty;
        TokenUsagePanel.ToolTip =
            $"{rangeLabel} · {selected.Start.ToLocalTime():yyyy-MM-dd HH:mm} 至 " +
            $"{selected.EndExclusive.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
            "从本机 Codex 会话中的 token_count 事件聚合；仅读取计数字段与模型名。\n" +
            $"金额按公开 Standard API Token 单价逐请求估算（核验于 {TokenCostEstimator.PricingVerifiedDate}），" +
            "不含工具调用费，也不代表订阅账单。" +
            unknownModels + cacheWriteNote +
            (string.IsNullOrWhiteSpace(snapshot.Warning) ? string.Empty : $"\n{snapshot.Warning}");
    }

    private void SetTokenSegmentWeights(long regular, long cached, long visible, long reasoning)
    {
        var total = Math.Max(0d, regular) + Math.Max(0d, cached) +
                    Math.Max(0d, visible) + Math.Max(0d, reasoning);
        if (total <= 0)
        {
            RegularInputSegmentColumn.Width = new GridLength(1, GridUnitType.Star);
            CachedInputSegmentColumn.Width = new GridLength(1, GridUnitType.Star);
            VisibleOutputSegmentColumn.Width = new GridLength(1, GridUnitType.Star);
            ReasoningOutputSegmentColumn.Width = new GridLength(1, GridUnitType.Star);
            TokenDistributionGrid.Opacity = 0.22;
            return;
        }

        TokenDistributionGrid.Opacity = 1;
        RegularInputSegmentColumn.Width = new GridLength(Math.Max(0, regular), GridUnitType.Star);
        CachedInputSegmentColumn.Width = new GridLength(Math.Max(0, cached), GridUnitType.Star);
        VisibleOutputSegmentColumn.Width = new GridLength(Math.Max(0, visible), GridUnitType.Star);
        ReasoningOutputSegmentColumn.Width = new GridLength(Math.Max(0, reasoning), GridUnitType.Star);
    }

    private static string FormatTokenCount(long value)
    {
        var nonNegative = Math.Max(0, value);
        return nonNegative switch
        {
            >= 1_000_000_000 => $"{nonNegative / 1_000_000_000d:0.#}B",
            >= 1_000_000 => $"{nonNegative / 1_000_000d:0.#}M",
            >= 1_000 => $"{nonNegative / 1_000d:0.#}K",
            _ => nonNegative.ToString("N0")
        };
    }

    private static string FormatUsd(decimal value)
    {
        var nonNegative = Math.Max(0m, value);
        return nonNegative switch
        {
            < 0.01m when nonNegative > 0 => nonNegative.ToString("0.0000"),
            < 1m => nonNegative.ToString("0.000"),
            _ => nonNegative.ToString("0.00")
        };
    }

    private static string FormatPeriodLabel(
        TokenUsagePeriod period,
        DateTimeOffset start,
        DateTimeOffset endExclusive) => period switch
    {
        TokenUsagePeriod.SevenDays => "累计 7 天",
        TokenUsagePeriod.ThirtyDays => "累计 30 天",
        TokenUsagePeriod.NinetyDays => "累计 90 天",
        TokenUsagePeriod.All => "累计全部",
        TokenUsagePeriod.Custom =>
            $"{start.ToLocalTime():yyyy-MM-dd} 至 {endExclusive.AddTicks(-1).ToLocalTime():yyyy-MM-dd}",
        _ => "累计"
    };

    private void ApplyWindow(
        RateLimitWindowSnapshot? window,
        System.Windows.Controls.TextBlock title,
        System.Windows.Documents.Run percentRun,
        System.Windows.Controls.ProgressBar progress,
        System.Windows.Controls.TextBlock resetText)
    {
        if (window is null)
        {
            title.Text = "额度窗口";
            percentRun.Text = "--";
            progress.Value = 0;
            resetText.Text = "暂不可用";
            return;
        }

        title.Text = FormatWindowDuration(window.WindowDurationMinutes);
        percentRun.Text = window.RemainingPercent.ToString();
        progress.Value = window.RemainingPercent;
        var brush = GetQuotaBrush(window.RemainingPercent);
        percentRun.Foreground = brush;
        progress.Foreground = brush;
        resetText.ToolTip = window.ResetsAt is { } reset
            ? $"重置时间：{reset.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : null;
    }

    private void ApplyRingWindow(
        RateLimitWindowSnapshot? window,
        TextBlock title,
        System.Windows.Documents.Run percentRun,
        Controls.CircularProgressRing ring,
        TextBlock usedText)
    {
        if (window is null)
        {
            title.Text = "额度窗口";
            percentRun.Text = "--";
            ring.Value = 0;
            usedText.Text = "已用 --%";
            return;
        }

        title.Text = FormatWindowDuration(window.WindowDurationMinutes);
        percentRun.Text = window.RemainingPercent.ToString();
        percentRun.Foreground = GetQuotaBrush(window.RemainingPercent);
        ring.Value = window.RemainingPercent;
        ring.ProgressBrush = GetQuotaBrush(window.RemainingPercent);
        usedText.Text = $"已用 {window.UsedPercent}%";
        usedText.ToolTip = window.ResetsAt is { } reset
            ? $"重置时间：{reset.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : null;
    }

    private void UpdateTimeLabels()
    {
        if (_snapshot is null)
        {
            return;
        }

        PrimaryResetText.Text = FormatResetCountdown(_snapshot.Primary?.ResetsAt);
        SecondaryResetText.Text = FormatResetCountdown(_snapshot.Secondary?.ResetsAt);
        RingNextResetText.Text = FormatNextReset(_snapshot.Primary?.ResetsAt);
    }

    private static string FormatWindowDuration(long? minutes)
    {
        return minutes switch
        {
            300 => "5 小时",
            10_080 => "7 天",
            null => "额度窗口",
            >= 1_440 when minutes.Value % 1_440 == 0 => $"{minutes.Value / 1_440} 天",
            >= 60 when minutes.Value % 60 == 0 => $"{minutes.Value / 60} 小时",
            _ => $"{minutes} 分钟"
        };
    }

    private static string FormatResetCountdown(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return "重置时间未知";
        }

        var remaining = resetsAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "等待额度刷新";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays} 天 {remaining.Hours} 小时后重置";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分后重置";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} 分钟后重置";
    }

    private static string FormatNextReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return "下次重置时间未知";
        }

        var countdown = FormatResetCountdown(resetsAt);
        return countdown == "等待额度刷新"
            ? countdown
            : $"下次重置 {countdown.Replace("后重置", string.Empty, StringComparison.Ordinal)}";
    }

    private Brush GetQuotaBrush(int remainingPercent) =>
        remainingPercent switch
        {
            > 50 => CreateBrush(_displayAccent),
            > 20 => CreateBrush("#FFFBBF24"),
            _ => CreateBrush("#FFFB7185")
        };

    public void ApplyAppearance(
        double width,
        double height,
        double opacity,
        string? accentColor,
        string? backgroundColor)
    {
        Width = NormalizeFinite(width, 372, MinWidth, MaxWidth);
        Height = NormalizeFinite(height, 252, MinHeight, MaxHeight);
        Opacity = NormalizeFinite(opacity, 1.0, 0.50, 1.0);

        if (TryNormalizeColor(accentColor, out var normalizedAccent))
        {
            AccentColorHex = normalizedAccent;
            _accentColor = ParseNormalizedColor(normalizedAccent);
        }

        if (TryNormalizeColor(backgroundColor, out var normalizedBackground))
        {
            BackgroundColorHex = normalizedBackground;
            _backgroundColor = ParseNormalizedColor(normalizedBackground);
        }

        ApplyTheme();
        ScheduleSettingsSave();
    }

    public void ApplyDisplayOptions(
        WidgetViewStyle viewStyle,
        bool showTokenUsage,
        TokenUsageScope tokenScope,
        TokenUsagePeriod tokenPeriod,
        DateTime? customStartDate,
        DateTime? customEndDate,
        bool resizeWindow = true)
    {
        var oldDesignHeight = DesignSurface.Height;
        viewStyle = Enum.IsDefined(viewStyle) ? viewStyle : WidgetViewStyle.Ring;
        tokenScope = Enum.IsDefined(tokenScope) ? tokenScope : TokenUsageScope.Today;
        tokenPeriod = Enum.IsDefined(tokenPeriod) ? tokenPeriod : TokenUsagePeriod.ThirtyDays;
        var today = DateTime.Today;
        var normalizedEnd = customEndDate?.Date;
        if (normalizedEnd is { } end && end > today)
        {
            normalizedEnd = today;
        }

        var normalizedStart = customStartDate?.Date;
        if (normalizedStart is not null && normalizedEnd is not null && normalizedStart > normalizedEnd)
        {
            normalizedStart = normalizedEnd;
        }

        if (tokenPeriod == TokenUsagePeriod.Custom &&
            (normalizedStart is null || normalizedEnd is null))
        {
            normalizedEnd = today;
            normalizedStart = today.AddDays(-29);
        }

        var visibilityChanged = _showTokenUsage != showTokenUsage;
        var scopeChanged = _tokenScope != tokenScope;
        var queryChanged = _tokenPeriod != tokenPeriod ||
                           _customStartDate?.Date != normalizedStart ||
                           _customEndDate?.Date != normalizedEnd;
        var tokenSemanticsChanged = visibilityChanged || scopeChanged || queryChanged;
        if (tokenSemanticsChanged)
        {
            unchecked
            {
                _tokenQueryVersion++;
            }
        }

        _viewStyle = viewStyle;
        _showTokenUsage = showTokenUsage;
        _tokenScope = tokenScope;
        _tokenPeriod = tokenPeriod;
        _customStartDate = normalizedStart;
        _customEndDate = normalizedEnd;

        CardQuotaView.Visibility = viewStyle == WidgetViewStyle.Card
            ? Visibility.Visible
            : Visibility.Collapsed;
        RingQuotaView.Visibility = viewStyle == WidgetViewStyle.Ring
            ? Visibility.Visible
            : Visibility.Collapsed;
        StyleSubtitleText.Text = viewStyle == WidgetViewStyle.Ring ? "环形视图" : "卡片视图";
        QuotaContentRow.Height = new GridLength(viewStyle == WidgetViewStyle.Ring ? 208 : 128);

        TokenUsagePanel.Visibility = showTokenUsage ? Visibility.Visible : Visibility.Collapsed;
        TokenTopGapRow.Height = new GridLength(showTokenUsage ? 8 : 0);
        TokenContentRow.Height = new GridLength(showTokenUsage ? 142 : 0);
        ShowTokenMenuItem.IsChecked = showTokenUsage;

        var newDesignHeight = GetDesignHeight(viewStyle, showTokenUsage);
        DesignSurface.Height = newDesignHeight;
        var wasSynchronizing = _synchronizingDisplayOptions;
        _synchronizingDisplayOptions = true;
        SelectPeriodItem(CumulativePeriodComboBox, tokenPeriod);
        _synchronizingDisplayOptions = wasSynchronizing;
        ApplyTokenScopeVisualState();

        if (resizeWindow && IsLoaded && oldDesignHeight > 0 && Math.Abs(oldDesignHeight - newDesignHeight) > 0.1)
        {
            var actualWidth = ActualWidth > 0 ? ActualWidth : Width;
            var actualHeight = ActualHeight > 0 ? ActualHeight : Height;
            var scale = Math.Min(actualWidth / Math.Max(1, DesignSurface.Width), actualHeight / oldDesignHeight);
            Height = Math.Clamp(actualHeight + (newDesignHeight - oldDesignHeight) * scale, MinHeight, MaxHeight);
        }

        ApplyTheme();
        if (_tokenUsageSnapshot is not null && !tokenSemanticsChanged)
        {
            ApplyTokenUsageSnapshot(_tokenUsageSnapshot);
        }

        if (_displayOptionsReady && showTokenUsage &&
            (tokenSemanticsChanged || _tokenUsageSnapshot is null))
        {
            _ = RefreshTokenUsageAsync(showBusyState: true);
        }

        ScheduleSettingsSave();
    }

    private static double GetDesignHeight(WidgetViewStyle viewStyle, bool showTokenUsage) =>
        (viewStyle, showTokenUsage) switch
        {
            (WidgetViewStyle.Ring, true) => 488,
            (WidgetViewStyle.Ring, false) => 338,
            (WidgetViewStyle.Card, true) => 408,
            _ => 258
        };

    private static void SelectPeriodItem(ComboBox comboBox, TokenUsagePeriod period)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (DisplayOptionParser.ParseTokenPeriod(item.Tag?.ToString()) == period)
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void ApplyTokenScopeVisualState()
    {
        var todaySelected = _tokenScope == TokenUsageScope.Today;
        TodayScopeButton.Opacity = todaySelected ? 1 : 0.62;
        CumulativePeriodComboBox.Opacity = todaySelected ? 0.72 : 1;
        TodayScopeButton.BorderBrush = todaySelected
            ? CreateBrush(_displayAccent)
            : CreateBrush("#FF454B59");
        TodayScopeButton.Foreground = todaySelected
            ? CreateBrush(_displayAccent)
            : (Brush)Resources["TextSecondary"];
    }

    public static bool TryNormalizeColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.StartsWith('#'))
        {
            candidate = candidate[1..];
        }

        if (candidate.Length != 6 || candidate.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        normalized = $"#{candidate.ToUpperInvariant()}";
        return true;
    }

    private void ApplyTheme()
    {
        var lightBackground = GetLuminance(_backgroundColor) > 0.55;
        var primaryText = lightBackground
            ? Color.FromRgb(0x11, 0x18, 0x27)
            : Color.FromRgb(0xF5, 0xF7, 0xFA);
        var secondaryText = lightBackground
            ? Color.FromRgb(0x4B, 0x55, 0x63)
            : Color.FromRgb(0x98, 0xA2, 0xB3);
        var contrastTarget = lightBackground ? Colors.Black : Colors.White;
        var cardColor = Blend(_backgroundColor, contrastTarget, lightBackground ? 0.055 : 0.065);
        var borderColor = Blend(_backgroundColor, contrastTarget, lightBackground ? 0.16 : 0.15);
        var accentContrast = Math.Abs(GetLuminance(_accentColor) - GetLuminance(cardColor));
        _displayAccent = accentContrast < 0.28
            ? Blend(_accentColor, lightBackground ? Colors.Black : Colors.White, 0.38)
            : _accentColor;

        Resources["TextPrimary"] = CreateBrush(primaryText);
        Resources["TextSecondary"] = CreateBrush(secondaryText);
        Resources["PanelBorder"] = CreateBrush(borderColor);
        Resources["RingTrack"] = CreateBrush(Blend(cardColor, contrastTarget, lightBackground ? 0.15 : 0.13));

        WindowChrome.Background = CreateBrush(Color.FromArgb(
            0xF5,
            _backgroundColor.R,
            _backgroundColor.G,
            _backgroundColor.B));
        WindowChrome.BorderBrush = CreateBrush(borderColor);
        PrimaryCard.Background = CreateBrush(cardColor);
        SecondaryCard.Background = CreateBrush(cardColor);
        PrimaryCard.BorderBrush = CreateBrush(borderColor);
        SecondaryCard.BorderBrush = CreateBrush(borderColor);
        TokenUsagePanel.Background = CreateBrush(Color.FromArgb(
            0xC8,
            cardColor.R,
            cardColor.G,
            cardColor.B));
        TokenUsagePanel.BorderBrush = CreateBrush(borderColor);
        PrimaryProgress.Background = CreateBrush(Blend(cardColor, contrastTarget, lightBackground ? 0.14 : 0.12));
        SecondaryProgress.Background = PrimaryProgress.Background;
        PrimaryRing.TrackBrush = (Brush)Resources["RingTrack"];
        SecondaryRing.TrackBrush = (Brush)Resources["RingTrack"];

        LogoBorder.Background = CreateBrush(_displayAccent);
        LogoText.Foreground = CreateBrush(GetLuminance(_displayAccent) > 0.55 ? Colors.Black : Colors.White);
        PlanBorder.Background = CreateBrush(Blend(_backgroundColor, contrastTarget, lightBackground ? 0.09 : 0.08));
        PlanText.Foreground = CreateBrush(_displayAccent);
        AppearanceButton.Foreground = CreateBrush(secondaryText);
        RefreshButton.Foreground = CreateBrush(secondaryText);
        CloseButton.Foreground = CreateBrush(secondaryText);
        PrimaryResetText.Foreground = CreateBrush(secondaryText);
        SecondaryResetText.Foreground = CreateBrush(secondaryText);
        RingNextResetText.Foreground = CreateBrush(secondaryText);
        HideTokenUsageButton.Foreground = CreateBrush(secondaryText);
        BadgeBorder.Background = CreateBrush(Blend(_backgroundColor, _displayAccent, 0.20));
        BadgeText.Foreground = CreateBrush(_displayAccent);
        ResizeHint.Stroke = CreateBrush(Blend(secondaryText, _backgroundColor, 0.30));

        if (_snapshot is not null)
        {
            ApplyWindow(
                _snapshot.Primary,
                PrimaryTitle,
                PrimaryPercentRun,
                PrimaryProgress,
                PrimaryResetText);
            ApplyWindow(
                _snapshot.Secondary,
                SecondaryTitle,
                SecondaryPercentRun,
                SecondaryProgress,
                SecondaryResetText);
            ApplyRingWindow(
                _snapshot.Primary,
                PrimaryRingTitle,
                PrimaryRingPercentRun,
                PrimaryRing,
                PrimaryRingUsedText);
            ApplyRingWindow(
                _snapshot.Secondary,
                SecondaryRingTitle,
                SecondaryRingPercentRun,
                SecondaryRing,
                SecondaryRingUsedText);
            StatusDot.Fill = string.Equals(_snapshot.Source, "实时", StringComparison.Ordinal)
                ? CreateBrush(_displayAccent)
                : CreateBrush("#FFF59E0B");
        }

        ApplyTokenScopeVisualState();
    }

    private static double NormalizeFinite(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static Color ParseNormalizedColor(string value) =>
        Color.FromRgb(
            Convert.ToByte(value.Substring(1, 2), 16),
            Convert.ToByte(value.Substring(3, 2), 16),
            Convert.ToByte(value.Substring(5, 2), 16));

    private static double GetLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) +
               0.7152 * Linearize(color.G) +
               0.0722 * Linearize(color.B);
    }

    private static Color Blend(Color first, Color second, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(first.R + (second.R - first.R) * amount),
            (byte)Math.Round(first.G + (second.G - first.G) * amount),
            (byte)Math.Round(first.B + (second.B - first.B) * amount));
    }

    private static SolidColorBrush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void ShowError(string message)
    {
        StatusDot.Fill = CreateBrush("#FFFB7185");
        StatusText.Text = "额度暂不可用 · 点击刷新";
        StatusText.ToolTip = message;
        PrimaryResetText.Text = _snapshot is null ? "请确认 Codex 已登录" : PrimaryResetText.Text;
        SecondaryResetText.Text = _snapshot is null ? "右键可打开 Usage 面板" : SecondaryResetText.Text;
    }

    private void QuotaService_RateLimitsUpdated(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPushRefresh < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastPushRefresh = now;
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Task.Delay(750, _lifetime.Token);
                await RefreshAsync(showBusyState: false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Window is closing.
            }
        }, DispatcherPriority.Background);
    }

    private void DragRegion_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button may have been released before WPF started the drag.
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string direction })
        {
            return;
        }

        var currentWidth = ActualWidth;
        var currentHeight = ActualHeight;

        if (direction.Contains("Left", StringComparison.Ordinal))
        {
            var right = Left + currentWidth;
            var newWidth = Math.Clamp(currentWidth - e.HorizontalChange, MinWidth, MaxWidth);
            Left = right - newWidth;
            Width = newWidth;
        }
        else if (direction.Contains("Right", StringComparison.Ordinal))
        {
            Width = Math.Clamp(currentWidth + e.HorizontalChange, MinWidth, MaxWidth);
        }

        if (direction.Contains("Top", StringComparison.Ordinal))
        {
            var bottom = Top + currentHeight;
            var newHeight = Math.Clamp(currentHeight - e.VerticalChange, MinHeight, MaxHeight);
            Top = bottom - newHeight;
            Height = newHeight;
        }
        else if (direction.Contains("Bottom", StringComparison.Ordinal))
        {
            Height = Math.Clamp(currentHeight + e.VerticalChange, MinHeight, MaxHeight);
        }

        e.Handled = true;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await Task.WhenAll(
            RefreshAsync(showBusyState: false),
            RefreshTokenUsageAsync());

    private void TodayScopeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyDisplayOptions(
            _viewStyle,
            _showTokenUsage,
            TokenUsageScope.Today,
            _tokenPeriod,
            _customStartDate,
            _customEndDate,
            resizeWindow: false);
    }

    private void CumulativePeriodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingDisplayOptions || !_displayOptionsReady ||
            CumulativePeriodComboBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        var period = DisplayOptionParser.ParseTokenPeriod(selected.Tag?.ToString());
        var start = _customStartDate;
        var end = _customEndDate;
        if (period == TokenUsagePeriod.Custom && (start is null || end is null))
        {
            end = DateTime.Today;
            start = end.Value.AddDays(-29);
        }

        ApplyDisplayOptions(
            _viewStyle,
            _showTokenUsage,
            TokenUsageScope.Cumulative,
            period,
            start,
            end,
            resizeWindow: false);
    }

    private void CumulativePeriodComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (_synchronizingDisplayOptions || !_displayOptionsReady ||
            _tokenScope == TokenUsageScope.Cumulative ||
            CumulativePeriodComboBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        var period = DisplayOptionParser.ParseTokenPeriod(selected.Tag?.ToString());
        ApplyDisplayOptions(
            _viewStyle,
            _showTokenUsage,
            TokenUsageScope.Cumulative,
            period,
            _customStartDate,
            _customEndDate,
            resizeWindow: false);
    }

    private void HideTokenUsageButton_Click(object sender, RoutedEventArgs e) =>
        ApplyDisplayOptions(
            _viewStyle,
            showTokenUsage: false,
            _tokenScope,
            _tokenPeriod,
            _customStartDate,
            _customEndDate);

    private void ShowTokenMenuItem_Click(object sender, RoutedEventArgs e) =>
        ApplyDisplayOptions(
            _viewStyle,
            ShowTokenMenuItem.IsChecked,
            _tokenScope,
            _tokenPeriod,
            _customStartDate,
            _customEndDate);

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (_appearanceWindow is not null)
        {
            _appearanceWindow.Activate();
            return;
        }

        _appearanceWindow = new AppearanceWindow(this);
        _appearanceWindow.Show();
    }

    public void NotifyAppearanceWindowClosed(AppearanceWindow window)
    {
        if (ReferenceEquals(_appearanceWindow, window))
        {
            _appearanceWindow = null;
            SaveCurrentSettings();
        }
    }

    private void OpenUsage_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://chatgpt.com/codex/settings/usage",
            UseShellExecute = true
        });
    }

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostMenuItem.IsChecked;
        if (_appearanceWindow is not null)
        {
            _appearanceWindow.Topmost = Topmost;
        }

        ScheduleSettingsSave();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _refreshTimer.Stop();
        _clockTimer.Stop();
        _settingsSaveTimer.Stop();
        _lifetime.Cancel();
        SaveCurrentSettings();
    }

    private void SaveCurrentSettings()
    {
        if (!_settingsReady)
        {
            return;
        }

        _lastSettingsSaveSucceeded = WidgetSettingsStore.Save(new WidgetSettings
        {
            Left = Left,
            Top = Top,
            Topmost = Topmost,
            Width = ActualWidth > 0 ? ActualWidth : Width,
            Height = ActualHeight > 0 ? ActualHeight : Height,
            Opacity = Opacity,
            AccentColor = AccentColorHex,
            BackgroundColor = BackgroundColorHex,
            ViewStyle = _viewStyle.ToString(),
            ShowTokenUsage = _showTokenUsage,
            TokenScope = _tokenScope.ToString(),
            TokenPeriod = _tokenPeriod.ToSettingValue(),
            CustomStartDate = _customStartDate,
            CustomEndDate = _customEndDate
        });
    }

    private void ScheduleSettingsSave()
    {
        if (!_settingsReady)
        {
            return;
        }

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _quotaService.RateLimitsUpdated -= QuotaService_RateLimitsUpdated;
        try
        {
            _quotaService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // The child process is best-effort cleaned up while the app exits.
        }

        _lifetime.Dispose();
    }
}
