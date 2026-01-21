using SocketIOClient;

namespace Armada;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Authenticating,
    Authenticated,
    InvalidApiKey,
    ServerUnreachable,
    Error
}

public class ArmadaClient : IDisposable
{
    private SocketIOClient.SocketIO? _socket;
    private bool _shouldReconnect;
    private Timer? _reconnectTimer;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private string? _lastError;
    private DateTime? _nextReconnectTime;
    private int _reconnectAttempts;
    private bool _isAuthenticating;
    private readonly object _authLock = new();

    // Reconnect intervals
    private const int QuickReconnectIntervalMs = 5 * 1000;  // 5 seconds for first 5 attempts
    private const int SlowReconnectIntervalMs = 5 * 60 * 1000;  // 5 minutes after that
    private const int MaxQuickRetries = 5;

    public ConnectionStatus Status => _status;
    public string? LastError => _lastError;
    public DateTime? NextReconnectTime => _nextReconnectTime;
    public int ReconnectAttempts => _reconnectAttempts;
    public bool IsConnected => _status == ConnectionStatus.Connected || _status == ConnectionStatus.Authenticating || _status == ConnectionStatus.Authenticated;
    public bool IsAuthenticated => _status == ConnectionStatus.Authenticated;

    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnAuthenticated;
    public event Action<string>? OnError;
    public event Action<ConnectionStatus>? OnStatusChanged;

    private void SetStatus(ConnectionStatus status, string? error = null)
    {
        _status = status;
        _lastError = error;

        // Clear reconnect time when connected
        if (status == ConnectionStatus.Connected || status == ConnectionStatus.Authenticated)
        {
            _nextReconnectTime = null;
        }

        // Only reset retry count on successful authentication
        if (status == ConnectionStatus.Authenticated)
        {
            _reconnectAttempts = 0;
        }

        OnStatusChanged?.Invoke(status);
    }

