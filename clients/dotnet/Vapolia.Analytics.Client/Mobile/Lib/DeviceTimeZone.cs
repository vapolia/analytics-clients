namespace Vapolia.Analytics.Client;

/// <summary>
/// The mobile device reads its own clock
/// </summary>
sealed class DeviceTimeZone : IAnalyticsTimeZone
{
    public DateTimeOffset? GetLocalTime(DateTimeOffset at) => at.ToLocalTime();
}
