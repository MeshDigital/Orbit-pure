using System.Collections.Generic;

namespace SLSKDONET.Models.Stem;

public class StemPreset
{
    public string Name { get; set; } = "Untitled Preset";
    public Dictionary<StemType, StemSettings> Settings { get; set; } = new();

    public static StemPreset Default => new()
    {
        Name = "Default",
        Settings = new Dictionary<StemType, StemSettings>
        {
            { StemType.Vocals, new StemSettings() },
            { StemType.Drums, new StemSettings() },
            { StemType.Bass, new StemSettings() },
            { StemType.Piano, new StemSettings() },
            { StemType.Other, new StemSettings() }
        }
    };

    public static StemPreset VocalUp => new()
    {
        Name = "Vocal Up",
        Settings = new Dictionary<StemType, StemSettings>
        {
            { StemType.Vocals, new StemSettings { Volume = 1.2f } }, // +20%
            { StemType.Drums, new StemSettings { Volume = 0.8f } },
            { StemType.Bass, new StemSettings { Volume = 0.8f } },
            { StemType.Piano, new StemSettings { Volume = 0.8f } },
            { StemType.Other, new StemSettings { Volume = 0.8f } }
        }
    };
    
    public static StemPreset Instrumental => new()
    {
        Name = "Instrumental",
        Settings = new Dictionary<StemType, StemSettings>
        {
            { StemType.Vocals, new StemSettings { IsMuted = true } },
            { StemType.Drums, new StemSettings() },
            { StemType.Bass, new StemSettings() },
            { StemType.Piano, new StemSettings() },
            { StemType.Other, new StemSettings() }
        }
    };
}
