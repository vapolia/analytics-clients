using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vapolia.Analytics.Client;

/// <summary>
/// Persists/Restores a list of events in a local file.
/// Truncate the list to the capacity.
/// </summary>
sealed class PersistPendingItemsToLocalStorageHelper(string path, int capacity, ILogger? logger)
{
    const int CurrentVersion = 1;

    /// <summary>
    /// Persists the items to a local file
    /// </summary>
    public void Save(IReadOnlyList<Pending> items)
    {
        if (items.Count == 0)
        {
            Clear();
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var file = new SpoolFile
            {
                Version = CurrentVersion,
                // Beyond the cap only the oldest items are kept
                Items = items.Take(capacity).Select(p => new SpoolItem
                {
                    InstallId = p.Key.InstallId,
                    Device = p.Key.Device,
                    Context = p.Key.Context,
                    Name = p.Event.Name,
                    Ts = p.Event.Ts,
                    Props = p.Event.Props,
                    Tz = p.Event.Tz,
                }).ToList(),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(file, AnalyticsJsonContext.Default.SpoolFile));
        }
        catch(Exception e)
        {
            logger?.LogError(e, "Failed to write spool file {path}", path);
        }
    }

    /// <summary>
    /// Restores the items from a local file
    /// </summary>
    /// <remarks>
    /// The file is deleted immediately after loading
    /// </remarks>
    public IReadOnlyList<Pending> Load()
    {
        if (!File.Exists(path))
            return [];

        try
        {
            var content = File.ReadAllText(path);
            Clear();

            var file = JsonSerializer.Deserialize(content, AnalyticsJsonContext.Default.SpoolFile);
            if (file is null || file.Version != CurrentVersion)
                return [];

            var result = new List<Pending>(Math.Min(file.Items.Count, capacity));
            foreach (var item in file.Items.Take(capacity))
            {
                var installId = Clean.InstallId(item.InstallId);
                if (installId is null || string.IsNullOrEmpty(item.Name))
                    continue;

                result.Add(new Pending(
                    new BatchKey(installId, item.Device, item.Context ?? "{}"),
                    new Event(item.Name, item.Ts, item.Props, item.Tz)));
            }

            return result;
        }
        catch(Exception e)
        {
            logger?.LogWarning(e, "Ignoring unreadable spool file {path}", path);
            Clear();
            return [];
        }
    }

    /// <summary>
    /// Deletes the local file.
    /// </summary>
    void Clear()
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch(Exception e)
        {
            logger?.LogError(e, "Failed to delete spool file {path}", path);
        }
    }
}
