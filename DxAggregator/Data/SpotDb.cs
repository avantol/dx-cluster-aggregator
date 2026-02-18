using Microsoft.EntityFrameworkCore;
using DxAggregator.Models;

namespace DxAggregator.Data;

public class SpotDb : DbContext
{
    public SpotDb(DbContextOptions<SpotDb> options) : base(options) { }

    public DbSet<SpotRecord> Spots => Set<SpotRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpotRecord>(entity =>
        {
            entity.ToTable("spots");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();

            // Index for deduplication: same call + frequency within time window
            entity.HasIndex(e => new { e.DxCall, e.Frequency, e.Timestamp })
                  .HasDatabaseName("IX_spots_dedup");

            // Index for filtered queries
            entity.HasIndex(e => new { e.Band, e.Mode, e.Timestamp })
                  .HasDatabaseName("IX_spots_filter");

            // Index for timestamp-based pruning
            entity.HasIndex(e => e.Timestamp)
                  .HasDatabaseName("IX_spots_timestamp");
        });
    }
}

/// <summary>
/// Background service that prunes spots older than 24 hours every 15 minutes.
/// </summary>
public class SpotPruneService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SpotPruneService> _logger;
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxSpotAge = TimeSpan.FromMinutes(20);

    public SpotPruneService(IServiceScopeFactory scopeFactory, ILogger<SpotPruneService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PruneInterval, stoppingToken);
                await PruneOldSpots(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error pruning old spots");
            }
        }
    }

    private async Task PruneOldSpots(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpotDb>();
        var cutoff = DateTime.UtcNow - MaxSpotAge;

        var oldSpots = await db.Spots
            .Where(s => s.Timestamp < cutoff)
            .ToListAsync(ct);

        if (oldSpots.Count > 0)
        {
            db.Spots.RemoveRange(oldSpots);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Pruned {Count} spots older than 20min", oldSpots.Count);
        }
    }
}
