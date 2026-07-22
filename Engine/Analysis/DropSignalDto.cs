namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// JSON-serializable shape for one entry of <c>AudioFeaturesEntity.NoveltyDropSignaturesJson</c>.
/// Mirrors the tuple SpectralFluxNoveltyEngine.DetectDropSignatures returns — a build-confirmed
/// drop candidate, not just a raw onset peak.
/// </summary>
public sealed record NoveltyDropSignatureDto(double DropSeconds, double BuildStartSeconds, float Strength);
