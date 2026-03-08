using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

public class SponsorStatistics
{
    public long Id { get; set; }

    public DateOnly Month { get; set; }
    public int SponsorId { get; set; }
    public int ViewableImpressions { get; set; }
    public int Impressions { get; set; }
    public long EngagementDurationMs { get; set; }
    public int ClickThroughs { get; set; }
    public bool ReportProcessed { get; set; }
    public bool ReportProcessingSuccessful { get; set; }

    public List<EventSponsorStatistics> EventStatistics { get; set; } = [];
    public List<SourceSponsorStatistics> SourceStatistics { get; set; } = [];
}

public class EventSponsorStatistics
{
    public long Id { get; set; }
    public long SponsorStatisticsId { get; set; }
    public int EventId { get; set; }
    public int SponsorId { get; set; }
    public int ViewableImpressions { get; set; }
    public int Impressions { get; set; }
    public long EngagementDurationMs { get; set; }
    public int ClickThroughs { get; set; }
}

public class SourceSponsorStatistics
{
    public long Id { get; set; }
    public long SponsorStatisticsId { get; set; }
    [MaxLength(200)]
    public string Source { get; set; } = string.Empty;
    public int SponsorId { get; set; }
    public int ViewableImpressions { get; set; }
    public int Impressions { get; set; }
    public long EngagementDurationMs { get; set; }
    public int ClickThroughs { get; set; }
}