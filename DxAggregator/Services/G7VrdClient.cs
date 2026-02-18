using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DxAggregator.Models;

namespace DxAggregator.Services;

/// <summary>
/// Connects to the G7VRD DX cluster WebSocket feed at ws.g7vrd.co.uk.
/// Uses SockJS raw WebSocket transport with STOMP framing.
/// Provides aggregated DX cluster + RBN + PSK Reporter + WSPR spots.
/// </summary>
public class G7VrdClient : BackgroundService
{
    private readonly ILogger<G7VrdClient> _logger;
    private readonly SpotPipeline _pipeline;

    // SockJS raw WebSocket endpoint pattern: /dx/{server_id}/{session_id}/websocket
    private const string BaseUrl = "wss://ws.g7vrd.co.uk/dx";
    private const string SpotsTopic = "/topic/spots/v1";
    private const string SkimsTopic = "/topic/skims/v1";
    private const string PskTopic = "/topic/psks/v1";


    // STOMP frame delimiters
    private const char Null = '\0';
    private const string Lf = "\n";

    public G7VrdClient(ILogger<G7VrdClient> logger, SpotPipeline pipeline)
    {
        _logger = logger;
        _pipeline = pipeline;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceive(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "G7VRD connection error, reconnecting in 10s");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ConnectAndReceive(CancellationToken ct)
    {
        // SockJS WebSocket URL: /dx/{3-digit-server}/{8-char-session}/websocket
        var serverId = Random.Shared.Next(0, 1000).ToString("D3");
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var wsUrl = $"{BaseUrl}/{serverId}/{sessionId}/websocket";

        _logger.LogInformation("Connecting to G7VRD at {Url}", wsUrl);

        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
        await ws.ConnectAsync(new Uri(wsUrl), ct);

        _logger.LogInformation("Connected to G7VRD WebSocket");

        // SockJS sends an "o" (open) frame first
        var openFrame = await ReceiveMessage(ws, ct);
        if (openFrame != "o")
        {
            _logger.LogWarning("Expected SockJS open frame 'o', got: {Frame}", openFrame);
        }

        // Send STOMP CONNECT frame (wrapped in SockJS array)
        await SendStomp(ws, "CONNECT", new Dictionary<string, string>
        {
            ["accept-version"] = "1.1,1.0",
            ["heart-beat"] = "10000,10000"
        }, null, ct);

        // Receive STOMP CONNECTED frame
        var connectedFrame = await ReceiveMessage(ws, ct);
        _logger.LogInformation("STOMP handshake response: {Frame}",
            connectedFrame?.Length > 100 ? connectedFrame[..100] + "..." : connectedFrame);

        // Subscribe to all spot-related topics
        await SendStomp(ws, "SUBSCRIBE", new Dictionary<string, string>
        {
            ["id"] = "sub-spots",
            ["destination"] = SpotsTopic
        }, null, ct);
        _logger.LogInformation("Subscribed to {Destination}", SpotsTopic);

        await SendStomp(ws, "SUBSCRIBE", new Dictionary<string, string>
        {
            ["id"] = "sub-skims",
            ["destination"] = SkimsTopic
        }, null, ct);
        _logger.LogInformation("Subscribed to {Destination}", SkimsTopic);

        await SendStomp(ws, "SUBSCRIBE", new Dictionary<string, string>
        {
            ["id"] = "sub-psk",
            ["destination"] = PskTopic
        }, null, ct);
        _logger.LogInformation("Subscribed to {Destination}", PskTopic);

        // Main receive loop
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var message = await ReceiveMessage(ws, ct);
            if (message == null) continue;

            // SockJS heartbeat
            if (message == "h")
            {
                continue;
            }

            // SockJS close frame
            if (message.StartsWith("c["))
            {
                _logger.LogWarning("SockJS close frame received: {Frame}", message);
                break;
            }

            // SockJS message array: a["..."]
            if (message.StartsWith("a["))
            {
                ProcessSockJsMessages(message);
            }
            else
            {
                _logger.LogDebug("Unhandled SockJS frame type: {Frame}",
                    message.Length > 200 ? message[..200] : message);
            }
        }
    }

