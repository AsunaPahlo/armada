using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace Armada;

public class FleetDataProvider : IDisposable
{
    // Mapping from item IDs to SubmarinePart row IDs (1-40)
    // Used for duration calculations on the server
    private static readonly Dictionary<int, int> ItemIdToRowId = new()
    {
        // Shark (original)
        { 21792, 1 },   // Bow
        { 21793, 2 },   // Bridge
        { 21794, 3 },   // Hull
        { 21795, 4 },   // Stern
        // Unkiu
        { 21796, 5 },   // Bow
        { 21797, 6 },   // Bridge
        { 21798, 7 },   // Hull
        { 21799, 8 },   // Stern
        // Whale
        { 22526, 9 },   // Bow
        { 22527, 10 },  // Bridge
        { 22528, 11 },  // Hull
        { 22529, 12 },  // Stern
        // Coelacanth
        { 23903, 13 },  // Bow
        { 23904, 14 },  // Bridge
        { 23905, 15 },  // Hull
        { 23906, 16 },  // Stern
        // Syldra
        { 24344, 17 },  // Bow
        { 24345, 18 },  // Bridge
        { 24346, 19 },  // Hull
        { 24347, 20 },  // Stern
        // Modified Shark
        { 24348, 21 },  // Bow
        { 24349, 22 },  // Bridge
        { 24350, 23 },  // Hull
        { 24351, 24 },  // Stern
        // Modified Unkiu
        { 24352, 25 },  // Bow
        { 24353, 26 },  // Bridge
        { 24354, 27 },  // Hull
        { 24355, 28 },  // Stern
        // Modified Whale
        { 24356, 29 },  // Bow
        { 24357, 30 },  // Bridge
        { 24358, 31 },  // Hull
        { 24359, 32 },  // Stern
        // Modified Coelacanth
        { 24360, 33 },  // Bow
        { 24361, 34 },  // Bridge
        { 24362, 35 },  // Hull
        { 24363, 36 },  // Stern
        // Modified Syldra
        { 24364, 37 },  // Bow
        { 24365, 38 },  // Bridge
        { 24366, 39 },  // Hull
        { 24367, 40 },  // Stern
    };

    private static int GetPartRowId(int itemId) => ItemIdToRowId.GetValueOrDefault(itemId, 0);

    // All submarine part item IDs (for inventory queries)
    private static readonly List<int> AllPartItemIds = ItemIdToRowId.Keys.ToList();

    // Salvage accessory item IDs (can be vendored for gil)
    // IDs from Garland Tools: https://garlandtools.org/db/
    private static readonly List<uint> SalvageItemIds = new()
    {
        22500, // Salvaged Ring
        22501, // Salvaged Bracelet
        22502, // Salvaged Earring
        22503, // Salvaged Necklace
        22504, // Extravagant Salvaged Ring
        22505, // Extravagant Salvaged Bracelet
        22506, // Extravagant Salvaged Earring
        22507, // Extravagant Salvaged Necklace
    };

    private AutoRetainerApi? _api;
    private InventoryToolsApi? _inventoryApi;
    private bool _isReady;
    private DateTime _lastApiCheckTime = DateTime.MinValue;
    private static readonly TimeSpan ApiCheckInterval = TimeSpan.FromSeconds(30);

    public bool IsReady => _isReady;
    public bool IsApiAvailable => _api?.Ready ?? false;
    public bool IsAllaganToolsAvailable => _inventoryApi?.IsAvailable ?? false;

    /// <summary>
    /// Check if APIs that weren't available at startup are now available.
    /// Called periodically to pick up plugins that get installed/enabled after startup.
    /// </summary>
    public void CheckAndInitializeApis()
    {
        // Only check periodically to avoid spam
        if (DateTime.Now - _lastApiCheckTime < ApiCheckInterval)
            return;

        _lastApiCheckTime = DateTime.Now;

        // Check AutoRetainer API
        if (_api == null || !_api.Ready)
        {
            TryInitializeAutoRetainerApi();
        }

        // Check InventoryTools API
        if (_inventoryApi == null || !_inventoryApi.IsAvailable)
        {
            TryInitializeInventoryToolsApi();
        }
    }

