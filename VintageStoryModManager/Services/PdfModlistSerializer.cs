using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VintageStoryModManager.Models;

namespace VintageStoryModManager.Services;

/// <summary>
///     Provides helpers for encoding modlist presets into PDF-friendly payloads and
///     recovering them when loading from a PDF export.
/// </summary>
public static class PdfModlistSerializer
{
    private const string ModlistDelimiter = "###";
    private const string ConfigDelimiter = "@@@";
    private const string MetadataPrefix = "SVSM:";
    private const string ModlistMetadataPrefix = MetadataPrefix + "MODLIST:";
    private const string ConfigMetadataPrefix = MetadataPrefix + "CONFIG:";
    private const string ConfigMetadataMissingValue = "NONE";

    private static readonly JsonSerializerOptions SerializationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions DeserializationOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string SerializeToBase64(SerializablePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var serialized = JsonSerializer.Serialize(preset, SerializationOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(serialized));
    }

    public static string SerializeConfigListToBase64(SerializableConfigList configList)
    {
        ArgumentNullException.ThrowIfNull(configList);

        var serialized = JsonSerializer.Serialize(configList, SerializationOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(serialized));
    }

    public static string CreateModlistMetadataValue(string encodedModlist)
    {
        ArgumentNullException.ThrowIfNull(encodedModlist);

        return ModlistMetadataPrefix + encodedModlist;
    }

    public static string CreateConfigMetadataValue(string? encodedConfigList)
    {
        var payload = string.IsNullOrWhiteSpace(encodedConfigList)
            ? ConfigMetadataMissingValue
            : encodedConfigList!;

        return ConfigMetadataPrefix + payload;
    }

    public static bool TryDeserializeFromJson(string json, out SerializablePreset? preset, out string? errorMessage)
    {
        preset = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            errorMessage = "The selected file was empty.";
            return false;
        }

        try
        {
            preset = JsonSerializer.Deserialize<SerializablePreset>(json, DeserializationOptions);
        }
        catch (JsonException ex)
        {
            errorMessage = ex.Message;
            return false;
        }

        if (preset is null)
        {
            errorMessage = "The selected file was empty.";
            return false;
        }

