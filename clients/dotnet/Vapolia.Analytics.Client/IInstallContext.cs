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
}