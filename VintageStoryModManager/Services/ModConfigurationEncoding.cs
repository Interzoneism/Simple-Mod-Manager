using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace VintageStoryModManager.Services;

internal static class ModConfigurationEncoding
{
    private const string BrotliPrefix = "br64:";

    public static string? Encode(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        var decoded = Decode(content);
        if (string.IsNullOrEmpty(decoded)) return decoded;

        var bytes = Encoding.UTF8.GetBytes(decoded);
        using var buffer = new MemoryStream();
        using (var brotli = new BrotliStream(buffer, CompressionLevel.SmallestSize, true))
        {
            brotli.Write(bytes, 0, bytes.Length);
        }

        var compressed = buffer.ToArray();

        // Only return compressed payload when it meaningfully shrinks the content.
        if (compressed.Length + BrotliPrefix.Length < bytes.Length)
            return $"{BrotliPrefix}{Convert.ToBase64String(compressed)}";

        return decoded;
    }

    public static string Decode(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content ?? string.Empty;

        if (!content.StartsWith(BrotliPrefix, StringComparison.Ordinal)) return content;

        try
        {
            var raw = Convert.FromBase64String(content[BrotliPrefix.Length..]);
            using var input = new MemoryStream(raw);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(brotli, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return content;
        }
    }
}
