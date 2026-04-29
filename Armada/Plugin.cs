using Armada.Windows;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using ECommons.Configuration;
using ECommons.Schedulers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Armada;

public sealed class Plugin : IDalamudPlugin
{
    public static Plugin P { get; private set; } = null!;
    public static Configuration C { get; private set; } = null!;

    [PluginService] public static IGameInteropProvider Hook { get; private set; } = null!;

    private const string CommandName = "/armada";

    public readonly WindowSystem WindowSystem = new("Armada");
    private ConfigWindow ConfigWindow { get; set; } = null!;

    public ArmadaClient ArmadaClient { get; private set; } = null!;
    public FleetDataProvider FleetDataProvider { get; private set; } = null!;
    public VoyageLootHook VoyageLootHook { get; private set; } = null!;
    public FCPointsHook FCPointsHook { get; private set; } = null!;
    public DataCache DataCache { get; private set; } = null!;

    // Deferred login send: wait for character to be fully available (e.g. inn cutscene over)
    private bool _pendingLoginSend;
    private DateTime _loginSendDeadline;
    private DateTime _lastLoginPollTime;
    private static readonly TimeSpan LoginSendTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoginPollInterval = TimeSpan.FromSeconds(2);

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        P = this;
        ECommonsMain.Init(pluginInterface, this);

