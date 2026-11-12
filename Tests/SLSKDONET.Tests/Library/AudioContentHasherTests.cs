using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Library;
using Xunit;

namespace SLSKDONET.Tests.Library;

public class AudioContentHasherTests
{
    private readonly AudioContentHasher _hasher = new(NullLogger<AudioContentHasher>.Instance);

    [Fact]
    public async Task Mp3_IgnoresId3v2Tags()
    {
        var payload = new byte[4096];
        new Random(1).NextBytes(payload);

        var a = Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}.mp3");
        var b = Path.Combine(Path.GetTempPath(), $"b-{Guid.NewGuid():N}.mp3");
        try
        {
            File.WriteAllBytes(a, Concat(BuildId3v2("AAA"), payload));
            File.WriteAllBytes(b, Concat(BuildId3v2("BBB"), payload));

            var ha = await _hasher.ComputeAsync(a);
            var hb = await _hasher.ComputeAsync(b);
            Assert.NotNull(ha);
            Assert.NotNull(hb);
            Assert.Equal(ha!.Value, hb!.Value);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task Mp3_IgnoresTrailingId3v1()
    {
        var payload = new byte[4096];
        new Random(2).NextBytes(payload);

        var a = Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}.mp3");
        var b = Path.Combine(Path.GetTempPath(), $"b-{Guid.NewGuid():N}.mp3");
        try
        {
            File.WriteAllBytes(a, Concat(BuildId3v2("X"), payload));
            File.WriteAllBytes(b, Concat(BuildId3v2("X"), payload, BuildId3v1()));
            Assert.Equal((await _hasher.ComputeAsync(a))!.Value, (await _hasher.ComputeAsync(b))!.Value);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task DifferentAudio_GivesDifferentHash()
    {
        var payloadA = new byte[4096];
        var payloadB = new byte[4096];
        new Random(3).NextBytes(payloadA);
        new Random(4).NextBytes(payloadB);

        var a = Path.Combine(Path.GetTempPath(), $"a-{Guid.NewGuid():N}.mp3");
        var b = Path.Combine(Path.GetTempPath(), $"b-{Guid.NewGuid():N}.mp3");
        try
        {
            File.WriteAllBytes(a, Concat(BuildId3v2("X"), payloadA));
            File.WriteAllBytes(b, Concat(BuildId3v2("X"), payloadB));
            Assert.NotEqual((await _hasher.ComputeAsync(a))!.Value, (await _hasher.ComputeAsync(b))!.Value);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task TruncatedWav_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trunc-{Guid.NewGuid():N}.wav");
        try
        {
            // RIFF/WAVE header + a "data" chunk claiming 1 MB but no payload present.
            var bytes = new byte[20];
            WriteAscii(bytes, 0, "RIFF");
            BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4);
            WriteAscii(bytes, 8, "WAVE");
            WriteAscii(bytes, 12, "data");
            BitConverter.GetBytes((uint)1_000_000).CopyTo(bytes, 16);
            File.WriteAllBytes(path, bytes);

            var result = await _hasher.ComputeAsync(path);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildId3v2(string tagText)
    {
        // ID3v2.3 header, syncsafe size = 0 (10-byte header only, no frames).
        var header = new byte[10];
        header[0] = (byte)'I';
        header[1] = (byte)'D';
        header[2] = (byte)'3';
        header[3] = 3;   // version 2.3
        header[4] = 0;   // revision
        header[5] = 0;   // flags
        // syncsafe size = 0
        return header;
    }

    private static byte[] BuildId3v1()
    {
        var tag = new byte[128];
        tag[0] = (byte)'T';
        tag[1] = (byte)'A';
        tag[2] = (byte)'G';
        return tag;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    private static void WriteAscii(byte[] buffer, int offset, string s)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, 0, buffer, offset, bytes.Length);
    }
}