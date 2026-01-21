using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace Armada;

public unsafe class VoyageLootHook : IDisposable
{
    // Signature from SubmarineTracker - CustomTalkEventResponsePacketHandler (https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Game/Network/Internal/NetworkHandlersAddressResolver.cs)
    private const string PacketReceiverSig = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 49 8B D9 41 0F B6 F8 0F B7 F2 8B E9 E8 ?? ?? ?? ?? 44 0F B6 54 24 ?? 44 0F B6 CF 44 88 54 24 ?? 44 0F B7 C6 8B D5";

    private delegate void PacketDelegate(nuint a1, ushort eventId, byte responseId, uint* args, byte argCount);
    private readonly Hook<PacketDelegate>? _packetHandlerHook;

    public event Action<VoyageLootData>? OnVoyageCompleted;

    public bool IsHooked => _packetHandlerHook?.IsEnabled ?? false;

    public VoyageLootHook()
    {
        try
        {
            var packetReceiverPtr = Svc.SigScanner.ScanText(PacketReceiverSig);
            _packetHandlerHook =  Hook.HookFromAddress<PacketDelegate>(
                packetReceiverPtr,
                PacketReceiver
            );
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Failed to create VoyageLootHook - {ex.Message}");
        }
    }

    public void Initialize()
    {
        try
        {
            _packetHandlerHook?.Enable();
            PluginLog.Information("Armada: VoyageLootHook initialized successfully");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Failed to enable VoyageLootHook - {ex.Message}");
        }
    }

    private void PacketReceiver(nuint a1, ushort eventId, byte responseId, uint* args, byte argCount)
    {
        // Call original first
        _packetHandlerHook?.Original(a1, eventId, responseId, args, argCount);

        // Check if this is the voyage result event (721343 = 0xB01FF)
        if (a1 != 721343)
            return;

        try
        {
            CaptureVoyageLoot();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error in PacketReceiver - {ex.Message}");
        }
    }

    private void CaptureVoyageLoot()
    {
        var instance = HousingManager.Instance();
        if (instance == null || instance->WorkshopTerritory == null)
        {
            PluginLog.Debug("Armada: Not in workshop territory");
            return;
        }

        // Index 4 is the submarine currently being collected
        var current = instance->WorkshopTerritory->Submersible.DataPointers[4];
        if (current.Value == null)
        {
            PluginLog.Debug("Armada: No current submarine data");
            return;
        }

        var sub = current.Value;
        var gatheredData = sub->GatheredData;

        // Check if there's any loot (first item's primary should have data)
        if (gatheredData[0].ItemIdPrimary == 0)
        {
            PluginLog.Debug("Armada: No loot data found");
            return;
        }

        // Get player info
        var characterName = "";
        var fcTag = "";
        var localPlayer = Svc.Objects.LocalPlayer;
        if (localPlayer != null)
        {
            characterName = localPlayer.Name.ToString();
            fcTag = localPlayer.CompanyTag.ToString();
        }

        // Get FC ID
        var fcId = InfoProxyFreeCompany.Instance()->Id;

        // Get submarine name (Name is a fixed byte span, need to extract as SeString)
        var nameSpan = sub->Name;
        var submarineName = new ReadOnlySeStringSpan((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(nameSpan))).ExtractText();
        var registerTime = sub->RegisterTime;

        PluginLog.Information($"Armada: Capturing loot for submarine '{submarineName}'");

        // Build loot data
        var lootData = new VoyageLootData
        {
            CharacterName = characterName,
            FcId = fcId.ToString(),
            FcTag = fcTag,
            SubmarineName = submarineName,
            Sectors = new List<int>(),
            Items = new List<VoyageLootItem>(),
            CapturedAt = DateTime.UtcNow
        };

        // Get item sheet for lookups
        var itemSheet = Svc.Data.GetExcelSheet<Item>();

        // Iterate through gathered data and extract loot from valid sectors
        foreach (var sector in gatheredData)
        {
            // Skip empty sectors (Point == 0 means no sector visited)
            if (sector.Point == 0)
                continue;

            lootData.Sectors.Add((int)sector.Point);

            // Skip if no items from this sector
            if (sector.ItemIdPrimary == 0 && sector.ItemIdAdditional == 0)
                continue;

            var lootItem = new VoyageLootItem
            {
                SectorId = (int)sector.Point
            };

            // Primary item
            if (sector.ItemIdPrimary > 0)
            {
                lootItem.ItemIdPrimary = sector.ItemIdPrimary;
                lootItem.CountPrimary = sector.ItemCountPrimary;
                lootItem.HqPrimary = sector.ItemHQPrimary;

                if (itemSheet != null)
                {
                    var item = itemSheet.GetRowOrDefault(sector.ItemIdPrimary);
                    if (item.HasValue)
                    {
                        lootItem.ItemNamePrimary = item.Value.Name.ToString();
                        lootItem.VendorPricePrimary = item.Value.PriceLow;
                    }
                }
            }

            // Additional item
            if (sector.ItemIdAdditional > 0)
            {
                lootItem.ItemIdAdditional = sector.ItemIdAdditional;
                lootItem.CountAdditional = sector.ItemCountAdditional;
                lootItem.HqAdditional = sector.ItemHQAdditional;

                if (itemSheet != null)
                {
                    var item = itemSheet.GetRowOrDefault(sector.ItemIdAdditional);
                    if (item.HasValue)
                    {
                        lootItem.ItemNameAdditional = item.Value.Name.ToString();
                        lootItem.VendorPriceAdditional = item.Value.PriceLow;
                    }
                }
            }

            lootData.Items.Add(lootItem);
        }

        if (lootData.Items.Count > 0)
        {
            PluginLog.Information($"Armada: Captured loot for '{submarineName}' - {lootData.Items.Count} items, {lootData.TotalGilValue:N0} gil value");
            OnVoyageCompleted?.Invoke(lootData);
        }
    }

    public void Dispose()
    {
        try
        {
            _packetHandlerHook?.Disable();
            _packetHandlerHook?.Dispose();
            PluginLog.Information("Armada: VoyageLootHook disposed");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error disposing VoyageLootHook - {ex.Message}");
        }
    }
}

public class VoyageLootData
{
    public string CharacterName { get; set; } = "";
    public string FcId { get; set; } = "";
    public string FcTag { get; set; } = "";
    public string SubmarineName { get; set; } = "";
    public List<int> Sectors { get; set; } = new();
    public List<VoyageLootItem> Items { get; set; } = new();
    public DateTime CapturedAt { get; set; }

    public int TotalGilValue => Items.Sum(i =>
        (int)(i.VendorPricePrimary * i.CountPrimary) +
        (int)(i.VendorPriceAdditional * i.CountAdditional));
}

public class VoyageLootItem
{
    public int SectorId { get; set; }

    // Primary item
    public uint ItemIdPrimary { get; set; }
    public string ItemNamePrimary { get; set; } = "";
    public ushort CountPrimary { get; set; }
    public bool HqPrimary { get; set; }
    public uint VendorPricePrimary { get; set; }

    // Additional item
    public uint ItemIdAdditional { get; set; }
    public string ItemNameAdditional { get; set; } = "";
    public ushort CountAdditional { get; set; }
    public bool HqAdditional { get; set; }
    public uint VendorPriceAdditional { get; set; }
}
