using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedMist.Database;

public class TsContext : DbContext
{
    public DbSet<Organization> Organizations { get; set; } = null!;
    public DbSet<Event> Events { get; set; } = null!;
    public DbSet<EventStatusLog> EventStatusLogs { get; set; } = null!;
    public DbSet<CarLapLog> CarLapLogs { get; set; } = null!;
    public DbSet<CarLastLap> CarLastLaps { get; set; } = null!;
    public DbSet<GoogleSheetsConfig> GoogleSheetsConfigs { get; set; } = null!;


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

        var x2Converter = new ValueConverter<X2Configuration, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<X2Configuration>(v, JsonSerializerOptions.Default) ?? new X2Configuration());

        modelBuilder.Entity<Organization>()
            .Property(o => o.X2)
            .HasConversion(x2Converter!);

        var broadcastConverter = new ValueConverter<BroadcasterConfig, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<BroadcasterConfig>(v, JsonSerializerOptions.Default) ?? new BroadcasterConfig());

        modelBuilder.Entity<Event>()
            .Property(o => o.Broadcast)
            .HasConversion(broadcastConverter!);

        var scheduleConverter = new ValueConverter<EventSchedule, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<EventSchedule>(v, JsonSerializerOptions.Default) ?? new EventSchedule());

        modelBuilder.Entity<Event>()
            .Property(o => o.Schedule)
            .HasConversion(scheduleConverter!);
    }
}
