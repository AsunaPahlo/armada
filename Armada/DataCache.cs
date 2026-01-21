namespace Armada;

/// <summary>
/// Cached entry for fleet data that couldn't be sent
/// </summary>
public class CachedFleetData
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object> Data { get; set; } = new();
}

/// <summary>
/// Cached entry for voyage loot that couldn't be sent
/// </summary>
public class CachedVoyageLoot
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public VoyageLootData Data { get; set; } = new();
}

/// <summary>
/// Persistent cache for data that failed to send to the server.
/// Data is stored on disk and retried when connection is restored.
/// </summary>
public class DataCache : IDisposable
{
    private readonly string _cacheFilePath;
    private readonly object _lock = new();
    private CacheData _cache = new();
    private bool _isDirty;

    // Limit cache size to prevent unbounded growth
    private const int MaxFleetDataEntries = 10;
    private const int MaxVoyageLootEntries = 500;

    public int PendingFleetDataCount => _cache.FleetData.Count;
    public int PendingVoyageLootCount => _cache.VoyageLoot.Count;
    public bool HasPendingData => _cache.FleetData.Count > 0 || _cache.VoyageLoot.Count > 0;

    public DataCache()
    {
        _cacheFilePath = Path.Combine(
            Svc.PluginInterface.ConfigDirectory.FullName,
            "pending_data.json"
        );

        Load();
    }

    /// <summary>
    /// Add fleet data to the cache for later sending
    /// </summary>
    public void CacheFleetData(Dictionary<string, object> data)
    {
        lock (_lock)
        {
            // Remove oldest entries if at capacity
            while (_cache.FleetData.Count >= MaxFleetDataEntries)
            {
                var oldest = _cache.FleetData.OrderBy(x => x.CapturedAt).First();
                _cache.FleetData.Remove(oldest);
                PluginLog.Debug($"Armada Cache: Removed oldest fleet data entry to make room");
            }

            _cache.FleetData.Add(new CachedFleetData
            {
                Data = data,
                CapturedAt = DateTime.UtcNow
            });

            _isDirty = true;
            PluginLog.Information($"Armada Cache: Cached fleet data ({_cache.FleetData.Count} pending)");
        }

        Save();
    }

    /// <summary>
    /// Add voyage loot to the cache for later sending
    /// </summary>
    public void CacheVoyageLoot(VoyageLootData lootData)
    {
        lock (_lock)
        {
            // Check for duplicates (same submarine, same capture time within 1 minute)
            var isDuplicate = _cache.VoyageLoot.Any(x =>
                x.Data.SubmarineName == lootData.SubmarineName &&
                x.Data.FcId == lootData.FcId &&
                Math.Abs((x.Data.CapturedAt - lootData.CapturedAt).TotalMinutes) < 1);

            if (isDuplicate)
            {
                PluginLog.Debug($"Armada Cache: Skipping duplicate loot entry for {lootData.SubmarineName}");
                return;
            }

            // Remove oldest entries if at capacity
            while (_cache.VoyageLoot.Count >= MaxVoyageLootEntries)
            {
                var oldest = _cache.VoyageLoot.OrderBy(x => x.CapturedAt).First();
                _cache.VoyageLoot.Remove(oldest);
                PluginLog.Debug($"Armada Cache: Removed oldest voyage loot entry to make room");
            }

            _cache.VoyageLoot.Add(new CachedVoyageLoot
            {
                Data = lootData,
                CapturedAt = DateTime.UtcNow
            });

            _isDirty = true;
            PluginLog.Information($"Armada Cache: Cached voyage loot for {lootData.SubmarineName} ({_cache.VoyageLoot.Count} pending)");
        }

        Save();
    }

    /// <summary>
    /// Get all pending fleet data entries
    /// </summary>
    public List<CachedFleetData> GetPendingFleetData()
    {
        lock (_lock)
        {
            return _cache.FleetData.ToList();
        }
    }

    /// <summary>
    /// Get all pending voyage loot entries
    /// </summary>
    public List<CachedVoyageLoot> GetPendingVoyageLoot()
    {
        lock (_lock)
        {
            return _cache.VoyageLoot.ToList();
        }
    }

