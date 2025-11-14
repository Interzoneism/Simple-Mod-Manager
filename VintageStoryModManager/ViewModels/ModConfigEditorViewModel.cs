using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using YamlDotNet.Serialization;

namespace VintageStoryModManager.ViewModels;

public sealed class ModConfigEditorViewModel : ObservableObject
{
    private string _filePath;
    private ModConfigFormat _format;
    private JsonNode? _rootNode;

    public ModConfigEditorViewModel(string modDisplayName, string filePath)
    {
        if (string.IsNullOrWhiteSpace(modDisplayName))
            throw new ArgumentException("Mod name is required.", nameof(modDisplayName));

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Configuration file path is required.", nameof(filePath));

        ModDisplayName = modDisplayName;
        _filePath = NormalizePath(filePath);
        WindowTitle = $"Edit Config - {ModDisplayName}";

        LoadConfiguration();
    }

    public string ModDisplayName { get; }

    public string WindowTitle { get; }

    public string FilePath
    {
        get => _filePath;
        private set
        {
            if (string.Equals(_filePath, value, StringComparison.OrdinalIgnoreCase)) return;

            _filePath = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<ModConfigNodeViewModel> RootNodes { get; private set; } = new();

    public void Save()
    {
        foreach (var node in RootNodes) node.ApplyChanges();

        var content = _format switch
        {
            ModConfigFormat.Json => SerializeJson(_rootNode),
            ModConfigFormat.Yaml => SerializeYaml(_rootNode),
            _ => throw new InvalidOperationException("Unsupported configuration format.")
        };

        File.WriteAllText(_filePath, content);
    }

    public void ReplaceConfigurationFile(string filePath)
    {
        var normalizedPath = NormalizePath(filePath);
        _format = DetermineFormat(normalizedPath);

        var node = _format switch
        {
            ModConfigFormat.Json => LoadJsonNode(normalizedPath),
            ModConfigFormat.Yaml => LoadYamlNode(normalizedPath),
            _ => null
        };

        _rootNode = node ?? new JsonObject();
        RootNodes = new ObservableCollection<ModConfigNodeViewModel>(CreateRootNodes(_rootNode));
        FilePath = normalizedPath;
        OnPropertyChanged(nameof(RootNodes));
    }

    private void LoadConfiguration()
    {
        ReplaceConfigurationFile(_filePath);
    }

    private static ModConfigFormat DetermineFormat(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
            return ModConfigFormat.Yaml;

        return ModConfigFormat.Json;
    }

    private static JsonNode? LoadJsonNode(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonNode.Parse(stream);
    }

    private static JsonNode? LoadYamlNode(string path)
    {
        var deserializer = new DeserializerBuilder().Build();
        using var reader = new StreamReader(path);
        var yamlObject = deserializer.Deserialize(reader);
        return ConvertYamlToJsonNode(yamlObject);
    }

    private static JsonNode? ConvertYamlToJsonNode(object? value)
    {
        if (value is null) return null;

        if (value is IDictionary dictionary)
        {
            var obj = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? string.Empty;
                obj[key] = ConvertYamlToJsonNode(entry.Value);
            }

            return obj;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var array = new JsonArray();
            foreach (var item in enumerable) array.Add(ConvertYamlToJsonNode(item));

            return array;
        }

        return ConvertToJsonValue(value);
    }

    private static JsonNode ConvertToJsonValue(object value)
    {
        return value switch
        {
            bool boolValue => JsonValue.Create(boolValue)!,
            string stringValue => JsonValue.Create(stringValue)!,
            sbyte sbyteValue => JsonValue.Create(sbyteValue)!,
            byte byteValue => JsonValue.Create(byteValue)!,
            short shortValue => JsonValue.Create(shortValue)!,
            ushort ushortValue => JsonValue.Create(ushortValue)!,
            int intValue => JsonValue.Create(intValue)!,
            uint uintValue => JsonValue.Create(uintValue)!,
            long longValue => JsonValue.Create(longValue)!,
            ulong ulongValue => JsonValue.Create(ulongValue)!,
            float floatValue => JsonValue.Create(floatValue)!,
            double doubleValue => JsonValue.Create(doubleValue)!,
            decimal decimalValue => JsonValue.Create(decimalValue)!,
            _ => JsonValue.Create(value.ToString() ?? string.Empty)!
        };
    }

    private static string SerializeJson(JsonNode? node)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        return node is null ? "null" : node.ToJsonString(options);
    }