        new TickScheduler(() =>
        {
            C = EzConfig.Init<Configuration>();

            // One-time migration: earlier builds mixed EzConfig.Save() with SavePluginConfig(C),
            // so supplier/connection data ended up in pluginConfigs/Armada.json while the EzConfig
            // file kept empty defaults. If the legacy file is richer, adopt it and persist via EzConfig.
            if (Svc.PluginInterface.GetPluginConfig() is Configuration legacy
                && (legacy.Suppliers.Count > C.Suppliers.Count
                    || (string.IsNullOrEmpty(C.ApiKey) && !string.IsNullOrEmpty(legacy.ApiKey))))
            {
                PluginLog.Warning("Armada: Migrating legacy Dalamud-path config into EzConfig store");
                C = legacy;
                EzConfig.Save();
            }

            // Initialize data cache first (so it's available for other components)
            DataCache = new DataCache();

            // Initialize Armada client and fleet data provider
            ArmadaClient = new ArmadaClient();
            FleetDataProvider = new FleetDataProvider();
            FleetDataProvider.Initialize();

            // Initialize voyage loot hook
            VoyageLootHook = new VoyageLootHook();
            VoyageLootHook.Initialize();
            VoyageLootHook.OnVoyageCompleted += OnVoyageLootCaptured;

            // Initialize FC points hook (live FC credits via packet intercept)
            FCPointsHook = new FCPointsHook();
            FCPointsHook.Initialize();

            // Send fleet data and flush cache after authentication
            ArmadaClient.OnAuthenticated += OnAuthenticated;

            // Subscribe to game events
            Svc.Condition.ConditionChange += OnConditionChange;
            Svc.Framework.Update += OnFrameworkUpdate;
            Svc.ClientState.Logout += OnLogout;

            // Initialize config window
            ConfigWindow = new ConfigWindow();
            WindowSystem.AddWindow(ConfigWindow);

            // Register command
            Svc.Commands.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
            {
                HelpMessage = "Open Armada configuration"
            });

            
            // Register UI callbacks
            Svc.PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
            Svc.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

            // Check if configured
            if (string.IsNullOrEmpty(C.ServerUrl) || string.IsNullOrEmpty(C.ApiKey))
            {
                // First launch - open config window
                ConfigWindow.IsOpen = true;
                PluginLog.Information("Armada plugin loaded - please configure");
            }
            else if (C.Enabled)
            {
                _ = ArmadaClient.ConnectAsync();
                PluginLog.Information("Armada plugin loaded");
            }
            
            
            ProperOnLogin.RegisterAvailable(() =>
            {
                var cid = Svc.PlayerState.ContentId;
                bool isSupplier;
                lock (C.Suppliers)
                {
                    isSupplier = C.Suppliers.ContainsKey(cid);
                }

                if (isSupplier && C.Enabled)
                {
                    var player = Svc.Objects.LocalPlayer!;
                    if (player.CurrentWorld.RowId != player.HomeWorld.RowId)
                        return;

                    if (IsCharacterReady())
                    {
                        PluginLog.Information($"Armada: Supplier {player.Name.TextValue} logged in on homeworld, sending data");
                        FleetDataProvider.ForceSend();
                    }
                    else
                    {
                        PluginLog.Information($"Armada: Supplier {player.Name.TextValue} logged in on homeworld but not ready (cutscene/loading), deferring send");
                        _pendingLoginSend = true;
                        _loginSendDeadline = DateTime.UtcNow + LoginSendTimeout;
                    }
                }
            }, true);
        });
    }
    


    public void Dispose()
    {
        // Unsubscribe from game events
        Svc.Condition.ConditionChange -= OnConditionChange;
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ClientState.Logout -= OnLogout;

        // Unsubscribe from internal events
        if (ArmadaClient != null)
            ArmadaClient.OnAuthenticated -= OnAuthenticated;
        if (VoyageLootHook != null)
            VoyageLootHook.OnVoyageCompleted -= OnVoyageLootCaptured;

        // Stop services
        FCPointsHook?.Dispose();
        VoyageLootHook?.Dispose();
        FleetDataProvider?.Dispose();
        ArmadaClient?.Dispose();
        DataCache?.Dispose();

        // Unregister UI callbacks
        Svc.PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        // Clean up windows
        WindowSystem.RemoveAllWindows();
        ConfigWindow?.Dispose();

        // Remove command
        Svc.Commands.RemoveHandler(CommandName);

        ECommonsMain.Dispose();
        P = null!;
    }

    private void OnCommand(string command, string args)
    {
        ConfigWindow.Toggle();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    private bool IsCharacterReady()
    {
        try
        {
            if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null)
                return false;

            var player = Svc.Objects.LocalPlayer;
            if (player.CurrentWorld.RowId != player.HomeWorld.RowId)
                return false;

            var cond = Svc.Condition;
            if (cond[ConditionFlag.OccupiedInCutSceneEvent] ||
                cond[ConditionFlag.WatchingCutscene] ||
                cond[ConditionFlag.WatchingCutscene78] ||
                cond[ConditionFlag.BetweenAreas] ||
                cond[ConditionFlag.BetweenAreas51] ||
                cond[ConditionFlag.BoundByDuty] ||
                cond[ConditionFlag.BoundByDuty56] ||
                cond[ConditionFlag.BoundByDuty95])
                return false;

            return IsScreenReady();
        }
        catch
        {
            return false;
        }
    }

    private unsafe void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (HousingManager.Instance()->WorkshopTerritory != null && flag == ConditionFlag.OccupiedInEvent && !value)
        {
            FleetDataProvider.ForceSend();
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_pendingLoginSend)
            return;

        var now = DateTime.UtcNow;

        if (now > _loginSendDeadline)
        {
            PluginLog.Warning("Armada: Deferred login send timed out, giving up");
            _pendingLoginSend = false;
            return;
        }

        if (now - _lastLoginPollTime < LoginPollInterval)
            return;

        _lastLoginPollTime = now;

        if (IsCharacterReady())
        {
            _pendingLoginSend = false;
            PluginLog.Information("Armada: Character now ready after login, sending deferred data");
            FleetDataProvider.ForceSend();
        }
    }

    private void OnLogout(int type, int code)
    {
        _pendingLoginSend = false;
    }

    private void OnAuthenticated()
    {
        // Send current fleet data
        FleetDataProvider.ForceSend();

        // Flush any cached data that couldn't be sent while disconnected
        if (DataCache.HasPendingData)
        {
            PluginLog.Information($"Armada: Flushing cached data ({DataCache.PendingFleetDataCount} fleet, {DataCache.PendingVoyageLootCount} loot)");
            Task.Run(() => DataCache.FlushAsync());
        }
    }

    private void OnVoyageLootCaptured(VoyageLootData lootData)
    {
        PluginLog.Information($"Armada: Voyage loot captured for {lootData.SubmarineName} - {lootData.Items.Count} items, {lootData.TotalGilValue:N0} gil value");

        // Send or cache the loot data (ArmadaClient will cache if not authenticated)
        Task.Run(() => ArmadaClient.SendVoyageLootAsync(lootData));
    }
}