    private void TryInitializeAutoRetainerApi()
    {
        try
        {
            // Dispose existing instance if any
            if (_api != null)
            {
                try { _api.Dispose(); } catch { }
                _api = null;
            }

            _api = new AutoRetainerApi();
            _isReady = _api.Ready;

            if (_isReady)
            {
                PluginLog.Information("Armada: AutoRetainer API initialized successfully");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"Armada: AutoRetainer API not available - {ex.Message}");
            _isReady = false;
        }
    }

    private void TryInitializeInventoryToolsApi()
    {
        try
        {
            // If we have an existing instance, try re-subscribing first
            if (_inventoryApi != null)
            {
                _inventoryApi.TryResubscribe();
                if (_inventoryApi.IsAvailable)
                {
                    PluginLog.Information("Armada: InventoryTools API became available");
                    return;
                }
            }

            // Create new instance if we don't have one
            if (_inventoryApi == null)
            {
                _inventoryApi = new InventoryToolsApi();
                if (_inventoryApi.IsAvailable)
                {
                    PluginLog.Information("Armada: InventoryTools API initialized successfully");
                }
                else
                {
                    // Still keep the instance - it can become available later without re-init
                    PluginLog.Debug("Armada: InventoryTools not available yet");
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"Armada: Failed to initialize InventoryTools API - {ex.Message}");
            _inventoryApi = null;
        }
    }

    public void Initialize()
    {
        _lastApiCheckTime = DateTime.MinValue; // Allow immediate check
        TryInitializeAutoRetainerApi();

        if (!_isReady)
        {
            PluginLog.Warning("Armada: AutoRetainer API not ready - is AutoRetainer installed and enabled? Will retry periodically.");
        }

        // Initialize InventoryTools API (optional - used for submarine parts inventory)
        TryInitializeInventoryToolsApi();

        if (_inventoryApi == null || !_inventoryApi.IsAvailable)
        {
            PluginLog.Debug("Armada: InventoryTools not available - submarine parts inventory will not be tracked. Will retry periodically.");
        }

        _lastApiCheckTime = DateTime.Now;
    }

    public void ForceSend()
    {
        Task.Run(() => SendFleetDataAsync(isManual: true));
    }

    public async Task SendFleetDataAsync(bool isManual = false)
    {
        // Check if APIs that weren't available at startup are now available
        CheckAndInitializeApis();

        if (!P.ArmadaClient.IsConnected || !P.ArmadaClient.IsAuthenticated)
        {
            PluginLog.Debug("Armada: Skipping send - not connected");
            return;
        }

        // Skip sending if player is in a state where FC data isn't available
        if (!ShouldSendData())
        {
            PluginLog.Debug("Armada: Skipping send - player is off-world or in instance");
            if (isManual)
            {
                Svc.Framework.RunOnFrameworkThread(() =>
                {
                    Svc.Chat.PrintError("[Armada] Cannot send data while off-world or in an instance.");
                });
            }
            return;
        }

        try
        {
            var data = GetFleetData();
            if (data != null)
            {
                await P.ArmadaClient.SendFleetDataAsync(new List<Dictionary<string, object>> { data });
                PluginLog.Information("Armada: Sent fleet data via API");
            }
            else
            {
                PluginLog.Warning("Armada: No valid fleet data to send");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Failed to send fleet data - {ex.Message}");
        }
    }

    private bool ShouldSendData()
    {
        try
        {
            return Svc.Framework.RunOnFrameworkThread(() =>
            {
                // Must be logged in with a valid player
                if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null)
                {
                    return false;
                }

                var player = Svc.Objects.LocalPlayer;

                // Skip if off-world (visiting another server)
                if (player.CurrentWorld.RowId != player.HomeWorld.RowId)
                {
                    PluginLog.Debug($"Armada: Off-world ({player.CurrentWorld.Value.Name} vs home {player.HomeWorld.Value.Name})");
                    return false;
                }

                // Skip if in a duty/instance (ContentFinderCondition will be non-zero)
                var condition = Svc.Condition;
                if (condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] ||
                    condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty56] ||
                    condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty95])
                {
                    PluginLog.Debug("Armada: In duty/instance");
                    return false;
                }

                return true;
            }).Result;
        }
        catch (Exception ex)
        {
            PluginLog.Warning($"Armada: Error checking send conditions: {ex.Message}");
            return false; // Default to NOT sending if we can't determine state
        }
    }

    public Dictionary<string, object>? GetFleetData()
    {
        if (_api == null || !_api.Ready)
        {
            PluginLog.Warning("Armada: AutoRetainer API not available");
            _isReady = false;
            return null;
        }

        _isReady = true;

        try
        {
            PluginLog.Debug("Armada: Reading data from AutoRetainer API...");

            // Get route plans
            var routePlans = GetRoutePlans();
            PluginLog.Debug($"Armada: Found {routePlans.Count} route plans");

            // Get character data with submarines (collects active FC IDs from non-excluded characters)
            var activeFcIds = new HashSet<string>();
            var characters = GetCharacterData(activeFcIds);
            PluginLog.Debug($"Armada: Found {characters.Count} characters with submarines");

            // Get FC data (filtered to only FCs with non-excluded characters)
            var fcData = GetFCData(activeFcIds);
            PluginLog.Debug($"Armada: Found {fcData.Count} FCs");

            return new Dictionary<string, object>
            {
                ["nickname"] = C.Nickname,
                ["characters"] = characters,
                ["fc_data"] = fcData,
                ["route_plans"] = routePlans
            };
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Failed to get fleet data - {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    private Dictionary<string, object> GetRoutePlans()
    {
        var routePlans = new Dictionary<string, object>();

        try
        {
            var plans = _api!.Config.SubmarinePointPlans;
            if (plans != null)
            {
                foreach (var plan in plans)
                {
                    var guid = plan.GUID;
                    if (!string.IsNullOrEmpty(guid))
                    {
                        var points = new List<uint>();
                        if (plan.Points != null)
                        {
                            points.AddRange(plan.Points);
                        }

                        routePlans[guid] = new Dictionary<string, object>
                        {
                            ["name"] = plan.Name ?? "",
                            ["points"] = points
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error reading route plans - {ex.Message}");
        }

        return routePlans;
    }

    private Dictionary<string, object> GetFCData(HashSet<string> activeFcIds)
    {
        var fcData = new Dictionary<string, object>();

        try
        {
            var fcs = _api!.Config.FCData;

            // Get current character's FC house info (only works on home world)
            var fcHouseInfo = GetCurrentFCHouseInfo();

            if (fcs != null)
            {
                foreach (var fc in fcs)
                {
                    var fcIdStr = fc.Key.ToString();

                    // Skip FCs that have no non-excluded characters
                    if (!activeFcIds.Contains(fcIdStr))
                        continue;

                    var fcDict = new Dictionary<string, object>
                    {
                        ["name"] = fc.Value.Name ?? "",
                        ["gil"] = fc.Value.Gil,
                        ["fc_points"] = fc.Value.FCPoints,
                        ["holder_chara"] = fc.Value.HolderChara.ToString()
                    };

                    // Add house info if available and this is the current FC
                    if (fcHouseInfo != null && fcHouseInfo.ContainsKey("fc_id") &&
                        fcHouseInfo["fc_id"].ToString() == fcIdStr)
                    {
                        fcDict["house_world"] = fcHouseInfo["world"];
                        fcDict["house_district"] = fcHouseInfo["district"];
                        fcDict["house_ward"] = fcHouseInfo["ward"];
                        fcDict["house_plot"] = fcHouseInfo["plot"];
                    }

                    fcData[fcIdStr] = fcDict;
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error reading FC data - {ex.Message}");
        }

        return fcData;
    }

    private unsafe Dictionary<string, object>? GetCurrentFCHouseInfo()
    {
        try
        {
            // Check if logged in and player exists (run on framework thread)
            bool isLoggedIn = Svc.Framework.RunOnFrameworkThread(() => Svc.ClientState.IsLoggedIn).Result;
            bool localPlayerExists = Svc.Framework.RunOnFrameworkThread(() => Svc.Objects.LocalPlayer != null).Result;

            if (!isLoggedIn || !localPlayerExists)
            {
                return null;
            }

            // Check if on home world (run on framework thread)
            bool isOnHomeWorld = Svc.Framework.RunOnFrameworkThread(() =>
            {
                var player = Svc.Objects.LocalPlayer;
                return player != null && player.CurrentWorld.RowId == player.HomeWorld.RowId;
            }).Result;

            if (!isOnHomeWorld)
            {
                PluginLog.Debug("Armada: Not on home world, skipping FC house info");
                return null;
            }

            // Get house info on framework thread
            var houseInfo = Svc.Framework.RunOnFrameworkThread(() =>
            {
                var houseId = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate);

                // Check if house exists (WorldId will be 0 if no house)
                if (houseId.WorldId == 0)
                {
                    return (valid: false, worldId: (uint)0, territoryId: (uint)0, ward: 0, plot: 0);
                }

                return (valid: true, worldId: houseId.WorldId, territoryId: houseId.TerritoryTypeId,
                        ward: houseId.WardIndex + 1, plot: houseId.PlotIndex + 1);
            }).Result;

            if (!houseInfo.valid)
            {
                PluginLog.Debug("Armada: No FC house found");
                return null;
            }

            // Validate IDs before looking up (65535 = 0xFFFF is an invalid sentinel value)
            if (houseInfo.worldId == 0 || houseInfo.worldId == 65535 ||
                houseInfo.territoryId == 0 || houseInfo.territoryId == 65535)
            {
                PluginLog.Debug("Armada: Invalid house info IDs (possibly in instance/dungeon)");
                return null;
            }

            // Get current character's FC ID
            var localContentId = Svc.Framework.RunOnFrameworkThread(() => Svc.PlayerState.ContentId).Result;
            var currentChar = _api!.Config.OfflineData?.FirstOrDefault(c => c.CID == localContentId);
            if (currentChar == null)
            {
                PluginLog.Debug("Armada: Could not find current character data");
                return null;
            }

            // World name
            var worldSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.World>();
            var worldRow = worldSheet?.GetRowOrDefault(houseInfo.worldId);
            var worldName = worldRow?.Name.ToString() ?? "Unknown";

            // Housing area name
            var territorySheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            var territoryRow = territorySheet?.GetRowOrDefault(houseInfo.territoryId);
            var areaName = territoryRow?.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";

            PluginLog.Debug($"Armada: FC House - {areaName} Ward {houseInfo.ward} Plot {houseInfo.plot} ({worldName})");

            return new Dictionary<string, object>
            {
                ["fc_id"] = currentChar.FCID.ToString(),
                ["world"] = worldName,
                ["district"] = areaName,
                ["ward"] = houseInfo.ward,
                ["plot"] = houseInfo.plot
            };
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error getting FC house info - {ex.Message}");
            return null;
        }
    }

    private unsafe List<int> GetUnlockedSectors()
    {
        var unlocked = new List<int>();
        try
        {
            // Must run on framework thread
            var result = Svc.Framework.RunOnFrameworkThread(() =>
            {
                var sectors = new List<int>();

                // Safety check: WorkshopTerritory must be loaded (player must be in or have visited FC workshop)
                var housingMgr = HousingManager.Instance();
                if (housingMgr == null) return sectors;
                if (housingMgr->WorkshopTerritory == null) return sectors;
                if (housingMgr->WorkshopTerritory->Submersible.Data.Length == 0) return sectors;
                if (housingMgr->WorkshopTerritory->Submersible.Data[0].Name[0] == 0) return sectors;

                // All sector IDs from maps 1-7 (1-143)
                for (byte sectorId = 1; sectorId <= 143; sectorId++)
                {
                    try
                    {
                        if (HousingManager.IsSubmarineExplorationUnlocked(sectorId))
                            sectors.Add(sectorId);
                    }
                    catch { }
                }
                return sectors;
            }).Result;
            return result;
        }
        catch (Exception ex)
        {
            PluginLog.Warning($"Armada: Failed to get unlocked sectors: {ex.Message}");
            return unlocked;
        }
    }

    private List<Dictionary<string, object>> GetCharacterData(HashSet<string> activeFcIds)
    {
        var characters = new List<Dictionary<string, object>>();

        // Get current character's CID - unlock data is only accurate for this character's FC
        var currentCid = Svc.Framework.RunOnFrameworkThread(() => Svc.PlayerState.ContentId).Result;

        // Get unlocked sectors - only valid for the currently logged-in character's FC
        var unlockedSectors = GetUnlockedSectors();

        try
        {
            var offlineData = _api!.Config.OfflineData;
            if (offlineData == null)
                return characters;

            foreach (var charData in offlineData)
            {
                // Skip characters that have workshop excluded in AutoRetainer settings
                if (charData.ExcludeWorkshop)
                    continue;

                var submarines = GetSubmarineData(charData);

                if (submarines.Count > 0)
                {
                    var enabledSubs = charData.EnabledSubs?.ToList() ?? new List<string>();

                    // Only include unlock data for the currently logged-in character
                    // HousingManager.IsSubmarineExplorationUnlocked only returns accurate data
                    // for the current character's FC, not for other FCs
                    var charUnlocks = charData.CID == currentCid ? unlockedSectors : new List<int>();

                    // Get submarine parts inventory from InventoryTools (if available)
                    var inventoryParts = GetCharacterSubmarinePartsInventory(charData.CID);

                    // Get salvage accessories value from InventoryTools (if available)
                    var salvageValue = GetCharacterSalvageValue(charData.CID);

                    characters.Add(new Dictionary<string, object>
                    {
                        ["cid"] = charData.CID.ToString(),
                        ["name"] = charData.Name ?? "",
                        ["world"] = charData.World ?? "",
                        ["fc_id"] = charData.FCID.ToString(),
                        ["gil"] = charData.Gil,
                        ["ceruleum"] = charData.Ceruleum,
                        ["repair_kits"] = charData.RepairKits,
                        ["num_sub_slots"] = charData.NumSubSlots,
                        ["enabled_subs"] = enabledSubs,
                        ["submarines"] = submarines,
                        ["unlocked_sectors"] = charUnlocks,
                        ["inventory_parts"] = inventoryParts,
                        ["salvage_value"] = salvageValue
                    });

                    // Track this FC as active (has non-excluded characters)
                    activeFcIds.Add(charData.FCID.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error reading character data - {ex.Message}");
        }

        return characters;
    }

    private List<Dictionary<string, object>> GetSubmarineData(OfflineCharacterData charData)
    {
        var submarines = new List<Dictionary<string, object>>();

        try
        {
            var offlineSubs = charData.OfflineSubmarineData;
            var additionalSubs = charData.AdditionalSubmarineData;

            if (offlineSubs == null)
                return submarines;

            foreach (var sub in offlineSubs)
            {
                var subName = sub.Name ?? "";
                var returnTime = sub.ReturnTime;

                if (returnTime <= 0)
                    continue;

                // Get additional data for this submarine
                AdditionalVesselData? addData = null;
                additionalSubs?.TryGetValue(subName, out addData);

                // Decode current route points
                var currentPoints = new List<int>();
                if (addData?.Points != null)
                {
                    foreach (var b in addData.Points)
                    {
                        if (b > 0) currentPoints.Add(b);
                    }
                }

                submarines.Add(new Dictionary<string, object>
                {
                    ["name"] = subName,
                    ["return_time"] = returnTime,
                    ["level"] = addData?.Level ?? 0,
                    ["current_exp"] = addData?.CurrentExp ?? 0,
                    ["next_level_exp"] = addData?.NextLevelExp ?? 0,
                    ["part1"] = addData?.Part1 ?? 0,
                    ["part2"] = addData?.Part2 ?? 0,
                    ["part3"] = addData?.Part3 ?? 0,
                    ["part4"] = addData?.Part4 ?? 0,
                    // Row IDs (1-40) for duration calculation - order: Hull, Stern, Bow, Bridge
                    ["part_row_ids"] = new List<int>
                    {
                        GetPartRowId(addData?.Part1 ?? 0),  // Hull
                        GetPartRowId(addData?.Part2 ?? 0),  // Stern
                        GetPartRowId(addData?.Part3 ?? 0),  // Bow
                        GetPartRowId(addData?.Part4 ?? 0),  // Bridge
                    },
                    ["selected_route"] = addData?.SelectedPointPlan ?? "",
                    ["current_route_points"] = currentPoints
                });
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error reading submarine data - {ex.Message}");
        }

        return submarines;
    }

    /// <summary>
    /// Calculate the total gil value of salvage accessories in a character's inventory.
    /// Uses AllaganTools IPC to get item counts and Lumina to get vendor prices.
    /// </summary>
    /// <param name="characterId">The character's Content ID (CID)</param>
    /// <returns>Total gil value of all salvage items</returns>
    private long GetCharacterSalvageValue(ulong characterId)
    {
        if (_inventoryApi == null)
        {
            PluginLog.Debug("Armada: Salvage check - InventoryApi is null");
            return 0;
        }

        if (!_inventoryApi.IsAvailable)
        {
            PluginLog.Debug("Armada: Salvage check - InventoryApi not available");
            return 0;
        }

        try
        {
            var itemSheet = Svc.Data.GetExcelSheet<Item>();
            if (itemSheet == null)
            {
                PluginLog.Debug("Armada: Salvage check - Item sheet is null");
                return 0;
            }

            long totalValue = 0;

            foreach (var itemId in SalvageItemIds)
            {
                var count = _inventoryApi.GetItemCount(itemId, characterId);
                if (count > 0)
                {
                    var item = itemSheet.GetRowOrDefault(itemId);
                    if (item.HasValue)
                    {
                        var itemValue = (long)item.Value.PriceLow * count;
                        totalValue += itemValue;
                        PluginLog.Debug($"Armada: Salvage - {item.Value.Name} x{count} = {itemValue:N0} gil (ID: {itemId}, PriceLow: {item.Value.PriceLow})");
                    }
                    else
                    {
                        PluginLog.Debug($"Armada: Salvage - Item ID {itemId} not found in sheet");
                    }
                }
            }

            PluginLog.Debug($"Armada: Character {characterId} salvage total: {totalValue:N0} gil");
            return totalValue;
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Failed to calculate salvage value for {characterId} - {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Get submarine parts inventory for a character using InventoryTools IPC.
    /// </summary>
    /// <param name="characterId">The character's Content ID (CID)</param>
    /// <returns>Dictionary of itemId (as string) -> count for parts in inventory</returns>
    private Dictionary<string, uint> GetCharacterSubmarinePartsInventory(ulong characterId)
    {
        var result = new Dictionary<string, uint>();

        if (_inventoryApi == null || !_inventoryApi.IsAvailable)
            return result;

        try
        {
            var parts = _inventoryApi.GetSubmarinePartsInventory(characterId, AllPartItemIds);
            foreach (var kvp in parts)
            {
                // Use string keys for JSON serialization consistency
                result[kvp.Key.ToString()] = kvp.Value;
            }

            if (result.Count > 0)
            {
                PluginLog.Debug($"Armada: Found {result.Count} submarine part types in inventory for character {characterId}");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"Armada: Failed to get submarine parts inventory for {characterId} - {ex.Message}");
        }

        return result;
    }

    public void Dispose()
    {
        try
        {
            _inventoryApi?.Dispose();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error disposing InventoryTools API - {ex.Message}");
        }

        try
        {
            _api?.Dispose();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error disposing AutoRetainer API - {ex.Message}");
        }

        _inventoryApi = null;
        _api = null;
        _isReady = false;
    }
}
