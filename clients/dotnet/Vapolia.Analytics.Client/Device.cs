namespace Vapolia.Analytics.Client;

/// <summary>
/// What the batch carries about the device, which is now the country alone: the platform and the
/// build come from the build token, and everything else was an installation fingerprint the
/// collector no longer stores. What is true of the installation belongs in the batch context.
/// </summary>
public sealed record Device
{
    /// <summary>ISO 3166-1 alpha-2, from the device locale — never from the request IP.</summary>
    public string? Country { get; init; }


    /// <summary>The device reduced to what the collector will store, or null when the batch is refused.</summary>
    internal Device? Cleaned(ICollection<string>? excludedCountries = null)
    {
        var country = Clean.Country(Country);
        if (country is not null && excludedCountries?.Any(c => string.Equals(c, country, StringComparison.OrdinalIgnoreCase)) == true)
            return null;

        return new Device { Country = country };
    }
}
