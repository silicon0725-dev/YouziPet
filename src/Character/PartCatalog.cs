using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace YuzuPet.Character;

/// <summary>A single drawable layer cut from the original PSD, tied to one PNG.</summary>
public class SpriteDef
{
    public string Id = "";
    public string ItemId = "";
    public int Order;
    public float X, Y, W, H;
    public string Bone = "";
    public string Src = "";
}

/// <summary>A wardrobe / face item the user can equip in one category slot.</summary>
public class ItemDef
{
    public string Id = "";
    public string Label = "";
    public string Category = "";
    public bool Skin;
    public List<string> Sprites = new();
}

public class GroupDef
{
    public string Key = "";
    public List<string> Ids = new();
}

public class PaletteEntry
{
    public string Name = "";
    public Color Color = Colors.White; // "" original color -> white tint (no-op)
    public bool IsOriginal;
}

public class PresetDef
{
    public string Name = "";
    public string Hint = "";
    public System.Collections.Generic.Dictionary<string, string> Selected = new();
    public System.Collections.Generic.Dictionary<string, string> Colors = new();
}

public class MotionDef
{
    public string Id = "";
    public string Name = "";
}

/// <summary>
/// The full character parts catalog exported from the original app
/// (data/character_catalog.json): groups, items, sprite layers, palettes,
/// presets and motions.
/// </summary>
public static class PartCatalog
{
    public const float CanvasW = 1525f;
    public const float CanvasH = 2048f;

    /// <summary>Base layers always shown (body / face / hands / legs).</summary>
    public static readonly List<string> SkinIds = new();

    public static readonly List<GroupDef> Groups = new();
    public static readonly System.Collections.Generic.Dictionary<string, ItemDef> Items = new();
    /// <summary>Catalog insertion order — the stable tie-break for layers
    /// that share the same PSD order (the original's stable sort relies on
    /// this ordering; without it stacking flips with equip order).</summary>
    public static readonly System.Collections.Generic.Dictionary<string, int> ItemIndex = new();
    public static readonly System.Collections.Generic.Dictionary<string, SpriteDef> Sprites = new();
    public static readonly List<PaletteEntry> Palettes = new();
    public static readonly List<PaletteEntry> SkinPalettes = new();
    public static readonly List<PresetDef> Presets = new();
    public static readonly List<MotionDef> Motions = new();

    /// <summary>Chinese labels for category keys (UI order matters).</summary>
    public static readonly string[] UiCategories =
    {
        "front", "back", "ponytail", "sideHair", "tuft", "streak", "hairHighlight",
        "headwear", "hairAcc", "ears", "humanEars", "eyes", "pupil", "eyeWhite",
        "eyeHighlight", "brows", "mouth", "glasses", "faceAcc",
        "dress", "top", "sleeves", "bottom", "shoes", "gloves",
        "topAcc", "legAcc", "tail", "wings",
    };

    private static readonly System.Collections.Generic.Dictionary<string, string> CategoryNames = new()
    {
        { "front", "前发" }, { "back", "后发" }, { "ponytail", "马尾" },
        { "sideHair", "鬓发" }, { "tuft", "呆毛" }, { "streak", "挑染" },
        { "hairHighlight", "头发高光" }, { "headwear", "头饰" },
        { "hairAcc", "发饰" }, { "ears", "兽耳" }, { "humanEars", "人耳" },
        { "eyes", "眼睛" }, { "pupil", "瞳孔" }, { "eyeWhite", "眼白" },
        { "eyeHighlight", "眼中高光" }, { "brows", "眉毛" }, { "mouth", "嘴巴" },
        { "glasses", "眼镜" }, { "faceAcc", "面部配饰" },
        { "dress", "连衣裙" }, { "top", "上衣" }, { "sleeves", "袖子" },
        { "bottom", "下装" }, { "shoes", "鞋子" }, { "gloves", "手套" },
        { "topAcc", "上装配饰" }, { "legAcc", "腿部配饰" },
        { "tail", "尾巴" }, { "wings", "翅膀" }, { "skin", "皮肤基础层" },
    };

    public static string CategoryName(string key) =>
        CategoryNames.GetValueOrDefault(key, key);

