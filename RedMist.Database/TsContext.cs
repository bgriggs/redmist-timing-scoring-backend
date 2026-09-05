#pragma warning disable CS0618
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RedMist.Database.Models;
using RedMist.TimingCommon;
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
    public DbSet<ExternalMessageLog> ExternalMessageLogs { get; set; } = null!;
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
    public DbSet<Models.DriverInfo> DriverInfo { get; set; } = null!;
    public DbSet<SponsorTelemetryLog> SponsorTelemetryLogs { get; set; } = null!;
    public DbSet<Sponsor> Sponsors { get; set; } = null!;
    public DbSet<SponsorExclusion> SponsorExclusions { get; set; } = null!;
    public DbSet<SponsorStatistics> SponsorStatistics { get; set; } = null!;
    public DbSet<EventSponsorStatistics> EventSponsorStatistics { get; set; } = null!;
    public DbSet<SourceSponsorStatistics> SourceSponsorStatistics { get; set; } = null!;
    public DbSet<TrackMapRecord> TrackMaps { get; set; } = null!;
    public DbSet<SocialPost> SocialPosts { get; set; } = null!;
    public DbSet<SocialPrompt> SocialPrompts { get; set; } = null!;


    public TsContext(DbContextOptions<TsContext> options) : base(options) { }


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

    /// <summary>
    /// Builds a CHECK constraint restricting a text column to the names of an enum, so that the value
    /// stored can always be converted back. Generated from the enum itself rather than written out,
    /// so adding a member cannot leave the constraint behind.
    /// </summary>
    private static string BuildEnumCheck<TEnum>(string columnName) where TEnum : struct, Enum =>
        $"\"{columnName}\" IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"))})";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure Organizations table
        modelBuilder.Entity<Organization>().ToTable("Organizations");
        modelBuilder.Entity<Organization>().HasIndex(o => o.ClientId).IsUnique();

        // JSON configuration for complex types - PostgreSQL uses JSONB
        // X2
        var x2Converter = new ValueConverter<X2Configuration, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<X2Configuration>(v, (JsonSerializerOptions?)null) ?? new X2Configuration());

        var x2Property = modelBuilder.Entity<Organization>().Property(o => o.X2);
        x2Property.HasConversion(x2Converter!);
        x2Property.HasColumnType("jsonb");

        // Class Metadata
        var classMetadataConverter = new ValueConverter<List<ClassMetadata>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<ClassMetadata>>(v, (JsonSerializerOptions?)null) ?? new List<ClassMetadata>());
        var classMetadataProperty = modelBuilder.Entity<Organization>().Property(o => o.Classes);
        classMetadataProperty.HasConversion(classMetadataConverter!);
        classMetadataProperty.HasColumnType("jsonb");

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

        var scheduleProperty = modelBuilder.Entity<TimingCommon.Models.Configuration.Event>()
            .Property(o => o.Schedule);
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

        var loopsMetadata = modelBuilder.Entity<TimingCommon.Models.Configuration.Event>()
            .Property(o => o.LoopsMetadata);
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
        var controlLogConverter = new ValueConverter<List<ControlLogEntry>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v) || v == "{}"
                ? new List<ControlLogEntry>()
                : JsonSerializer.Deserialize<List<ControlLogEntry>>(v, (JsonSerializerOptions?)null) ?? new List<ControlLogEntry>());

        var payloadProperty = modelBuilder.Entity<SessionResult>().Property(o => o.Payload);
        payloadProperty.HasConversion(payloadConverter!);
        payloadProperty.HasColumnType("jsonb");

        var sessionStateProperty = modelBuilder.Entity<SessionResult>().Property(o => o.SessionState);
        sessionStateProperty.HasConversion(sessionStateConverter!);
        sessionStateProperty.HasColumnType("jsonb");

        var controlLogsProperty = modelBuilder.Entity<SessionResult>().Property(o => o.ControlLogs);
        controlLogsProperty.HasConversion(controlLogConverter!);
        controlLogsProperty.HasColumnType("jsonb");

        // Track map (one per event); the learned map is stored as JSONB.
        modelBuilder.Entity<TrackMapRecord>().HasKey(t => t.EventId);
        modelBuilder.Entity<TrackMapRecord>().Property(t => t.EventId).ValueGeneratedNever();
        var trackMapConverter = new ValueConverter<TrackMap, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<TrackMap>(v, (JsonSerializerOptions?)null) ?? new TrackMap());
        var trackMapProperty = modelBuilder.Entity<TrackMapRecord>().Property(t => t.Map);
        trackMapProperty.HasConversion(trackMapConverter!);
        trackMapProperty.HasColumnType("jsonb");

        // Sponsor exclusions: one row per (organization, blocked sponsor). Keyed on OrganizationId first so
        // the lookup for "what does this event's organization exclude" is a prefix scan of the PK index.
        modelBuilder.Entity<SponsorExclusion>().HasKey(e => new { e.OrganizationId, e.SponsorId });

        // Social posts. The enums are stored as text rather than the default ordinal: this table is
        // read by a person during review and triage, and "Publishing" tells them something that "3"
        // does not. It also means inserting a new enum member cannot silently renumber existing rows.
        modelBuilder.Entity<SocialPost>().Property(p => p.Kind).HasConversion<string>().HasMaxLength(50);
        modelBuilder.Entity<SocialPost>().Property(p => p.Channel).HasConversion<string>().HasMaxLength(50);
        modelBuilder.Entity<SocialPost>().Property(p => p.State).HasConversion<string>().HasMaxLength(50);

        // One post per thing-being-posted-about. This is what stops a retried compose job, or a job
        // that ran twice, producing two drafts for the same event.
        modelBuilder.Entity<SocialPost>().HasIndex(p => p.IdempotencyKey).IsUnique();

        // Optimistic concurrency on every write, not just the publish job's conditional claim. The
        // Publishing state is a trap that must survive until a human clears it, but a tracked-entity
        // save is "UPDATE ... WHERE Id = @id" with no state predicate, so a reviewer holding a page
        // loaded before the claim could save an unrelated text edit and put the row back to Approved
        // -- releasing the trap and letting the same result be posted to Facebook twice. With xmin as
        // a token that write throws instead of silently winning.
        // PostgreSQL's own row version. A shadow property rather than a mapped column, so this adds no
        // DDL: xmin is a system column every table already has.
        modelBuilder.Entity<SocialPost>().Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // The enum columns are plain text, and a value outside the enum fails during materialization
        // rather than on the offending row alone -- one hand-typo would make every SocialPosts query
        // throw. Since reading and occasionally correcting this table by hand is the whole point of
        // storing enums as text, the database enforces the vocabulary.
        modelBuilder.Entity<SocialPost>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_SocialPosts_State", BuildEnumCheck<SocialPostState>("State"));
            t.HasCheckConstraint("CK_SocialPosts_Kind", BuildEnumCheck<SocialPostKind>("Kind"));
            t.HasCheckConstraint("CK_SocialPosts_Channel", BuildEnumCheck<SocialChannel>("Channel"));
        });

        // The publish job's claim query: pick up approved posts whose scheduled time has arrived.
        modelBuilder.Entity<SocialPost>().HasIndex(p => new { p.State, p.ScheduledUtc });

        var imageRefsConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
        var imageRefsComparer = new ValueComparer<List<string>>(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            c => c != null ? c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())) : 0,
            c => c != null ? c.ToList() : new List<string>());
        var imageRefsProperty = modelBuilder.Entity<SocialPost>().Property(p => p.ImageRefs);
        imageRefsProperty.HasConversion(imageRefsConverter!);
        imageRefsProperty.Metadata.SetValueComparer(imageRefsComparer);
        imageRefsProperty.HasColumnType("jsonb");

        // The digest is written and read as JSON text but stored as jsonb so it stays queryable when
        // investigating why a particular draft said what it said.
        modelBuilder.Entity<SocialPost>().Property(p => p.DigestJson).HasColumnType("jsonb");

        modelBuilder.Entity<SocialPrompt>().Property(p => p.Kind).HasConversion<string>().HasMaxLength(50);
        modelBuilder.Entity<SocialPrompt>().Property(p => p.Channel).HasConversion<string>().HasMaxLength(50);
        modelBuilder.Entity<SocialPrompt>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_SocialPrompts_Kind", BuildEnumCheck<SocialPostKind>("Kind"));
            t.HasCheckConstraint("CK_SocialPrompts_Channel", BuildEnumCheck<SocialChannel>("Channel"));
        });
        modelBuilder.Entity<SocialPrompt>().HasIndex(p => new { p.Kind, p.Channel, p.Version }).IsUnique();

        // At most one active prompt per kind and channel. A filtered unique index makes "which prompt
        // is live" unambiguous by construction rather than by whoever last remembered to deactivate.
        modelBuilder.Entity<SocialPrompt>()
            .HasIndex(p => new { p.Kind, p.Channel })
            .IsUnique()
            .HasFilter("\"IsActive\"");

        // Configure TimingCommon models
        modelBuilder.Entity<Session>().HasKey(s => new { s.Id, s.EventId });
        modelBuilder.Entity<CompetitorMetadata>().HasKey(c => new { c.EventId, c.CarNumber });
        modelBuilder.Entity<Loop>().HasKey(l => new { l.OrganizationId, l.EventId, l.Id });
        modelBuilder.Entity<Passing>().HasKey(p => new { p.OrganizationId, p.EventId, p.Id });
        modelBuilder.Entity<UIVersionInfo>().HasNoKey().ToTable("UIVersions");
        modelBuilder.Entity<Models.DriverInfo>().HasIndex(o => o.FlagtronicsId).IsUnique();
        modelBuilder.Entity<CarLapLog>().HasIndex(l => new { l.EventId, l.SessionId, l.CarNumber, l.LapNumber });
        modelBuilder.Entity<ExternalMessageLog>().HasIndex(l => new { l.EventId, l.SessionId });
        // Keyed to match the log download queries: filter on EventId, order by Timestamp/Id descending.
        // SessionId is an INCLUDE rather than a key column so a single index serves both the all-sessions
        // and single-session forms without falling back to a sort.
        modelBuilder.Entity<EventStatusLog>()
            .HasIndex(l => new { l.EventId, l.Timestamp, l.Id })
            .IncludeProperties(l => l.SessionId);
    }
}
#pragma warning restore CS0618