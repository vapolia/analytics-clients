using System.Text.Json;

namespace Vapolia.Analytics.Client;

/// <summary>
/// What survives the process going away — a phone reclaiming a backgrounded app, or a server restart.
///
/// Events are read back once: <see cref="Load"/> deletes the file. A duplicated event is a wrong
/// count, while a lost one is only a missing count, so the ambiguity is resolved towards losing.
/// </summary>
sealed class Spool(string path, int capacity)
{
    const int CurrentVersion = 1;

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
                // Beyond the cap the oldest are kept: they are the ones a restart is meant to recover.
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

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, AnalyticsJsonContext.Default.SpoolFile));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // Nothing to do about it, and nothing a caller could do either.
        }
    }

    public IReadOnlyList<Pending> Load()
    {
        try
        {
            if (!File.Exists(path))
                return [];

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
        catch
        {
            // A truncated or older file is a process killed mid-write; not worth a recovery path.
            Clear();
            return [];
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Same as above.
        }
    }
}
