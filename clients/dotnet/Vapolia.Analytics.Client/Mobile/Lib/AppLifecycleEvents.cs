namespace Vapolia.Analytics.Client;

sealed class AppLifecycleEvents : IAppLifecycle
{
    readonly Lock gate = new();
    Action? foreground;
    bool pending;

    /// <summary>
    /// A foreground raised before anyone subscribed is handed to the first subscriber: the client
    /// starts when <see cref="IAnalytics"/> is first resolved, which can be after the launch.
    /// </summary>
    public event Action? Foreground
    {
        add
        {
            bool replay;
            lock (gate)
            {
                foreground += value;
                replay = pending;
                pending = false;
            }
            if (replay)
                value?.Invoke();
        }
        remove
        {
            lock (gate)
                foreground -= value;
        }
    }

    public event Action? Background;

    public void RaiseForeground()
    {
        Action? handlers;
        lock (gate)
        {
            handlers = foreground;
            pending = handlers is null;
        }
        handlers?.Invoke();
    }

    public void RaiseBackground()
    {
        lock (gate)
            pending = false;
        Background?.Invoke();
    }
}