        return true;
    }

    public static bool TryDeserializeConfigListFromJson(string json, out SerializableConfigList? configList,
        out string? errorMessage)
    {
        configList = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            errorMessage = "The selected file was empty.";
            return false;
        }

        try
        {
            configList = JsonSerializer.Deserialize<SerializableConfigList>(json, DeserializationOptions);
        }
        catch (JsonException ex)
        {
            errorMessage = ex.Message;
            return false;
        }

        if (configList is null)
        {
            errorMessage = "The selected file was empty.";
            return false;
        }

        return true;
    }

    public static bool TryExtractModlistJson(string pdfText, out string? json, out string? errorMessage)
    {
        if (!TryExtractSection(
                pdfText,
                ModlistDelimiter,
                true,
                "The PDF did not contain a modlist section.",
                "The PDF did not contain the end of the modlist section.",
                "The PDF did not contain any modlist data.",
                out var normalized,
                out errorMessage))
        {
            json = null;
            return false;
        }

        return TryConvertPayloadToJson(normalized!, out json, out errorMessage,
            "The PDF modlist data was not in a recognized format.");
    }

    public static bool TryExtractModlistJsonFromMetadata(string? metadataValue, out string? json,
        out string? errorMessage)
    {
        json = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(metadataValue)) return false;

        var trimmed = metadataValue.Trim();
        if (!trimmed.StartsWith(ModlistMetadataPrefix, StringComparison.Ordinal)) return false;

        var payload = trimmed[ModlistMetadataPrefix.Length..];
        if (string.IsNullOrWhiteSpace(payload))
        {
            errorMessage = "The PDF modlist metadata did not contain any modlist data.";
            return false;
        }

        var normalizedPayload = NormalizePayload(payload);
        if (string.IsNullOrWhiteSpace(normalizedPayload))
        {
            errorMessage = "The PDF modlist metadata did not contain any modlist data.";
            return false;
        }

        return TryConvertPayloadToJson(normalizedPayload, out json, out errorMessage,
            "The PDF modlist metadata was not in a recognized format.");
    }

    public static bool TryExtractConfigJson(string pdfText, out string? json, out string? errorMessage)
    {
        if (!TryExtractSection(
                pdfText,
                ConfigDelimiter,
                false,
                "The PDF did not contain a configuration section.",
                "The PDF did not contain the end of the configuration section.",
                "The PDF did not contain any configuration data.",
                out var normalized,
                out errorMessage))
        {
            json = null;
            return false;
        }

        if (normalized is null)
        {
            json = null;
            errorMessage = null;
            return true;
        }

        return TryConvertPayloadToJson(normalized, out json, out errorMessage,
            "The PDF configuration data was not in a recognized format.");
    }

    public static bool TryExtractConfigJsonFromMetadata(string? metadataValue, out string? json,
        out string? errorMessage)
    {
        json = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(metadataValue)) return false;

        var trimmed = metadataValue.Trim();
        if (!trimmed.StartsWith(ConfigMetadataPrefix, StringComparison.Ordinal)) return false;

        var payload = trimmed[ConfigMetadataPrefix.Length..];
        if (string.Equals(payload, ConfigMetadataMissingValue, StringComparison.Ordinal)) return true;

        if (string.IsNullOrWhiteSpace(payload))
        {
            errorMessage = "The PDF configuration metadata did not contain any configuration data.";
            return false;
        }

        var normalizedPayload = NormalizePayload(payload);
        if (string.IsNullOrWhiteSpace(normalizedPayload))
        {
            errorMessage = "The PDF configuration metadata did not contain any configuration data.";
            return false;
        }

        return TryConvertPayloadToJson(normalizedPayload, out json, out errorMessage,
            "The PDF configuration metadata was not in a recognized format.");
    }

    public static string NormalizePayload(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;

        var sanitized = content
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("\0", string.Empty)
            .Replace("\u00A0", " ");

        var builder = new StringBuilder();
        var lines = sanitized.Split('\n');

        foreach (var line in lines)
        {
            if (builder.Length > 0) builder.Append('\n');

            builder.Append(line.TrimEnd());
        }

        return builder.ToString().Trim();
    }

    private static bool TryExtractSection(
        string pdfText,
        string delimiter,
        bool required,
        string missingSectionMessage,
        string missingEndMessage,
        string missingDataMessage,
        out string? normalized,
        out string? errorMessage)
    {
        normalized = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(pdfText))
        {
            if (required)
            {
                errorMessage = missingDataMessage;
                return false;
            }

            return true;
        }

        var startIndex = pdfText.IndexOf(delimiter, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            if (required)
            {
                errorMessage = missingSectionMessage;
                return false;
            }

            return true;
        }

        startIndex += delimiter.Length;
        var endIndex = pdfText.IndexOf(delimiter, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            errorMessage = missingEndMessage;
            return false;
        }

        if (endIndex <= startIndex)
        {
            errorMessage = missingDataMessage;
            return false;
        }

        var rawContent = pdfText.Substring(startIndex, endIndex - startIndex);
        var normalizedContent = NormalizePayload(rawContent);

        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            errorMessage = missingDataMessage;
            return false;
        }

        normalized = normalizedContent;
        return true;
    }

    private static bool TryConvertPayloadToJson(
        string normalized,
        out string? json,
        out string? errorMessage,
        string formatErrorMessage)
    {
        json = null;
        errorMessage = null;

        if (IsProbableJson(normalized))
        {
            json = normalized;
            return true;
        }

        if (TryDecodeBase64(normalized, out var decoded))
        {
            json = decoded;
            return true;
        }

        errorMessage = formatErrorMessage;
        return false;
    }

    private static bool IsProbableJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        foreach (var ch in content)
        {
            if (char.IsWhiteSpace(ch)) continue;

            return ch == '{' || ch == '[';
        }

        return false;
    }

    private static bool TryDecodeBase64(string content, out string? decoded)
    {
        decoded = null;

        if (string.IsNullOrWhiteSpace(content)) return false;

        var compact = RemoveWhitespace(content);
        if (compact.Length == 0 || compact.Length % 4 != 0) return false;

        Span<byte> buffer = new byte[compact.Length];
        if (!Convert.TryFromBase64String(compact, buffer, out var bytesWritten)) return false;

        decoded = Encoding.UTF8.GetString(buffer[..bytesWritten]);
        return !string.IsNullOrWhiteSpace(decoded);
    }

    private static string RemoveWhitespace(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;

        var builder = new StringBuilder(content.Length);
        foreach (var ch in content)
            if (!char.IsWhiteSpace(ch))
                builder.Append(ch);

        return builder.ToString();
    }
}