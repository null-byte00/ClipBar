using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClipBar.Core;

namespace ClipBar.UI.Notifications;

public partial class ToastHostWindow : Window
{
    public const int MaxToasts = 3;
    const double EdgeGap = 12;

    public ToastHostWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Reposition();
        SystemParameters.StaticPropertyChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);
    }

    public int Count => Stack.Children.Count;

    public void Add(ToastItem toast)
    {
        while (Stack.Children.Count >= MaxToasts && Stack.Children[0] is ToastItem oldest)
        {
            Stack.Children.RemoveAt(0);
        }
        toast.Dismissed += (_, _) =>
        {
            Stack.Children.Remove(toast);
            if (Stack.Children.Count == 0) Hide();
        };
        Stack.Children.Add(toast);
        if (!IsVisible)
        {
            Reposition();
            Show();
        }
        Reposition();
    }

    void Reposition()
    {
        try
        {
            var area = SystemParameters.WorkArea;
            var w = ActualWidth > 0 ? ActualWidth : Width;
            var h = ActualHeight > 0 ? ActualHeight : Height;
            if (double.IsNaN(w) || double.IsNaN(h)) return;
            Left = area.Right - w - EdgeGap;
            Top = area.Bottom - h - EdgeGap;
        }
        catch (Exception ex) { Log.Warn($"Toast reposition failed: {ex.Message}"); }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Topmost = true;
    }

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
