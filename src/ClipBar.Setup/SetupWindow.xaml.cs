using System.IO;
using System.Windows;
using System.Windows.Media;

namespace ClipBar.Setup;

public partial class SetupWindow
{
    enum Stage { Options, Installing, Done }

    static bool AutoUpdate => Environment.GetCommandLineArgs().Contains("--update", StringComparer.OrdinalIgnoreCase);
    Stage _stage = Stage.Options;

    public SetupWindow()
    {
        InitializeComponent();
        VersionText.Text = Installer.Version;
        var existing = Installer.ExistingInstall();
        PathBox.Text = existing ?? Installer.DefaultDirectory;
        if (existing is not null)
        {
            PrimaryButton.Content = "Обновить";
            UpdateNote.Visibility = Visibility.Visible;
            UpdateNote.Text = $"ClipBar {Installer.ExistingVersion() ?? ""} уже установлен — он будет закрыт и обновлён. Клипы и настройки сохранятся.";
        }
        RefreshChecks();
        // Авто-обновление: ставим в найденную папку, а если ключа реестра нет — в папку по умолчанию,
        // чтобы --update не завис пустым окном без пользователя.
        if (AutoUpdate && Installer.HasPayload)
        {
            if (existing is null) PathBox.Text = Installer.DefaultDirectory;
            DesktopCheck.IsChecked = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ClipBar.lnk"));
            AutostartCheck.IsChecked = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("ClipBar") is not null;
            LaunchCheck.IsChecked = true;
            Loaded += (_, _) => Primary_Click(this, new RoutedEventArgs());
        }
        else _ = DetectOthersAsync();
        if (!Installer.HasPayload)
        {
            PrimaryButton.IsEnabled = false;
            UpdateNote.Visibility = Visibility.Visible;
            UpdateNote.Text = "Это сборка установщика без файлов программы. Собери её через build\\build-installer.ps1.";
        }
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Куда установить ClipBar", InitialDirectory = Path.GetDirectoryName(PathBox.Text) };
        if (dlg.ShowDialog(this) == true)
        {
            var dir = dlg.FolderName;
            if (!Path.GetFileName(dir.TrimEnd('\\')).Equals("ClipBar", StringComparison.OrdinalIgnoreCase)) dir = Path.Combine(dir, "ClipBar");
            PathBox.Text = dir;
        }
    }

    async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Done) { Close(); return; }
        if (_stage != Stage.Options) return;

        _stage = Stage.Installing;
        OptionsScroll.Visibility = Visibility.Collapsed;
        ProgressPage.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = CancelButton.IsEnabled = false;

        var options = new InstallOptions(PathBox.Text, DesktopCheck.IsChecked == true, AutostartCheck.IsChecked == true, LaunchCheck.IsChecked == true);
        var progress = new Progress<(double Fraction, string Status)>(p => { Progress.Value = p.Fraction; StatusText.Text = p.Status; });
        try
        {
            await Installer.InstallAsync(options, progress);
            var failed = new List<string>();
            foreach (var other in _others.Where(o => o.Remove))
            {
                StatusText.Text = $"Удаляю {other.Tool.Name}…";
                if (!await OtherCaptureTools.RemoveAsync(other.Tool)) failed.Add(other.Tool.Name);
            }
            if (failed.Count > 0) DoneNote.Text = "Не удалось удалить: " + string.Join(", ", failed) + ". Их можно удалить в «Параметры → Приложения».";
            _stage = Stage.Done;
            ProgressPage.Visibility = Visibility.Collapsed;
            DonePage.Visibility = Visibility.Visible;
            PrimaryButton.Content = "Готово";
            PrimaryButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            if (AutoUpdate) { await Task.Delay(1500); Close(); }
        }
        catch (Exception ex)
        {
            _stage = Stage.Options;
            ProgressPage.Visibility = Visibility.Collapsed;
            OptionsScroll.Visibility = Visibility.Visible;
            PrimaryButton.IsEnabled = CancelButton.IsEnabled = true;
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "Не удалось установить",
                Content = ex.Message,
                CloseButtonText = "OK",
            }.ShowDialogAsync();
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class OtherRow(OtherCaptureTools.Tool tool)
    {
        public OtherCaptureTools.Tool Tool { get; } = tool;
        public bool Remove { get; set; }
    }

    List<OtherRow> _others = [];

    async Task DetectOthersAsync()
    {
        try
        {
            var tools = await Task.Run(OtherCaptureTools.Detect);
            _others = tools.Select(t => new OtherRow(t)).ToList();
            OthersList.ItemsSource = _others;
            OthersPanel.Visibility = _others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    public sealed record CheckRow(string Title, string Detail, Brush Dot, Visibility FixVisibility, SystemCheck.Item Item);

    static readonly Brush Good = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    static readonly Brush Bad = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20));
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    void RefreshChecks()
    {
        CheckList.ItemsSource = SystemCheck.Run()
            .Select(i => new CheckRow(i.Title, i.Detail, i.Ok ? Good : Bad, !i.Ok && i.CanFix ? Visibility.Visible : Visibility.Collapsed, i))
            .ToList();
        if (!SystemCheck.CanInstall) PrimaryButton.IsEnabled = false;
    }

    async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CheckRow row } button) return;
        button.IsEnabled = false;
        StatusText.Text = "";
        var ok = row.Item.Title == "Воспроизведение видео" && await SystemCheck.InstallMediaFeaturePackAsync();
        RefreshChecks();
        if (!ok)
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "Не получилось доставить",
                Content = "Можно поставить вручную: Параметры → Приложения → Дополнительные компоненты → «Пакет функций мультимедиа». Запись и экспорт работают и без него.",
                CloseButtonText = "OK",
            }.ShowDialogAsync();
    }
}
