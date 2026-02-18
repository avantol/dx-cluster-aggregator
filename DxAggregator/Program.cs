using Microsoft.EntityFrameworkCore;
using DxAggregator.Data;
using DxAggregator.Hubs;
using DxAggregator.Services;

var builder = WebApplication.CreateBuilder(args);

// SQLite database
builder.Services.AddDbContext<SpotDb>(options =>
    options.UseSqlite("Data Source=spots.db"));

// Core pipeline (singleton — all data sources feed into it)
builder.Services.AddSingleton<SpotPipeline>();

// Callsign-to-location lookup via cty.dat
builder.Services.AddSingleton<CtyParser>();

// User location (single-user desktop app)
builder.Services.AddSingleton<UserLocation>();

// Background services
builder.Services.AddHostedService<SpotProcessor>();
builder.Services.AddHostedService<G7VrdClient>();
builder.Services.AddHostedService<SpotBroadcaster>();
builder.Services.AddHostedService<SpotPruneService>();

// SignalR for browser WebSocket push
builder.Services.AddSignalR();

// Swagger for API exploration
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS — allow browser clients from any origin during development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
    // SignalR requires credentials, so add a named policy
    options.AddPolicy("SignalR", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Fresh database on each startup (spots are ephemeral with 20min expiry)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SpotDb>();
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
}

// Swagger UI in development
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseDefaultFiles(); // serves index.html at /
app.UseStaticFiles();

// --- REST API Endpoints ---

// GET /api/spots — recent spots with optional filtering
app.MapGet("/api/spots", async (SpotDb db,
    string? band, string? mode, string? call, int? limit) =>
{
    var take = Math.Clamp(limit ?? 50, 1, 500);

    var query = db.Spots.AsQueryable();

    if (!string.IsNullOrWhiteSpace(band))
        query = query.Where(s => s.Band == band);

    if (!string.IsNullOrWhiteSpace(mode))
        query = query.Where(s => s.Mode == mode.ToUpperInvariant());

    if (!string.IsNullOrWhiteSpace(call))
        query = query.Where(s => s.DxCall.StartsWith(call.ToUpperInvariant()));

    var spots = await query
        .OrderByDescending(s => s.Timestamp)
        .Take(take)
        .Select(s => new
        {
            s.Id,
            s.DxCall,
            s.Frequency,
            s.Band,
            s.Mode,
            s.Spotter,
            s.Snr,
            Timestamp = s.Timestamp.ToString("o"),
            s.Source,
            s.DxccEntity,
            s.Grid,
            s.DistanceKm,
            s.Bearing,
            s.Comment,
            s.DesirabilityScore
        })
        .ToListAsync();

    return Results.Ok(spots);
})
.WithName("GetSpots")
;

// GET /api/spots/count — total spot count and per-band breakdown
app.MapGet("/api/spots/count", async (SpotDb db) =>
{
    var total = await db.Spots.CountAsync();
    var byBand = await db.Spots
        .GroupBy(s => s.Band)
        .Select(g => new { Band = g.Key, Count = g.Count() })
        .ToListAsync();

    return Results.Ok(new { total, byBand });
})
.WithName("GetSpotCount")
;

// GET /api/spots/bands — list of active bands (have spots in last hour)
app.MapGet("/api/spots/bands", async (SpotDb db) =>
{
    var cutoff = DateTime.UtcNow.AddHours(-1);
    var bands = await db.Spots
        .Where(s => s.Timestamp > cutoff)
        .Select(s => s.Band)
        .Distinct()
        .ToListAsync();

    return Results.Ok(bands);
})
.WithName("GetActiveBands")
;

// POST /api/location — set user location from browser geolocation
app.MapPost("/api/location", async (SpotDb db, UserLocation store, CtyParser cty,
    double lat, double lon) =>
{
    store.Latitude = lat;
    store.Longitude = lon;
    store.GridSquare = CtyParser.LatLonToGrid(lat, lon);

    // Recalculate distance for all existing spots that have DxLatitude/DxLongitude
    var spots = await db.Spots
        .Where(s => s.DxLatitude != null && s.DxLongitude != null)
        .ToListAsync();

    foreach (var s in spots)
    {
        s.DistanceKm = Math.Round(CtyParser.HaversineKm(lat, lon, s.DxLatitude!.Value, s.DxLongitude!.Value));
        s.Bearing = Math.Round(CtyParser.BearingDeg(lat, lon, s.DxLatitude!.Value, s.DxLongitude!.Value));
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { lat, lon, grid = store.GridSquare, recalculated = spots.Count });
})
.WithName("SetUserLocation")
;

// POST /api/location/grid — set user location from grid square
app.MapPost("/api/location/grid", async (SpotDb db, UserLocation store, string grid) =>
{
    var loc = CtyParser.GridToLatLon(grid);
    if (loc == null)
        return Results.BadRequest(new { error = "Invalid grid square. Use 4 or 6 characters, e.g. FN31pr" });

    var lat = loc.Value.Lat;
    var lon = loc.Value.Lon;
    store.Latitude = lat;
    store.Longitude = lon;
    store.GridSquare = grid.ToUpperInvariant();

    var spots = await db.Spots
        .Where(s => s.DxLatitude != null && s.DxLongitude != null)
        .ToListAsync();

    foreach (var s in spots)
    {
        s.DistanceKm = Math.Round(CtyParser.HaversineKm(lat, lon, s.DxLatitude!.Value, s.DxLongitude!.Value));
        s.Bearing = Math.Round(CtyParser.BearingDeg(lat, lon, s.DxLatitude!.Value, s.DxLongitude!.Value));
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { lat, lon, grid = store.GridSquare, recalculated = spots.Count });
})
.WithName("SetUserLocationFromGrid")
;

// GET /api/location — get current user location
app.MapGet("/api/location", (UserLocation store) =>
{
    if (store.Latitude == null) return Results.NotFound();
    return Results.Ok(new { lat = store.Latitude, lon = store.Longitude, grid = store.GridSquare });
})
.WithName("GetUserLocation")
;

// SignalR hub endpoint
app.MapHub<SpotHub>("/hubs/spots").RequireCors("SignalR");

app.Run();

/// <summary>
/// Simple in-memory store for the user's location (single-user desktop app).
/// </summary>
public class UserLocation
{
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? GridSquare { get; set; }
}
