using System.Windows;

namespace SLSKDONET.Models
{
    /// <summary>
    /// Represents the real-time progress of a track during orchestration (search, rank, match).
    /// Updates the UI as each track moves through the orchestration pipeline.
    /// </summary>
    public class OrchestratedQueryProgress
    {
        public string QueryId { get; set; } // Unique identifier for progress tracking
        public string Query { get; set; }   // Artist - Title or search string
        public string State { get; set; }   // "Queued" | "Searching" | "Ranking" | "Matched" | "Failed"
        public int TotalResults { get; set; } = 0; // Updated as search completes
        public string MatchedTrack { get; set; } = ""; // Filled when best match found
        public double MatchScore { get; set; } = 0; // Rank score (0-100)
        public bool IsProcessing { get; set; } = false; // Animates spinner when true
        public bool IsComplete { get; set; } = false; // True when matched or failed
        public string ErrorMessage { get; set; } = ""; // Non-empty if failed

        public OrchestratedQueryProgress(string queryId, string query)
        {
            QueryId = queryId;
            Query = query;
            State = "Queued";
        }

        // Animated emoji sequence for status indicator
        public string GetStatusEmoji()
        {
            return State switch
            {
                "Queued" => "⏳",
                "Searching" => "🔍",
                "Ranking" => "⭐",
                "Matched" => "✅",
                "Failed" => "❌",
                _ => "◌"
            };
        }
    }
}
