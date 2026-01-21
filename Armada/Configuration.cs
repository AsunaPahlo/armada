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
}
