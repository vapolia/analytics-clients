namespace Vapolia.Analytics.Client;

/// <summary>
/// Installation context for the current poster.
/// </summary>
public interface IInstallContext
{
    /// <summary>
    /// The current installation id, or null when there is none and none may be created
    /// TODO: + Use a getter.
    /// </summary>
    string? GetInstallId();

    /// <summary>
    /// The device context for the current caller
    /// TODO: simplify so Device can stay internal. + Use a getter.
    /// </summary>
    Device GetDevice();
 
    /// <summary>
    /// Whether this installation, or this visitor, has opposed the measurement
    /// </summary>
    bool OptedOut { get; set; }
}