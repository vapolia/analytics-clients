namespace Vapolia.Analytics.Client;

sealed class AppLifecycleEvents : IAppLifecycle
{
    public event Action? Foreground;
    public event Action? Background;

    public void RaiseForeground() => Foreground?.Invoke();
    public void RaiseBackground() => Background?.Invoke();
}