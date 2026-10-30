using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace YuzuPet.Character;

/// <summary>
/// The equipped look: one item per category plus per-category tint colors.
/// Serialized shape matches the original app's `cotton-closet-v22` save
/// ({"selected":{...},"colors":{...}}) so saves can be exchanged both ways.
/// The chosen skin tone is stored under colors["skin"].
/// </summary>
public class Appearance
{
    public System.Collections.Generic.Dictionary<string, string> Selected = new();
    public System.Collections.Generic.Dictionary<string, string> Colors = new();

    public const string SavePath = "user://appearance.json";

    public static Appearance Default()
    {
        var a = new Appearance();
        if (PartCatalog.Presets.Count > 0)
        {
            foreach (var (k, v) in PartCatalog.Presets[0].Selected)
                a.Selected[k] = v;
            foreach (var (k, v) in PartCatalog.Presets[0].Colors)
                a.Colors[k] = NormalizeColor(v);
        }
        return a;
    }

    /// <summary>
    /// The original app stores palette indexes (0=original, 1..N into
    /// palettes); normalize any of {index, "#rrggbb", ""} to a hex string
    /// ("" meaning original colors).
    /// </summary>
    private static string NormalizeColor(string value)
    {
        value ??= "";
        value = value.Trim();
        if (value.StartsWith("#"))
            return value;
        if (int.TryParse(value, out int idx) && idx >= 0 && idx < PartCatalog.Palettes.Count)
        {
            var p = PartCatalog.Palettes[idx];
            return p.IsOriginal ? "" : p.Color.ToHtml(false);
        }
        return "";
    }

    public static Appearance Load()
    {
        if (!FileAccess.FileExists(SavePath))
            return Default();
        using var f = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
        if (f == null)
            return Default();
        var root = Json.ParseString(f.GetAsText()).AsGodotDictionary();
        var a = new Appearance();
        if (root.ContainsKey("selected"))
            foreach (var k in root["selected"].AsGodotDictionary().Keys)
                a.Selected[k.AsString()] = root["selected"].AsGodotDictionary()[k].AsString();
        if (root.ContainsKey("colors"))
            foreach (var k in root["colors"].AsGodotDictionary().Keys)
                a.Colors[k.AsString()] = NormalizeColor(root["colors"].AsGodotDictionary()[k].AsString());
        return a;
    }

    public void Save()
    {
        var selected = new Godot.Collections.Dictionary();
        foreach (var (k, v) in Selected)
            selected[k] = v;
        var colors = new Godot.Collections.Dictionary();
        foreach (var (k, v) in Colors)
            colors[k] = v;
        var root = new Godot.Collections.Dictionary
        {
            { "selected", selected },
            { "colors", colors },
        };
        using var f = FileAccess.Open(SavePath, FileAccess.ModeFlags.Write);
        f?.StoreString(Json.Stringify(root, "\t"));
    }

    /// <summary>Tint hex for a category; "" means original color.</summary>
    public string ColorOf(string category) => Colors.GetValueOrDefault(category, "");

    public Color TintOf(string category)
    {
        var hex = ColorOf(category);
        return string.IsNullOrEmpty(hex) ? Godot.Colors.White : Color.FromHtml(hex);
    }
}
