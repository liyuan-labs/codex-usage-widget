using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodexUsageWidget.Models;

namespace CodexUsageWidget;

public partial class AppearanceWindow : Window
{
    private readonly MainWindow _owner;
    private bool _synchronizing = true;

    public AppearanceWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
        Owner = owner;
        Topmost = owner.Topmost;
        ViewStyleComboBox.SelectionChanged += ViewStyleComboBox_SelectionChanged;
        ShowTokenUsageCheckBox.Checked += ShowTokenUsageCheckBox_Changed;
        ShowTokenUsageCheckBox.Unchecked += ShowTokenUsageCheckBox_Changed;
        TokenPeriodComboBox.SelectionChanged += TokenPeriodComboBox_SelectionChanged;
        CustomStartDatePicker.SelectedDateChanged += CustomDatePicker_SelectedDateChanged;
        CustomEndDatePicker.SelectedDateChanged += CustomDatePicker_SelectedDateChanged;
        SynchronizeFromOwner();
        _owner.SizeChanged += Owner_SizeChanged;
    }

    private void SynchronizeFromOwner()
    {
        _synchronizing = true;
        WidthSlider.Value = Math.Clamp(_owner.ActualWidth > 0 ? _owner.ActualWidth : _owner.Width, 160, 720);
        HeightSlider.Value = Math.Clamp(_owner.ActualHeight > 0 ? _owner.ActualHeight : _owner.Height, 120, 520);
        OpacitySlider.Value = Math.Clamp(_owner.Opacity * 100, 50, 100);
        AccentHexTextBox.Text = _owner.AccentColorHex;
        BackgroundHexTextBox.Text = _owner.BackgroundColorHex;
        SelectComboItem(ViewStyleComboBox, _owner.ViewStyle.ToString());
        ShowTokenUsageCheckBox.IsChecked = _owner.ShowTokenUsage;
        SelectComboItem(TokenPeriodComboBox, _owner.TokenPeriod.ToString());
        CustomStartDatePicker.SelectedDate = _owner.CustomStartDate;
        CustomEndDatePicker.SelectedDate = _owner.CustomEndDate;
        CustomDatePanel.Visibility = _owner.TokenPeriod == TokenUsagePeriod.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateValueLabels();
        _synchronizing = false;
    }

    private static void SelectComboItem(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void ViewStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizing || ViewStyleComboBox.SelectedItem is not ComboBoxItem selected ||
            !Enum.TryParse<WidgetViewStyle>(selected.Tag?.ToString(), true, out var viewStyle))
        {
            return;
        }

        _owner.ApplyDisplayOptions(
            viewStyle,
            ShowTokenUsageCheckBox.IsChecked == true,
            _owner.TokenScope,
            _owner.TokenPeriod,
            _owner.CustomStartDate,
            _owner.CustomEndDate);
    }

    private void ShowTokenUsageCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_synchronizing)
        {
            return;
        }

        _owner.ApplyDisplayOptions(
            _owner.ViewStyle,
            ShowTokenUsageCheckBox.IsChecked == true,
            _owner.TokenScope,
            _owner.TokenPeriod,
            _owner.CustomStartDate,
            _owner.CustomEndDate);
    }

    private void TokenPeriodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizing || TokenPeriodComboBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        var period = DisplayOptionParser.ParseTokenPeriod(selected.Tag?.ToString());
        CustomDatePanel.Visibility = period == TokenUsagePeriod.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;

        var start = CustomStartDatePicker.SelectedDate ?? _owner.CustomStartDate;
        var end = CustomEndDatePicker.SelectedDate ?? _owner.CustomEndDate;
        if (period == TokenUsagePeriod.Custom && (start is null || end is null))
        {
            end = DateTime.Today;
            start = end.Value.AddDays(-29);
            _synchronizing = true;
            CustomStartDatePicker.SelectedDate = start;
            CustomEndDatePicker.SelectedDate = end;
            _synchronizing = false;
        }

        _owner.ApplyDisplayOptions(
            _owner.ViewStyle,
            ShowTokenUsageCheckBox.IsChecked == true,
            _owner.TokenScope,
            period,
            start,
            end,
            resizeWindow: false);
    }

    private void CustomDatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_synchronizing || _owner.TokenPeriod != TokenUsagePeriod.Custom)
        {
            return;
        }

        var start = CustomStartDatePicker.SelectedDate;
        var end = CustomEndDatePicker.SelectedDate;
        if (start is null || end is null)
        {
            ValidationText.Text = "请选择完整的开始和结束日期。";
            return;
        }

        if (end.Value.Date > DateTime.Today)
        {
            ValidationText.Text = "结束日期不能晚于今天。";
            return;
        }

        if (start.Value.Date > end.Value.Date)
        {
            ValidationText.Text = "开始日期不能晚于结束日期。";
            return;
        }

        ValidationText.Text = string.Empty;
        _owner.ApplyDisplayOptions(
            _owner.ViewStyle,
            ShowTokenUsageCheckBox.IsChecked == true,
            _owner.TokenScope,
            TokenUsagePeriod.Custom,
            start,
            end,
            resizeWindow: false);
    }

    private void Owner_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_synchronizing)
        {
            return;
        }

        _synchronizing = true;
        WidthSlider.Value = Math.Clamp(e.NewSize.Width, 160, 720);
        HeightSlider.Value = Math.Clamp(e.NewSize.Height, 120, 520);
        UpdateValueLabels();
        _synchronizing = false;
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_synchronizing)
        {
            return;
        }

        UpdateValueLabels();
        _owner.ApplyAppearance(
            WidthSlider.Value,
            HeightSlider.Value,
            OpacitySlider.Value / 100,
            _owner.AccentColorHex,
            _owner.BackgroundColorHex);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_synchronizing)
        {
            return;
        }

        UpdateValueLabels();
        _owner.ApplyAppearance(
            WidthSlider.Value,
            HeightSlider.Value,
            OpacitySlider.Value / 100,
            _owner.AccentColorHex,
            _owner.BackgroundColorHex);
    }

    private void UpdateValueLabels()
    {
        if (WidthValueText is null || HeightValueText is null || OpacityValueText is null)
        {
            return;
        }

        WidthValueText.Text = $"{WidthSlider.Value:0} px";
        HeightValueText.Text = $"{HeightSlider.Value:0} px";
        OpacityValueText.Text = $"{OpacitySlider.Value:0}%";
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            ApplyColor(color, isAccent: true);
        }
    }

    private void BackgroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            ApplyColor(color, isAccent: false);
        }
    }

    private void ApplyHex_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target })
        {
            return;
        }

        var isAccent = string.Equals(target, "Accent", StringComparison.Ordinal);
        var input = isAccent ? AccentHexTextBox.Text : BackgroundHexTextBox.Text;
        ApplyColor(input, isAccent);
    }

    private void ApplyColor(string input, bool isAccent)
    {
        if (!MainWindow.TryNormalizeColor(input, out var normalized))
        {
            ValidationText.Text = "颜色格式无效，请输入 #RRGGBB，例如 #3B82F6。";
            return;
        }

        ValidationText.Text = string.Empty;
        if (isAccent)
        {
            AccentHexTextBox.Text = normalized;
        }
        else
        {
            BackgroundHexTextBox.Text = normalized;
        }

        _owner.ApplyAppearance(
            WidthSlider.Value,
            HeightSlider.Value,
            OpacitySlider.Value / 100,
            isAccent ? normalized : _owner.AccentColorHex,
            isAccent ? _owner.BackgroundColorHex : normalized);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _owner.ApplyAppearance(230, 244, 1.0, "#10A37F", "#191C23");
        _owner.ApplyDisplayOptions(
            WidgetViewStyle.Ring,
            showTokenUsage: true,
            TokenUsageScope.Today,
            TokenUsagePeriod.ThirtyDays,
            customStartDate: null,
            customEndDate: null,
            resizeWindow: false);
        SynchronizeFromOwner();
        ValidationText.Text = string.Empty;
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _owner.SizeChanged -= Owner_SizeChanged;
        _owner.NotifyAppearanceWindowClosed(this);
    }
}
