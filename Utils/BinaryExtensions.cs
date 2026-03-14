using System;
using System.Buffers.Binary;
using System.IO;

namespace SLSKDONET.Utils;

/// <summary>
/// Big-Endian binary reader extensions for Rekordbox ANLZ file parsing.
/// Rekordbox uses Motorola (Big-Endian) byte order, .NET is Intel (Little-Endian).
/// Uses hardware intrinsics (Bswap) via BinaryPrimitives for maximum performance.
/// </summary>
public static class BinaryExtensions
{
    /// <summary>
    /// Reads a 16-bit unsigned integer in Big-Endian format.
    /// </summary>
    public static ushort ReadUInt16BigEndian(this BinaryReader reader)
    {
        Span<byte> buffer = stackalloc byte[2];
        reader.Read(buffer);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }
    
    /// <summary>
    /// Reads a 32-bit unsigned integer in Big-Endian format.
    /// </summary>
    public static uint ReadUInt32BigEndian(this BinaryReader reader)
    {
        Span<byte> buffer = stackalloc byte[4];
        reader.Read(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }
    
    /// <summary>
    /// Reads a 16-bit signed integer in Big-Endian format.
    /// </summary>
    public static short ReadInt16BigEndian(this BinaryReader reader)
    {
        Span<byte> buffer = stackalloc byte[2];
        reader.Read(buffer);
        return BinaryPrimitives.ReadInt16BigEndian(buffer);
    }
    
    /// <summary>
    /// Reads a 32-bit signed integer in Big-Endian format.
    /// </summary>
    public static int ReadInt32BigEndian(this BinaryReader reader)
    {
        Span<byte> buffer = stackalloc byte[4];
        reader.Read(buffer);
        return BinaryPrimitives.ReadInt32BigEndian(buffer);
    }
    
    /// <summary>
    /// Writes a 32-bit unsigned integer in Big-Endian format.
    /// </summary>
    public static void WriteUInt32BigEndian(this BinaryWriter writer, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        writer.Write(buffer);
    }
    
    /// <summary>
    /// Writes a 16-bit unsigned integer in Big-Endian format.
    /// </summary>
    public static void WriteUInt16BigEndian(this BinaryWriter writer, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        writer.Write(buffer);
    }
}
