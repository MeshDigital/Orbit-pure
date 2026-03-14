namespace SLSKDONET.Data;

public enum IntegrityLevel
{
    None = 0,
    Verified = 1,   // Hash matches known good
    Suspicious = 2, // Bitrate mismatch or Spectral analysis failed
    Gold = 3        // Perfect Match (Duration + BPM + Key + Audio Hash)
}

public enum ActiveWorkspace
{
    Selector, // Central Only (Triage)
    Analyst,  // Inspector (Verification)
    Preparer, // Mix Helper + Inspector (Cueing)
    Forensic  // Full Page Lab (Deep Dive)
}

public enum AnalysisStage
{
    Probing,      // Hashing/Integrity
    Waveform,     // Generating shimmers
    Intelligence, // Essentia/ML
    Forensics,    // Spectral Analysis
    Finalizing,   // Writing Tags/Cues
    Complete
}
