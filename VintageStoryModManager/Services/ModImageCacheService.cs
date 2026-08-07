using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides efficient caching for mod database images to avoid repeated downloads.
///     <para>
///     Cache files are stored with descriptor-based names (e.g., "modsource_modid.png")
///     for better organization and readability. Files are stored in the mod database
///     image cache directory managed by <see cref="ModCacheLocator"/>.
///     </para>
///     <para>
///     Performance optimizations:
///     - Uses Span&lt;T&gt; and stackalloc for zero-allocation hash computation
///     - Eliminates redundant file system checks
///     - Employs concurrent file locking to prevent race conditions
///     - Atomic writes using temp files to ensure cache consistency
///     </para>
/// </summary>
internal static class ModImageCacheService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Attempts to retrieve a cached image for the given URL synchronously.
    ///     This method does not use file locking and is intended for UI thread usage
    ///     where blocking is acceptable but deadlocks must be avoided.
    /// </summary>
    /// <param name="imageUrl">The URL of the image.</param>
    /// <param name="descriptor">Additional details used to generate a readable cache name.</param>
    /// <returns>The cached image bytes, or null if not cached.</returns>
    public static byte[]? TryGetCachedImage(string imageUrl, ModImageCacheDescriptor? descriptor = null)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;

        var cachePath = GetCachePath(imageUrl, descriptor);

        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath)) return null;

        try
        {
            return File.ReadAllBytes(cachePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     Attempts to retrieve a cached image for the given URL.
    /// </summary>
    /// <param name="imageUrl">The URL of the image.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <param name="descriptor">Additional details used to generate a readable cache name.</param>
    /// <returns>The cached image bytes, or null if not cached.</returns>
    public static async Task<byte[]?> TryGetCachedImageAsync(
        string imageUrl,
        CancellationToken cancellationToken,
        ModImageCacheDescriptor? descriptor = null)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;

        var cachePath = GetCachePath(imageUrl, descriptor);

        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath)) return null;

        var fileLock = await AcquireLockAsync(cachePath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(cachePath)) return null;

            return await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    ///     Stores an image in the cache.
    /// </summary>
    /// <param name="imageUrl">The URL of the image.</param>
    /// <param name="imageBytes">The image data to cache.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <param name="descriptor">Additional details used to generate a readable cache name.</param>
    public static async Task StoreImageAsync(
        string imageUrl,
        byte[] imageBytes,
        CancellationToken cancellationToken,
        ModImageCacheDescriptor? descriptor = null)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return;
        if (imageBytes is null || imageBytes.Length == 0) return;

        var cachePath = GetCachePath(imageUrl, descriptor);
        if (string.IsNullOrWhiteSpace(cachePath)) return;

        var directory = Path.GetDirectoryName(cachePath);
        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            return;
        }

        var fileLock = await AcquireLockAsync(cachePath, cancellationToken).ConfigureAwait(false);
        try
        {
            var tempPath = cachePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, imageBytes, cancellationToken).ConfigureAwait(false);

            try
            {
                File.Move(tempPath, cachePath, true);
            }
            catch (IOException)
            {
                try
                {
                    File.Replace(tempPath, cachePath, null);
                }
                catch (Exception)
                {
                    // Clean up temp file only if Replace also fails
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch (Exception)
                    {
                        // Ignore cleanup errors
                    }
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Ignore cache storage failures - caching is best effort.
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    ///     Clears all cached images.
    /// </summary>
    internal static void ClearCacheDirectory()
    {
        var cacheDirectory = ModCacheLocator.GetModDatabaseImageCacheDirectory();
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory)) return;

        try
        {
            Directory.Delete(cacheDirectory, true);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to delete the mod image cache at {cacheDirectory}.", ex);
        }
    }

    private static string? GetCachePath(
        string imageUrl,
        ModImageCacheDescriptor? descriptor)
    {
        var cacheDirectory = ModCacheLocator.GetModDatabaseImageCacheDirectory();
        if (string.IsNullOrWhiteSpace(cacheDirectory)) return null;

        var fileName = GenerateCacheFileName(imageUrl, descriptor);
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        return Path.Combine(cacheDirectory, fileName);
    }

    private static string GenerateCacheFileName(string imageUrl, ModImageCacheDescriptor? descriptor)
    {
        var extension = GetImageExtension(imageUrl);

        // Always use descriptor-based naming for better cache organization
        // Fall back to hash-based naming only when descriptor is unavailable
        if (descriptor is null)
        {
            var hash = ComputeUrlHash(imageUrl);
            return string.IsNullOrWhiteSpace(extension) ? hash : $"{hash}{extension}";
        }

        var segments = new List<string>(3);

        var sourceSegment = NormalizeSegment(descriptor.ApiSource);
        if (!string.IsNullOrWhiteSpace(sourceSegment)) segments.Add(sourceSegment);

        var modSegment = NormalizeSegment(descriptor.ModId ?? descriptor.ModName);
        if (!string.IsNullOrWhiteSpace(modSegment)) segments.Add(modSegment);

        // If descriptor provided but no usable segments, fall back to hash
        if (segments.Count == 0)
        {
            var hash = ComputeUrlHash(imageUrl);
            return string.IsNullOrWhiteSpace(extension) ? hash : $"{hash}{extension}";
        }

        return string.Concat(string.Join('_', segments), extension);
    }

    private static string NormalizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sanitized = ModCacheLocator.SanitizeFileName(value, "image");
        var normalized = sanitized.Replace(' ', '_').Trim('_');

        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : normalized.ToLowerInvariant();
    }

    private static string GetImageExtension(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return ".png";

        try
        {
            // Remove query string if present using Span for better performance
            var urlSpan = imageUrl.AsSpan();
            var queryIndex = urlSpan.IndexOf('?');
            if (queryIndex >= 0)
            {
                urlSpan = urlSpan.Slice(0, queryIndex);
            }

            // Find the last dot for extension
            var lastDotIndex = urlSpan.LastIndexOf('.');
            if (lastDotIndex < 0 || lastDotIndex == urlSpan.Length - 1) return ".png";

            var extensionSpan = urlSpan.Slice(lastDotIndex);

            // Check for common image extensions (case-insensitive)
            if (extensionSpan.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                extensionSpan.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extensionSpan.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                extensionSpan.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                extensionSpan.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                extensionSpan.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                return extensionSpan.ToString().ToLowerInvariant();
            }
        }
        catch (Exception)
        {
            // Ignore parsing errors
        }

        return ".png";
    }

    private static string ComputeUrlHash(string url)
    {
        // Use Span-based operations for better performance
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(url.Length);
        Span<byte> bytes = maxByteCount <= 1024 ? stackalloc byte[maxByteCount] : new byte[maxByteCount];
        var actualByteCount = Encoding.UTF8.GetBytes(url.AsSpan(), bytes);

        Span<byte> hashBytes = stackalloc byte[32]; // SHA256 produces 32 bytes
        SHA256.HashData(bytes.Slice(0, actualByteCount), hashBytes);

        // Use a URL-safe base64 encoding and take first 32 characters for a reasonable filename length
        Span<char> base64Chars = stackalloc char[44]; // Base64 of 32 bytes = 44 chars
        if (!Convert.TryToBase64Chars(hashBytes, base64Chars, out var charsWritten) || charsWritten == 0)
        {
            // Fallback to standard Base64 encoding if conversion fails
            return Convert.ToBase64String(hashBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=').Substring(0, 32);
        }

        // Make URL-safe and truncate
        var targetLength = Math.Min(32, charsWritten);
        Span<char> urlSafeChars = stackalloc char[targetLength];

        for (var i = 0; i < targetLength; i++)
        {
            var c = base64Chars[i];
            urlSafeChars[i] = c switch
            {
                '+' => '-',
                '/' => '_',
                '=' => '_',
                _ => c
            };
        }

        return urlSafeChars.ToString();
    }

    private static async Task<SemaphoreSlim> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        var gate = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return gate;
    }
}

internal sealed record ModImageCacheDescriptor(string? ModId, string? ModName, string? ApiSource);