    private void ProcessSockJsMessages(string sockJsFrame)
    {
        try
        {
            // Strip "a" prefix to get JSON array
            var jsonArray = sockJsFrame[1..];
            var messages = JsonSerializer.Deserialize<string[]>(jsonArray);
            if (messages == null) return;

            _logger.LogInformation("Received {Count} STOMP message(s) from G7VRD", messages.Length);
            foreach (var msg in messages)
            {
                ProcessStompFrame(msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing SockJS message array");
        }
    }

    private void ProcessStompFrame(string frame)
    {
        // STOMP frame format: COMMAND\nheader:value\n...\n\nbody\0
        var nullIdx = frame.IndexOf(Null);
        var content = nullIdx >= 0 ? frame[..nullIdx] : frame;

        var parts = content.Split("\n\n", 2);
        if (parts.Length < 2) return;

        var headerSection = parts[0];
        var body = parts[1];

        var lines = headerSection.Split('\n');
        var command = lines[0];

        if (command != "MESSAGE")
        {
            _logger.LogDebug("Non-MESSAGE STOMP command: {Command}", command);
            return;
        }

        // Extract destination header to detect WSPR array messages
        string? destination = null;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("destination:", StringComparison.OrdinalIgnoreCase))
            {
                destination = lines[i]["destination:".Length..];
                break;
            }
        }

        _logger.LogDebug("STOMP MESSAGE on {Dest} ({Len} chars)",
            destination, body.Length);

        try
        {
            var spot = ParseG7VrdSpot(body);
            if (spot != null)
            {
                _logger.LogDebug("Parsed spot: {Call} on {Freq} {Mode}",
                    spot.DxCall, spot.Frequency, spot.Mode);
                _pipeline.Submit(spot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing spot from G7VRD body: {Body}",
                body.Length > 300 ? body[..300] : body);
        }
    }

    /// <summary>
    /// Parses a G7VRD JSON spot into a SpotRecord.
    /// G7VRD uses different formats per topic:
    ///   Skims:  { "call":{"callsign":"OZ4MMK",...}, "skimmer":{"callsign":"CT1EYQ",...}, "hz":1831300, "db":4, "mode":"CW", "band":"160m", "ts":"..." }
    ///   PSK:    { "tx":{"callsign":"BH2RJL",...}, "rx":{"callsign":"JI1HFJ",...}, "khz":14075.1, "db":-2, "mode":"FT8", "band":"20m", "ts":"..." }
    ///   Spots:  { "dx":{"callsign":"...",...}, "spotter":{"callsign":"...",...}, "khz":..., "mode":"...", "band":"...", "ts":"..." }
    /// </summary>
    private SpotRecord? ParseG7VrdSpot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? dxCall = null;
        string? spotter = null;
        double? freqKhz = null;
        string? grid = null;
        string? dxCountry = null;
        string source = "G7VRD";

        // Determine message type by checking for type-specific properties

        // Skim format: call.callsign = DX, skimmer.callsign = spotter, hz = freq in Hz
        if (root.TryGetProperty("skimmer", out var skimmerEl))
        {
            dxCall = GetNestedCallsign(root, "call");
            spotter = GetNestedCallsign(root, "skimmer");
            freqKhz = GetDoubleProp(root, "hz") / 1000.0; // Hz to kHz
            grid = GetNestedString(root, "call", "grid");
            dxCountry = GetNestedString(root, "call", "country");
            source = "RBN";
        }
        // PSK format: tx.callsign = DX, rx.callsign = spotter, khz = freq in kHz
        else if (root.TryGetProperty("tx", out _))
        {
            dxCall = GetNestedCallsign(root, "tx");
            spotter = GetNestedCallsign(root, "rx");
            freqKhz = GetDoubleProp(root, "khz");
            grid = GetNestedString(root, "tx", "grid");
            dxCountry = GetNestedString(root, "tx", "country");
            source = "PSKReporter";
        }
        // DX spot format: dx.callsign, spotter.callsign, khz
        else if (root.TryGetProperty("dx", out _))
        {
            dxCall = GetNestedCallsign(root, "dx");
            spotter = GetNestedCallsign(root, "spotter");
            freqKhz = GetDoubleProp(root, "khz");
            grid = GetNestedString(root, "dx", "grid");
            dxCountry = GetNestedString(root, "dx", "country");
            source = "DXCluster";
        }
        // Fallback: try flat properties
        else
        {
            dxCall = GetStringProp(root, "dx", "dxCall", "callsign");
            spotter = GetStringProp(root, "spotter", "de");
            freqKhz = GetDoubleProp(root, "khz", "freq", "frequency");
        }

        if (string.IsNullOrWhiteSpace(dxCall) || freqKhz == null || freqKhz <= 0)
            return null;

        var spot = new SpotRecord
        {
            DxCall = dxCall.Trim().ToUpperInvariant(),
            Frequency = freqKhz.Value,
            Band = GetStringProp(root, "band") ?? SpotRecord.FrequencyToBand(freqKhz.Value),
            Mode = GetStringProp(root, "mode") ?? SpotRecord.InferModeFromFrequency(freqKhz.Value) ?? "unknown",
            Spotter = spotter?.Trim().ToUpperInvariant() ?? "unknown",
            Snr = GetIntProp(root, "db", "snr"),
            Timestamp = GetTimestamp(root) ?? DateTime.UtcNow,
            Source = source,
            Grid = grid,
            DxccEntity = dxCountry,
            Comment = GetStringProp(root, "comment", "info", "text")
        };

        return spot;
    }

