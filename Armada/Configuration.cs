using Dalamud.Configuration;

namespace Armada;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Whether the plugin is enabled
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Armada server WebSocket URL (e.g., "ws://localhost:5000")
    /// </summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>
    /// API key for authentication
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Nickname for this client instance
    /// </summary>
    public string Nickname { get; set; } = Environment.MachineName;

    /// <summary>
    /// Characters designated as suppliers (inventory mules for ceruleum/repair materials).
    /// Key is character Content ID (CID).
    /// </summary>
    public Dictionary<ulong, SupplierCharacter> Suppliers { get; set; } = new();
}

/// <summary>
/// A character designated as a supplier of ceruleum and repair materials.
/// </summary>
public class SupplierCharacter
{
    public string Name { get; set; } = "";
    public string World { get; set; } = "";
    public uint Ceruleum { get; set; }
    public uint RepairKits { get; set; }
    public ulong FcId { get; set; }
    public long FcCredits { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
}
