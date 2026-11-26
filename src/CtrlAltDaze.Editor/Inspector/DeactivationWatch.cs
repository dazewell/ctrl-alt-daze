using Microsoft.UI.Xaml;

namespace CtrlAltDaze.Editor.Inspector;

/// <summary>
/// Ends a key recorder when the editor window loses activation: a chord Windows keeps (Alt+Tab, Win+D) moves the user to
/// another app without the box losing focus, and the engine's shortcuts stay paused while a box records (D-021, D-157).
/// </summary>
internal sealed class DeactivationWatch : IDisposable
{
    private readonly Window? _window;
    private readonly Action _stop;

    private DeactivationWatch(Window? window, Action stop)
    {
        _window = window;
        _stop = stop;
        if (_window is not null)
            _window.Activated += OnActivated;
    }

    public static DeactivationWatch Start(Action stop) => new((Application.Current as App)?.MainWindow, stop);

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            _stop();
    }

    public void Dispose()
    {
        if (_window is not null)
            _window.Activated -= OnActivated;
    }
}
