using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using YuzuPet.Character;
using YuzuPet.Pet;

namespace YuzuPet.UI;

/// <summary>
/// The face / wardrobe studio: preset row + category nav on the left, the
/// selected category's parts in the middle, and a live character preview
/// rendered in its own viewport on the right. Changes preview on both the
/// studio preview and the desktop pet; 保存 writes appearance.json in the
/// original app's save format.
/// </summary>
public partial class WardrobeWindow : Window
{
    private Appearance _appearance;
    private PetActor _actor;
    private PetBrain _brain;
    private readonly Action _onClose;

    private PetActor _preview;
    private SubViewportContainer _vpContainer;
    private Label _status;
    private Label _categoryTitle;
    private VBoxContainer _partsBox;
    private ScrollContainer _partsScroll;
    private GridContainer _grid;
    private int _gridColumns;

    /// <summary>Middle column width = window minus side panels and margins.</summary>
    private float StudioMiddleWidth =>
        Mathf.Max(200f, GetWindow().Size.X - 545f);

    /// <summary>Conservative usable width for the part grid (calibrated).</summary>
    private float PartsAreaWidth =>
        Mathf.Max(150f, GetWindow().Size.X - 650f);

    /// <summary>Re-flow part buttons: ~150px per column, 1..6 columns.</summary>
    private void ReflowPartGrid(float availableWidth)
    {
        if (_grid == null)
            return;
        int cols = Mathf.Clamp((int)(availableWidth / 152f), 1, 6);
        if (cols != _gridColumns)
        {
            _gridColumns = cols;
            _grid.Columns = cols;
            GD.Print("part grid columns -> ", cols);
        }
    }
    private ItemList _categoryList;
    private string _category = "front";
    private readonly Dictionary<string, List<Button>> _partButtons = new();
    private readonly List<Button> _presetButtons = new();
    private readonly List<Button> _skinButtons = new();
    private readonly Dictionary<string, ImageTexture> _thumbCache = new();

    /// <summary>
    /// A part's thumbnail, ported from the original thumb(id): compose ALL
    /// of the item's sprites into a 200x150 canvas over their union rect,
    /// contain-scaled with a margin and centered (multi-piece items like
    /// paired sleeves or upper/lower legs show every piece).
    /// </summary>
    private ImageTexture ThumbOf(ItemDef item)
    {
        if (_thumbCache.TryGetValue(item.Id, out var cached))
            return cached;

        ImageTexture result = null;
        var layers = new List<(SpriteDef Sp, Image Img)>();
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var sid in item.Sprites)
        {
            if (!PartCatalog.Sprites.TryGetValue(sid, out var sp) || string.IsNullOrEmpty(sp.Src))
                continue;
            var name = sp.Src.Substring(sp.Src.LastIndexOf('/') + 1);
            var tex = ResourceLoader.Load<Texture2D>($"res://assets/parts/{name}");
            if (tex == null)
                continue;
            layers.Add((sp, tex.GetImage()));
            minX = Mathf.Min(minX, sp.X);
            minY = Mathf.Min(minY, sp.Y);
            maxX = Mathf.Max(maxX, sp.X + sp.W);
            maxY = Mathf.Max(maxY, sp.Y + sp.H);
        }

        if (layers.Count > 0)
        {
            float w = maxX - minX;
            float h = maxY - minY;
            float scale = Mathf.Min(178f / w, 128f / h);
            var canvas = Image.Create(200, 150, false, Image.Format.Rgba8);
            int offX = Mathf.RoundToInt((200f - w * scale) * 0.5f);
            int offY = Mathf.RoundToInt((150f - h * scale) * 0.5f);
            foreach (var (sp, img) in layers)
            {
                int dw = Mathf.Max(1, Mathf.RoundToInt(sp.W * scale));
                int dh = Mathf.Max(1, Mathf.RoundToInt(sp.H * scale));
                // GetImage() returns a fresh copy; resize it into the
                // logical slice box (bleed padding folds back in, as upstream)
                img.Resize(dw, dh, Godot.Image.Interpolation.Lanczos);
                canvas.BlitRect(img, new Rect2I(0, 0, dw, dh),
                    new Vector2I(offX + Mathf.RoundToInt((sp.X - minX) * scale),
                                 offY + Mathf.RoundToInt((sp.Y - minY) * scale)));
            }
            result = ImageTexture.CreateFromImage(canvas);
        }

