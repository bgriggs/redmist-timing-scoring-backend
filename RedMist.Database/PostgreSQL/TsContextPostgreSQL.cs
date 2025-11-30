using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RedMist.Database.Models;
using RedMist.TimingCommon;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using System.Text.Json;

namespace RedMist.Database.PostgreSQL;

/// <summary>
/// PostgreSQL-specific DbContext for migrations.
/// This is separate from TsContext to avoid SQL Server migration conflicts.
/// </summary>
public class TsContextPostgreSQL : DbContext
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
    public DbSet<FlagLog> FlagLog { get; set; } = null!;
    public DbSet<CompetitorMetadata> CompetitorMetadata { get; set; } = null!;
    public DbSet<UserOrganizationMapping> UserOrganizationMappings { get; set; } = null!;
    public DbSet<DefaultOrgImage> DefaultOrgImages { get; set; } = null!;
    public DbSet<RelayLog> RelayLogs { get; set; } = null!;
    public DbSet<UIVersionInfo> UIVersions { get; set; } = null!;

    public TsContextPostgreSQL(DbContextOptions<TsContextPostgreSQL> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        if (!optionsBuilder.IsConfigured)
        {
            // Enable legacy timestamp behavior BEFORE configuring Npgsql
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
            optionsBuilder.UseNpgsql("Host=localhost;Database=redmist-timing-dev;Username=postgres;Password=");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure Organizations table (NO VIEW CONFIGURATION)
        modelBuilder.Entity<Organization>().ToTable("Organizations");
        modelBuilder.Entity<Organization>().HasIndex(o => o.ClientId).IsUnique();

        // JSON configuration for complex types - PostgreSQL uses JSONB
        var orbitsConverter = new ValueConverter<OrbitsConfiguration, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<OrbitsConfiguration>(v, (JsonSerializerOptions?)null) ?? new OrbitsConfiguration());

        var orbitsProperty = modelBuilder.Entity<Organization>().Property(o => o.Orbits);
        orbitsProperty.HasConversion(orbitsConverter!);
        orbitsProperty.HasColumnType("jsonb");

        // X2
        var x2Converter = new ValueConverter<X2Configuration, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<X2Configuration>(v, (JsonSerializerOptions?)null) ?? new X2Configuration());

        var x2Property = modelBuilder.Entity<Organization>().Property(o => o.X2);
        x2Property.HasConversion(x2Converter!);
        x2Property.HasColumnType("jsonb");

        // Broadcast
        var broadcastConverter = new ValueConverter<BroadcasterConfig, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<BroadcasterConfig>(v, (JsonSerializerOptions?)null) ?? new BroadcasterConfig());

        var broadcastProperty = modelBuilder.Entity<TimingCommon.Models.Configuration.Event>().Property(o => o.Broadcast);
        broadcastProperty.HasConversion(broadcastConverter!);
        broadcastProperty.HasColumnType("jsonb");

        // Schedule
        var dtJsonOptions = new JsonSerializerOptions { Converters = { new UnspecifiedDateTimeConverter() } };
        var scheduleConverter = new ValueConverter<EventSchedule, string>(
            v => JsonSerializer.Serialize(v, dtJsonOptions),
            v => JsonSerializer.Deserialize<EventSchedule>(v, dtJsonOptions) ?? new EventSchedule());

        var scheduleProperty = modelBuilder.Entity<TimingCommon.Models.Configuration.Event>().Property(o => o.Schedule);
        scheduleProperty.HasConversion(scheduleConverter!);
        scheduleProperty.HasColumnType("jsonb");

        // Loops
        var loopMetadataConverter = new ValueConverter<List<LoopMetadata>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<LoopMetadata>>(v, (JsonSerializerOptions?)null) ?? new List<LoopMetadata>());

        var loopsComparer = new ValueComparer<List<LoopMetadata>>(
            (c1, c2) => c1 != null && c2 != null && c1.SequenceEqual(c2),
            c => c != null ? c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())) : 0,
            c => c != null ? c.ToList() : new List<LoopMetadata>());

        var loopsMetadata = modelBuilder.Entity<TimingCommon.Models.Configuration.Event>().Property(o => o.LoopsMetadata);
        loopsMetadata.HasConversion(loopMetadataConverter!);
        loopsMetadata.Metadata.SetValueComparer(loopsComparer);
        loopsMetadata.HasColumnType("jsonb");

        // SessionResult Payload
        var payloadConverter = new ValueConverter<Payload, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<Payload>(v, (JsonSerializerOptions?)null) ?? new Payload());
        var sessionStateConverter = new ValueConverter<SessionState, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<SessionState>(v, (JsonSerializerOptions?)null) ?? new SessionState());

        var payloadProperty = modelBuilder.Entity<SessionResult>().Property(o => o.Payload);
        payloadProperty.HasConversion(payloadConverter!);
        payloadProperty.HasColumnType("jsonb");

        var sessionStateProperty = modelBuilder.Entity<SessionResult>().Property(o => o.SessionState);
        sessionStateProperty.HasConversion(sessionStateConverter!);
        sessionStateProperty.HasColumnType("jsonb");

        // Configure TimingCommon models
        modelBuilder.Entity<Session>().HasKey(s => new { s.Id, s.EventId });

        modelBuilder.Entity<CompetitorMetadata>().HasKey(c => new { c.EventId, c.CarNumber });

        modelBuilder.Entity<Loop>().HasKey(l => new { l.OrganizationId, l.EventId, l.Id });

        modelBuilder.Entity<Passing>().HasKey(p => new { p.OrganizationId, p.EventId, p.Id });

        modelBuilder.Entity<UIVersionInfo>().HasNoKey().ToTable("UIVersions");
    }
}
