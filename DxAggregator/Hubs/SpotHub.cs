using Microsoft.AspNetCore.SignalR;

namespace DxAggregator.Hubs;

/// <summary>
/// SignalR hub for pushing real-time spots to browser clients.
/// Clients connect to /hubs/spots and receive "NewSpot" messages.
/// </summary>
public class SpotHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}
