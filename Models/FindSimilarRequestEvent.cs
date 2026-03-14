using SLSKDONET.Models;

namespace SLSKDONET.Models
{
    public class FindSimilarRequestEvent
    {
        public PlaylistTrack SeedTrack { get; }
        public bool UseAi { get; }
        
        public FindSimilarRequestEvent(PlaylistTrack seedTrack, bool useAi = false)
        {
            SeedTrack = seedTrack;
            UseAi = useAi;
        }
    }
}
