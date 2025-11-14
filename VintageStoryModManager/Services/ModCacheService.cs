using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides helpers for ensuring installed mods are cached on disk.
/// </summary>
internal static class ModCacheService
{
    public static void EnsureModCached(string? modId, string? version, string? sourcePath, ModSourceKind sourceKind)
    {
        if (string.IsNullOrWhiteSpace(modId)
            || string.IsNullOrWhiteSpace(version)
            || string.IsNullOrWhiteSpace(sourcePath))
            return;

        var cacheFileName = Path.GetFileName(sourcePath);
        var cachePath = ModCacheLocator.GetModCachePath(modId, version, cacheFileName);
        if (string.IsNullOrWhiteSpace(cachePath)) return;

        if (ModCacheLocator.TryPromoteLegacyCacheFile(modId, version, cacheFileName, cachePath)
            && File.Exists(cachePath))
            return;

        if (string.Equals(sourcePath, cachePath, StringComparison.OrdinalIgnoreCase)) return;

        if (File.Exists(cachePath)) return;

        if (ModCacheLocator.TryLocateCachedModFile(modId, version, cacheFileName, out var existingCacheFile)
            && existingCacheFile is not null)
        {
            var existingDirectory = Path.GetDirectoryName(existingCacheFile);
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(existingDirectory)
                && !string.IsNullOrWhiteSpace(cacheDirectory)
                && string.Equals(existingDirectory, cacheDirectory, StringComparison.OrdinalIgnoreCase))
                return;
        }

        try
        {
            switch (sourceKind)
            {
                case ModSourceKind.Folder:
                    CacheDirectory(sourcePath, cachePath);
                    break;
                case ModSourceKind.ZipArchive:
                case ModSourceKind.Assembly:
                case ModSourceKind.SourceCode:
                default:
                    CacheFile(sourcePath, cachePath);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or InvalidDataException)
        {
            Trace.TraceWarning("Failed to cache mod {0} {1}: {2}", modId, version, ex.Message);
            TryDelete(cachePath);
        }
    }

    private static void CacheFile(string sourcePath, string cachePath)
    {
        if (!File.Exists(sourcePath)) return;

        var cacheDirectory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(cacheDirectory)) Directory.CreateDirectory(cacheDirectory);

        try
        {
            File.Copy(sourcePath, cachePath, false);
        }
        catch (IOException ex)
        {
            // If the cache file was created concurrently we can ignore the failure.
            if (!File.Exists(cachePath)) throw new IOException(ex.Message, ex);
        }
    }

    private static void CacheDirectory(string sourceDirectory, string cachePath)
    {
        if (!Directory.Exists(sourceDirectory)) return;

        var cacheDirectory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(cacheDirectory)) Directory.CreateDirectory(cacheDirectory);

        try
        {
            ZipFile.CreateFromDirectory(sourceDirectory, cachePath, CompressionLevel.Optimal, false);
        }
        catch (IOException ex)
        {
            if (!File.Exists(cachePath)) throw new IOException(ex.Message, ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Failed to clean up cache file {0}: {1}", path, ex.Message);
        }
    }
}