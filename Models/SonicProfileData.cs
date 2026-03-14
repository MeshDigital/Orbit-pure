namespace SLSKDONET.Models;

public class SonicProfileData
{
    public double Energy { get; set; }
    public double Valence { get; set; }
    public bool IsInstrumental { get; set; }
    public double Instrumentalness { get; set; }
    
    // Default constructor
    public SonicProfileData() { }

    public SonicProfileData(double energy, double valence, double instrumentalness)
    {
        Energy = energy;
        Valence = valence;
        Instrumentalness = instrumentalness;
        IsInstrumental = instrumentalness > 0.8;
    }
}
