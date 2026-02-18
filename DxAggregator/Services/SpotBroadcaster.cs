using Microsoft.AspNetCore.SignalR;
using DxAggregator.Hubs;
using DxAggregator.Models;

namespace DxAggregator.Services;

/// <summary>
/// Bridges the SpotPipeline's OnNewSpot event to SignalR,
/// pushing new spots to all connected browser clients.
/// </summary>
public class SpotBroadcaster : IHostedService
{
    private readonly SpotPipeline _pipeline;
    private readonly IHubContext<SpotHub> _hubContext;
    private readonly ILogger<SpotBroadcaster> _logger;

    public SpotBroadcaster(SpotPipeline pipeline, IHubContext<SpotHub> hubContext, ILogger<SpotBroadcaster> logger)
    {
        _pipeline = pipeline;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pipeline.OnNewSpot += BroadcastSpot;
        _logger.LogInformation("SpotBroadcaster started, will push new spots to SignalR clients");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pipeline.OnNewSpot -= BroadcastSpot;
        return Task.CompletedTask;
    }

    private void BroadcastSpot(SpotRecord spot)
    {
        // Fire and forget — SignalR handles client disconnects gracefully
        _ = _hubContext.Clients.All.SendAsync("NewSpot", new
        {
            spot.Id,
            spot.DxCall,
            spot.Frequency,
            spot.Band,
            spot.Mode,
            spot.Spotter,
            spot.Snr,
            Timestamp = spot.Timestamp.ToString("o"),
            spot.Source,
            spot.DxccEntity,
            spot.Grid,
            spot.Comment,
            spot.DesirabilityScore
        });
    }
}
