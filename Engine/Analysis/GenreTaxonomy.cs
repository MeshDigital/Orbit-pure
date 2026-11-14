using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// The five canonical genre buckets ORBIT's genre decision collapses into. <c>Other</c> is a
/// first-class, mandatory bucket — every pop/hip-hop/hardstyle track that is <em>not</em> one of
/// the four club scenes must land here rather than being pushed into the nearest club bucket.
/// </summary>
public enum GenreBucket
{
    Other,
    DrumAndBass,
    Dubstep,
    Techno,
    House
}

/// <summary>
/// A resolved genre: which <see cref="GenreBucket"/> a label votes for, an optional canonical
/// subgenre string, and a 0–1 weight. <c>Weight == 0</c> (or <see cref="IsJunk"/>) means "no vote"
/// — the label is too vague to contribute (e.g. "Electronic", "Dance").
/// </summary>
public readonly record struct GenreBucketMapping(
    GenreBucket Bucket,
    string? Subgenre,
    float Weight,
    bool IsJunk = false);

/// <summary>
/// The single canonical mapping from any genre text or model class to a <see cref="GenreBucket"/>.
/// Pure and static: no DI, no database, no models. It replaces the scattered private mappers that
/// lived in <c>AudioAnalysisService</c> (<c>CanonicalizeGenre</c>, <c>MapJamendoToElectronicGenre</c>,
/// <c>MapDiscogs400ToElectronicGenre</c>) and the substring checks in <c>GenreFamilyClassifier</c>.
///
/// The Discogs-400 member lists below are exactly the table in <c>DOCS/GENRE_ACCURACY_PLAN.md</c>;
/// every other of the 400 keys resolves to <see cref="GenreBucket.Other"/> with weight 1.
/// </summary>
public static class GenreTaxonomy
{
    // ---------------------------------------------------------------------------------------
    // Discogs-400 (the model's 400 style keys, "Parent---Name"). Exact member lists per the plan.
    // ---------------------------------------------------------------------------------------

    private static readonly Dictionary<string, GenreBucketMapping> DiscogsMap = new(StringComparer.Ordinal)
    {
        // DrumAndBass
        ["Electronic---Drum n Bass"] = new(GenreBucket.DrumAndBass, null, 1f),
        ["Electronic---Jungle"]     = new(GenreBucket.DrumAndBass, "Jungle", 1f),
        ["Electronic---Halftime"]   = new(GenreBucket.DrumAndBass, "Halftime", 1f),

        // Dubstep (the "140" scene)
        ["Electronic---Dubstep"] = new(GenreBucket.Dubstep, null, 1f),
        ["Electronic---Grime"]   = new(GenreBucket.Dubstep, "Grime", 1f),
        ["Hip Hop---Grime"]      = new(GenreBucket.Dubstep, "Grime", 1f),

        // Techno
        ["Electronic---Techno"]         = new(GenreBucket.Techno, null, 1f),
        ["Electronic---Hard Techno"]    = new(GenreBucket.Techno, "Hard Techno", 1f),
        ["Electronic---Minimal Techno"] = new(GenreBucket.Techno, "Minimal Techno", 1f),
        ["Electronic---Deep Techno"]    = new(GenreBucket.Techno, "Deep Techno", 1f),
        ["Electronic---Dub Techno"]     = new(GenreBucket.Techno, "Dub Techno", 1f),
        ["Electronic---Schranz"]        = new(GenreBucket.Techno, "Schranz", 1f),
        // Ambiguous classes: fractional weights, never forced.
        ["Electronic---Acid"]       = new(GenreBucket.Techno, "Acid", 0.5f),
        ["Electronic---Industrial"] = new(GenreBucket.Techno, "Industrial", 0.5f),
        ["Electronic---EBM"]        = new(GenreBucket.Techno, "EBM", 0.3f),
        ["Electronic---Minimal"]    = new(GenreBucket.Techno, "Minimal", 0.5f),

        // House
        ["Electronic---House"]            = new(GenreBucket.House, null, 1f),
        ["Electronic---Tech House"]       = new(GenreBucket.House, "Tech House", 1f),
        ["Electronic---Deep House"]       = new(GenreBucket.House, "Deep House", 1f),
        ["Electronic---Progressive House"] = new(GenreBucket.House, "Progressive House", 1f),
        ["Electronic---Acid House"]       = new(GenreBucket.House, "Acid House", 1f),
        ["Electronic---Garage House"]     = new(GenreBucket.House, "Garage House", 1f),
        ["Electronic---Ghetto House"]     = new(GenreBucket.House, "Ghetto House", 1f),
        ["Electronic---Tribal House"]     = new(GenreBucket.House, "Tribal House", 1f),
        ["Electronic---Hip-House"]        = new(GenreBucket.House, "Hip-House", 1f),
        ["Electronic---Italo House"]      = new(GenreBucket.House, "Italo House", 1f),
        ["Electronic---Euro House"]       = new(GenreBucket.House, "Euro House", 1f),
        ["Electronic---Electro House"]    = new(GenreBucket.House, "Electro House", 0.6f),
        ["Electronic---Tribal"]           = new(GenreBucket.House, "Tribal", 0.5f),
        ["Electronic---Nu-Disco"]         = new(GenreBucket.House, "Nu-Disco", 0.4f),
    };

