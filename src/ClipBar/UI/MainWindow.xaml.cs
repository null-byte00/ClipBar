using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using ClipBar.Core;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace ClipBar.UI;

public partial class MainWindow : FluentWindow
{
    public PageProvider Pages { get; } = new();
    bool _navigatedOnce, _hiddenOnce;

    public MainWindow()
    {
        InitializeComponent();
        Nav.SetPageProviderService(Pages);
        Loaded += (_, _) =>
        {
            if (_navigatedOnce) return;
            _navigatedOnce = true;
            NavigateTo("home");
        };
    }

    public void NavigateTo(string tag)
    {
        if (!IsLoaded) { Loaded += (_, _) => Nav.Navigate(tag); return; }
        if (!Nav.Navigate(tag)) Log.Warn($"Navigate('{tag}') failed");
    }

    public void ShowAndActivate()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        var wasTopmost = Topmost;
        Topmost = true;
        Topmost = wasTopmost;
        Activate();
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.Current.IsExiting)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        Hide();
        if (_hiddenOnce) return;
        _hiddenOnce = true;
        AppServices.Notifier?.Show("ClipBar свёрнут в трей",
            $"Откат продолжает работать · {Actions.HotkeyText(HotkeyAction.SaveReplay)} — сохранить",
            NotifyKind.Info);
    }
}

public sealed class PageProvider : INavigationViewPageProvider
{
    readonly Dictionary<Type, object> _pages = new();

    public object? GetPage(Type pageType)
    {
        if (!_pages.TryGetValue(pageType, out var page))
        {
            page = Activator.CreateInstance(pageType) ?? throw new InvalidOperationException($"Cannot create {pageType}");
            _pages[pageType] = page;
        }
        return page;
    }

    public T Get<T>() where T : class => (T)GetPage(typeof(T))!;
}
