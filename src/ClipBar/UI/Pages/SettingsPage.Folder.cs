using System.IO;
using System.Windows;
using ClipBar.Core;

namespace ClipBar.UI.Pages;

public partial class SettingsPage
{
    void LoadFolder()
    {
        FolderHeader.Description = S.ClipsFolder;
        AppNameToggle.IsChecked = S.UseAppNameInFileName;
    }

    async Task LoadFreeSpaceAsync()
    {
        var folder = S.ClipsFolder;
        try
        {
            var text = await Task.Run(() =>
            {
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (string.IsNullOrEmpty(root)) return "Свободное место неизвестно";
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return $"Диск {root} недоступен";
                return $"Свободно {FormatBytes(drive.AvailableFreeSpace)} из {FormatBytes(drive.TotalSize)} на диске {root.TrimEnd('\\')}";
            });
            FolderFreeText.Text = text;
        }
        catch (Exception ex)
        {
            Log.Warn("Free space check failed: " + ex.Message);
            FolderFreeText.Text = "Свободное место неизвестно";
        }
    }

    void OnOpenFolderClick(object sender, RoutedEventArgs e) => OpenInExplorer(S.ClipsFolder);

    void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Папка для клипов и скриншотов",
                Multiselect = false,
            };
            if (Directory.Exists(S.ClipsFolder)) dlg.InitialDirectory = S.ClipsFolder;
            var owner = Window.GetWindow(this);
            var ok = owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            if (ok != true || string.IsNullOrWhiteSpace(dlg.FolderName)) return;

            var folder = Path.GetFullPath(dlg.FolderName);
            Directory.CreateDirectory(folder);
            if (string.Equals(folder, S.ClipsFolder, StringComparison.OrdinalIgnoreCase)) return;
            S.ClipsFolder = folder;
            Save();
            Guarded(LoadFolder);
            FolderFreeText.Text = "Считаю свободное место…";
            _ = LoadFreeSpaceAsync();
            if (AppServices.Library is { } lib)
                _ = lib.RefreshAsync().ContinueWith(t => Log.Error("Library refresh failed", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
            AppServices.Notifier?.Show("Папка клипов изменена", folder, NotifyKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Change folder failed", ex);
            AppServices.Notifier?.Show("Не удалось выбрать папку", ex.Message, NotifyKind.Error);
        }
    }

    void OnAppNameToggleClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.UseAppNameInFileName = AppNameToggle.IsChecked == true;
        Save();
    }
}
