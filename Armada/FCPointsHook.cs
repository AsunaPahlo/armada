using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using ECommons.Automation.NeoTaskManager;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Callback = ECommons.Automation.Callback;

namespace Armada;

/// <summary>
/// Hooks the FC dialog packet to capture live FC points (credits).
/// Periodically triggers the FC window open/close to force the packet.
/// Falls back gracefully if the hook fails (signature breaks, etc).
/// </summary>
public unsafe class FCPointsHook : IDisposable
{
    private const string FreeCompanyDialogSig =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? " +
        "48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? " +
        "0F B6 42 31";

    private delegate nint FreeCompanyDialogDelegate(nint a1, nint a2);
    private readonly Hook<FreeCompanyDialogDelegate>? _hook;

    private readonly TaskManager _taskManager;

    // FC ID -> (points, timestamp)
    private readonly ConcurrentDictionary<ulong, CachedFCPoints> _cache = new();

    private DateTime _lastRefreshTime = DateTime.MinValue;
    private DateTime _refreshStartTime = DateTime.MinValue;
    private bool _refreshInProgress;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CacheStaleThreshold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(15);

    public bool IsHooked => _hook?.IsEnabled ?? false;

    public FCPointsHook()
    {
        _taskManager = new TaskManager(new TaskManagerConfiguration(timeLimitMS: 10000, abortOnTimeout: true, showDebug: false));

        try
        {
            var ptr = Svc.SigScanner.ScanText(FreeCompanyDialogSig);
            _hook = Hook.HookFromAddress<FreeCompanyDialogDelegate>(ptr, OnFCDialogPacket);
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Failed to create FCPointsHook - {ex.Message}");
        }
    }

    public void Initialize()
    {
        try
        {
            _hook?.Enable();
            if (_hook != null)
                PluginLog.Information("Armada: FCPointsHook initialized successfully");
            else
                PluginLog.Warning("Armada: FCPointsHook not available (signature scan failed)");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Failed to enable FCPointsHook - {ex.Message}");
        }
    }

    // --- Hook callback ---

    private nint OnFCDialogPacket(nint a1, nint a2)
    {
        try
        {
            var fcPoints = *(int*)(a2 + 24);
            var fcId = InfoProxyFreeCompany.Instance()->Id;

            if (fcId != 0 && fcPoints >= 0)
            {
                _cache[fcId] = new CachedFCPoints
                {
                    Points = fcPoints,
                    CapturedAt = DateTime.UtcNow
                };
                PluginLog.Debug($"Armada: Captured live FC points for FC {fcId}: {fcPoints:N0}");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error in FCPointsHook callback - {ex.Message}");
        }

        return _hook!.Original(a1, a2);
    }

    // --- Public API ---

    /// <summary>
    /// Get cached live FC points if available and fresh.
    /// Returns null if no live data or data is stale.
    /// </summary>
    public long? GetLiveFCPoints(ulong fcId)
    {
        if (_cache.TryGetValue(fcId, out var cached))
        {
            var age = DateTime.UtcNow - cached.CapturedAt;
            if (age < CacheStaleThreshold)
            {
                PluginLog.Verbose($"Armada: FC points cache hit for FC {fcId}: {cached.Points:N0} ({age.TotalMinutes:F1} min old)");
                return cached.Points;
            }
            PluginLog.Verbose($"Armada: FC points cache stale for FC {fcId}: {cached.Points:N0} ({age.TotalMinutes:F1} min old, threshold: {CacheStaleThreshold.TotalMinutes} min)");
        }
        else
        {
            PluginLog.Verbose($"Armada: FC points cache miss for FC {fcId} - no live data, using AutoRetainer fallback");
        }
        return null;
    }

    /// <summary>
    /// Request a refresh of FC points for the currently logged-in character's FC.
    /// Opens and immediately closes the FC window to trigger the packet.
    /// No-op if refresh is in progress, too recent, or conditions aren't met.
    /// </summary>
    public void RequestRefresh()
    {
        // Safety: auto-reset if refresh has been stuck
        if (_refreshInProgress && DateTime.UtcNow - _refreshStartTime > RefreshTimeout)
        {
            PluginLog.Warning("Armada: FC points refresh timed out, resetting");
            _refreshInProgress = false;
            _taskManager.Abort();
        }

        if (_refreshInProgress)
        {
            PluginLog.Verbose("Armada: FC points refresh skipped - already in progress");
            return;
        }

        if (DateTime.UtcNow - _lastRefreshTime < RefreshInterval)
        {
            PluginLog.Verbose($"Armada: FC points refresh skipped - last refresh was {(DateTime.UtcNow - _lastRefreshTime).TotalMinutes:F1} min ago (interval: {RefreshInterval.TotalMinutes} min)");
            return;
        }

        // Check if current FC's cache is still fresh — skip if so
        try
        {
            var currentFcId = Svc.Framework.RunOnFrameworkThread(() =>
                InfoProxyFreeCompany.Instance()->Id
            ).Result;

            if (currentFcId != 0 && _cache.TryGetValue(currentFcId, out var cached) &&
                DateTime.UtcNow - cached.CapturedAt < RefreshInterval)
            {
                PluginLog.Verbose($"Armada: FC points refresh skipped - cache for FC {currentFcId} is fresh ({(DateTime.UtcNow - cached.CapturedAt).TotalMinutes:F1} min old, value: {cached.Points:N0})");
                return;
            }
        }
        catch
        {
            // If we can't check, proceed with the refresh attempt
        }

        if (!CanTriggerRefresh())
        {
            PluginLog.Verbose("Armada: FC points refresh skipped - conditions not met (off-world, in duty, etc.)");
            return;
        }

        PluginLog.Information("Armada: Triggering FC points refresh via /fccmd");
        _refreshInProgress = true;
        _refreshStartTime = DateTime.UtcNow;
        _lastRefreshTime = DateTime.UtcNow;
        _taskManager.Abort();

        EnqueueRefreshTasks();
    }

    // --- Trigger logic ---

    private bool CanTriggerRefresh()
    {
        try
        {
            return Svc.Framework.RunOnFrameworkThread(() =>
            {
                if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null)
                    return false;

                var player = Svc.Objects.LocalPlayer;
                if (player.CurrentWorld.RowId != player.HomeWorld.RowId)
                    return false;

                var cond = Svc.Condition;
                if (cond[ConditionFlag.BoundByDuty] ||
                    cond[ConditionFlag.BoundByDuty56] ||
                    cond[ConditionFlag.BoundByDuty95] ||
                    cond[ConditionFlag.OccupiedInCutSceneEvent] ||
                    cond[ConditionFlag.WatchingCutscene] ||
                    cond[ConditionFlag.WatchingCutscene78] ||
                    cond[ConditionFlag.BetweenAreas] ||
                    cond[ConditionFlag.BetweenAreas51] ||
                    cond[ConditionFlag.OccupiedSummoningBell])
                    return false;

                return IsScreenReady();
            }).Result;
        }
        catch
        {
            return false;
        }
    }

