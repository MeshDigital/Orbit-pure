namespace SLSKDONET.Models;

/// <summary>
/// Represents a file condition for filtering.
/// </summary>
public abstract class FileCondition
{
    public abstract bool Evaluate(Track file);
    public abstract int Priority { get; } // Higher = more important for ranking
}

/// <summary>
/// Format condition (e.g., mp3, flac, wav).
/// </summary>
public class FormatCondition : FileCondition
{
    public List<string> AllowedFormats { get; set; } = new();
    public override int Priority => 3;

    public override bool Evaluate(Track file)
    {
        if (!AllowedFormats.Any())
            return true;

        var ext = file.GetExtension().ToLower();
        return AllowedFormats.Contains(ext);
    }
}

/// <summary>
/// Length tolerance condition (seconds).
/// </summary>
public class LengthCondition : FileCondition
{
    public int? ExpectedLength { get; set; }
    public int ToleranceSeconds { get; set; } = 3;
    public override int Priority => 1;

    public override bool Evaluate(Track file)
    {
        if (!ExpectedLength.HasValue || file.Length == null)
            return true;

        var diff = Math.Abs(file.Length.Value - ExpectedLength.Value);
        return diff <= ToleranceSeconds;
    }
}

/// <summary>
/// Bitrate condition (min/max bitrate range).
/// </summary>
public class BitrateCondition : FileCondition
{
    public int? MinBitrate { get; set; }
    public int? MaxBitrate { get; set; }
    public override int Priority => 2;

    public override bool Evaluate(Track file)
    {
        if (MinBitrate.HasValue && file.Bitrate < MinBitrate.Value)
            return false;

        if (MaxBitrate.HasValue && file.Bitrate > MaxBitrate.Value)
            return false;

        return true;
    }
}

//public class SampleRateCondition : FileCondition
//{
//    public int? MinSampleRate { get; set; }
//    public int? MaxSampleRate { get; set; }
//    public override int Priority => 2;

//    public override bool Evaluate(Track file)
//    {
//        if (file.SampleRate == null)
//            return true;

//        if (MinSampleRate.HasValue && file.SampleRate < MinSampleRate)
//            return false;

//        if (MaxSampleRate.HasValue && file.SampleRate > MaxSampleRate)
//            return false;

//        return true;
//    }
//}

/// <summary>
/// Path content condition (strict matching).
/// </summary>
public class StrictPathCondition : FileCondition
{
    public string? RequiredInPath { get; set; }
    public override int Priority => 2;

    public override bool Evaluate(Track file)
    {
        if (string.IsNullOrEmpty(RequiredInPath) || file.Filename == null)
            return true;

        return file.Filename.Contains(RequiredInPath, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// User filter condition.
/// </summary>
public class UserCondition : FileCondition
{
    public List<string> BannedUsers { get; set; } = new();
    public override int Priority => 4;

    public override bool Evaluate(Track file)
    {
        if (!BannedUsers.Any())
            return true;

        return !BannedUsers.Contains(file.Username, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Evaluates file conditions with ranking.
/// </summary>
public class FileConditionEvaluator
{
    private readonly List<FileCondition> _requiredConditions = new();
    private readonly List<FileCondition> _preferredConditions = new();

    /// <summary>
    /// Adds a required condition (all must pass).
    /// </summary>
    public void AddRequired(FileCondition condition)
    {
        _requiredConditions.Add(condition);
    }

    /// <summary>
    /// Adds a preferred condition (ranked by how many pass).
    /// </summary>
    public void AddPreferred(FileCondition condition)
    {
        _preferredConditions.Add(condition);
    }

    /// <summary>
    /// Evaluates if file passes all required conditions.
    /// </summary>
    public bool PassesRequired(Track file)
    {
        return _requiredConditions.All(c => c.Evaluate(file));
    }

    /// <summary>
    /// Scores file based on how many preferred conditions it passes (0-1).
    /// </summary>
    public double ScorePreferred(Track file)
    {
        if (!_preferredConditions.Any())
            return 1.0;

        var passed = _preferredConditions.Count(c => c.Evaluate(file));
        return (double)passed / _preferredConditions.Count;
    }

    /// <summary>
    /// Filters and ranks results (required first, then by preferred score).
    /// </summary>
    public List<Track> FilterAndRank(IEnumerable<Track> files)
    {
        return files
            .Where(PassesRequired)
            .OrderByDescending(ScorePreferred)
            //.ThenByDescending(f => f.Bitrate ?? 0)
            .ThenBy(f => Math.Abs((f.Length ?? 0) - 0)) // Prefer closer to expected length
            .ToList();
    }
}
