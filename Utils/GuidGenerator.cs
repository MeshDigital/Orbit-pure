using System;
using System.Security.Cryptography;
using System.Text;

namespace SLSKDONET.Utils;

/// <summary>
/// Helper for generating deterministic GUIDs (UUID v5 style) from strings.
/// Useful for creating consistent IDs for playlists based on their source URL.
/// </summary>
public static class GuidGenerator
{
    // Namespace for URL-based GUIDs (arbitrary constant to salt our generation)
    private static readonly Guid UrlNamespace = Guid.Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8"); // ISO OID namespace

    /// <summary>
    /// Creates a deterministic GUID from a URL string.
    /// Identical URLs will always produce the identical GUID.
    /// </summary>
    public static Guid CreateFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return Guid.NewGuid();

        // Use MD5 as per RFC 4122 for UUID v3 (or SHA1 for v5, but MD5 is standard for simple deterministic GUIDs in .NET usually)
        // Here we implement a variant of UUID v5 (SHA-1) for better collision resistance than MD5.
        
        // 1. Concatenate Namespace + Name
        var nsBytes = UrlNamespace.ToByteArray();
        SwapByteOrder(nsBytes); // .NET is little-endian, RFC 4122 is big-endian
        
        var nameBytes = Encoding.UTF8.GetBytes(url);
        
        var data = new byte[nsBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(nsBytes, 0, data, 0, nsBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, data, nsBytes.Length, nameBytes.Length);

        // 2. Hash
        using var algorithm = SHA1.Create();
        var hash = algorithm.ComputeHash(data);

        // 3. Truncate to 16 bytes
        var newGuid = new byte[16];
        Array.Copy(hash, 0, newGuid, 0, 16);

        // 4. Set Version (5) and Variant (RFC 4122)
        newGuid[6] = (byte)((newGuid[6] & 0x0F) | (5 << 4));
        newGuid[8] = (byte)((newGuid[8] & 0x3F) | 0x80);

        // 5. Convert back to little-endian for .NET
        SwapByteOrder(newGuid);

        return new Guid(newGuid);
    }

    // Helper to handle endianness differences
    private static void SwapByteOrder(byte[] guid)
    {
        Swap(guid, 0, 3);
        Swap(guid, 1, 2);
        Swap(guid, 4, 5);
        Swap(guid, 6, 7);
    }

    private static void Swap(byte[] b, int left, int right)
    {
        var temp = b[left];
        b[left] = b[right];
        b[right] = temp;
    }
}
