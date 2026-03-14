namespace SLSKDONET.Services.Models;

public class IndexAuditReport
{
    public DateTime AuditDate { get; set; }
    public List<string> ExistingIndexes { get; set; } = new();
    public List<IndexRecommendation> MissingIndexes { get; set; } = new();
    public List<string> UnusedIndexes { get; set; } = new();
}

public class IndexRecommendation
{
    public string TableName { get; set; } = string.Empty;
    public string[] ColumnNames { get; set; } = Array.Empty<string>();
    public string Reason { get; set; } = string.Empty;
    public string EstimatedImpact { get; set; } = string.Empty;
    public string CreateIndexSql { get; set; } = string.Empty;
}

public class PerformanceBenchmark
{
    public long Read1000TracksMs { get; set; }
    public long FilteredQueryMs { get; set; }
    public long JoinQueryMs { get; set; }
}