    /// <summary>
    /// Maps a Discogs-400 style key (<c>Parent---Name</c>) to a bucket. Every key resolves — the
    /// four club buckets use the plan's exact member lists; everything else is
    /// <see cref="GenreBucket.Other"/> at weight 1, preserving the style name as subgenre.
    /// Keys are compared ordinally (they contain non-ASCII such as "Musique Concrète").
    /// </summary>
    public static GenreBucketMapping MapDiscogsStyle(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (DiscogsMap.TryGetValue(key, out var mapped)) return mapped;

        var sep  = key.IndexOf("---", StringComparison.Ordinal);
        var name = sep > 0 && sep + 3 < key.Length ? key[(sep + 3)..] : key;
        return new GenreBucketMapping(GenreBucket.Other, string.IsNullOrWhiteSpace(name) ? null : name, 1f);
    }

    // ---------------------------------------------------------------------------------------
    // Jamendo (the 87-class classifier head). Labels are the lowercase model taxonomy.
    // ---------------------------------------------------------------------------------------

    private static readonly HashSet<string> JamendoJunk = new(StringComparer.Ordinal)
    {
        "club", "dance", "edm", "electronic", "electronica",
    };

    /// <summary>
    /// Maps one of the 87 MTG-Jamendo labels. Vague labels (<c>club</c>, <c>dance</c>, <c>edm</c>,
    /// <c>electronic</c>, <c>electronica</c>) are a no-vote (<see cref="GenreBucketMapping.IsJunk"/>).
    /// Non-club labels (<c>breakbeat</c>, <c>trance</c>, <c>rock</c>…) map to
    /// <see cref="GenreBucket.Other"/>; only the four club scenes map to their bucket.
    /// </summary>
    public static GenreBucketMapping MapJamendoLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var l = label.Trim();
        if (JamendoJunk.Contains(l))
            return new GenreBucketMapping(GenreBucket.Other, null, 0f, IsJunk: true);