        _thumbCache[item.Id] = result;
        return result;
    }

    // ---- theme: light/dark palettes (session-wide preference) -------------

    private static bool PreferDark; // remembered while the pet runs

    private bool _dark = PreferDark;

    private Godot.Color BgColor => _dark ? new Godot.Color("211c1f") : new Godot.Color("f7edeb");
    private Godot.Color PanelColor => _dark ? new Godot.Color("2b2528") : Colors.White;
    private Godot.Color Accent => new("e58aa0"); // same pink in both modes
    private Godot.Color InkColor => _dark ? new Godot.Color("f2e7eb") : new Godot.Color("3d2f35");
    private Godot.Color DimColor => _dark ? new Godot.Color("b39aa5") : new Godot.Color("a08a90");
    private Godot.Color HoverBg => _dark ? new Godot.Color("3a3135") : new Godot.Color("fbe3e8");
    private Godot.Color RowSelBg => _dark ? new Godot.Color("463841") : new Godot.Color("fbe0e6");
    private Godot.Color BorderColor => _dark ? new Godot.Color("3d3339") : new Godot.Color("f0dcd8");
    private Godot.Color SelectedBg => new("d06e8a"); // deep pink, both modes

    public WardrobeWindow(Appearance appearance, PetActor actor, PetBrain brain, Action onClose)
    {
        _appearance = appearance;
        _actor = actor;
        _brain = brain;
        _onClose = onClose;

        // custom chrome: the native title bar proved unreliable under
        // borderless+always-on-top on Windows, so draw our own instead
        Title = "柚松屋 · 捏脸换装";
        Size = new Vector2I(980, 680);
        MinSize = new Vector2I(860, 580);
        Borderless = true;
        AlwaysOnTop = true;
        Unresizable = false;
        Transparent = false;
    }

    public override void _Ready()
    {
        // global font bump + palette-aware button chrome
        Theme = BuildTheme();

        // center on the primary screen's usable area
        var screen = DisplayServer.ScreenGetUsableRect();
        Position = new Vector2I(
            screen.Position.X + (screen.Size.X - Size.X) / 2,
            screen.Position.Y + (screen.Size.Y - Size.Y) / 2 - 30);

        // self-check hook: force dark palette to verify the theme
        if (System.Environment.GetEnvironmentVariable("YUZUPET_STUDIO_DARK") == "1")
            _dark = true;

        // self-check hook: override the studio size to verify responsive layout
        // (deferred — an early Size set gets reset once the window is shown)
        var wEnv = System.Environment.GetEnvironmentVariable("YUZUPET_STUDIO_W");
        var hEnv = System.Environment.GetEnvironmentVariable("YUZUPET_STUDIO_H");
        if (int.TryParse(wEnv, out var w) && int.TryParse(hEnv, out var h))
            Callable.From(() => Size = new Vector2I(Math.Max(w, MinSize.X), Math.Max(h, MinSize.Y)))
                .CallDeferred();

        BuildAll();

        // rounded corners once the native window exists; re-apply on resize
        // (throttled: live-resize rebuilds churn the window region)
        SetupCornerUpkeep();
    }

    /// <summary>Palette-aware control theme: font size always; dark mode also
    /// re-skins every default Button (normal/hover/pressed) so nothing keeps
    /// the light-mode gray chrome.</summary>
    private Theme BuildTheme()
    {
        var t = new Theme { DefaultFontSize = 17 };
        if (_dark)
        {
            StyleBoxFlat Flat(Godot.Color c) => new()
            {
                BgColor = c,
                CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
                ContentMarginLeft = 10, ContentMarginRight = 10,
            };
            t.SetStylebox("normal", "Button", Flat(new Godot.Color("3a3135")));
            t.SetStylebox("hover", "Button", Flat(new Godot.Color("463a40")));
            t.SetStylebox("pressed", "Button", Flat(new Godot.Color("4a3d44")));
            t.SetColor("font_color", "Button", InkColor);
            t.SetColor("font_hover_color", "Button", Colors.White);
        }
        return t;
    }

    /// <summary>(Re)build the whole interface — also used to re-skin on
    /// theme toggle. Remembers the selected category row.</summary>
    private void BuildAll()
    {
        Theme = BuildTheme(); // fresh theme: no stale overrides between modes
        int selectedRow = -1;
        if (_categoryList != null)
        {
            var sel = _categoryList.GetSelectedItems();
            selectedRow = sel.Length > 0 ? sel[0] : -1;
        }
        foreach (var child in GetChildren())
            if (child is Control)
                ((Node)child).QueueFree();

        var root = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        // a Window does not auto-stretch its child control — anchor it to the
        // full client rect or it stays at minimum size in the top-left corner
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);
        root.AddChild(BuildTitleBar());
        root.AddChild(BuildBody());
        root.AddChild(BuildFooter());

        RebuildCategoryView();
        if (selectedRow >= 0)
        {
            _categoryList.Select(selectedRow);
            HighlightCategoryRow(selectedRow);
        }
        else
        {
            _categoryList.Select(0);
            HighlightCategoryRow(0);
        }
    }

    private void SetupCornerUpkeep()
    {
        Callable.From(ApplyRoundedCorners).CallDeferred();
        ulong lastCornerApply = 0;
        SizeChanged += () =>
        {
            if (_liveResizing)
                return; // corners come back once, on release
            ulong now = Time.GetTicksMsec();
            if (now - lastCornerApply > 120)
            {
                lastCornerApply = now;
                ApplyRoundedCorners();
            }
        };
    }

    private static partial class Win32
    {
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private static readonly IntPtr HTCAPTION = new(2);

    private IntPtr NativeHandle()
    {
        if (_hwnd == IntPtr.Zero)
            _hwnd = Win32.FindWindowW(null, "柚松屋 · 捏脸换装");
        return _hwnd;
    }

    private IntPtr _hwnd;
    private IntPtr _cornerRgn;
    private bool _liveResizing; // grip drag in progress: region stays off

    /// <summary>Win10 rounded corners via a window region (rebuilt on resize).</summary>
    private void ApplyRoundedCorners()
    {
        var hwnd = NativeHandle();
        if (hwnd == IntPtr.Zero)
            return;
        if (_cornerRgn != IntPtr.Zero)
            Win32.DeleteObject(_cornerRgn);
        _cornerRgn = Win32.CreateRoundRectRgn(0, 0, Size.X + 1, Size.Y + 1, 16, 16);
        // bRedraw=false: a redraw-per-frame during live resizing made the
        // whole window flicker continuously
        Win32.SetWindowRgn(hwnd, _cornerRgn, false);
    }

    /// <summary>Drop the round region while resizing; reapply once released.</summary>
    private void SetCornersDuringResize(bool resizing)
    {
        var hwnd = NativeHandle();
        if (hwnd == IntPtr.Zero)
            return;
        if (resizing)
        {
            // rectangular region = no region churn while dragging the grip
            Win32.SetWindowRgn(hwnd, IntPtr.Zero, false);
        }
        else
        {
            ApplyRoundedCorners();
        }
    }

    /// <summary>Flip the palette and rebuild the interface in place.</summary>
    private void ToggleTheme()
    {
        _dark = !_dark;
        PreferDark = _dark; // remembered for the next studio open
        BuildAll();
        ApplyRoundedCorners(); // the native region survives, just in case
    }

    /// <summary>Custom title bar: drag-to-move + close button.</summary>
    private Control BuildTitleBar()
    {
        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 44) };
        var sb = new StyleBoxFlat
        {
            BgColor = Accent,
            ContentMarginLeft = 14, ContentMarginRight = 8,
            ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        bar.AddThemeStyleboxOverride("panel", sb);

        var row = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", 8);
        var title = new Label
        {
            Text = "柚松屋 · 捏脸换装",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        title.AddThemeFontSizeOverride("font_size", 19);
        title.AddThemeColorOverride("font_color", Colors.White);
        row.AddChild(title);

        var close = new Button
        {
            Text = "✕",
            CustomMinimumSize = new Vector2(38, 32),
            FocusMode = Control.FocusModeEnum.None,
            Flat = true,
        };
        close.AddThemeColorOverride("font_color", Colors.White);
        close.Pressed += DoClose;

        // title-bar actions: theme toggle + save, then close
        var themeBtn = new Button
        {
            Text = _dark ? "日间" : "夜间",
            TooltipText = _dark ? "切换到浅色模式" : "切换到深色模式",
            CustomMinimumSize = new Vector2(72, 32),
            FocusMode = Control.FocusModeEnum.None,
            Flat = true,
        };
        themeBtn.AddThemeColorOverride("font_color", Colors.White);
        themeBtn.Pressed += ToggleTheme;

        var saveBtn = new Button
        {
            Text = "保存并关闭",
            CustomMinimumSize = new Vector2(118, 32),
            FocusMode = Control.FocusModeEnum.None,
            Flat = true,
        };
        saveBtn.AddThemeColorOverride("font_color", Colors.White);
        saveBtn.Pressed += DoClose;

        row.AddChild(themeBtn);
        row.AddChild(saveBtn);
        row.AddChild(close);
        bar.AddChild(row);

        // hand the drag to Windows itself (WM_NCLBUTTONDOWN/HTCAPTION): the
        // OS moves the window natively — buttery smooth, no engine-loop lag
        bar.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton b && b.ButtonIndex == MouseButton.Left && b.Pressed)
            {
                var hwnd = NativeHandle();
                if (hwnd != IntPtr.Zero)
                {
                    Win32.ReleaseCapture();
                    Win32.SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
                }
            }
        };
        return bar;
    }

    // ---- layout ----------------------------------------------------------

    private Control BuildBody()
    {
        var body = new HBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        body.AddThemeConstantOverride("separation", 0);
        body.AddChild(BuildLeftPanel());
        body.AddChild(BuildCenterPanel());
        body.AddChild(BuildRightPanel());

        // light background for the whole body so all text reads well
        var bg = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var bodyBg = BgColor; // local: an initializer's `BgColor = BgColor` binds to the new object
        bg.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = bodyBg,
        });
        bg.AddChild(body);
        return bg;
    }

    private StyleBoxFlat SelectedStyle()
    {
        var sb = new StyleBoxFlat
        {
            BgColor = SelectedBg,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 8, ContentMarginRight = 8,
        };
        return sb;
    }

    private StyleBoxFlat HoverStyle()
    {
        var sb = new StyleBoxFlat
        {
            BgColor = HoverBg,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 8, ContentMarginRight = 8,
        };
        return sb;
    }

    private StyleBoxFlat PanelStyle(int radius = 0)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = PanelColor,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            ContentMarginLeft = 10, ContentMarginRight = 10,
            ContentMarginTop = 10, ContentMarginBottom = 10,
        };
        if (radius > 0)
        {
            sb.BorderWidthBottom = 1; sb.BorderWidthTop = 1;
            sb.BorderWidthLeft = 1; sb.BorderWidthRight = 1;
            sb.BorderColor = BorderColor;
        }
        return sb;
    }

    private Label MkTitle(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 18);
        l.AddThemeColorOverride("font_color", InkColor);
        return l;
    }

    private Control BuildLeftPanel()
    {
        var panel = new VBoxContainer { CustomMinimumSize = new Vector2(236, 0) };
        panel.AddThemeConstantOverride("separation", 6);

        panel.AddChild(MkTitle("预设穿搭"));
        foreach (var preset in PartCatalog.Presets)
        {
            var b = new Button
            {
                Text = " " + preset.Name,
                TooltipText = preset.Hint,
                CustomMinimumSize = new Vector2(0, 42),
                Alignment = HorizontalAlignment.Left,
                FocusMode = Control.FocusModeEnum.None,
            };
            b.Pressed += () =>
            {
                _appearance.Selected.Clear();
                _appearance.Colors.Clear();
                foreach (var (k, v) in preset.Selected) _appearance.Selected[k] = v;
                foreach (var (k, v) in preset.Colors) _appearance.Colors[k] = NormalizeColor(v);
                ApplyAndSay($"换上了「{preset.Name}」");
                RefreshAll();
            };
            _presetButtons.Add(b);
            panel.AddChild(b);
        }

        var spacer = new HSeparator { CustomMinimumSize = new Vector2(0, 12) };
        panel.AddChild(spacer);
        panel.AddChild(MkTitle("分类"));

        _categoryList = new ItemList
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None,
        };
        // themed look: white rounded panel, soft-pink selected row with
        // accent text, no focus outline, roomy rows
        _categoryList.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = PanelColor,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            DrawCenter = true,
        });
        _categoryList.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _categoryList.AddThemeStyleboxOverride("selected", new StyleBoxEmpty());
        _categoryList.AddThemeStyleboxOverride("selected_focus", new StyleBoxEmpty());
        _categoryList.AddThemeColorOverride("font_color", InkColor);
        // dark text on the soft-pink row: same-hue pink-on-pink was unreadable
        _categoryList.AddThemeColorOverride("font_selected_color", InkColor);
        _categoryList.AddThemeConstantOverride("v_separation", 2);
        foreach (var cat in PartCatalog.UiCategories)
        {
            var group = PartCatalog.Groups.Find(g => g.Key == cat);
            if (group == null || group.Ids.Count == 0)
                continue;
            _categoryList.AddItem($"{PartCatalog.CategoryName(cat)}  {group.Ids.Count}");
        }
        _categoryList.ItemSelected += idx =>
        {
            HighlightCategoryRow((int)idx);
            var text = _categoryList.GetItemText((int)idx);
            _category = PartCategoryKeyOf(text);
            RebuildCategoryView();
        };
        panel.AddChild(_categoryList);

        var wrap = new PanelContainer();
        wrap.AddThemeStyleboxOverride("panel", PanelStyle(10));
        wrap.AddChild(panel);
        var box = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        box.AddThemeConstantOverride("margin_left", 8);
        box.AddThemeConstantOverride("margin_right", 8);
        box.AddThemeConstantOverride("margin_top", 8);
        box.AddThemeConstantOverride("margin_bottom", 8);
        box.AddChild(wrap);
        return box;
    }

    /// <summary>Soft-pink background for the selected category row only.</summary>
    private void HighlightCategoryRow(int selected)
    {
        for (int i = 0; i < _categoryList.ItemCount; i++)
            _categoryList.SetItemCustomBgColor(i,
                i == selected ? RowSelBg : Colors.Transparent);
    }

    private static string PartCategoryKeyOf(string display)
    {
        var name = display.Split("  ")[0];
        foreach (var cat in PartCatalog.UiCategories)
            if (PartCatalog.CategoryName(cat) == name)
                return cat;
        return "front";
    }

    private Control BuildCenterPanel()
    {
        var panel = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        panel.AddThemeConstantOverride("separation", 8);

        var header = new HBoxContainer();
        _categoryTitle = MkTitle("");
        _categoryTitle.AddThemeFontSizeOverride("font_size", 24);
        header.AddChild(_categoryTitle);
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var randomBtn = new Button
        {
            Text = " 随机一身",
            CustomMinimumSize = new Vector2(96, 32),
            FocusMode = Control.FocusModeEnum.None,
        };
        randomBtn.Pressed += Randomize;
        header.AddChild(randomBtn);
        panel.AddChild(header);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        // note: ScrollMode.Disabled *shrinks* the viewport to the child's min
        // width instead of clamping the child, so keep the default auto mode
        // and pick a conservative column count from the window width instead
        scroll.Resized += () => ReflowPartGrid(PartsAreaWidth);
        _partsScroll = scroll;
        _partsBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _partsBox.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_partsBox);
        panel.AddChild(scroll);

        var box = new MarginContainer
        {
            // the middle column is the only one that grows with the window
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        box.AddThemeConstantOverride("margin_left", 8);
        box.AddThemeConstantOverride("margin_right", 8);
        box.AddThemeConstantOverride("margin_top", 8);
        box.AddThemeConstantOverride("margin_bottom", 8);
        box.AddChild(panel);
        return box;
    }

    private Control BuildRightPanel()
    {
        var panel = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        panel.AddThemeConstantOverride("separation", 8);

        panel.AddChild(MkTitle("实时预览"));
        var vpWrap = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vpWrap.AddThemeStyleboxOverride("panel", PanelStyle(12));
        _vpContainer = new SubViewportContainer
        {
            CustomMinimumSize = new Vector2(240, 300),
            Stretch = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        var sv = new SubViewport { OwnWorld3D = false, TransparentBg = true };
        sv.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _preview = new PetActor();
        sv.AddChild(_preview);
        _vpContainer.AddChild(sv);
        vpWrap.AddChild(_vpContainer);
        panel.AddChild(vpWrap);
        // re-fit the character whenever the preview viewport resizes
        _vpContainer.Resized += () =>
        {
            var sz = _vpContainer.Size;
            if (sz.X < 40 || sz.Y < 40)
                return;
            _preview.Setup(_appearance, sz);
            _preview.Position = new Vector2(sz.X * 0.5f, sz.Y * 0.94f);
        };
        _preview.Setup(_appearance, new Vector2(260, 420));
        _preview.Position = new Vector2(130, 420 * 0.94f);
        _preview.PlayMotion("breath");

        panel.AddChild(MkTitle("肤色"));
        var skinRow = new HFlowContainer();
        foreach (var tone in PartCatalog.SkinPalettes)
        {
            var b = new Button
            {
                Text = tone.IsOriginal ? "原" : "●",
                TooltipText = tone.Name,
                CustomMinimumSize = new Vector2(tone.IsOriginal ? 44 : 36, 30),
                FocusMode = Control.FocusModeEnum.None,
            };
            if (!tone.IsOriginal) b.Modulate = tone.Color;
            b.Pressed += () =>
            {
                _appearance.Colors["skin"] = tone.IsOriginal ? "" : tone.Color.ToHtml(false);
                ApplyAndSay($"肤色：{tone.Name}");
                RefreshAll();
            };
            _skinButtons.Add(b);
            skinRow.AddChild(b);
        }
        panel.AddChild(skinRow);

        var hint = new Label
        {
            Text = "左右两侧同时生效：\n编辑器预览与桌面上的她",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 15);
        hint.AddThemeColorOverride("font_color", DimColor);
        panel.AddChild(hint);

        var box = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        box.AddThemeConstantOverride("margin_left", 8);
        box.AddThemeConstantOverride("margin_right", 8);
        box.AddThemeConstantOverride("margin_top", 8);
        box.AddThemeConstantOverride("margin_bottom", 8);
        box.AddChild(panel);
        return box;
    }

    private Control BuildFooter()
    {
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 10);
        var bg = new PanelContainer();
        var footerBg = BgColor; // local: avoid same-name initializer self-bind
        bg.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = footerBg,
            ContentMarginLeft = 14, ContentMarginRight = 14,
            ContentMarginTop = 8, ContentMarginBottom = 8,
        });

        _status = new Label
        {
            Text = "改动会实时预览（保存按钮在标题栏）",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _status.AddThemeColorOverride("font_color", DimColor);
        footer.AddChild(_status);

        // custom resize grip (a borderless window has no native edges)
        var grip = new Label
        {
            Text = "◢",
            CustomMinimumSize = new Vector2(22, 22),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Modulate = DimColor,
        };
        grip.AddThemeFontSizeOverride("font_size", 16);
        Vector2I? gripStart = null;
        Vector2I sizeStart = Vector2I.Zero;
        grip.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton b && b.ButtonIndex == MouseButton.Left)
            {
                gripStart = b.Pressed ? DisplayServer.MouseGetPosition() : null;
                sizeStart = Size;
                _liveResizing = b.Pressed;
                // go rectangular while dragging; rounded corners come back
                // in one apply on release (no per-frame region rebuilds)
                SetCornersDuringResize(b.Pressed);
            }
            else if (@event is InputEventMouseMotion && gripStart is { } g0)
            {
                var delta = DisplayServer.MouseGetPosition() - g0;
                Size = new Vector2I(
                    Mathf.Max(MinSize.X, sizeStart.X + delta.X),
                    Mathf.Max(MinSize.Y, sizeStart.Y + delta.Y));
            }
        };
        footer.AddChild(grip);
        bg.AddChild(footer);

        CloseRequested += DoClose;
        return bg;
    }

    private void DoClose()
    {
        _appearance.Save();
        _onClose?.Invoke();
        QueueFree();
    }

    // ---- category view ----------------------------------------------------

    private void RebuildCategoryView()
    {
        foreach (var child in _partsBox.GetChildren())
            child.QueueFree();

        _categoryTitle.Text = PartCatalog.CategoryName(_category);
        var group = PartCatalog.Groups.Find(g => g.Key == _category);
        var selected = _appearance.Selected.GetValueOrDefault(_category, "");

        _grid = new GridContainer
        {
            // a fresh GridContainer defaults to 1 column — inherit the
            // current flow so randomize/category switches keep the layout
            Columns = _gridColumns > 0 ? _gridColumns : 1,
        };
        _grid.AddThemeConstantOverride("h_separation", 4);
        _grid.AddThemeConstantOverride("v_separation", 4);

        void PartButton(string itemId, string label)
        {
            var b = new Button
            {
                Text = "",
                ToggleMode = true,
                ButtonPressed = selected == itemId,
                CustomMinimumSize = new Vector2(172, 48),
                TooltipText = label,
                FocusMode = Control.FocusModeEnum.None,
            };
            b.AddThemeStyleboxOverride("pressed", SelectedStyle());
            b.AddThemeStyleboxOverride("hover", HoverStyle());
            // dark text on the soft-pink hover fill (white was invisible)
            b.AddThemeColorOverride("font_hover_color", InkColor);

            // thumbnail + caption inside the button (children must not eat clicks)
            var row = new HBoxContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            row.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            row.AddThemeConstantOverride("separation", 8);
            row.Alignment = BoxContainer.AlignmentMode.Begin; // left-aligned
            // spacer: a small left inset so art isn't glued to the edge
            row.AddChild(new Control
            {
                CustomMinimumSize = new Vector2(8, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            if (!string.IsNullOrEmpty(itemId) && PartCatalog.Items.TryGetValue(itemId, out var item))
            {
                var thumb = new TextureRect
                {
                    Texture = ThumbOf(item),
                    CustomMinimumSize = new Vector2(34, 34),
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                };
                row.AddChild(thumb);
            }
            var caption = new Label
            {
                Text = label,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            caption.AddThemeColorOverride("font_color", InkColor);
            row.AddChild(caption);
            b.AddChild(row);

            string captured = itemId;
            b.Pressed += () =>
            {
                _appearance.Selected[_category] = captured;
                ApplyAndSay(null);
                RefreshCategoryToggleStates();
            };
            _grid.AddChild(b);
            var key = _category + ":" + itemId;
            _partButtons[key] = new List<Button> { b };
        }

        PartButton("", "无");
        if (group != null)
        {
            foreach (var itemId in group.Ids)
            {
                if (!PartCatalog.Items.TryGetValue(itemId, out var item) || item.Skin)
                    continue;
                PartButton(itemId, item.Label);
            }
        }
        _partsBox.AddChild(_grid);
        // initial column count: wait out the first layout pass (viewport is 0
        // during _Ready and call_deferred still runs before sorting)
        GetTree().CreateTimer(0.05f).Timeout += () =>
            ReflowPartGrid(PartsAreaWidth);

        // tint swatches for this category (flow: wraps when the column narrows)
        _partsBox.AddChild(MkTitle("染色"));
        var colorRow = new HFlowContainer();
        foreach (var p in PartCatalog.Palettes)
        {
            var cb = new Button
            {
                Text = p.IsOriginal ? "原色" : "●",
                TooltipText = p.Name,
                CustomMinimumSize = new Vector2(p.IsOriginal ? 56 : 40, 30),
                FocusMode = Control.FocusModeEnum.None,
            };
            if (!p.IsOriginal) cb.Modulate = p.Color;
            string cat = _category;
            cb.Pressed += () =>
            {
                _appearance.Colors[cat] = p.IsOriginal ? "" : p.Color.ToHtml(false);
                ApplyAndSay(null);
            };
            colorRow.AddChild(cb);
        }
        _partsBox.AddChild(colorRow);
    }

    private void RefreshCategoryToggleStates()
    {
        var selected = _appearance.Selected.GetValueOrDefault(_category, "");
        foreach (var kvp in _partButtons)
        {
            var key = kvp.Key;
            if (!key.StartsWith(_category + ":"))
                continue;
            var id = key.Substring(key.IndexOf(':') + 1);
            foreach (var b in kvp.Value)
                b.ButtonPressed = id == selected;
        }
    }

    private void Randomize()
    {
        var rng = new Random();
        foreach (var cat in PartCatalog.UiCategories)
        {
            var group = PartCatalog.Groups.Find(g => g.Key == cat);
            if (group == null || group.Ids.Count == 0)
                continue;
            var pool = new List<string>();
            foreach (var id in group.Ids)
                if (PartCatalog.Items.TryGetValue(id, out var it) && !it.Skin)
                    pool.Add(id);
            pool.Add("");
            _appearance.Selected[cat] = pool[rng.Next(pool.Count)];
            _appearance.Colors[cat] = rng.Next(10) == 0
                ? PartCatalog.Palettes[rng.Next(PartCatalog.Palettes.Count)].Color.ToHtml(false)
                : "";
        }
        ApplyAndSay("随机换了一身！");
        RefreshAll();
    }

    private void RefreshAll()
    {
        RebuildCategoryView();
    }

    /// <summary>Palette indexes from the original save normalize to hex here.</summary>
    private static string NormalizeColor(string value)
    {
        value ??= "";
        if (value.StartsWith("#"))
            return value;
        if (int.TryParse(value.Trim(), out int idx) && idx >= 0 && idx < PartCatalog.Palettes.Count)
        {
            var p = PartCatalog.Palettes[idx];
            return p.IsOriginal ? "" : p.Color.ToHtml(false);
        }
        return "";
    }

    private void ApplyAndSay(string line)
    {
        _actor.Rebuild();
        _preview?.Rebuild();
        _status.Text = line ?? "已预览（记得保存）";
        if (line != null)
            _brain?.SetIdle();
    }
}