    private static Color ParseHex(string hex)
    {
        return string.IsNullOrEmpty(hex) ? Colors.White : Color.FromHtml(hex);
    }

    /// <summary>Load the catalog from a res:// json path. Safe to call once at boot.</summary>
    public static void Load(string resPath)
    {
        using var f = FileAccess.Open(resPath, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PushError($"catalog missing: {resPath} ({FileAccess.GetOpenError()})");
            return;
        }
        var root = Json.ParseString(f.GetAsText()).AsGodotDictionary();

        foreach (var g in root["groups"].AsGodotArray())
        {
            var gd = g.AsGodotDictionary();
            var def = new GroupDef { Key = gd["key"].AsString() };
            foreach (var id in gd["ids"].AsGodotArray())
                def.Ids.Add(id.AsString());
            Groups.Add(def);
        }

        foreach (var it in root["items"].AsGodotArray())
        {
            var d = it.AsGodotDictionary();
            var def = new ItemDef
            {
                Id = d["id"].AsString(),
                Label = d.ContainsKey("label") ? d["label"].AsString() : d["id"].AsString(),
                Category = d["category"].AsString(),
                Skin = d.ContainsKey("skin") && d["skin"].AsBool(),
            };
            if (d.ContainsKey("sprites"))
                foreach (var s in d["sprites"].AsGodotArray())
                    def.Sprites.Add(s.AsString());
            Items[def.Id] = def;
            ItemIndex[def.Id] = Items.Count;
        }

        foreach (var sp in root["sprites"].AsGodotArray())
        {
            var d = sp.AsGodotDictionary();
            var def = new SpriteDef
            {
                Id = d["id"].AsString(),
                ItemId = d.ContainsKey("itemId") ? d["itemId"].AsString() : d["id"].AsString(),
                Order = d.ContainsKey("order") ? d["order"].AsInt32() : 0,
                X = d["x"].AsSingle(),
                Y = d["y"].AsSingle(),
                W = d["w"].AsSingle(),
                H = d["h"].AsSingle(),
                Bone = d.ContainsKey("bone") ? d["bone"].AsString() : "",
                Src = d.ContainsKey("src") ? d["src"].AsString() : "",
            };
            Sprites[def.Id] = def;
        }

        foreach (var p in root["palettes"].AsGodotArray())
        {
            var d = p.AsGodotDictionary();
            Palettes.Add(new PaletteEntry
            {
                Name = d["name"].AsString(),
                Color = ParseHex(d["color"].AsString()),
                IsOriginal = string.IsNullOrEmpty(d["color"].AsString()),
            });
        }

        foreach (var p in root["skinPalettes"].AsGodotArray())
        {
            var d = p.AsGodotDictionary();
            SkinPalettes.Add(new PaletteEntry
            {
                Name = d["name"].AsString(),
                Color = ParseHex(d["color"].AsString()),
                IsOriginal = string.IsNullOrEmpty(d["color"].AsString()),
            });
        }

        foreach (var pr in root["presets"].AsGodotArray())
        {
            var d = pr.AsGodotDictionary();
            var def = new PresetDef
            {
                Name = d["name"].AsString(),
                Hint = d.ContainsKey("hint") ? d["hint"].AsString() : "",
            };
            var sel = d["selected"].AsGodotDictionary();
            foreach (var k in sel.Keys)
                def.Selected[k.AsString()] = sel[k].AsString();
            var col = d["colors"].AsGodotDictionary();
            foreach (var k in col.Keys)
                def.Colors[k.AsString()] = col[k].AsString();
            Presets.Add(def);
        }

        foreach (var m in root["motions"].AsGodotArray())
        {
            var d = m.AsGodotDictionary();
            Motions.Add(new MotionDef { Id = d["id"].AsString(), Name = d["name"].AsString() });
        }

        foreach (var sid in root["skinIds"].AsGodotArray())
            SkinIds.Add(sid.AsString());

        GD.Print($"catalog: {Groups.Count} groups, {Items.Count} items, " +
                 $"{Sprites.Count} sprites, {Presets.Count} presets, {Motions.Count} motions");
    }
}
