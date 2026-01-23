using ECommons.Configuration;

namespace Armada.Windows;

public class ConfigWindow : Window, IDisposable
{
    // Color constants for consistent styling
    private static readonly Vector4 HeaderColor = new(0.4f, 0.7f, 1.0f, 1.0f);
    private static readonly Vector4 SubtleTextColor = new(0.6f, 0.6f, 0.6f, 1.0f);
    private static readonly Vector4 WarningColor = new(1.0f, 0.6f, 0.2f, 1.0f);
    private static readonly Vector4 SuccessColor = new(0.2f, 0.8f, 0.2f, 1.0f);
    private static readonly Vector4 ErrorColor = new(0.9f, 0.3f, 0.3f, 1.0f);
    private static readonly Vector4 PendingColor = new(0.8f, 0.8f, 0.2f, 1.0f);

    public ConfigWindow()
        : base("Armada Configuration###ArmadaConfig", ImGuiWindowFlags.NoCollapse)
    {
        Size = new Vector2(550, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(450, 420),
            MaximumSize = new Vector2(700, 750)
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Connection Status Section
        DrawConnectionStatus();

        ImGui.Spacing();
        ImGui.Spacing();

        // Server Settings Section
        DrawSectionHeader("Server Settings");
        DrawServerSettings();

        ImGui.Spacing();

        // AutoRetainer API Section
        DrawSectionHeader("AutoRetainer Integration");
        DrawAutoRetainerStatus();

        // Show cache status if there's pending data
        if (P.DataCache?.HasPendingData == true)
        {
            ImGui.Spacing();
            DrawCacheStatus();
        }

        ImGui.Spacing();

        // Setup Instructions Section
        DrawSetupInstructions();

        // Action Buttons (always at bottom)
        ImGui.Spacing();
        DrawSeparator();
        ImGui.Spacing();
        DrawActionButtons();
    }

    private void DrawConnectionStatus()
    {
        var status = P.ArmadaClient.Status;
        var (statusColor, statusText) = GetStatusDisplay(status);

        // Status box with background
        var windowWidth = ImGui.GetContentRegionAvail().X;
        var boxHeight = 60f;

        // Draw background
        var cursorPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var bgColor = status == ConnectionStatus.Authenticated
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.25f, 0.1f, 0.5f))
            : status is ConnectionStatus.InvalidApiKey or ConnectionStatus.ServerUnreachable or ConnectionStatus.Error
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.1f, 0.1f, 0.5f))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.15f, 0.15f, 0.5f));

        drawList.AddRectFilled(
            cursorPos,
            new Vector2(cursorPos.X + windowWidth, cursorPos.Y + boxHeight),
            bgColor,
            4f);

        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(8, 8));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);

        // Enabled toggle on the left
        var enabled = C.Enabled;
        if (ImGui.Checkbox("##enabled", ref enabled))
        {
            C.Enabled = enabled;
            EzConfig.Save();

            if (enabled)
            {
                _ = P.ArmadaClient.ConnectAsync();
            }
            else
            {
                P.ArmadaClient.Disconnect();
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Enable/disable Armada connection");
        }

        ImGui.SameLine();
        ImGui.TextColored(statusColor, statusText);

        // Show error details if available
        if (status is ConnectionStatus.InvalidApiKey or ConnectionStatus.ServerUnreachable or ConnectionStatus.Error)
        {
            if (!string.IsNullOrEmpty(P.ArmadaClient.LastError))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(?)");
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.PushTextWrapPos(ImGui.GetFontSize() * 25.0f);
                    ImGui.TextUnformatted(P.ArmadaClient.LastError);
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
            }
        }

        // Second line: helpful message or reconnect info
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 32);

        if (status == ConnectionStatus.InvalidApiKey)
        {
            ImGui.TextColored(WarningColor, "Check your API key or generate a new one.");
        }
        else if (status == ConnectionStatus.ServerUnreachable)
        {
            ImGui.TextColored(WarningColor, "Check the server URL and ensure the server is running.");
        }
        else if (status == ConnectionStatus.Disconnected && P.ArmadaClient.NextReconnectTime.HasValue)
        {
            DrawReconnectStatus();
        }
        else if (status == ConnectionStatus.Authenticated)
        {
            ImGui.TextColored(SubtleTextColor, "Ready to sync fleet data");
        }
        else if (status is ConnectionStatus.Connecting or ConnectionStatus.Authenticating)
        {
            ImGui.TextColored(SubtleTextColor, "Please wait...");
        }
        else
        {
            ImGui.TextColored(SubtleTextColor, enabled ? "Waiting to connect..." : "Connection disabled");
        }

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.EndGroup();
    }

    private void DrawReconnectStatus()
    {
        var nextRetry = P.ArmadaClient.NextReconnectTime;
        if (!nextRetry.HasValue)
            return;

        var timeUntilRetry = nextRetry.Value - DateTime.Now;
        if (timeUntilRetry.TotalSeconds <= 0)
        {
            ImGui.TextColored(PendingColor, "Reconnecting...");
        }
        else
        {
            var minutes = (int)timeUntilRetry.TotalMinutes;
            var seconds = timeUntilRetry.Seconds;
            var attemptNum = P.ArmadaClient.ReconnectAttempts + 1;

            ImGui.TextColored(SubtleTextColor, $"Reconnect attempt #{attemptNum} in ");
            ImGui.SameLine(0, 0);
            ImGui.TextColored(PendingColor, $"{minutes}:{seconds:D2}");
        }
    }

    private static void DrawSectionHeader(string title)
    {
        ImGui.Spacing();
        DrawSeparator();
        ImGui.Spacing();
        ImGui.TextColored(HeaderColor, title);
        ImGui.Spacing();
    }

    private static void DrawSeparator()
    {
        var drawList = ImGui.GetWindowDrawList();
        var cursorPos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        drawList.AddLine(
            new Vector2(cursorPos.X, cursorPos.Y),
            new Vector2(cursorPos.X + width, cursorPos.Y),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.3f, 0.5f)),
            1f);
        ImGui.Dummy(new Vector2(0, 2));
    }

    private void DrawServerSettings()
    {
        var inputWidth = ImGui.GetContentRegionAvail().X - 100;

        // Server URL
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(SubtleTextColor, "URL");
        ImGui.SameLine(70);
        var serverUrl = C.ServerUrl;
        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.InputText("##serverurl", ref serverUrl, 256))
        {
            C.ServerUrl = serverUrl;
            EzConfig.Save();
        }
        ImGui.SameLine();
        HelpMarker("Server URL (e.g., ws://localhost:5000)");

        // API Key
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(SubtleTextColor, "API Key");
        ImGui.SameLine(70);
        var apiKey = C.ApiKey;
        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.InputText("##apikey", ref apiKey, 128, ImGuiInputTextFlags.Password))
        {
            C.ApiKey = apiKey;
            EzConfig.Save();
        }
        ImGui.SameLine();
        HelpMarker("API key from the Armada webui. See instructions below.");

        // Nickname
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(SubtleTextColor, "Nickname");
        ImGui.SameLine(70);
        var nickname = C.Nickname;
        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.InputText("##nickname", ref nickname, 64))
        {
            C.Nickname = nickname;
            EzConfig.Save();
        }
        ImGui.SameLine();
        HelpMarker("Display name shown in the webui for this client.");
    }

    private void DrawSetupInstructions()
    {
        ImGui.SetNextItemOpen(false, ImGuiCond.FirstUseEver);
        if (ImGui.CollapsingHeader("How to Get an API Key"))
        {
            ImGui.Indent(16);
            ImGui.Spacing();

            ImGui.TextWrapped("To connect this plugin to the Armada webui, you need to generate an API key:");
            ImGui.Spacing();

            var steps = new[]
            {
                "Log in to the Armada webui as an administrator",
                "Click the Settings gear icon in the top right corner",
                "Click \"API Keys\" from the menu",
                "Enter a name for your key (e.g., \"Main Account\")",
                "Click \"Create Key\"",
                "Copy the API key that appears and paste it above"
            };

            for (var i = 0; i < steps.Length; i++)
            {
                ImGui.TextColored(SubtleTextColor, $"{i + 1}.");
                ImGui.SameLine();
                ImGui.TextUnformatted(steps[i]);
            }

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.5f, 0.7f, 0.9f, 1.0f), "Tip: Create one API key per game account/client.");

            ImGui.Spacing();
            ImGui.Unindent(16);
        }
    }

    private void DrawAutoRetainerStatus()
    {
        var isReady = P.FleetDataProvider.IsReady;
        var isAvailable = P.FleetDataProvider.IsApiAvailable;

        ImGui.Indent(4);

        if (isReady && isAvailable)
        {
            ImGui.TextColored(SuccessColor, "●");
            ImGui.SameLine();
            ImGui.TextUnformatted("AutoRetainer API connected");
            ImGui.SameLine();
            ImGui.TextColored(SubtleTextColor, "- Ready to send fleet data");
        }
        else if (isReady && !isAvailable)
        {
            ImGui.TextColored(PendingColor, "●");
            ImGui.SameLine();
            ImGui.TextUnformatted("Waiting for AutoRetainer");
            ImGui.SameLine();
            ImGui.TextColored(SubtleTextColor, "- API initialized");
        }
        else
        {
            ImGui.TextColored(ErrorColor, "○");
            ImGui.SameLine();
            ImGui.TextUnformatted("AutoRetainer not available");
            ImGui.Spacing();
            ImGui.TextColored(SubtleTextColor, "Make sure AutoRetainer is installed and enabled.");
        }

        // AllaganTools status
        ImGui.Spacing();
        var isAllaganToolsAvailable = P.FleetDataProvider.IsAllaganToolsAvailable;

        if (isAllaganToolsAvailable)
        {
            ImGui.TextColored(SuccessColor, "●");
            ImGui.SameLine();
            ImGui.TextUnformatted("AllaganTools API connected");
            ImGui.SameLine();
            ImGui.TextColored(SubtleTextColor, "- Submarine parts inventory tracking enabled");
        }
        else
        {
            ImGui.TextColored(SubtleTextColor, "○");
            ImGui.SameLine();
            ImGui.TextColored(SubtleTextColor, "AllaganTools not available");
            ImGui.SameLine();
            ImGui.TextColored(SubtleTextColor, "- Optional, for parts inventory");
        }

        ImGui.Unindent(4);
    }

    private void DrawCacheStatus()
    {
        var fleetCount = P.DataCache.PendingFleetDataCount;
        var lootCount = P.DataCache.PendingVoyageLootCount;

        ImGui.Indent(4);

        ImGui.TextColored(PendingColor, "●");
        ImGui.SameLine();
        ImGui.TextUnformatted("Pending data cached");
        ImGui.SameLine();
        ImGui.TextColored(SubtleTextColor, "-");
        ImGui.SameLine();

        var parts = new List<string>();
        if (fleetCount > 0)
            parts.Add($"{fleetCount} fleet update{(fleetCount > 1 ? "s" : "")}");
        if (lootCount > 0)
            parts.Add($"{lootCount} voyage loot");

        ImGui.TextColored(SubtleTextColor, string.Join(", ", parts));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Data will be sent automatically when reconnected");
        }

        ImGui.Unindent(4);
    }

    private void DrawActionButtons()
    {
        var buttonWidth = 90f;
        var status = P.ArmadaClient.Status;
        var isConnecting = status is ConnectionStatus.Connecting or ConnectionStatus.Authenticating;

        // Connect button
        if (isConnecting) ImGui.BeginDisabled();
        if (ImGui.Button("Connect", new Vector2(buttonWidth, 0)))
        {
            _ = P.ArmadaClient.ConnectAsync();
        }
        if (isConnecting) ImGui.EndDisabled();

        ImGui.SameLine();

        // Disconnect button
        var canDisconnect = P.ArmadaClient.IsConnected || P.ArmadaClient.NextReconnectTime.HasValue;
        if (!canDisconnect) ImGui.BeginDisabled();
        if (ImGui.Button("Disconnect", new Vector2(buttonWidth, 0)))
        {
            P.ArmadaClient.Disconnect();
        }
        if (!canDisconnect) ImGui.EndDisabled();

        ImGui.SameLine();

        // Send Now button
        var canSend = P.FleetDataProvider.IsApiAvailable && P.ArmadaClient.IsAuthenticated;
        if (!canSend) ImGui.BeginDisabled();

        if (ImGui.Button("Send Now", new Vector2(buttonWidth, 0)))
        {
            P.FleetDataProvider.ForceSend();
        }

        if (!canSend) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canSend)
        {
            ImGui.SetTooltip("Requires AutoRetainer API and authenticated connection");
        }

        // Show reconnect attempts if disconnected with pending reconnect
        if (P.ArmadaClient.ReconnectAttempts > 0 && status == ConnectionStatus.Disconnected)
        {
            ImGui.SameLine();
            ImGui.TextColored(SubtleTextColor, $"(Attempt {P.ArmadaClient.ReconnectAttempts})");
        }
    }

    private static void HelpMarker(string desc)
    {
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
            ImGui.TextUnformatted(desc);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private static (Vector4 color, string text) GetStatusDisplay(ConnectionStatus status)
    {
        return status switch
        {
            ConnectionStatus.Disconnected => (new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Disconnected"),
            ConnectionStatus.Connecting => (PendingColor, "Connecting..."),
            ConnectionStatus.Connected => (PendingColor, "Connected"),
            ConnectionStatus.Authenticating => (PendingColor, "Authenticating..."),
            ConnectionStatus.Authenticated => (SuccessColor, "Connected"),
            ConnectionStatus.InvalidApiKey => (ErrorColor, "Invalid API Key"),
            ConnectionStatus.ServerUnreachable => (ErrorColor, "Server Unreachable"),
            ConnectionStatus.Error => (ErrorColor, "Error"),
            _ => (new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Unknown")
        };
    }
}
