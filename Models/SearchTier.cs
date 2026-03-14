namespace SLSKDONET.Models
{
    public enum SearchTier
    {
        Platinum, // Lossless/320k + Metadata
        Gold,     // 192k+ 
        Silver,   // 128k+
        Bronze,   // < 128k
        Garbage,  // Fake/Upscaled
        Unknown
    }
}
