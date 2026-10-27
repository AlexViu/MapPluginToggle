using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Plugin;
using CounterStrikeSharp.API.Core.Plugin.Host;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CS2MenuManager.API.Enum;
using CS2MenuManager.API.Menu;
using Microsoft.Extensions.Logging;

namespace MapPluginToggle;

public class MapPluginToggleConfig : BasePluginConfig
{
    [JsonPropertyName("Permission")] public string Permission { get; set; } = "@css/root";
    [JsonPropertyName("ChatPrefix")] public string ChatPrefix { get; set; } = " \x04[MapPlugins]\x01";
}

/// <summary>
/// maps.json: map name (or pattern with '*') -> plugin folder names that must be unloaded on that map.
/// </summary>
public class MapRules
{
    [JsonPropertyName("Maps")]
    public Dictionary<string, List<string>> Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class MapPluginToggle : BasePlugin, IPluginConfig<MapPluginToggleConfig>
{
    public override string ModuleName => "MapPluginToggle";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "RandomPicks";
    public override string ModuleDescription => "Disable other plugins per map (css_mapplugins)";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MapPluginToggleConfig Config { get; set; } = new();

    private IPluginManager? _pluginManager;
    private MapRules _rules = new();
    private string _rulesPath = "";

    public void OnConfigParsed(MapPluginToggleConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        // The core does not expose IPluginManager to plugins; Application holds it in a private field.
        _pluginManager = typeof(Application)
            .GetField("_pluginManager", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(Application.Instance) as IPluginManager;
        if (_pluginManager == null)
            Logger.LogError("Could not access the CounterStrikeSharp plugin manager; this plugin will do nothing");

        _rulesPath = Path.Combine(Application.RootDirectory, "configs", "plugins", ModuleName, "maps.json");
        LoadRules();

        RegisterListener<Listeners.OnMapStart>(_ => Server.NextWorldUpdate(ApplyForCurrentMap));

        // Mid-map (hot reload or late load): bring the server in line right away.
        if (hotReload)
            Server.NextWorldUpdate(ApplyForCurrentMap);
    }

    // ---------------------------------------------------------------- commands

    [ConsoleCommand("css_mapplugins", "List/toggle plugins for the current map")]
    [CommandHelper(usage: "[list | toggle <plugin|#n> | reload]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnMapPluginsCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player != null && !AdminManager.PlayerHasPermissions(player, Config.Permission))
        {
            info.ReplyToCommand($"{Config.ChatPrefix} No tienes permiso.");
            return;
        }
        if (_pluginManager == null)
        {
            info.ReplyToCommand($"{Config.ChatPrefix} Plugin manager no disponible (ver logs).");
            return;
        }

        var sub = info.ArgCount > 1 ? info.GetArg(1).ToLowerInvariant() : "";
        switch (sub)
        {
            case "":
                if (player != null)
                    OpenMenu(player);
                else
                    PrintList(info);
                break;
            case "list":
                PrintList(info);
                break;
            case "toggle":
                if (info.ArgCount < 3)
                {
                    info.ReplyToCommand($"{Config.ChatPrefix} Uso: css_mapplugins toggle <plugin|#n>");
                    return;
                }
                var target = ResolvePlugin(info.GetArg(2));
                if (target == null)
                {
                    info.ReplyToCommand($"{Config.ChatPrefix} Plugin no encontrado: {info.GetArg(2)}");
                    return;
                }
                Toggle(target.Folder, msg => info.ReplyToCommand(msg));
                break;
            case "reload":
                LoadRules();
                Server.NextWorldUpdate(ApplyForCurrentMap);
                info.ReplyToCommand($"{Config.ChatPrefix} maps.json recargado y aplicado.");
                break;
            default:
                info.ReplyToCommand($"{Config.ChatPrefix} Uso: css_mapplugins [list | toggle <plugin|#n> | reload]");
                break;
        }
    }

    private void OpenMenu(CCSPlayerController player, int selectedIndex = 0)
    {
        var map = Server.MapName;
        var menu = new WasdMenu($"Plugins en {map}", this);
        var plugins = GetManagedPlugins();
        for (var i = 0; i < plugins.Count; i++)
        {
            var p = plugins[i];
            var index = i;
            var item = menu.AddItem($"{p.Folder} [{StatusText(p, map)}]", (pl, _) =>
                Toggle(p.Folder, msg => pl.PrintToChat(msg), reopenFor: pl, reopenIndex: index));
            item.PostSelectAction = PostSelectAction.Close;
        }
        menu.DisplayAt(player, selectedIndex, 0);
    }

    private void PrintList(CommandInfo info)
    {
        var map = Server.MapName;
        info.ReplyToCommand($"{Config.ChatPrefix} Plugins en {map}:");
        var i = 1;
        foreach (var p in GetManagedPlugins())
            info.ReplyToCommand($"  #{i++} {p.Folder} ({p.ModuleName}) [{StatusText(p, map)}]");
    }

    private string StatusText(PluginEntry p, string map)
    {
        var rule = FindDisablingRule(p.Folder, map);
        var loaded = p.Context.State == PluginState.Loaded;
        if (rule != null)
        {
            var by = rule.Equals(map, StringComparison.OrdinalIgnoreCase) ? "" : $" por {rule}";
            return loaded ? $"OFF{by}, aún cargado" : $"OFF{by}";
        }
        return loaded ? "ON" : "ON, no cargado";
    }

    // ---------------------------------------------------------------- toggling

    private void Toggle(string folder, Action<string> reply, CCSPlayerController? reopenFor = null, int reopenIndex = 0)
    {
        var map = Server.MapName;
        var rule = FindDisablingRule(folder, map);

        if (rule != null && !rule.Equals(map, StringComparison.OrdinalIgnoreCase))
        {
            reply($"{Config.ChatPrefix} {folder} está desactivado por el patrón \"{rule}\"; edita maps.json para cambiarlo.");
            return;
        }

        if (rule == null)
        {
            if (!_rules.Maps.TryGetValue(map, out var list))
                _rules.Maps[map] = list = new List<string>();
            list.Add(folder);
        }
        else
        {
            var list = _rules.Maps[rule];
            list.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0)
                _rules.Maps.Remove(rule);
        }
        SaveRules();

        var nowDisabled = rule == null;
        // Never load/unload from inside a command or menu callback: defer to the next game frame.
        Server.NextWorldUpdate(() =>
        {
            var entry = GetManagedPlugins().FirstOrDefault(p => p.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
                SetLoaded(entry, !nowDisabled);
            reply($"{Config.ChatPrefix} {folder}: {(nowDisabled ? "\x02OFF" : "\x04ON")}\x01 en {map}.");
            if (reopenFor is { IsValid: true })
                OpenMenu(reopenFor, reopenIndex);
        });
    }

    private void ApplyForCurrentMap()
    {
        if (_pluginManager == null)
            return;
        var map = Server.MapName;

        // Only plugins named somewhere in maps.json are touched; everything else is left alone.
        var mentioned = new HashSet<string>(_rules.Maps.Values.SelectMany(v => v), StringComparer.OrdinalIgnoreCase);
        foreach (var p in GetManagedPlugins().Where(p => mentioned.Contains(p.Folder)))
            SetLoaded(p, FindDisablingRule(p.Folder, map) == null);
    }

    private void SetLoaded(PluginEntry p, bool shouldBeLoaded)
    {
        var ctx = p.Context;
        try
        {
            if (shouldBeLoaded && ctx.State != PluginState.Loaded)
            {
                Logger.LogInformation("Loading {Plugin} on {Map}", p.Folder, Server.MapName);
                // hotReload: true so the plugin initialises as if it was reloaded mid-map.
                ctx.Load(hotReload: true);
                if (ctx.State == PluginState.Loaded)
                    ctx.Plugin.OnAllPluginsLoaded(hotReload: true);
            }
            else if (!shouldBeLoaded && ctx.State == PluginState.Loaded)
            {
                Logger.LogInformation("Unloading {Plugin} on {Map}", p.Folder, Server.MapName);
                ctx.Unload(hotReload: false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to {Action} {Plugin}", shouldBeLoaded ? "load" : "unload", p.Folder);
        }
    }

    // ---------------------------------------------------------------- helpers

    private record PluginEntry(string Folder, string ModuleName, PluginContext Context);

    private List<PluginEntry> GetManagedPlugins()
    {
        if (_pluginManager == null)
            return new List<PluginEntry>();
        return _pluginManager.GetLoadedPlugins()
            .Where(c => !string.Equals(c.FilePath, ModulePath, StringComparison.Ordinal))
            .Select(c => new PluginEntry(Path.GetFileNameWithoutExtension(c.FilePath), SafeModuleName(c), c))
            .OrderBy(p => p.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string SafeModuleName(PluginContext c)
    {
        try { return c.Plugin?.ModuleName ?? "?"; }
        catch { return "?"; }
    }

    private PluginEntry? ResolvePlugin(string query)
    {
        var plugins = GetManagedPlugins();
        if (query.StartsWith('#') && int.TryParse(query[1..], out var n) && n >= 1 && n <= plugins.Count)
            return plugins[n - 1];
        return plugins.FirstOrDefault(p => p.Folder.Equals(query, StringComparison.OrdinalIgnoreCase))
               ?? plugins.FirstOrDefault(p => p.ModuleName.Equals(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the maps.json key that disables <paramref name="folder"/> on <paramref name="map"/>, exact key first.</summary>
    private string? FindDisablingRule(string folder, string map)
    {
        bool Lists(List<string> l) => l.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));

        if (_rules.Maps.TryGetValue(map, out var exact) && Lists(exact))
            return map;
        foreach (var (key, list) in _rules.Maps)
        {
            if (key.Contains('*') && Lists(list) && WildcardMatch(key, map))
                return key;
        }
        return null;
    }

    private static bool WildcardMatch(string pattern, string value) =>
        Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);

    private void LoadRules()
    {
        try
        {
            if (File.Exists(_rulesPath))
            {
                var parsed = JsonSerializer.Deserialize<MapRules>(File.ReadAllText(_rulesPath));
                _rules = new MapRules
                {
                    Maps = new Dictionary<string, List<string>>(parsed?.Maps ?? new(), StringComparer.OrdinalIgnoreCase)
                };
            }
            else
            {
                _rules = new MapRules();
                SaveRules();
            }
        }
        catch (Exception ex)
        {
            // Keep whatever we had; do not overwrite a file the admin is hand-editing.
            Logger.LogError(ex, "Could not read {Path}", _rulesPath);
        }
    }

    private void SaveRules()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_rulesPath)!);
            var tmp = _rulesPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_rules, JsonOptions));
            File.Move(tmp, _rulesPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not write {Path}", _rulesPath);
        }
    }
}
