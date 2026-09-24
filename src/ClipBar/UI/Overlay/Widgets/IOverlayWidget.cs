using Wpf.Ui.Controls;

namespace ClipBar.UI.Overlay.Widgets;

public interface IOverlayHost
{
    void HideOverlay();
    bool ExcludedFromCapture { get; }
}

public interface IOverlayWidget
{
    string Id { get; }
    string Title { get; }
    SymbolRegular Icon { get; }

    IOverlayHost? Host { get; set; }

    void Activate();
    void Deactivate();

    bool NeedsFastTick { get; }
    void FastTick();
    void SlowTick();
}
