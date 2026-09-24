using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClipBar.UI.Pages;

public sealed class SettingsRowHeader : StackPanel
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsRowHeader),
        new PropertyMetadata("", (d, e) => ((SettingsRowHeader)d)._title.Text = e.NewValue as string ?? ""));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsRowHeader),
        new PropertyMetadata("", (d, e) => ((SettingsRowHeader)d).SetLine(((SettingsRowHeader)d)._desc, e.NewValue as string)));

    public static readonly DependencyProperty WarningProperty = DependencyProperty.Register(
        nameof(Warning), typeof(string), typeof(SettingsRowHeader),
        new PropertyMetadata("", (d, e) => ((SettingsRowHeader)d).SetLine(((SettingsRowHeader)d)._warn, e.NewValue as string)));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Warning { get => (string)GetValue(WarningProperty); set => SetValue(WarningProperty, value); }

    readonly TextBlock _title, _desc, _warn;

    public SettingsRowHeader()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(0, 0, 12, 0);
        _title = new TextBlock
        {
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("CB.TextPrimary", Colors.White),
        };
        _desc = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = Brush("CB.TextSecondary", Color.FromRgb(0xC5, 0xC5, 0xC5)),
            Visibility = Visibility.Collapsed,
        };
        _warn = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brush("CB.Warning", Color.FromRgb(0xFC, 0xE1, 0x00)),
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_title);
        Children.Add(_desc);
        Children.Add(_warn);
    }

    void SetLine(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    static System.Windows.Media.Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? new SolidColorBrush(fallback);
}
