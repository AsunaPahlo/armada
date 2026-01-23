using Dalamud.Plugin.Ipc;

namespace Armada;

/// <summary>
/// IPC consumer for InventoryTools (AllaganTools) plugin.
/// Used to query submarine part inventory counts per character.
/// </summary>
public class InventoryToolsApi : IDisposable
{
    private ICallGateSubscriber<bool>? _isInitialized;
    private ICallGateSubscriber<uint, ulong, int, uint>? _itemCount;

    private bool _isSubscribed;

    public InventoryToolsApi()
    {
        Subscribe();
    }

    private void Subscribe()
    {
        try
        {
            _isInitialized = Svc.PluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
            _itemCount = Svc.PluginInterface.GetIpcSubscriber<uint, ulong, int, uint>("AllaganTools.ItemCount");
            _isSubscribed = true;
            PluginLog.Debug("Armada: Subscribed to InventoryTools IPC");
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"Armada: Failed to subscribe to InventoryTools IPC - {ex.Message}");
            _isSubscribed = false;
        }
    }

    /// <summary>
    /// Attempt to re-subscribe to the IPC if not currently subscribed.
    /// Call this to pick up InventoryTools if it gets installed/enabled after startup.
    /// </summary>
    /// <returns>True if subscribed (may or may not be available), false otherwise</returns>
    public bool TryResubscribe()
    {
        if (_isSubscribed)
            return true;

        Subscribe();
        return _isSubscribed;
    }

    /// <summary>
    /// Check if InventoryTools is available and initialized.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (!_isSubscribed || _isInitialized == null)
                return false;

            try
            {
                _isInitialized.InvokeFunc();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Get the count of a specific item for a character across all inventory types.
    /// </summary>
    /// <param name="itemId">The item ID to search for</param>
    /// <param name="characterId">The character's Content ID (CID)</param>
    /// <returns>The total count of the item, or 0 if unavailable</returns>
    public uint GetItemCount(uint itemId, ulong characterId)
    {
        if (!_isSubscribed || _itemCount == null)
            return 0;

        try
        {
            // inventoryType = -1 means search all inventory types
            return _itemCount.InvokeFunc(itemId, characterId, -1);
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"Armada: Failed to get item count for {itemId} - {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Get submarine part inventory counts for a character.
    /// </summary>
    /// <param name="characterId">The character's Content ID (CID)</param>
    /// <param name="partItemIds">List of submarine part item IDs to check</param>
    /// <returns>Dictionary of itemId -> count for items with count > 0</returns>
    public Dictionary<uint, uint> GetSubmarinePartsInventory(ulong characterId, IEnumerable<int> partItemIds)
    {
        var result = new Dictionary<uint, uint>();

        if (!IsAvailable)
            return result;

        foreach (var itemId in partItemIds)
        {
            var count = GetItemCount((uint)itemId, characterId);
            if (count > 0)
            {
                result[(uint)itemId] = count;
            }
        }

        return result;
    }

    public void Dispose()
    {
        _isInitialized = null;
        _itemCount = null;
        _isSubscribed = false;
    }
}