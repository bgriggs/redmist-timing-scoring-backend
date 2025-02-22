using Microsoft.EntityFrameworkCore;
using RedMist.TimingAndScoringService.Database.Models;

namespace RedMist.TimingAndScoringService.Database;

public class TsContext : DbContext
{
    public DbSet<Organization> Organizations { get; set; } = null!;
    public DbSet<Event> Events { get; set; } = null!;
    public DbSet<EventStatusLog> EventStatusLogs { get; set; } = null!;
    public DbSet<CarLapLog> CarLapLogs { get; set; } = null!;


    public TsContext(DbContextOptions<TsContext> options)
           : base(options) { }


    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlServer("Server=localhost;Database=redmist-timing-dev;User Id=sa;Password=;TrustServerCertificate=True");
        }
    }
}
