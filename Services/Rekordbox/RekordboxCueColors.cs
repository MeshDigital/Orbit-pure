using System;
using System.Globalization;

namespace SLSKDONET.Services.Rekordbox;

/// <summary>
/// Rekordbox's hot cue colour table: the value of djmdCue.ColorTableIndex (and of the colour id in
/// exported PCO2 cue entries). 0 = no colour (the CDJ's default), 1–62 = the colours below; values
/// above 62 show as black on hardware. Table from Deep Symmetry's beat-link
/// (CueList.findRekordboxColor, EPL-2.0), checked against a real Rekordbox 7.2.14 library: its
/// default hot cue colour is index 22 (#28E214), and ORBIT's eight cue swatches are entries 42, 38,
/// 30, 22, 18, 1, 60 and 49. (djmdCue.Color is only a flag: 255 = coloured, −1 = none.)
/// </summary>
public static class RekordboxCueColors
{
    private static readonly string[] Table =
    {
        "#305AFF", // 0x01
        "#5073FF", // 0x02
        "#508CFF", // 0x03
        "#50A0FF", // 0x04
        "#50B4FF", // 0x05
        "#50B0F2", // 0x06
        "#50AEE8", // 0x07
        "#45ACDB", // 0x08
        "#00E0FF", // 0x09
        "#19DAF0", // 0x0a
        "#32D2E6", // 0x0b
        "#21B4B9", // 0x0c
        "#20AAA0", // 0x0d
        "#1FA392", // 0x0e
        "#19A08C", // 0x0f
        "#14A584", // 0x10
        "#14AA7D", // 0x11
        "#10B176", // 0x12
        "#30D26E", // 0x13
        "#37DE5A", // 0x14
        "#3CEB50", // 0x15
        "#28E214", // 0x16
        "#7DC13D", // 0x17
        "#8CC832", // 0x18
        "#9BD723", // 0x19
        "#A5E116", // 0x1a
        "#A5DC0A", // 0x1b
        "#AAD208", // 0x1c
        "#B4C805", // 0x1d
        "#B4BE04", // 0x1e
        "#BAB404", // 0x1f
        "#C3AF04", // 0x20
        "#E1AA00", // 0x21
        "#FFA000", // 0x22
        "#FF9600", // 0x23
        "#FF8C00", // 0x24
        "#FF7500", // 0x25
        "#E0641B", // 0x26
        "#E0461E", // 0x27
        "#E0301E", // 0x28
        "#E02823", // 0x29
        "#E62828", // 0x2a
        "#FF376F", // 0x2b
        "#FF2D6F", // 0x2c
        "#FF127B", // 0x2d
        "#F51E8C", // 0x2e
        "#EB2DA0", // 0x2f
        "#E637B4", // 0x30
        "#DE44CF", // 0x31
        "#DE448D", // 0x32
        "#E630B4", // 0x33
        "#E619DC", // 0x34
        "#E600FF", // 0x35
        "#DC00FF", // 0x36
        "#CC00FF", // 0x37
        "#B432FF", // 0x38
        "#B93CFF", // 0x39
        "#C542FF", // 0x3a
        "#AA5AFF", // 0x3b
        "#AA72FF", // 0x3c
        "#8272FF", // 0x3d
        "#6473FF", // 0x3e
    };

    /// <summary>"#RRGGBB" for a colour index, or null for 0 / unknown.</summary>
    public static string? HexForIndex(int index) => index >= 1 && index <= Table.Length ? Table[index - 1] : null;

    /// <summary>The table index whose colour is nearest to <paramref name="hex"/> (exact for ORBIT's
    /// swatches); 0 when the value isn't a colour. For writing cues back to Rekordbox.</summary>
    public static int IndexForHex(string? hex)
    {
        if (!TryParse(hex, out var r, out var g, out var b)) return 0;
        int best = 0;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < Table.Length; i++)
        {
            TryParse(Table[i], out var tr, out var tg, out var tb);
            long d = (long)(r - tr) * (r - tr) + (long)(g - tg) * (g - tg) + (long)(b - tb) * (b - tb);
            if (d < bestDistance) { bestDistance = d; best = i + 1; }
        }
        return best;
    }

    private static bool TryParse(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s[2..]; // #AARRGGBB
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        r = (v >> 16) & 0xFF; g = (v >> 8) & 0xFF; b = v & 0xFF;
        return true;
    }
}
