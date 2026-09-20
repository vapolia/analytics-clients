using System.Globalization;

namespace Vapolia.Analytics.Client;

/// <summary>
/// The installation id and the device context of a phone. The id lives in the app's own preferences
/// — <c>SharedPreferences</c> on Android, <c>NSUserDefaults</c> on iOS — and deliberately not in the
/// keychain, which survives the app being deleted and would make the id outlive the installation it
/// names. Renewing it is the editor's obligation, not the collector's.
/// </summary>
public sealed class MobileInstallIdentityProvider : IInstallContext
{
    const string KeyId = "vapolia.analytics.installId";
    const string KeyIssuedAt = "vapolia.analytics.installIdIssuedAt";
    const string KeyFirstSeen = "vapolia.analytics.firstSeen";
    const string KeyOptedOut = "vapolia.analytics.optedOut";

    readonly AnalyticsOptions options;
    readonly Lock gate = new();
    Device? detected;

    /// <summary>Reads and renews the id according to <see cref="AnalyticsOptions.InstallIdLifetime"/>.</summary>
    /// <param name="options">The same options the sender was given.</param>
    public MobileInstallIdentityProvider(AnalyticsOptions options) => this.options = options;

    /// <summary>
    /// The right of opposition. Turning it on forgets the id, so opting back in cannot resume the same
    /// installation.
    /// </summary>
    public bool OptedOut
    {
        get => Preferences.Get(KeyOptedOut) == "true";
        set
        {
            lock (gate)
            {
                Preferences.Set(KeyOptedOut, value ? "true" : "false");
                if (value)
                {
                    Preferences.Remove(KeyId);
                    Preferences.Remove(KeyIssuedAt);
                    Preferences.Remove(KeyFirstSeen);
                }
            }
        }
    }

    /// <summary>
    /// The current id, reissued when the old one reached its ceiling. Null when opted out, which is
    /// what stops the tracking.
    /// </summary>
    public string? GetInstallId()
    {
        if (OptedOut)
            return null;

        lock (gate)
        {
            SeedIfEmpty();

            var now = DateTimeOffset.UtcNow;
            var stored = Clean.InstallId(Preferences.Get(KeyId));
            var issuedAt = ReadTime(KeyIssuedAt);

            // A device clock moved backwards would otherwise freeze the id: reissue rather than extend.
            var expired = issuedAt is null
                          || now - issuedAt >= options.AdvancedOptions.InstallIdLifetime
                          || issuedAt > now.AddDays(1);

            if (stored is not null && !expired)
                return stored;

            var issued = Guid.NewGuid().ToString("D");
            Preferences.Set(KeyId, issued);
            WriteTime(KeyIssuedAt, now);
            // Deliberately not touched by a renewal: an installation that has run for thirteen months
            // is not a new one, and an age bucket reset at every rotation would designate nobody.
            if (ReadTime(KeyFirstSeen) is null)
                WriteTime(KeyFirstSeen, now);

            return issued;
        }
    }

    /// <summary>
    /// Whether this installation has been seen before — false only until the first id is issued.
    /// What decides <c>first_open</c>, and it is stored apart from the id so a renewal does not count
    /// as a new installation.
    /// </summary>
    public bool IsFirstRun
    {
        get
        {
            lock (gate)
            {
                SeedIfEmpty();
                return ReadTime(KeyFirstSeen) is null;
            }
        }
    }

    /// <summary>
    /// Adopts an installation this client did not create, once: the id an app used before switching
    /// over, with the date it was issued and the date the installation was first measured. Ignored as
    /// soon as an id is stored, so calling it at every start is safe.
    /// </summary>
    /// <returns>Whether the seed was taken.</returns>
    public bool Seed(InstallSeed seed)
    {
        if (Clean.InstallId(seed.InstallId) is not { } id)
            return false;

        lock (gate)
        {
            if (OptedOut || Clean.InstallId(Preferences.Get(KeyId)) is not null)
                return false;

            Preferences.Set(KeyId, id);
            WriteTime(KeyIssuedAt, seed.IssuedAt);
            WriteTime(KeyFirstSeen, seed.FirstSeen);
            return true;
        }
    }

    /// <summary>
    /// What the device reports about itself: its region setting. Detected once — it does not change
    /// while the process runs — and never completed with anything about the app: that is the batch
    /// context.
    /// </summary>
    public Device GetDevice()
    {
        lock (gate)
            return detected ??= DeviceProbe.Detect();
    }

    /// <summary>
    /// When this installation was first measured, kept across every id renewal. Null before the first
    /// event, and when opted out.
    ///
    /// The client is the only thing that knows it and does nothing with it: an app that wants to
    /// segment on the age of an installation puts <see cref="InstallAge.Bucket"/> in its own context,
    /// under a key its source whitelists.
    /// </summary>
    public DateTimeOffset? FirstSeen
    {
        get
        {
            if (OptedOut)
                return null;

            lock (gate)
            {
                SeedIfEmpty();
                return ReadTime(KeyFirstSeen);
            }
        }
    }

    /// <summary>
    /// Corrects what the device probe reported — a country an app reads better than the region
    /// setting. Not for anything about the app itself, which belongs in
    /// <see cref="IAnalyticsContext"/>.
    /// </summary>
    /// <param name="transform">Given the current device, returns the next one.</param>
    public void Update(Func<Device, Device> transform)
    {
        lock (gate)
            detected = transform(detected ??= DeviceProbe.Detect());
    }

    void SeedIfEmpty()
    {
        if (options.SeedInstallId is null || Clean.InstallId(Preferences.Get(KeyId)) is not null)
            return;

        var seed = options.SeedInstallId();
        // Read once: an empty store with no seed to offer must not ask again on every event.
        options.SeedInstallId = null;
        if (seed is not null)
            Seed(seed);
    }

    static DateTimeOffset? ReadTime(string key)
        => long.TryParse(Preferences.Get(key), CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    static void WriteTime(string key, DateTimeOffset value)
        => Preferences.Set(key, value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
}