    private void EnqueueRefreshTasks()
    {
        // Step 1: If FC window is already open, close it first
        _taskManager.Enqueue(() =>
        {
            if (TryGetAddonByName<AtkUnitBase>("FreeCompany", out var addon) && addon->IsReady())
            {
                PluginLog.Verbose("Armada: FC window already open, closing first");
                Callback.Fire(addon, true, -1);
                return false; // keep checking until closed
            }
            return true; // not open, proceed
        }, "EnsureFCWindowClosed");

        // Step 2: Open FC window (triggers the packet → hook captures points)
        _taskManager.Enqueue(() =>
        {
            PluginLog.Verbose("Armada: Opening FC window via /fccmd");
            ECommons.Automation.Chat.ExecuteCommand("/fccmd");
        }, "OpenFCWindow");

        // Step 3: Wait for FC window to appear and be ready
        _taskManager.Enqueue(() =>
        {
            return TryGetAddonByName<AtkUnitBase>("FreeCompany", out var addon) && addon->IsReady();
        }, "WaitForFCWindow");

        // Step 4: Close FC window and resend fleet data with updated FC points
        _taskManager.Enqueue(() =>
        {
            if (TryGetAddonByName<AtkUnitBase>("FreeCompany", out var addon) && addon->IsReady())
            {
                PluginLog.Verbose("Armada: FC window ready, closing");
                Callback.Fire(addon, true, -1);
            }
            _refreshInProgress = false;
            PluginLog.Information("Armada: FC points refresh complete, triggering resend");

            // Resend fleet data so the web gets updated FC points immediately.
            // This is important for accounts with no subs — they have no condition
            // change triggers, so the initial send (before the hook fired) may be
            // the only one, and it would have fc_credits: 0.
            P.FleetDataProvider?.ForceSend();
        }, "CloseFCWindow");
    }

    public void Dispose()
    {
        try
        {
            _refreshInProgress = false;
            _taskManager.Abort();
            _hook?.Disable();
            _hook?.Dispose();
            _cache.Clear();
            PluginLog.Information("Armada: FCPointsHook disposed");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Error disposing FCPointsHook - {ex.Message}");
        }
    }
}

public class CachedFCPoints
{
    public long Points { get; set; }
    public DateTime CapturedAt { get; set; }
}
