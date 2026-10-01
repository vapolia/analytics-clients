namespace Vapolia.Analytics.Client;

/// <summary>
/// Installation context for the current poster.
/// </summary>
public interface IInstallContext
{
    /// <summary>
    /// The current installation id, or null when there is none and none may be created. Reading it
    /// creates the id when there is none yet and the person has not opposed.
    /// </summary>
    string? InstallId { get; }

    /// <summary>
    /// ISO 3166-1 alpha-2 country of the current caller, from the device region setting or the browser
    /// language, never from the IP. Null when unknown.
    /// </summary>
    string? Country { get; }

    /// <summary>
    /// Whether this installation, or this visitor, has opposed the measurement
    /// </summary>
    bool IsOptedOut { get; set; }

    /// <summary>
    /// The person's answer to the consent question: true accepted, false refused, null not answered
    /// yet. While it is null, <see cref="IsOptedOut"/> equals <see cref="RequiresPriorConsent"/>.
    /// </summary>
    bool? ConsentAnswer { get; }

    /// <summary>
    /// Whether the regime of this installation, or of this request, requires consent before anything
    /// is stored, whatever the person answered. What decides the consent question of the welcome
    /// popup or the cookie banner.
    /// </summary>
    bool RequiresPriorConsent { get; }
}