namespace Vapolia.Analytics.Client;

/// <summary>
/// When the app leaves the foreground and when it comes back, straight from the platform: MAUI's own
/// lifecycle events would pin this package to MAUI, which it deliberately is not. Subscribes once per
/// process, later calls are ignored.
///
/// Backgrounding is the only moment iOS guarantees the process still runs, so it is where the queue
/// has to leave; coming back is where <c>app_open</c> belongs.
/// </summary>
static class AppLifecycle
{
#if ANDROID
    static Callbacks? callbacks;

    public static void Subscribe(Action onForeground, Action onBackground)
    {
        if (callbacks is not null || Android.App.Application.Context is not Android.App.Application application)
            return;

        callbacks = new Callbacks(onForeground, onBackground);
        application.RegisterActivityLifecycleCallbacks(callbacks);
    }

    /// <summary>
    /// Counts started activities rather than trusting one of them: a rotation stops an activity and
    /// starts another, and treating that as a background trip would emit an app_open per rotation.
    /// </summary>
    sealed class Callbacks(Action onForeground, Action onBackground)
        : Java.Lang.Object, Android.App.Application.IActivityLifecycleCallbacks
    {
        int started;

        public void OnActivityStarted(Android.App.Activity activity)
        {
            if (started++ == 0)
                onForeground();
        }

        public void OnActivityStopped(Android.App.Activity activity)
        {
            if (--started <= 0)
            {
                started = 0;
                onBackground();
            }
        }

        public void OnActivityCreated(Android.App.Activity activity, Android.OS.Bundle? savedInstanceState) { }
        public void OnActivityDestroyed(Android.App.Activity activity) { }
        public void OnActivityPaused(Android.App.Activity activity) { }
        public void OnActivityResumed(Android.App.Activity activity) { }
        public void OnActivitySaveInstanceState(Android.App.Activity activity, Android.OS.Bundle outState) { }
    }
#elif IOS || MACCATALYST
    static Foundation.NSObject? enteredBackground, willEnterForeground;

    public static void Subscribe(Action onForeground, Action onBackground)
    {
        if (enteredBackground is not null)
            return;

        var center = Foundation.NSNotificationCenter.DefaultCenter;
        enteredBackground = center.AddObserver(UIKit.UIApplication.DidEnterBackgroundNotification, _ => onBackground());
        // Not DidBecomeActive, which also fires after a phone call or a pulled-down notification centre.
        willEnterForeground = center.AddObserver(UIKit.UIApplication.WillEnterForegroundNotification, _ => onForeground());
    }
#else
    public static void Subscribe(Action onForeground, Action onBackground)
    {
        // A desktop app is never backgrounded the way a phone is
    }
#endif
}