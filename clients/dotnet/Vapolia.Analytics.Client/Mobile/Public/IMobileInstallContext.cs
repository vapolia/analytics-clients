namespace Vapolia.Analytics.Client;

/// <summary>
/// Additional install context for mobile apps.
/// </summary>
public interface IMobileInstallContext : IInstallContext
{
    /// <summary>
    /// The answer to the consent question.
    /// </summary>
    bool? ConsentAnswer { get; }
    /// <summary>
    /// Whether the app was installed for the first time.
    /// </summary>
    bool IsFirstRun { get; }
    /// <summary>
    /// Seed the install context with the given seed.
    /// </summary>
    bool Seed(InstallSeed seed);
    /// <summary>
    /// The date and time when the app was first seen.
    /// </summary>
    DateTimeOffset? FirstSeen { get; }
}