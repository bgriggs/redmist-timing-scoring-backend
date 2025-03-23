using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using System.Text.Json;

namespace RedMist.Database;

public class TsContext : DbContext
{
    public DbSet<Organization> Organizations { get; set; } = null!;
    public DbSet<TimingCommon.Models.Configuration.Event> Events { get; set; } = null!;
    public DbSet<Session> Sessions { get; set; } = null!;
    public DbSet<EventStatusLog> EventStatusLogs { get; set; } = null!;
    public DbSet<CarLapLog> CarLapLogs { get; set; } = null!;
    public DbSet<CarLastLap> CarLastLaps { get; set; } = null!;
    public DbSet<GoogleSheetsConfig> GoogleSheetsConfigs { get; set; } = null!;
    public DbSet<SessionResult> SessionResults { get; set; } = null!;
    public DbSet<Loop> X2Loops { get; set; } = null!;
    public DbSet<Passing> X2Passings { get; set; } = null!;


    public TsContext(DbContextOptions<TsContext> options) : base(options) { }


    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlServer("Server=localhost;Database=redmist-timing-dev;User Id=sa;Password=;TrustServerCertificate=True");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Organization>()
            .HasIndex(o => o.ClientId)
            .IsUnique();

        var orbitsConverter = new ValueConverter<OrbitsConfiguration, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<OrbitsConfiguration>(v, JsonSerializerOptions.Default) ?? new OrbitsConfiguration());

        modelBuilder.Entity<Organization>()
            .Property(o => o.Orbits)
            .HasConversion(orbitsConverter!);

        // X2
        var x2Converter = new ValueConverter<X2Configuration, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<X2Configuration>(v, JsonSerializerOptions.Default) ?? new X2Configuration());

        modelBuilder.Entity<Organization>()
            .Property(o => o.X2)
            .HasConversion(x2Converter!);

        // Broadcast
        var broadcastConverter = new ValueConverter<BroadcasterConfig, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<BroadcasterConfig>(v, JsonSerializerOptions.Default) ?? new BroadcasterConfig());

        modelBuilder.Entity<TimingCommon.Models.Configuration.Event>()
            .Property(o => o.Broadcast)
            .HasConversion(broadcastConverter!);

        // Schedule
        var scheduleConverter = new ValueConverter<EventSchedule, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<EventSchedule>(v, JsonSerializerOptions.Default) ?? new EventSchedule());

        modelBuilder.Entity<TimingCommon.Models.Configuration.Event>()
            .Property(o => o.Schedule)
            .HasConversion(scheduleConverter!);

        // Loops
        var loopMetadataConverter = new ValueConverter<List<LoopMetadata>, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<List<LoopMetadata>>(v, JsonSerializerOptions.Default) ?? new List<LoopMetadata>());

        modelBuilder.Entity<TimingCommon.Models.Configuration.Event>()
            .Property(o => o.LoopsMetadata)
            .HasConversion(loopMetadataConverter!);

        // SessionResult Payload
        var payloadConverter = new ValueConverter<Payload, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<Payload>(v, JsonSerializerOptions.Default) ?? new Payload());

        modelBuilder.Entity<SessionResult>()
            .Property(o => o.Payload)
            .HasConversion(payloadConverter!);
    }
}