        switch (l)
        {
            case "drumnbass": return new GenreBucketMapping(GenreBucket.DrumAndBass, null, 1f);
            case "dubstep":   return new GenreBucketMapping(GenreBucket.Dubstep, null, 1f);
            case "techno":    return new GenreBucketMapping(GenreBucket.Techno, null, 1f);
            case "minimal":   return new GenreBucketMapping(GenreBucket.Techno, "Minimal", 0.5f);
            case "house":     return new GenreBucketMapping(GenreBucket.House, null, 1f);
            case "deephouse": return new GenreBucketMapping(GenreBucket.House, "Deep House", 1f);

            // Meaningful non-club electronic labels: preserve the name as an "Other" subgenre so
            // they don't display as a vague "Other" while still never voting for a club bucket.
            case "breakbeat":  return new GenreBucketMapping(GenreBucket.Other, "Breakbeat", 1f);
            case "trance":     return new GenreBucketMapping(GenreBucket.Other, "Trance", 1f);
            case "acidjazz":   return new GenreBucketMapping(GenreBucket.Other, "Acid Jazz", 1f);
            case "ambient":    return new GenreBucketMapping(GenreBucket.Other, "Ambient", 1f);
            case "chillout":   return new GenreBucketMapping(GenreBucket.Other, "Chillout", 1f);
            case "downtempo":  return new GenreBucketMapping(GenreBucket.Other, "Downtempo", 1f);
            case "dub":        return new GenreBucketMapping(GenreBucket.Other, "Dub", 1f);
            case "idm":        return new GenreBucketMapping(GenreBucket.Other, "IDM", 1f);
            case "triphop":    return new GenreBucketMapping(GenreBucket.Other, "Trip Hop", 1f);
            case "disco":      return new GenreBucketMapping(GenreBucket.Other, "Disco", 1f);
            case "synthpop":   return new GenreBucketMapping(GenreBucket.Other, "Synth-pop", 1f);

            default: return new GenreBucketMapping(GenreBucket.Other, null, 1f);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Free text (user tags, file tags, provider genre strings).
    // ---------------------------------------------------------------------------------------

    private static GenreBucketMapping M(GenreBucket bucket, string? subgenre = null, float weight = 1f)
        => new(bucket, subgenre, weight);

    /// <summary>
    /// Ordered free-text rules on the normalised (lowercase alphanumeric) token. Order matters:
    /// "deep dubstep" before "deep house", "melodic house &amp; techno" before both, "hard techno"
    /// before "techno", "tech house" before "house", "electro house" before "electro".
    /// </summary>
    private static readonly (Func<string, bool> Test, GenreBucketMapping Map)[] FreeTextRules =
    {
        // -- Drum & Bass --
        (n => n is "dnb" or "db" or "dandb" || n.Contains("drumbass") || n.Contains("drumandbass") || n.Contains("drumnbass"), M(GenreBucket.DrumAndBass)),
        (n => n.Contains("jungle"),    M(GenreBucket.DrumAndBass, "Jungle")),
        (n => n.Contains("liquid"),    M(GenreBucket.DrumAndBass, "Liquid")),
        (n => n.Contains("neurofunk") || n == "neuro", M(GenreBucket.DrumAndBass, "Neurofunk")), // "Neuro" is the common short tag (#249 traces)
        (n => n.Contains("jumpup"),    M(GenreBucket.DrumAndBass, "Jump Up")),
        (n => n.Contains("halftime"),  M(GenreBucket.DrumAndBass, "Halftime")),

        // -- Dubstep / 140 --
        (n => n.Contains("deepdubstep"), M(GenreBucket.Dubstep, "Deep Dubstep")),
        (n => n.Contains("dubstep"),     M(GenreBucket.Dubstep)),
        (n => n.Contains("riddim"),      M(GenreBucket.Dubstep, "Riddim")),
        (n => n.Contains("brostep"),     M(GenreBucket.Dubstep, "Brostep")),
        (n => n.Contains("grime"),       M(GenreBucket.Dubstep, "Grime")),
        (n => n == "140",                M(GenreBucket.Dubstep)),

        // -- Techno --
        (n => n.Contains("melodic") && n.Contains("techno"), M(GenreBucket.Techno, "Melodic Techno")),
        (n => n.Contains("peaktime"),    M(GenreBucket.Techno)),
        (n => n.Contains("hardtechno"),  M(GenreBucket.Techno, "Hard Techno")),
        (n => n.Contains("minimaltechno"), M(GenreBucket.Techno, "Minimal Techno")),
        (n => n.Contains("deeptechno"),  M(GenreBucket.Techno, "Deep Techno")),
        (n => n.Contains("dubtechno"),   M(GenreBucket.Techno, "Dub Techno")),
        (n => n.Contains("acidtechno"),  M(GenreBucket.Techno, "Acid Techno")),
        (n => n.Contains("schranz"),     M(GenreBucket.Techno, "Schranz")),
        (n => n.Contains("deeptech"),    M(GenreBucket.Techno, "Minimal Techno")),
        (n => n.Contains("techno"),      M(GenreBucket.Techno)),
        (n => n == "minimal",            M(GenreBucket.Techno, "Minimal", 0.5f)),
        (n => n == "industrial",         M(GenreBucket.Techno, "Industrial", 0.5f)),
        (n => n == "ebm",                M(GenreBucket.Techno, "EBM", 0.3f)),

        // -- Acid specials (before bare "acid", before bare "house") --
        (n => n.Contains("acidhouse"), M(GenreBucket.House, "Acid House")),
        (n => n.Contains("acidjazz"),  M(GenreBucket.Other, "Acid Jazz")),
        (n => n == "acid",             M(GenreBucket.Techno, "Acid", 0.5f)),

        // -- House --
        (n => n.Contains("techhouse"),        M(GenreBucket.House, "Tech House")),
        (n => n.Contains("melodic") && n.Contains("house"), M(GenreBucket.House)),
        (n => n.Contains("afrohouse"),        M(GenreBucket.House, "Afro House")),
        (n => n.Contains("basshouse"),        M(GenreBucket.House, "Bass House")),
        (n => n.Contains("deephouse"),        M(GenreBucket.House, "Deep House")),
        (n => n.Contains("progressivehouse"), M(GenreBucket.House, "Progressive House")),
        (n => n.Contains("garagehouse"),      M(GenreBucket.House, "Garage House")),
        (n => n.Contains("ghettohouse"),      M(GenreBucket.House, "Ghetto House")),
        (n => n.Contains("tribalhouse"),      M(GenreBucket.House, "Tribal House")),
        (n => n.Contains("hiphouse"),         M(GenreBucket.House, "Hip-House")),
        (n => n.Contains("italohouse"),       M(GenreBucket.House, "Italo House")),
        (n => n.Contains("eurohouse"),        M(GenreBucket.House, "Euro House")),
        (n => n.Contains("electrohouse"),     M(GenreBucket.House, "Electro House", 0.6f)),
        (n => n.Contains("nudisco"),          M(GenreBucket.House, "Nu-Disco", 0.4f)),
        (n => n == "tribal",                  M(GenreBucket.House, "Tribal", 0.5f)),
        (n => n.Contains("house"),            M(GenreBucket.House)),

        // -- Other confusers (explicit, so a tempo-only rule never pushes them into a club bucket) --
        (n => n.Contains("hardhouse"),        M(GenreBucket.Other, "Hard House")),
        (n => n.Contains("tropicalhouse"),    M(GenreBucket.Other, "Tropical House")),
        (n => n.Contains("hardstyle"),        M(GenreBucket.Other, "Hardstyle")),
        (n => n.Contains("gabber"),           M(GenreBucket.Other, "Gabber")),
        (n => n.Contains("happyhardcore"),    M(GenreBucket.Other, "Happy Hardcore")),
        (n => n.Contains("speedcore"),        M(GenreBucket.Other, "Speedcore")),
        (n => n.Contains("jumpstyle"),        M(GenreBucket.Other, "Jumpstyle")),
        (n => n.Contains("harddance"),        M(GenreBucket.Other, "Hard Dance")),
        (n => n.Contains("hardcore"),         M(GenreBucket.Other, "Hardcore")),
        (n => n.Contains("ukgarage"),         M(GenreBucket.Other, "UK Garage")),
        (n => n == "ukg",                     M(GenreBucket.Other, "UK Garage")),
        (n => n.Contains("speedgarage"),      M(GenreBucket.Other, "Speed Garage")),
        (n => n.Contains("bassline"),         M(GenreBucket.Other, "Bassline")),
        (n => n is "2step" or "twostep",      M(GenreBucket.Other, "2-Step")),
        (n => n.Contains("garage"),           M(GenreBucket.Other, "Garage")),
        (n => n.Contains("breakbeat"),        M(GenreBucket.Other, "Breakbeat")),
        (n => n.Contains("progressivebreaks"), M(GenreBucket.Other, "Progressive Breaks")),
        (n => n.Contains("breaks"),           M(GenreBucket.Other, "Breaks")),
        (n => n.Contains("bigbeat"),          M(GenreBucket.Other, "Big Beat")),
        (n => n.Contains("ukbass"),           M(GenreBucket.Other, "UK Bass")),
        (n => n.Contains("trance"),           M(GenreBucket.Other, "Trance")),
        (n => n.Contains("electro"),          M(GenreBucket.Other, "Electro")),
        (n => n.Contains("footwork"),         M(GenreBucket.Other, "Footwork")),
        (n => n.Contains("juke"),             M(GenreBucket.Other, "Juke")),
        (n => n.Contains("downtempo"),        M(GenreBucket.Other, "Downtempo")),
        (n => n.Contains("ambient"),          M(GenreBucket.Other, "Ambient")),
        (n => n.Contains("chillout"),         M(GenreBucket.Other, "Chillout")),
        (n => n.Contains("triphop"),          M(GenreBucket.Other, "Trip Hop")),
        (n => n == "idm",                     M(GenreBucket.Other, "IDM")),
        (n => n == "dub",                     M(GenreBucket.Other, "Dub")),
        (n => n.Contains("trap"),             M(GenreBucket.Other, "Trap")),
        (n => n.Contains("hiphop"),           M(GenreBucket.Other, "Hip Hop")),
        (n => n == "rap",                     M(GenreBucket.Other, "Rap")),
        (n => n is "rnb" or "rb",             M(GenreBucket.Other, "R&B")),
        (n => n.Contains("bassmusic"),        M(GenreBucket.Other, "Bass Music")),
        (n => n == "bass",                    M(GenreBucket.Other, "Bass")),
    };

    /// <summary>
    /// Maps free-form genre text (tags, provider strings). Comma / semicolon / slash / newline
    /// separated; case- and punctuation-insensitive. Junk values are skipped, and the first
    /// non-junk value that matches a rule wins. When every value is junk (or the text is empty),
    /// the result is a no-vote (<see cref="GenreBucketMapping.IsJunk"/>).
    /// </summary>
    public static GenreBucketMapping MapFreeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new GenreBucketMapping(GenreBucket.Other, null, 0f, IsJunk: true);

        var parts = text.Split(new[] { ',', ';', '/', '\n', '\r', '|' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (IsJunkGenre(part)) continue;

            var n = Normalize(part);
            if (n.Length == 0) continue;

            foreach (var (test, map) in FreeTextRules)
                if (test(n)) return map;

            return new GenreBucketMapping(GenreBucket.Other, part.Trim(), 1f);
        }

        return new GenreBucketMapping(GenreBucket.Other, null, 0f, IsJunk: true);
    }

    // ---------------------------------------------------------------------------------------
    // Junk detection, canonical display, family mapping.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// True when a genre label carries no signal and must be treated as missing: the generic
    /// electronic/dance umbrella terms, "Other"/"Unknown"/"Misc"/"Various", empty text, and bare
    /// ID3v1 numeric style codes such as <c>(52)</c> (= Electronic).
    /// </summary>
    public static bool IsJunkGenre(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        var n = Normalize(t);
        if (n.Length == 0) return true;

        // "deep" alone and non-English "dance music" tags say nothing about the scene (#249 traces).
        if (n is "electronic" or "electronica" or "dance" or "dancedj" or "club" or "edm" or "deep"
            or "dancemusic" or "tanecnihudba" or "tanečníhudba" or "musiqueelectronique" or "elektronischemusik"
            or "electronicdancemusic" or "other" or "unknown" or "misc" or "genre" or "various")
            return true;

        // ID3v1 numeric style codes like "(52)".
        if (t.Length > 2 && t[0] == '(' && t[^1] == ')' && t[1..^1].All(char.IsAsciiDigit))
            return true;

        return false;
    }

    /// <summary>
    /// Canonical, stable display string for a bucket. Subgenres are only emitted when they still
    /// contain the family term (e.g. "Hard Techno"), so downstream substring matchers keep working.
    /// </summary>
    public static string CanonicalDisplay(GenreBucket bucket, string? subgenre)
    {
        var sub = subgenre?.Trim();
        return bucket switch
        {
            GenreBucket.DrumAndBass => FamilyOrSub(sub, "drum and bass", "Drum and Bass"),
            GenreBucket.Dubstep     => FamilyOrSub(sub, "dubstep", "Dubstep"),
            GenreBucket.Techno      => FamilyOrSub(sub, "techno", "Techno"),
            GenreBucket.House       => FamilyOrSub(sub, "house", "House"),
            _                       => string.IsNullOrEmpty(sub) ? "Other" : sub,
        };
    }

    /// <summary>
    /// Maps a bucket + subgenre to the existing <see cref="GenreFamily"/> /
    /// <see cref="FourOnTheFloorSubgenre"/> pair. No new enum members are introduced: "Other"
    /// becomes <see cref="GenreFamily.Unknown"/>, which every consumer treats as generic behaviour.
    /// </summary>
    public static (GenreFamily Family, FourOnTheFloorSubgenre Subgenre) ToFamily(GenreBucket bucket, string? subgenre)
    {
        return bucket switch
        {
            GenreBucket.DrumAndBass => (GenreFamily.Breakbeat, FourOnTheFloorSubgenre.Unknown),
            GenreBucket.Dubstep     => (GenreFamily.Dubstep, FourOnTheFloorSubgenre.Unknown),
            GenreBucket.Techno      => (GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.TechHouseTechno),
            GenreBucket.House       => IsTechHouse(subgenre)
                ? (GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.TechHouseTechno)
                : (GenreFamily.FourOnTheFloor, FourOnTheFloorSubgenre.House),
            _                       => (GenreFamily.Unknown, FourOnTheFloorSubgenre.Unknown),
        };
    }

    private static string FamilyOrSub(string? subgenre, string familyTerm, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(subgenre) &&
            subgenre.Contains(familyTerm, StringComparison.OrdinalIgnoreCase))
            return subgenre;
        return fallback;
    }

    private static bool IsTechHouse(string? subgenre)
        => subgenre?.Trim().Equals("Tech House", StringComparison.OrdinalIgnoreCase) == true;

    private static string Normalize(string s)
        => new string(s.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
