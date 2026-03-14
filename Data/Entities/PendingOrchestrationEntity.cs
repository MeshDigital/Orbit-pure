using System;
using System.ComponentModel.DataAnnotations;

namespace SLSKDONET.Data.Entities
{
    public class PendingOrchestrationEntity
    {
        [Key]
        public string TrackUniqueHash { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
