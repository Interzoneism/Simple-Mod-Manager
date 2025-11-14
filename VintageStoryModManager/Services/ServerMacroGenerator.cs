using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VintageStoryModManager.Services;

/// <summary>
///     Generates server macro files that can be imported by Vintage Story servers.
/// </summary>
public static class ServerMacroGenerator
{
    private const string DefaultPrivilege = "controlserver";

    /// <summary>
    ///     Creates a default macro name using the current UTC timestamp.
    /// </summary>
    public static string CreateDefaultMacroName()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return $"svsminstall{timestamp}";
    }

    /// <summary>
    ///     Generates a macro file that installs every mod in <paramref name="mods" /> using
    ///     <c>/moddb install</c> commands.
    /// </summary>
    /// <param name="filePath">Destination path for the generated JSON file.</param>
    /// <param name="macroName">Name of the macro that will be created.</param>
    /// <param name="mods">Collection of mod identifiers and versions to install.</param>
    /// <param name="description">Optional description to associate with the macro.</param>
    /// <param name="privilege">Privilege required to run the macro. Defaults to <c>controlserver</c>.</param>
    /// <returns>Details about the generated macro.</returns>
    public static ServerMacroGenerationResult CreateInstallMacro(
        string filePath,
        string macroName,
        IEnumerable<(string ModId, string Version)> mods,
        string? description = null,
        string? privilege = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(mods);

        var normalizedMacroName = NormalizeMacroName(
            string.IsNullOrWhiteSpace(macroName) ? CreateDefaultMacroName() : macroName);

        var commands = new List<string>();
        foreach (var mod in mods)
        {
            var command = ServerCommandBuilder.TryBuildInstallCommand(mod.ModId, mod.Version);
            if (!string.IsNullOrWhiteSpace(command)) commands.Add(command);
        }

        if (commands.Count == 0) return ServerMacroGenerationResult.Empty;

        var macro = new ServerMacroExport
        {
            Name = normalizedMacroName,
            Privilege = string.IsNullOrWhiteSpace(privilege) ? DefaultPrivilege : privilege.Trim(),
            Commands = string.Join('\n', commands),
            Description = description,
            CreatedByPlayerUid = string.Empty,
            Syntax = string.Empty
        };

        var json = JsonSerializer.Serialize(new[] { macro }, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(filePath, json, Encoding.UTF8);

        return new ServerMacroGenerationResult(normalizedMacroName, commands.Count);
    }

    private static string NormalizeMacroName(string macroName)
    {
        ArgumentNullException.ThrowIfNull(macroName);

        var builder = new StringBuilder(macroName.Length);
        foreach (var c in macroName)
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                builder.Append(char.ToLowerInvariant(c));

        if (builder.Length == 0) return CreateDefaultMacroName();

        if (!char.IsLetter(builder[0])) builder.Insert(0, 'm');

        return builder.ToString();
    }

    private sealed class ServerMacroExport
    {
        public string Privilege { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Commands { get; set; } = string.Empty;

        public string CreatedByPlayerUid { get; set; } = string.Empty;

        public string Syntax { get; set; } = string.Empty;

        public string? Description { get; set; }
    }

    public readonly struct ServerMacroGenerationResult
    {
        public static readonly ServerMacroGenerationResult Empty = new(string.Empty, 0);

        public ServerMacroGenerationResult(string macroName, int commandCount)
        {
            MacroName = macroName ?? string.Empty;
            CommandCount = commandCount < 0 ? 0 : commandCount;
        }

        public string MacroName { get; }

        public int CommandCount { get; }

        public string Command => string.IsNullOrEmpty(MacroName) ? string.Empty : "/" + MacroName;

        public bool HasMacro => CommandCount > 0 && !string.IsNullOrEmpty(MacroName);
    }
}