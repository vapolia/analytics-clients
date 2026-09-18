using System.Globalization;

namespace Vapolia.Analytics.Client;

/// <summary>
/// The app's own key/value store, straight from the platform: <c>SharedPreferences</c> on Android,
/// <c>NSUserDefaults</c> on iOS. Used rather than MAUI Essentials so the package pulls in no MAUI
/// dependency and builds with the android/ios workloads alone.
/// </summary>
static class Preferences
{
#if ANDROID
    static Android.Content.ISharedPreferences Store =>
        Android.App.Application.Context.GetSharedPreferences("vapolia.analytics", Android.Content.FileCreationMode.Private)!;

    public static string? Get(string key) => Store.GetString(key, null);

    public static void Set(string key, string value)
    {
        using var editor = Store.Edit()!;
        editor.PutString(key, value);
        editor.Apply();
    }

    public static void Remove(string key)
    {
        using var editor = Store.Edit()!;
        editor.Remove(key);
        editor.Apply();
    }
#elif IOS || MACCATALYST
    static Foundation.NSUserDefaults Store => Foundation.NSUserDefaults.StandardUserDefaults;

    public static string? Get(string key) => Store.StringForKey(key);

    public static void Set(string key, string value) => Store.SetString(value, key);

    public static void Remove(string key) => Store.RemoveObject(key);
#else
    // Desktop (Windows, and anything else this package is compiled for): one small file in the
    // per-user application data. An in-memory store would reissue the id at every launch, which is
    // the one thing an installation id may not do.
    static readonly Lock FileGate = new();
    static Dictionary<string, string>? cache;

    static string Path =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "vapolia-analytics-identity.json");

    public static string? Get(string key)
    {
        lock (FileGate)
            return Load().GetValueOrDefault(key);
    }

    public static void Set(string key, string value)
    {
        lock (FileGate)
        {
            Load()[key] = value;
            Save();
        }
    }

    public static void Remove(string key)
    {
        lock (FileGate)
        {
            Load().Remove(key);
            Save();
        }
    }

    static Dictionary<string, string> Load()
    {
        if (cache is not null)
            return cache;

        try
        {
            cache = File.Exists(Path)
                ? System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Path), AnalyticsJsonContext.Default.DictionaryStringString)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // An unreadable store is an empty one: measurement never fails an app.
        }

        return cache ??= new(StringComparer.Ordinal);
    }

    static void Save()
    {
        try
        {
            File.WriteAllText(Path, System.Text.Json.JsonSerializer.Serialize(cache!, AnalyticsJsonContext.Default.DictionaryStringString));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
#endif
}

/// <summary>
/// The device context, read from the device itself: the region setting, and nothing else. No
/// hardware id, no IDFA/GAID, no geolocation. The platform and the build are the build token's
/// business, not the device's.
/// </summary>
static class DeviceProbe
{
    public static Device Detect() => new() { Country = CurrentRegion() };

    /// <summary>
    /// The device's region setting, not its language: a phone set to French in Belgium is BE, and
    /// reading the culture would call it FR. The culture is only the fallback, for the platforms that
    /// leave the region unset.
    /// </summary>
    static string? CurrentRegion()
    {
        try
        {
            return RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch (Exception e) when (e is ArgumentException or PlatformNotSupportedException)
        {
            return RegionOf(CultureInfo.CurrentUICulture);
        }
    }

    static string? RegionOf(CultureInfo culture)
    {
        try
        {
            return culture.IsNeutralCulture ? null : new RegionInfo(culture.Name).TwoLetterISORegionName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
