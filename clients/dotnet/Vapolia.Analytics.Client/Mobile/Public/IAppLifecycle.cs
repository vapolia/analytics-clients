namespace Vapolia.Analytics.Client;

/// <summary>
/// The app coming to the foreground, and leaving it. The client subscribes to the platform once
/// — to flush before the process is suspended — and an app that names its own <c>app_open</c> hangs
/// off the same subscription rather than writing its own, Android's rotation guard included.
/// </summary>
public interface IAppLifecycle
{
    /// <summary>
    /// The app comes to the foreground, the launch included. Not raised for a process launched in the
    /// background until the app is opened. A launch raised before the first subscriber is handed to it.
    /// </summary>
    event Action? Foreground;

    /// <summary>The app is leaving the foreground. On iOS, the last moment the process is guaranteed.</summary>
    event Action? Background;
}