using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

/// <summary>
/// Service for interacting with the Vintage Story mod database API.
/// </summary>
public interface IModApiService
{
    /// <summary>
    /// Queries the mod database with optional filters.
    /// </summary>
    Task<List<DownloadableModOnList>> QueryModsAsync(
        string? textFilter = null,
        ModAuthor? authorFilter = null,
        IEnumerable<GameVersion>? versionsFilter = null,
        IEnumerable<ModTag>? tagsFilter = null,
        string orderBy = "follows",
        string orderByOrder = "desc",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single mod by its ID.
    /// </summary>
    Task<DownloadableMod?> GetModAsync(int modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single mod by its string ID.
    /// </summary>
    Task<DownloadableMod?> GetModAsync(string modIdStr, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available authors.
    /// </summary>
    Task<List<ModAuthor>> GetAuthorsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available game versions.
    /// </summary>
    Task<List<GameVersion>> GetGameVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available tags.
    /// </summary>
    Task<List<ModTag>> GetTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a mod file to the specified path.
    /// </summary>
    Task<bool> DownloadModAsync(
        string downloadUrl,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of the mod API service.
/// </summary>
public class ModApiService : IModApiService
{
    private const string BaseUrl = "https://mods.vintagestory.at";
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public ModApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<List<DownloadableModOnList>> QueryModsAsync(
        string? textFilter = null,
        ModAuthor? authorFilter = null,
        IEnumerable<GameVersion>? versionsFilter = null,
        IEnumerable<ModTag>? tagsFilter = null,
        string orderBy = "follows",
        string orderByOrder = "desc",
        CancellationToken cancellationToken = default)
    {
        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            System.Diagnostics.Debug.WriteLine("Internet access is disabled - skipping mod query");
            return [];
        }

        try
        {
            var queryBuilder = new StringBuilder($"{BaseUrl}/api/mods?");
            var parameters = new List<string>();

            if (!string.IsNullOrWhiteSpace(textFilter) && textFilter.Length > 1)
            {
                parameters.Add($"text={Uri.EscapeDataString(textFilter)}");
            }

            if (authorFilter != null && !string.IsNullOrEmpty(authorFilter.UserId))
            {
                parameters.Add($"author={Uri.EscapeDataString(authorFilter.UserId)}");
            }

            if (versionsFilter != null)
            {
                foreach (var version in versionsFilter)
                {
                    parameters.Add($"gameversions[]={Uri.EscapeDataString(version.TagId.ToString())}");
                }
            }

            if (tagsFilter != null)
            {
                foreach (var tag in tagsFilter)
                {
                    parameters.Add($"tagids[]={tag.TagId}");
                }
            }

            parameters.Add($"orderby={Uri.EscapeDataString(orderBy)}");
            parameters.Add($"orderdirection={Uri.EscapeDataString(orderByOrder)}");

            queryBuilder.Append(string.Join("&", parameters));

            var response = await _httpClient.GetStringAsync(queryBuilder.ToString(), cancellationToken);
            var result = JsonSerializer.Deserialize<ModListResponse>(response, _jsonOptions);

            return result?.Mods ?? [];
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] JSON deserialization error fetching mods: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] JSON exception details: {ex}");
            return [];
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] HTTP request error fetching mods: {ex.Message}");
            return [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Unexpected error fetching mods: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Exception details: {ex}");
            return [];
        }
    }

    public async Task<DownloadableMod?> GetModAsync(int modId, CancellationToken cancellationToken = default)
    {
        return await GetModAsync(modId.ToString(), cancellationToken);
    }

    public async Task<DownloadableMod?> GetModAsync(string modIdStr, CancellationToken cancellationToken = default)
    {
        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            System.Diagnostics.Debug.WriteLine("Internet access is disabled - skipping mod fetch");
            return null;
        }

        try
        {
            var url = $"{BaseUrl}/api/mod/{Uri.EscapeDataString(modIdStr)}";
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Fetching mod from: {url}");

            var response = await _httpClient.GetStringAsync(url, cancellationToken);

            // Log response metadata instead of full content for performance
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Received response: {response.Length} characters");

            var result = JsonSerializer.Deserialize<ModResponse>(response, _jsonOptions);

            if (result?.StatusCode != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[ModApiService] Non-200 status code: {result?.StatusCode}");
                return null;
            }

            var mod = result.Mod;
            if (mod != null)
            {
                System.Diagnostics.Debug.WriteLine($"[ModApiService] Successfully deserialized mod: {mod.Name}");
                System.Diagnostics.Debug.WriteLine($"[ModApiService] Mod has {mod.Releases?.Count ?? 0} releases");

                // Filter out releases with invalid file IDs (null was converted to 0)
                if (mod.Releases != null)
                {
                    var removedCount = mod.Releases.RemoveAll(r => r.FileId <= 0);

                    if (removedCount > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ModApiService] Filtered out {removedCount} releases with invalid file IDs");
                    }
                }

                // Filter out screenshots with invalid file IDs (null was converted to 0)
                if (mod.Screenshots != null)
                {
                    var removedCount = mod.Screenshots.RemoveAll(s => s.FileId <= 0);

                    if (removedCount > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ModApiService] Filtered out {removedCount} screenshots with invalid file IDs");
                    }
                }

                if (mod.Releases == null || mod.Releases.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[ModApiService] WARNING: Mod {mod.Name} has NO valid releases!");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ModApiService] Deserialized mod is null");
            }

            return mod;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] JSON deserialization error fetching mod {modIdStr}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] JSON exception details: {ex}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] HTTP request error fetching mod {modIdStr}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Unexpected error fetching mod {modIdStr}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Exception details: {ex}");
            return null;
        }
    }

    public async Task<List<ModAuthor>> GetAuthorsAsync(CancellationToken cancellationToken = default)
    {
        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            System.Diagnostics.Debug.WriteLine("Internet access is disabled - skipping authors fetch");
            return [];
        }

        try
        {
            var response = await _httpClient.GetStringAsync($"{BaseUrl}/api/authors", cancellationToken);
            var result = JsonSerializer.Deserialize<AuthorsResponse>(response, _jsonOptions);

            return result?.Authors ?? [];
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] JSON deserialization error fetching authors: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] JSON exception details: {ex}");
            return [];
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] HTTP request error fetching authors: {ex.Message}");
            return [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Unexpected error fetching authors: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Exception details: {ex}");
            return [];
        }
    }

    public async Task<List<GameVersion>> GetGameVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            System.Diagnostics.Debug.WriteLine("Internet access is disabled - skipping game versions fetch");
            return [];
        }

        try
        {
            System.Diagnostics.Debug.WriteLine($"Fetching game versions from {BaseUrl}/api/gameversions");
            var response = await _httpClient.GetStringAsync($"{BaseUrl}/api/gameversions", cancellationToken);
            System.Diagnostics.Debug.WriteLine($"Raw API response (first 500 chars): {(response.Length > 500 ? response.Substring(0, 500) : response)}");

            var result = JsonSerializer.Deserialize<GameVersionsResponse>(response, _jsonOptions);
            System.Diagnostics.Debug.WriteLine($"Deserialized {result?.GameVersions?.Count ?? 0} game versions");

            var versions = result?.GameVersions ?? [];
            // Return in reverse order (most recent first) without modifying original
            var reversedVersions = versions.AsEnumerable().Reverse().ToList();
            System.Diagnostics.Debug.WriteLine($"Returning {reversedVersions.Count} game versions");
            return reversedVersions;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] JSON deserialization error fetching game versions: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] JSON exception details: {ex}");
            return [];
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] HTTP request error fetching game versions: {ex.Message}");
            return [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Unexpected error fetching game versions: {ex.GetType().Name} - {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Stack trace: {ex.StackTrace}");
            return [];
        }
    }

    public async Task<List<ModTag>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            System.Diagnostics.Debug.WriteLine("Internet access is disabled - skipping tags fetch");
            return [];
        }

        try
        {
            var response = await _httpClient.GetStringAsync($"{BaseUrl}/api/tags", cancellationToken);
            var result = JsonSerializer.Deserialize<TagsResponse>(response, _jsonOptions);

            return result?.Tags ?? [];
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] JSON deserialization error fetching tags: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] JSON exception details: {ex}");
            return [];
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] HTTP request error fetching tags: {ex.Message}");
            return [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Unexpected error fetching tags: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Exception details: {ex}");
            return [];
        }
    }

    public async Task<bool> DownloadModAsync(
        string downloadUrl,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (InternetAccessManager.IsInternetAccessDisabled)
        {
            System.Diagnostics.Debug.WriteLine("Internet access is disabled - skipping mod download");
            return false;
        }

        try
        {
            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            var totalBytesRead = 0L;
            int bytesRead;

            try
            {
                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        progress?.Report((double)totalBytesRead / totalBytes * 100);
                    }
                }
            }
            catch (IOException ioEx)
            {
                System.Diagnostics.Debug.WriteLine($"[ModApiService] IOException during download from {downloadUrl}");
                System.Diagnostics.Debug.WriteLine($"[ModApiService] Bytes read: {totalBytesRead}/{totalBytes}");
                System.Diagnostics.Debug.WriteLine($"[ModApiService] IOException details: {ioEx.Message}");
                if (ioEx.InnerException != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[ModApiService] Inner exception: {ioEx.InnerException.GetType().Name} - {ioEx.InnerException.Message}");
                }
                throw;
            }
            catch (ObjectDisposedException odEx)
            {
                System.Diagnostics.Debug.WriteLine($"[ModApiService] ObjectDisposedException during download from {downloadUrl} - stream was disposed");
                System.Diagnostics.Debug.WriteLine($"[ModApiService] Object name: {odEx.ObjectName}");
                System.Diagnostics.Debug.WriteLine($"[ModApiService] Bytes read before disposal: {totalBytesRead}/{totalBytes}");
                System.Diagnostics.Debug.WriteLine($"[ModApiService] This may indicate cancellation or premature stream closure");
                throw;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Download cancelled by user or timeout: {downloadUrl}");
            return false;
        }
        catch (IOException ioEx)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] IOException establishing connection to {downloadUrl}: {ioEx.Message}");
            if (ioEx.InnerException != null)
            {
                System.Diagnostics.Debug.WriteLine($"[ModApiService] Inner exception: {ioEx.InnerException.GetType().Name} - {ioEx.InnerException.Message}");
            }
            return false;
        }
        catch (ObjectDisposedException odEx)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] ObjectDisposedException: {odEx.ObjectName} was disposed while downloading {downloadUrl}");
            return false;
        }
        catch (HttpRequestException httpEx)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] HTTP request error downloading mod from {downloadUrl}: {httpEx.Message}");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Unexpected error downloading mod from {downloadUrl}: {ex.GetType().Name} - {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ModApiService] Exception details: {ex}");
            return false;
        }
    }
}
