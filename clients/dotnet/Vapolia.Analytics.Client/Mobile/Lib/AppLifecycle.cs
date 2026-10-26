namespace Vapolia.Analytics.Client;

/// <summary>
/// When the app leaves the foreground and when it comes back, straight from the platform: MAUI's own
/// lifecycle events would pin this package to MAUI, which it deliberately is not. Subscribes once per
/// process, later calls are ignored.
///
/// Backgrounding is the only moment iOS guarantees the process still runs, so it is where the queue
/// has to leave. Coming to the foreground, the launch included, is where <c>app_open</c> belongs.
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
    /// Foreground is raised for the first activity of the process too, and not for a process started
    /// by a service. A client started while an activity is already visible raises it at once.
    /// </summary>
    sealed class Callbacks : Java.Lang.Object, Android.App.Application.IActivityLifecycleCallbacks
    {
        readonly Action onForeground, onBackground;
        int started;

        public Callbacks(Action onForeground, Action onBackground)
        {
            this.onForeground = onForeground;
            this.onBackground = onBackground;

            var state = new Android.App.ActivityManager.RunningAppProcessInfo();
            Android.App.ActivityManager.GetMyMemoryState(state);
            // Not ForegroundService, which is a process started by a service with no activity.
            if (state.Importance is Android.App.Importance.Foreground or Android.App.Importance.Visible)
            {
                started = 1;
                onForeground();
            }
        }

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
    static Foundation.NSObject? enteredBackground, willEnterForeground, didBecomeActive;
    static bool inForeground;

    /// <summary>
    /// Foreground is raised once per foreground period, on the first of WillEnterForeground and
    /// DidBecomeActive. A cold launch posts only DidBecomeActive, a background launch neither until
    /// the app is opened, and DidBecomeActive alone also follows a phone call or a pulled-down
    /// notification centre. A client started while the app is already in the foreground raises it at once.
    /// </summary>
    public static void Subscribe(Action onForeground, Action onBackground)
    {
        if (enteredBackground is not null)
            return;

        var center = Foundation.NSNotificationCenter.DefaultCenter;
        enteredBackground = center.AddObserver(UIKit.UIApplication.DidEnterBackgroundNotification, _ =>
        {
            inForeground = false;
            onBackground();
        });
        willEnterForeground = center.AddObserver(UIKit.UIApplication.WillEnterForegroundNotification, _ => Enter(onForeground));
        didBecomeActive = center.AddObserver(UIKit.UIApplication.DidBecomeActiveNotification, _ => Enter(onForeground));

        // The notifications are posted on the main thread, and ApplicationState is read there only.
        Foundation.NSRunLoop.Main.BeginInvokeOnMainThread(() =>
        {
            if (UIKit.UIApplication.SharedApplication.ApplicationState != UIKit.UIApplicationState.Background)
                Enter(onForeground);
        });
    }

    static void Enter(Action onForeground)
    {
        if (inForeground)
            return;
        inForeground = true;
        onForeground();
    }
#else
    public static void Subscribe(Action onForeground, Action onBackground)
    {
        // A desktop app is never backgrounded the way a phone is
    }
#endif
}