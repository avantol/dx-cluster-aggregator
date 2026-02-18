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

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SpotDb>();
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

// SignalR hub endpoint
app.MapHub<SpotHub>("/hubs/spots").RequireCors("SignalR");

app.Run();
