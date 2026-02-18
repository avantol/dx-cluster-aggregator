using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using DxAggregator.Data;
using DxAggregator.Models;

namespace DxAggregator.Services;

/// <summary>
/// Central spot processing pipeline. All data sources submit spots here.
/// Pipeline stages: validate → deduplicate → store → broadcast.
/// </summary>
public class SpotPipeline
{
    private readonly Channel<SpotRecord> _inbound = Channel.CreateBounded<SpotRecord>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    private readonly ILogger<SpotPipeline> _logger;

    /// <summary>
    /// Event raised when a new (non-duplicate) spot is processed and stored.
    /// Subscribers like the WebSocket hub use this to push to browser clients.
    /// </summary>
    public event Action<SpotRecord>? OnNewSpot;

    public SpotPipeline(ILogger<SpotPipeline> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Raises the OnNewSpot event. Called by SpotProcessor after a spot is stored.
    /// </summary>
    public void RaiseNewSpot(SpotRecord spot)
    {
        OnNewSpot?.Invoke(spot);
    }

    /// <summary>
    /// Submit a spot from any data source. Non-blocking, drops oldest if full.
    /// </summary>
    public void Submit(SpotRecord spot)
    {
        if (!_inbound.Writer.TryWrite(spot))
        {
            _logger.LogWarning("Pipeline channel full, dropping spot for {Call}", spot.DxCall);
        }
    }

    public ChannelReader<SpotRecord> Reader => _inbound.Reader;
}

/// <summary>
/// Background service that reads from the pipeline channel, deduplicates,
/// stores to SQLite, and raises events for broadcasting.
/// </summary>
public class SpotProcessor : BackgroundService
{
    private readonly SpotPipeline _pipeline;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SpotProcessor> _logger;
    private readonly Deduplicator _dedup = new();

    public SpotProcessor(SpotPipeline pipeline, IServiceScopeFactory scopeFactory, ILogger<SpotProcessor> logger)
    {
        _pipeline = pipeline;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SpotProcessor started, reading from pipeline");

        await foreach (var spot in _pipeline.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Stage 1: Validate
                if (!IsValid(spot))
                    continue;

                // Stage 2: Normalize
                Normalize(spot);

                // Stage 3: Deduplicate (same call + freq within 60s)
                if (_dedup.IsDuplicate(spot))
                    continue;

                // Stage 4: Store in SQLite
                await Store(spot, stoppingToken);

                // Stage 5: Broadcast to subscribers
                _pipeline.RaiseNewSpot(spot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing spot for {Call} on {Freq}",
                    spot.DxCall, spot.Frequency);
            }
        }
    }

    private static bool IsValid(SpotRecord spot)
    {
        if (string.IsNullOrWhiteSpace(spot.DxCall)) return false;
        if (spot.Frequency <= 0) return false;
        if (spot.DxCall.Length < 3 || spot.DxCall.Length > 10) return false;
        return true;
    }

    private static void Normalize(SpotRecord spot)
    {
        spot.DxCall = spot.DxCall.Trim().ToUpperInvariant();
        spot.Spotter = spot.Spotter?.Trim().ToUpperInvariant() ?? "unknown";
        spot.Band = SpotRecord.FrequencyToBand(spot.Frequency);
        if (spot.Mode == "unknown" || string.IsNullOrEmpty(spot.Mode))
            spot.Mode = SpotRecord.InferModeFromFrequency(spot.Frequency) ?? "unknown";
        if (spot.Timestamp == default)
            spot.Timestamp = DateTime.UtcNow;
    }

    private async Task Store(SpotRecord spot, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpotDb>();
        db.Spots.Add(spot);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// In-memory deduplicator. Considers a spot a duplicate if the same DX callsign
/// was spotted within 3 kHz and 60 seconds.
/// Self-cleans entries older than 2 minutes.
/// </summary>
public class Deduplicator
{
    private readonly ConcurrentDictionary<string, DateTime> _seen = new();
    private DateTime _lastCleanup = DateTime.UtcNow;
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(2);
    private const double FrequencyToleranceKhz = 3.0;

    public bool IsDuplicate(SpotRecord spot)
    {
        CleanupIfNeeded();

        // Key: callsign + rounded frequency (to nearest 3 kHz)
        var freqBucket = Math.Round(spot.Frequency / FrequencyToleranceKhz) * FrequencyToleranceKhz;
        var key = $"{spot.DxCall}|{freqBucket:F0}";

        var now = DateTime.UtcNow;
        if (_seen.TryGetValue(key, out var lastSeen) && (now - lastSeen) < DedupeWindow)
        {
            return true;
        }

        _seen[key] = now;
        return false;
    }

    private void CleanupIfNeeded()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastCleanup) < CleanupInterval) return;
        _lastCleanup = now;

        var cutoff = now - DedupeWindow;
        foreach (var kvp in _seen)
        {
            if (kvp.Value < cutoff)
                _seen.TryRemove(kvp.Key, out _);
        }
    }
}
