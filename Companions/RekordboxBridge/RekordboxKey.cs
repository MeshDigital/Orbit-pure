using System.IO.Compression;
using System.Text;

namespace RekordboxBridge;

/// <summary>
/// The SQLCipher passphrase of Rekordbox 6/7's master.db, kept obfuscated exactly as pyrekordbox
/// ships it (MIT licence, Copyright (c) 2022-2025 Dylan Jones, github.com/dylanljones/pyrekordbox:
/// pyrekordbox/utils.py deobfuscate + pyrekordbox/masterdb/database.py BLOB). Python's base85
/// alphabet, XOR with a repeating key, then zlib.
/// </summary>
internal static class RekordboxKey
{
    private const string Blob = "PN_Pq^*N>(JYe*u^8;Yg76HuZ<mR13S?=>)b9;DpoTXV(6ItkU`}8*m6tx_I{Solh_N#dfe{v=";
    private static readonly byte[] BlobKey = Encoding.ASCII.GetBytes("657f48f84c437cc1");

    /// <summary>pyrekordbox's sanity check on a key.</summary>
    internal const string ExpectedPrefix = "402fd";

    internal static string Get()
    {
        var data = Base85Decode(Blob);
        for (int i = 0; i < data.Length; i++) data[i] ^= BlobKey[i % BlobKey.Length];
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(z, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&()*+-;<=>?@^_`{|}~";

    /// <summary>Python's base64.b85decode (RFC 1924 alphabet, padded with '~').</summary>
    internal static byte[] Base85Decode(string text)
    {
        int padding = (5 - text.Length % 5) % 5;
        text += new string('~', padding);
        var output = new List<byte>(text.Length / 5 * 4);
        for (int i = 0; i < text.Length; i += 5)
        {
            uint acc = 0;
            for (int j = 0; j < 5; j++)
            {
                int digit = Alphabet.IndexOf(text[i + j]);
                if (digit < 0) throw new FormatException($"Invalid base85 character '{text[i + j]}'.");
                acc = unchecked(acc * 85 + (uint)digit);
            }
            output.Add((byte)(acc >> 24));
            output.Add((byte)(acc >> 16));
            output.Add((byte)(acc >> 8));
            output.Add((byte)acc);
        }
        output.RemoveRange(output.Count - padding, padding);
        return output.ToArray();
    }
}