    private static string? GetNestedCallsign(JsonElement root, string objectName)
    {
        if (root.TryGetProperty(objectName, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty("callsign", out var cs) && cs.ValueKind == JsonValueKind.String)
                return cs.GetString();
        }
        return null;
    }

    private static string? GetNestedString(JsonElement root, string objectName, string propertyName)
    {
        if (root.TryGetProperty(objectName, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }

    // Helper methods for flexible JSON property access (G7VRD format can vary)

    private static string? GetStringProp(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }

    private static double? GetDoubleProp(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetDouble();
                if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var d))
                    return d;
            }
        }
        return null;
    }

    private static int? GetIntProp(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetInt32();
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var i))
                    return i;
            }
        }
        return null;
    }

    private static DateTime? GetTimestamp(JsonElement el)
    {
        var ts = GetStringProp(el, "time", "timestamp", "t", "datetime");
        if (ts != null && DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToUniversalTime();

        var epoch = GetDoubleProp(el, "time", "timestamp", "t");
        if (epoch != null)
        {
            // Could be seconds or milliseconds since epoch
            var val = epoch.Value;
            if (val > 1_000_000_000_000) // milliseconds
                return DateTimeOffset.FromUnixTimeMilliseconds((long)val).UtcDateTime;
            else
                return DateTimeOffset.FromUnixTimeSeconds((long)val).UtcDateTime;
        }

        return null;
    }

    private async Task SendStomp(ClientWebSocket ws, string command,
        Dictionary<string, string> headers, string? body, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(command);
        sb.Append(Lf);
        foreach (var (key, value) in headers)
        {
            sb.Append(key);
            sb.Append(':');
            sb.Append(value);
            sb.Append(Lf);
        }
        sb.Append(Lf);
        if (body != null) sb.Append(body);
        sb.Append(Null);

        // Wrap in SockJS send format: ["..."]
        var stompFrame = sb.ToString();
        var sockJsPayload = JsonSerializer.Serialize(new[] { stompFrame });

        var bytes = Encoding.UTF8.GetBytes(sockJsPayload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveMessage(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);

        return sb.ToString();
    }
}
