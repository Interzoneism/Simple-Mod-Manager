using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VintageStoryModManager.ViewModels;

public abstract class ModConfigNodeViewModel : ObservableObject
{
    protected ModConfigNodeViewModel(string name, string typeDisplay, string? displayName = null)
    {
        Name = name;
        TypeDisplay = typeDisplay;
        DisplayName = displayName ?? name;
    }

    public string Name { get; }

    public string DisplayName { get; }

    public bool HasDisplayName => !string.IsNullOrWhiteSpace(DisplayName);

    public string DisplayLabel => HasDisplayName ? DisplayName : Name;

    public string TypeDisplay { get; }

    public virtual ObservableCollection<ModConfigNodeViewModel>? Children => null;

    public virtual bool IsEditable => false;

    public abstract void ApplyChanges();
}

public abstract class ModConfigContainerNodeViewModel : ModConfigNodeViewModel
{
    private readonly ObservableCollection<ModConfigNodeViewModel> _children;

    protected ModConfigContainerNodeViewModel(
        string name,
        string typeDisplay,
        ObservableCollection<ModConfigNodeViewModel> children,
        string? displayName = null)
        : base(name, typeDisplay, displayName)
    {
        _children = children;
    }

    public override ObservableCollection<ModConfigNodeViewModel>? Children => _children;

    public override void ApplyChanges()
    {
        foreach (var child in _children) child.ApplyChanges();
    }
}

public sealed class ModConfigObjectNodeViewModel : ModConfigContainerNodeViewModel
{
    public ModConfigObjectNodeViewModel(string name, ObservableCollection<ModConfigNodeViewModel> children,
        string? displayName = null)
        : base(name, "Object", children, displayName)
    {
    }
}

public sealed class ModConfigArrayNodeViewModel : ModConfigContainerNodeViewModel
{
    private readonly Func<int> _countAccessor;

    public ModConfigArrayNodeViewModel(
        string name,
        ObservableCollection<ModConfigNodeViewModel> children,
        Func<int> countAccessor,
        string? displayName = null)
        : base(name, "Array", children, displayName)
    {
        _countAccessor = countAccessor;
    }

    public int ItemCount => _countAccessor();
}

public sealed class ModConfigValueNodeViewModel : ModConfigNodeViewModel
{
    private readonly JsonValueKind _valueKind;
    private readonly Action<JsonNode?> _valueSetter;
    private string _valueText;

    public ModConfigValueNodeViewModel(string name, JsonNode? node, Action<JsonNode?> valueSetter,
        string? displayName = null)
        : base(name, DetermineTypeDisplay(node), displayName)
    {
        _valueSetter = valueSetter ?? throw new ArgumentNullException(nameof(valueSetter));
        _valueKind = node?.GetValueKind() ?? JsonValueKind.Null;
        _valueText = ConvertNodeToText(node, _valueKind);
    }

    public override bool IsEditable => true;

    public string ValueText
    {
        get => _valueText;
        set
        {
            if (SetProperty(ref _valueText, value))
                if (IsBoolean)
                    OnPropertyChanged(nameof(BooleanValue));
        }
    }

    public bool IsBoolean => _valueKind is JsonValueKind.True or JsonValueKind.False;

    public bool BooleanValue
    {
        get => string.Equals(_valueText, bool.TrueString, StringComparison.OrdinalIgnoreCase);
        set
        {
            var newValue = value ? bool.TrueString : bool.FalseString;
            if (SetProperty(ref _valueText, newValue)) OnPropertyChanged(nameof(ValueText));
        }
    }

    public override void ApplyChanges()
    {
        var value = ConvertTextToNode(ValueText, _valueKind, DisplayLabel);
        _valueSetter(value);
    }

    private static string DetermineTypeDisplay(JsonNode? node)
    {
        var kind = node?.GetValueKind() ?? JsonValueKind.Null;
        return kind switch
        {
            JsonValueKind.String => "String",
            JsonValueKind.Number => "Number",
            JsonValueKind.True or JsonValueKind.False => "Boolean",
            JsonValueKind.Null => "Null",
            JsonValueKind.Object => "Object",
            JsonValueKind.Array => "Array",
            _ => kind.ToString()
        };
    }

    private static string ConvertNodeToText(JsonNode? node, JsonValueKind kind)
    {
        if (node is null) return string.Empty;

        return kind switch
        {
            JsonValueKind.String => node.GetValue<string>() ?? string.Empty,
            JsonValueKind.Number => node.ToJsonString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Null => string.Empty,
            _ => node.ToJsonString()
        };
    }

    private static JsonNode? ConvertTextToNode(string? text, JsonValueKind kind, string propertyName)
    {
        switch (kind)
        {
            case JsonValueKind.String:
                return JsonValue.Create(text ?? string.Empty);
            case JsonValueKind.True:
            case JsonValueKind.False:
            {
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException($"Value for '{propertyName}' cannot be empty.");

                if (!bool.TryParse(text, out var boolValue))
                    throw new InvalidOperationException($"Value for '{propertyName}' must be a boolean (true/false).");

                return JsonValue.Create(boolValue);
            }
            case JsonValueKind.Number:
            {
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException($"Value for '{propertyName}' cannot be empty.");

                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                    return JsonValue.Create(longValue);

                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
                    return JsonValue.Create(decimalValue);

                throw new InvalidOperationException($"Value for '{propertyName}' must be a number.");
            }
            case JsonValueKind.Null:
            {
                if (string.IsNullOrWhiteSpace(text)) return null;

                if (string.Equals(text.Trim(), "null", StringComparison.OrdinalIgnoreCase)) return null;

                return JsonValue.Create(text);
            }
            default:
                return JsonValue.Create(text);
        }
    }
}