    /// <summary>
    /// Remove a fleet data entry after successful send
    /// </summary>
    public void RemoveFleetData(string id)
    {
        lock (_lock)
        {
            var entry = _cache.FleetData.FirstOrDefault(x => x.Id == id);
            if (entry != null)
            {
                _cache.FleetData.Remove(entry);
                _isDirty = true;
                PluginLog.Debug($"Armada Cache: Removed sent fleet data ({_cache.FleetData.Count} remaining)");
            }
        }

        Save();
    }

    /// <summary>
    /// Remove a voyage loot entry after successful send
    /// </summary>
    public void RemoveVoyageLoot(string id)
    {
        lock (_lock)
        {
            var entry = _cache.VoyageLoot.FirstOrDefault(x => x.Id == id);
            if (entry != null)
            {
                _cache.VoyageLoot.Remove(entry);
                _isDirty = true;
                PluginLog.Debug($"Armada Cache: Removed sent voyage loot ({_cache.VoyageLoot.Count} remaining)");
            }
        }

        Save();
    }

    /// <summary>
    /// Clear all cached data
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.FleetData.Clear();
            _cache.VoyageLoot.Clear();
            _isDirty = true;
        }

        Save();
        PluginLog.Information("Armada Cache: Cleared all cached data");
    }

    /// <summary>
    /// Flush all cached data by attempting to send it
    /// </summary>
    public async Task FlushAsync()
    {
        if (!HasPendingData)
            return;

        if (!P.ArmadaClient.IsAuthenticated)
        {
            PluginLog.Debug("Armada Cache: Cannot flush - not authenticated");
            return;
        }

        PluginLog.Information($"Armada Cache: Flushing cached data ({PendingFleetDataCount} fleet, {PendingVoyageLootCount} loot)");

        // Send fleet data
        var fleetEntries = GetPendingFleetData();
        foreach (var entry in fleetEntries)
        {
            try
            {
                var success = await P.ArmadaClient.SendFleetDataAsync(
                    new List<Dictionary<string, object>> { entry.Data },
                    fromCache: true
                );

                if (success)
                {
                    RemoveFleetData(entry.Id);
                }
                else
                {
                    PluginLog.Warning($"Armada Cache: Failed to send cached fleet data (will retry later)");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Armada Cache: Error sending cached fleet data - {ex.Message}");
            }

            // Small delay between sends to avoid overwhelming the server
            await Task.Delay(500);
        }

        // Send voyage loot
        var lootEntries = GetPendingVoyageLoot();
        foreach (var entry in lootEntries)
        {
            try
            {
                var success = await P.ArmadaClient.SendVoyageLootAsync(entry.Data, fromCache: true);

                if (success)
                {
                    RemoveVoyageLoot(entry.Id);
                }
                else
                {
                    PluginLog.Warning($"Armada Cache: Failed to send cached voyage loot for {entry.Data.SubmarineName} (will retry later)");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Armada Cache: Error sending cached voyage loot - {ex.Message}");
            }

            // Small delay between sends
            await Task.Delay(250);
        }

        PluginLog.Information($"Armada Cache: Flush complete ({PendingFleetDataCount} fleet, {PendingVoyageLootCount} loot remaining)");
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                var json = File.ReadAllText(_cacheFilePath);
                var loaded = JsonConvert.DeserializeObject<CacheData>(json);

                if (loaded != null)
                {
                    _cache = loaded;
                    PluginLog.Information($"Armada Cache: Loaded {_cache.FleetData.Count} fleet data, {_cache.VoyageLoot.Count} voyage loot from cache");
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada Cache: Failed to load cache - {ex.Message}");
            _cache = new CacheData();
        }
    }

    private void Save()
    {
        if (!_isDirty)
            return;

        try
        {
            lock (_lock)
            {
                var json = JsonConvert.SerializeObject(_cache, Formatting.Indented);
                File.WriteAllText(_cacheFilePath, json);
                _isDirty = false;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada Cache: Failed to save cache - {ex.Message}");
        }
    }

    public void Dispose()
    {
        Save();
    }

    private class CacheData
    {
        public List<CachedFleetData> FleetData { get; set; } = new();
        public List<CachedVoyageLoot> VoyageLoot { get; set; } = new();
    }
}
