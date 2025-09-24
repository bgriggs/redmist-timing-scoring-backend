using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
    public DbSet<Organization> OrganizationExtView { get; set; } = null!;
    public DbSet<TimingCommon.Models.Configuration.Event> Events { get; set; } = null!;
    public DbSet<Session> Sessions { get; set; } = null!;
    public DbSet<EventStatusLog> EventStatusLogs { get; set; } = null!;
    public DbSet<CarLapLog> CarLapLogs { get; set; } = null!;
    public DbSet<CarLastLap> CarLastLaps { get; set; } = null!;
    public DbSet<GoogleSheetsConfig> GoogleSheetsConfigs { get; set; } = null!;
    public DbSet<SessionResult> SessionResults { get; set; } = null!;
    public DbSet<Loop> X2Loops { get; set; } = null!;
    public DbSet<Passing> X2Passings { get; set; } = null!;
    public DbSet<FlagLog> FlagLog { get; set; } = null!;
    public DbSet<CompetitorMetadata> CompetitorMetadata { get; set; } = null!;
    public DbSet<UserOrganizationMapping> UserOrganizationMappings { get; set; } = null!;
    public DbSet<DefaultOrgImage> DefaultOrgImages { get; set; } = null!;
    public DbSet<RelayLog> RelayLogs { get; set; } = null!;


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

        modelBuilder.Entity<Organization>().ToTable("Organizations");
        modelBuilder.Entity<Organization>()
            .HasIndex(o => o.ClientId)
            .IsUnique();

        modelBuilder.Entity<Organization>()
            .ToView("OrganizationExtView", "dbo")
            .HasKey(o => o.Id);

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
        var dtJsonOptions = new JsonSerializerOptions { Converters = { new UnspecifiedDateTimeConverter() } };
        var scheduleConverter = new ValueConverter<EventSchedule, string>(
            v => JsonSerializer.Serialize(v, dtJsonOptions),
            v => JsonSerializer.Deserialize<EventSchedule>(v, dtJsonOptions) ?? new EventSchedule());

        modelBuilder.Entity<TimingCommon.Models.Configuration.Event>()
            .Property(o => o.Schedule)
            .HasConversion(scheduleConverter!);

        // Loops
        var loopMetadataConverter = new ValueConverter<List<LoopMetadata>, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<List<LoopMetadata>>(v, JsonSerializerOptions.Default) ?? new List<LoopMetadata>());

        var loopsComparer = new ValueComparer<List<LoopMetadata>>(
            (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2), // Ensure both lists are non-null before calling SequenceEqual
            c => c != null ? c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())) : 0, // Handle null case for hash code
            c => c != null ? c.ToList() : new List<LoopMetadata>()); // Handle null case for snapshot

        modelBuilder.Entity<TimingCommon.Models.Configuration.Event>()
            .Property(o => o.LoopsMetadata)
            .HasConversion(loopMetadataConverter!)
            .Metadata.SetValueComparer(loopsComparer);

        // SessionResult Payload
        var payloadConverter = new ValueConverter<Payload, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<Payload>(v, JsonSerializerOptions.Default) ?? new Payload());
        var sessionStateConverter = new ValueConverter<SessionState, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<SessionState>(v, JsonSerializerOptions.Default) ?? new SessionState());

        modelBuilder.Entity<SessionResult>()
            .Property(o => o.Payload)
            .HasConversion(payloadConverter!);
        modelBuilder.Entity<SessionResult>()
            .Property(o => o.SessionState)
            .HasConversion(sessionStateConverter!);

        // Configure TimingCommon models
        modelBuilder.Entity<Session>()
            .HasKey(s => new { s.Id, s.EventId });
            
        modelBuilder.Entity<CompetitorMetadata>()
            .HasKey(c => new { c.EventId, c.CarNumber });
            
        // Configure other TimingCommon models as needed...
        modelBuilder.Entity<Loop>()
            .HasKey(l => new { l.OrganizationId, l.EventId, l.Id });
            
        modelBuilder.Entity<Passing>()
            .HasKey(p => new { p.OrganizationId, p.EventId, p.Id });
    }
}