    private static string SerializeYaml(JsonNode? node)
    {
        var yamlObject = ConvertJsonNodeToYamlObject(node);
        var serializer = new SerializerBuilder().Build();
        using var writer = new StringWriter();
        serializer.Serialize(writer, yamlObject);
        return writer.ToString();
    }

    private static object? ConvertJsonNodeToYamlObject(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var pair in obj) dictionary[pair.Key] = ConvertJsonNodeToYamlObject(pair.Value);

                return dictionary;
            }
            case JsonArray array:
            {
                var list = new List<object?>(array.Count);
                foreach (var item in array) list.Add(ConvertJsonNodeToYamlObject(item));

                return list;
            }
            case JsonValue value:
                return ConvertJsonValueToPrimitive(value);
            default:
                return null;
        }
    }

    private static object? ConvertJsonValueToPrimitive(JsonValue value)
    {
        var json = value.ToJsonString();
        if (string.Equals(json, "null", StringComparison.OrdinalIgnoreCase)) return null;

        if (value.TryGetValue(out bool boolResult)) return boolResult;

        if (value.TryGetValue(out long longResult)) return longResult;

        if (value.TryGetValue(out ulong ulongResult)) return ulongResult;

        if (value.TryGetValue(out decimal decimalResult)) return decimalResult;

        if (value.TryGetValue(out double doubleResult)) return doubleResult;

        if (value.TryGetValue(out string? stringResult)) return stringResult;

        return json;
    }

    private static string NormalizePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Configuration file path is required.", nameof(filePath));

        return Path.GetFullPath(filePath);
    }

    private IEnumerable<ModConfigNodeViewModel> CreateRootNodes(JsonNode node)
    {
        if (node is JsonObject obj)
            return obj
                .Select(pair => CreateNode(pair.Key, pair.Value, value => obj[pair.Key] = value))
                .ToList();

        if (node is JsonArray array)
            return new[]
            {
                CreateArrayNode("(root)", array, "Items")
            };

        return new[]
        {
            CreateValueNode("(root)", node, value => _rootNode = value, string.Empty)
        };
    }

    private ModConfigNodeViewModel CreateNode(string name, JsonNode? node, Action<JsonNode?> setter,
        string? displayName = null)
    {
        if (node is JsonObject obj)
        {
            var children = new ObservableCollection<ModConfigNodeViewModel>(
                obj.Select(pair => CreateNode(pair.Key, pair.Value, value => obj[pair.Key] = value)));
            return new ModConfigObjectNodeViewModel(name, children, displayName);
        }

        if (node is JsonArray array) return CreateArrayNode(name, array, displayName);

        return CreateValueNode(name, node, setter, displayName);
    }

    private ModConfigNodeViewModel CreateArrayNode(string name, JsonArray array, string? displayName = null)
    {
        var children = new ObservableCollection<ModConfigNodeViewModel>();
        for (var i = 0; i < array.Count; i++)
        {
            var index = i;
            var childNode = array[index];
            var childDisplayName = childNode switch
            {
                JsonObject => $"Item {i + 1}",
                JsonArray => $"Item {i + 1}",
                _ => string.Empty
            };
            children.Add(CreateNode($"[{i}]", childNode, value => array[index] = value, childDisplayName));
        }

        return new ModConfigArrayNodeViewModel(name, children, () => array.Count, displayName);
    }

    private ModConfigNodeViewModel CreateValueNode(string name, JsonNode? node, Action<JsonNode?> setter,
        string? displayName)
    {
        return new ModConfigValueNodeViewModel(name, node, setter, displayName);
    }

    private enum ModConfigFormat
    {
        Json,
        Yaml
    }
}