    public async Task ConnectAsync()
    {
        // Stop any pending reconnect timer
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        _shouldReconnect = true;

        // Reset authentication state
        lock (_authLock)
        {
            _isAuthenticating = false;
        }

        if (_socket != null)
        {
            // Detach event handlers before disposing to prevent race conditions
            _socket.OnConnected -= Socket_OnConnected;
            _socket.OnDisconnected -= Socket_OnDisconnected;
            _socket.OnError -= Socket_OnError;
            _socket.Dispose();
            _socket = null;
        }

        SetStatus(ConnectionStatus.Connecting);

        try
        {
            var serverUrl = C.ServerUrl;

            var uri = new Uri(serverUrl + "/plugin");
            _socket = new SocketIOClient.SocketIO(uri, new SocketIOOptions
            {
                Reconnection = false,  // Disabled - using custom reconnection logic instead
                Transport = SocketIOClient.Transport.TransportProtocol.WebSocket
            });

            _socket.OnConnected += Socket_OnConnected;
            _socket.OnDisconnected += Socket_OnDisconnected;
            _socket.OnError += Socket_OnError;

            // Handle authentication response
            _socket.On("auth_response", response =>
            {
                try
                {
                    var data = response.GetValue<JsonElement>();
                    var success = data.GetProperty("success").GetBoolean();

                    if (success)
                    {
                        SetStatus(ConnectionStatus.Authenticated);
                        PluginLog.Information("Armada: Authenticated successfully");
                        OnAuthenticated?.Invoke();
                    }
                    else
                    {
                        var error = data.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown error";
                        PluginLog.Error($"Armada: Authentication failed - {error}");

                        // Check if it's an invalid API key error
                        if (error != null && (error.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                                              error.Contains("api key", StringComparison.OrdinalIgnoreCase) ||
                                              error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)))
                        {
                            SetStatus(ConnectionStatus.InvalidApiKey, error);
                        }
                        else
                        {
                            SetStatus(ConnectionStatus.Error, error);
                        }

                        OnError?.Invoke($"Authentication failed: {error}");
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"Armada: Error processing auth_response - {ex.Message}");
                    SetStatus(ConnectionStatus.Error, ex.Message);
                }
            });

            // Handle data response
            _socket.On("data_response", response =>
            {
                try
                {
                    var data = response.GetValue<JsonElement>();
                    var success = data.GetProperty("success").GetBoolean();
                    var message = data.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "";

                    if (success)
                    {
                        PluginLog.Debug($"Armada: Data sent successfully - {message}");
                    }
                    else
                    {
                        var error = data.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown error";
                        PluginLog.Error($"Armada: Data send failed - {error}");
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"Armada: Error processing data_response - {ex.Message}");
                }
            });

            // Handle pong (keepalive)
            _socket.On("pong", _ =>
            {
                PluginLog.Verbose("Armada: Pong received");
            });

            // Handle loot response
            _socket.On("loot_response", response =>
            {
                try
                {
                    var data = response.GetValue<JsonElement>();
                    var success = data.GetProperty("success").GetBoolean();
                    var message = data.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "";

                    if (success)
                    {
                        PluginLog.Debug($"Armada: Loot recorded successfully - {message}");
                    }
                    else
                    {
                        var error = data.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown error";
                        PluginLog.Error($"Armada: Loot recording failed - {error}");
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"Armada: Error processing loot_response - {ex.Message}");
                }
            });

            await _socket.ConnectAsync();
        }
        catch (Exception ex)
        {
            var errorMessage = ex.InnerException?.Message ?? ex.Message;
            PluginLog.Warning($"Armada: Connection failed - {errorMessage}");
            SetStatus(ConnectionStatus.ServerUnreachable, errorMessage);
            OnError?.Invoke($"Connection failed: {errorMessage}");

            // Schedule reconnection attempt if enabled
            ScheduleNextReconnect();
        }
    }


    private async void Socket_OnConnected(object? sender, EventArgs e)
    {
        SetStatus(ConnectionStatus.Connected);
        PluginLog.Information("Armada: Connected to server");

        // Prevent multiple concurrent authentication attempts
        lock (_authLock)
        {
            if (_isAuthenticating)
            {
                PluginLog.Debug("Armada: Authentication already in progress, skipping");
                return;
            }
            _isAuthenticating = true;
        }

        try
        {
            // Check socket is still valid
            var socket = _socket;
            if (socket == null)
            {
                PluginLog.Warning("Armada: Socket is null, cannot authenticate");
                return;
            }

            // Authenticate
            SetStatus(ConnectionStatus.Authenticating);
            PluginLog.Information("Armada: Sending authenticate event...");
            await socket.EmitAsync("authenticate", new
            {
                api_key = C.ApiKey,
                nickname = C.Nickname,
                plugin_version = "1.0.0"
            });
            PluginLog.Information("Armada: Authenticate event sent");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Failed to send authenticate - {ex.Message}");
            SetStatus(ConnectionStatus.Error, ex.Message);
        }
        finally
        {
            lock (_authLock)
            {
                _isAuthenticating = false;
            }
        }

        OnConnected?.Invoke();
    }

    private void Socket_OnDisconnected(object? sender, string reason)
    {
        // Reset authentication state
        lock (_authLock)
        {
            _isAuthenticating = false;
        }

        SetStatus(ConnectionStatus.Disconnected, reason);
        PluginLog.Information($"Armada: Disconnected - {reason}");
        OnDisconnected?.Invoke();

        // Start reconnect timer if we should keep trying
        if (_shouldReconnect && _reconnectTimer == null)
        {
            ScheduleNextReconnect();
        }
    }

    private async void AttemptReconnect()
    {
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        _nextReconnectTime = null;

        if (!_shouldReconnect || IsConnected)
        {
            return;
        }

        _reconnectAttempts++;
        PluginLog.Information($"Armada: Attempting reconnection (attempt #{_reconnectAttempts})...");

        try
        {
            await ConnectAsync();
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Reconnection attempt failed - {ex.Message}");
            ScheduleNextReconnect();
        }
    }

    private void ScheduleNextReconnect()
    {
        if (!_shouldReconnect || IsConnected || _reconnectTimer != null)
            return;

        int intervalMs;
        string intervalDesc;

        if (_reconnectAttempts == 0)
        {
            // First attempt: small delay to let old socket release
            intervalMs = 1000;
            intervalDesc = "1 second";
        }
        else if (_reconnectAttempts < MaxQuickRetries)
        {
            // Quick retries for attempts 2-5
            intervalMs = QuickReconnectIntervalMs;
            intervalDesc = "5 seconds";
        }
        else
        {
            // Slow retries after 5 failed attempts
            intervalMs = SlowReconnectIntervalMs;
            intervalDesc = "5 minutes";
        }

        _nextReconnectTime = intervalMs > 0 ? DateTime.Now.AddMilliseconds(intervalMs) : DateTime.Now;
        PluginLog.Information($"Armada: Will attempt reconnection {intervalDesc} (attempt #{_reconnectAttempts + 1})");
        _reconnectTimer = new Timer(_ => AttemptReconnect(), null, intervalMs, Timeout.Infinite);
    }

    private void Socket_OnError(object? sender, string error)
    {
        PluginLog.Error($"Armada: Socket error - {error}");
        SetStatus(ConnectionStatus.Error, error);
        OnError?.Invoke(error);
    }

    public async Task<bool> SendFleetDataAsync(List<Dictionary<string, object>> accountsData, bool fromCache = false)
    {
        if (!IsConnected || !IsAuthenticated)
        {
            PluginLog.Warning("Armada: Cannot send data - not connected or authenticated");

            // Cache the data for later if not already from cache
            if (!fromCache && accountsData.Count > 0)
            {
                foreach (var data in accountsData)
                {
                    P.DataCache?.CacheFleetData(data);
                }
            }

            return false;
        }

        try
        {
            // Serialize the accounts data to JSON
            var jsonData = System.Text.Json.JsonSerializer.Serialize(accountsData);

            // Compress the JSON data
            var compressedData = CompressString(jsonData);

            PluginLog.Debug($"Armada: Compressed {jsonData.Length} bytes to {compressedData.Length} bytes ({100 - (compressedData.Length * 100 / jsonData.Length)}% reduction)");

            await _socket!.EmitAsync("fleet_data", new
            {
                api_key = C.ApiKey,
                timestamp = DateTime.UtcNow.ToString("o"),
                compressed = true,
                data = compressedData
            });

            PluginLog.Debug($"Armada: Fleet data sent ({accountsData.Count} account(s))");
            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Failed to send fleet data - {ex.Message}");

            // Cache the data for later if not already from cache
            if (!fromCache && accountsData.Count > 0)
            {
                foreach (var data in accountsData)
                {
                    P.DataCache?.CacheFleetData(data);
                }
            }

            return false;
        }
    }

    public async Task<bool> SendVoyageLootAsync(VoyageLootData lootData, bool fromCache = false)
    {
        if (!IsConnected || !IsAuthenticated)
        {
            PluginLog.Warning("Armada: Cannot send voyage loot - not connected or authenticated");

            // Cache the loot for later if not already from cache
            if (!fromCache)
            {
                P.DataCache?.CacheVoyageLoot(lootData);
            }

            return false;
        }

        try
        {
            // Build items array for payload
            var items = lootData.Items.Select(item => new Dictionary<string, object>
            {
                ["sector_id"] = item.SectorId,
                ["item_id_primary"] = item.ItemIdPrimary,
                ["item_name_primary"] = item.ItemNamePrimary,
                ["count_primary"] = item.CountPrimary,
                ["hq_primary"] = item.HqPrimary,
                ["vendor_price_primary"] = item.VendorPricePrimary,
                ["item_id_additional"] = item.ItemIdAdditional,
                ["item_name_additional"] = item.ItemNameAdditional,
                ["count_additional"] = item.CountAdditional,
                ["hq_additional"] = item.HqAdditional,
                ["vendor_price_additional"] = item.VendorPriceAdditional
            }).ToList();

            await _socket!.EmitAsync("voyage_loot", new
            {
                api_key = C.ApiKey,
                character_name = lootData.CharacterName,
                fc_id = lootData.FcId,
                fc_tag = lootData.FcTag,
                submarine_name = lootData.SubmarineName,
                sectors = lootData.Sectors,
                items = items,
                total_gil_value = lootData.TotalGilValue,
                captured_at = lootData.CapturedAt.ToString("o")
            });

            PluginLog.Information($"Armada: Voyage loot sent for {lootData.SubmarineName}");
            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Armada: Failed to send voyage loot - {ex.Message}");

            // Cache the loot for later if not already from cache
            if (!fromCache)
            {
                P.DataCache?.CacheVoyageLoot(lootData);
            }

            return false;
        }
    }

    private static string CompressString(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        using var outputStream = new MemoryStream();
        using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Optimal))
        {
            gzipStream.Write(bytes, 0, bytes.Length);
        }
        return Convert.ToBase64String(outputStream.ToArray());
    }

    public async Task SendPingAsync()
    {
        if (!IsConnected)
            return;

        try
        {
            await _socket!.EmitAsync("ping");
        }
        catch (Exception ex)
        {
            PluginLog.Verbose($"Armada: Ping failed - {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        // Stop reconnection attempts
        _shouldReconnect = false;
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        _nextReconnectTime = null;
        _reconnectAttempts = 0;

        // Reset authentication state
        lock (_authLock)
        {
            _isAuthenticating = false;
        }

        if (_socket != null)
        {
            try
            {
                // Detach event handlers before disconnecting
                _socket.OnConnected -= Socket_OnConnected;
                _socket.OnDisconnected -= Socket_OnDisconnected;
                _socket.OnError -= Socket_OnError;
                await _socket.DisconnectAsync();
            }
            catch (Exception ex)
            {
                PluginLog.Verbose($"Armada: Disconnect error (ignored) - {ex.Message}");
            }
            finally
            {
                _socket.Dispose();
                _socket = null;
            }
        }

        SetStatus(ConnectionStatus.Disconnected);
    }

    public void Disconnect()
    {
        _ = DisconnectAsync();
    }

    public void Dispose()
    {
        _shouldReconnect = false;
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        _nextReconnectTime = null;
        _reconnectAttempts = 0;

        // Reset authentication state
        lock (_authLock)
        {
            _isAuthenticating = false;
        }

        // Synchronously dispose the socket
        if (_socket != null)
        {
            try
            {
                // Detach event handlers
                _socket.OnConnected -= Socket_OnConnected;
                _socket.OnDisconnected -= Socket_OnDisconnected;
                _socket.OnError -= Socket_OnError;

                // Remove message handlers
                _socket.Off("auth_response");
                _socket.Off("data_response");
                _socket.Off("pong");
                _socket.Off("loot_response");

                // Dispose the socket (this will disconnect if connected)
                _socket.Dispose();
            }
            catch (Exception ex)
            {
                PluginLog.Verbose($"Armada: Dispose error (ignored) - {ex.Message}");
            }
            finally
            {
                _socket = null;
            }
        }

        SetStatus(ConnectionStatus.Disconnected);
    }
}
