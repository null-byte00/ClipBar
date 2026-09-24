using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ClipBar.Core;

namespace ClipBar.UI.Overlay;

public partial class ScreenshotViewer : UserControl
{
    List<ClipInfo> _shots = [];
    int _index;

    public event Action? Emptied;
    public event Action<string>? Shown;

    public ScreenshotViewer() => InitializeComponent();

    public void Show(string path)
    {
        _shots = (AppServices.Library?.Items.Where(c => c.IsScreenshot) ?? []).ToList();
        _index = _shots.FindIndex(c => string.Equals(c.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (_index < 0) { _shots.Insert(0, new ClipInfo { FilePath = path, IsScreenshot = true, CreatedAt = File.GetLastWriteTime(path) }); _index = 0; }
        Display();
        Dispatcher.BeginInvoke(() => Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    ClipInfo? Current => _index >= 0 && _index < _shots.Count ? _shots[_index] : null;

    void Display()
    {
        var c = Current;
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _shots.Count - 1;
        CounterText.Text = _shots.Count > 0 ? $"{_index + 1} / {_shots.Count}" : "";
        if (c is null) return;

        NameText.Text = c.Title;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(c.FilePath, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 2560;
            bmp.EndInit();
            bmp.Freeze();
            Picture.Source = bmp;
            ErrorText.Visibility = Visibility.Collapsed;
            var size = new FileInfo(c.FilePath).Length;
            var dims = GetPixelSize(c.FilePath);
            MetaText.Text = $"{c.CreatedAt:d MMMM yyyy, HH:mm}  ·  {dims}  ·  {ClipBar.Library.ClipFormat.Size(size)}";
        }
        catch (Exception ex)
        {
            Picture.Source = null;
            ErrorText.Text = "Не удалось открыть: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            MetaText.Text = "";
        }
        Shown?.Invoke(c.FilePath);
    }

    static string GetPixelSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return $"{frame.PixelWidth} × {frame.PixelHeight}";
        }
        catch { return ""; }
    }

    void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);
    void Next_Click(object sender, RoutedEventArgs e) => Step(+1);

    void Step(int d)
    {
        var i = _index + d;
        if (i < 0 || i >= _shots.Count) return;
        _index = i;
        Display();
    }

    void Viewer_KeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Left: Step(-1); break;
            case Key.Right: Step(+1); break;
            case Key.Delete: Delete_Click(this, new RoutedEventArgs()); break;
            case Key.C when ctrl: Copy_Click(this, new RoutedEventArgs()); break;
            default: return;
        }
        e.Handled = true;
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Picture.Source is not BitmapSource || Current is not { } c) return;
        try
        {
            var full = new BitmapImage();
            full.BeginInit();
            full.UriSource = new Uri(c.FilePath);
            full.CacheOption = BitmapCacheOption.OnLoad;
            full.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            full.EndInit();
            full.Freeze();
            Clipboard.SetImage(full);
            AppServices.Notifier?.Show("Скопировано в буфер обмена", $"{full.PixelWidth} × {full.PixelHeight} — вставь через Ctrl+V", NotifyKind.Success);
        }
        catch (Exception ex) { AppServices.Notifier?.Show("Не удалось скопировать", ex.Message, NotifyKind.Error); }
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } c) return;
        AppServices.Shell?.HideOverlay();
        AppServices.Library?.ShowInExplorer(c);
    }

    void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } c) return;
        try
        {
            AppServices.Shell?.HideOverlay();
            Process.Start(new ProcessStartInfo(c.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { AppServices.Notifier?.Show("Не удалось открыть", ex.Message, NotifyKind.Error); }
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } c || AppServices.Library is not { } lib) return;
        try
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Удалить скриншот?",
                Content = new TextBlock { Text = $"«{c.Title}» будет перемещён в корзину.", TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
                PrimaryButtonText = "Удалить",
                PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
                CloseButtonText = "Отмена",
            };
            if (Window.GetWindow(this) is { } owner) { box.Owner = owner; box.Topmost = owner.Topmost; }
            if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            Picture.Source = null;
            await lib.DeleteAsync(c);
            _shots.RemoveAt(_index);
            AppServices.Notifier?.Show("Скриншот удалён", "Файл перемещён в корзину", NotifyKind.Info);
            if (_shots.Count == 0) { Emptied?.Invoke(); return; }
            if (_index >= _shots.Count) _index = _shots.Count - 1;
            Display();
            Focus();
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot delete failed", ex);
            AppServices.Notifier?.Show("Не удалось удалить", ex.Message, NotifyKind.Error);
        }
    }
}
