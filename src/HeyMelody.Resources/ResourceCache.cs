using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;

namespace HeyMelodyWindows.Resources;

/// <summary>Official private-cache envelope: nonce12 + gzip(JSON) ciphertext + tag16.</summary>
public static class ResourceCacheCodec
{
    public static byte[] Encrypt(ReadOnlySpan<byte> json, ReadOnlySpan<byte> key)
    {
        if (json.Length > ResourceLimits.ConfigBytes) throw new InvalidDataException("Configuration exceeds size limit.");
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(json);
        byte[] compressed = output.ToArray(), envelope = new byte[12 + compressed.Length + 16];
        RandomNumberGenerator.Fill(envelope.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(envelope.AsSpan(0, 12), compressed, envelope.AsSpan(12, compressed.Length), envelope.AsSpan(12 + compressed.Length, 16));
        return envelope;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key)
    {
        if (envelope.Length < 28 || envelope.Length > ResourceLimits.ConfigBytes + 1024 * 1024) throw new InvalidDataException("Invalid cache envelope.");
        byte[] compressed = new byte[envelope.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(envelope[..12], envelope.Slice(12, compressed.Length), envelope[^16..], compressed);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192]; int read;
        while ((read = gzip.Read(buffer)) > 0)
        { if (output.Length + read > ResourceLimits.ConfigBytes) throw new InvalidDataException("Expanded cache exceeds limit."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
}

internal static class ResourceFiles
{
    internal static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Streams the file through SHA-256 without holding it in memory. 64 KiB stays below the large-object threshold, unlike a pooled 80 KiB request.</summary>
    internal static string Sha256File(string path, out long length)
    {
        length = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536); int read;
        try
        {
            while ((read = stream.Read(buffer, 0, 65536)) > 0) { hash.AppendData(buffer, 0, read); length += read; }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
