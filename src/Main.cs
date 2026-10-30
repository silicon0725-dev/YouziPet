using System.Linq;
using System.Collections.Generic;
using Godot;
using YuzuPet.Capture;
using YuzuPet.Character;
using YuzuPet.Pet;
using YuzuPet.UI;

namespace YuzuPet;

/// <summary>
/// Desktop pet entry: transparent always-on-top window, the layered-sprite
/// character, drag / head-pat / feed interactions, a small control bar and
/// the wardrobe editor window. Mouse clicks outside the character's
/// silhouette pass through to the desktop.
/// </summary>
public partial class Main : Node
{
    private PetActor _actor;
    private PetBrain _brain;
    private Appearance _appearance;
    private PanelContainer _bubble;
    private Label _bubbleLabel;
    private Timer _bubbleTimer;
    private Timer _idleTimer;
    private Timer _passthroughTimer;
    private bool _dragging;
    private Vector2I _dragOffset;
    private Vector2 _actorPos;
    private int _danceIndex;
    private bool _headPetting;   // cursor held on the head (pat, not drag)
    private float _petScrub;     // accumulated horizontal scrub distance
    private float _petLastX;
    private bool _petSatisfied;  // scrubbed far enough for the content reaction
    private FaceRig _faceRig;
    private FaceCaptureOpenSee _capture;
    private FaceCaptureUdp _captureDemo;
    private int _sidecarPid = -1;
    private bool _liftSim;
    private float _simTime;

    public override void _Ready()
    {
        PartCatalog.Load("res://data/character_catalog.json");
        _appearance = Appearance.Load();
        _brain = new PetBrain();
        AddChild(_brain);

        var win = GetWindow();
        win.Size = new Vector2I(440, 660);
        win.Position = new Vector2I(
            DisplayServer.ScreenGetUsableRect().End.X - win.Size.X - 40,
            DisplayServer.ScreenGetUsableRect().End.Y - win.Size.Y - 40);

        // original theatre layout: floor line at 94% height
        _actorPos = new Vector2(win.Size.X * 0.5f, win.Size.Y * 0.94f);
        _actor = new PetActor();
        AddChild(_actor);
        _actor.Position = _actorPos;
        _actor.Setup(_appearance, win.Size);
        _actor.PlayMotion("breath");

        BuildBubble();
        BuildControlBar();

        _idleTimer = new Timer { WaitTime = 22.0, Autostart = true };
        _idleTimer.Timeout += () => Say(_brain.MaybeIdleChat());
        AddChild(_idleTimer);

        _passthroughTimer = new Timer { WaitTime = 0.6, Autostart = true };
        _passthroughTimer.Timeout += UpdatePassthrough;
        AddChild(_passthroughTimer);
        UpdatePassthrough();

        // cursor hygiene: restore the arrow when the pointer leaves the
        // window, otherwise the pointing hand would stick outside
        GetWindow().MouseExited += () =>
            DisplayServer.CursorSetShape(DisplayServer.CursorShape.Arrow);

        if (System.Environment.GetEnvironmentVariable("YUZUPET_SELCHECK") == "1")
            _ = CaptureSelfCheck();
        if (System.Environment.GetEnvironmentVariable("YUZUPET_WARDROBE") == "1")
            OpenWardrobe();

        var startDance = System.Environment.GetEnvironmentVariable("YUZUPET_DANCE");
        if (!string.IsNullOrEmpty(startDance))
            _actor.PlayMotion(startDance);

        SetupPlatformIntegrations();

        // face capture (v2): OpenSeeFace binary receiver (JSON demo fallback);
        // sidecar is spawned on toggle
        _capture = new FaceCaptureOpenSee();
        _captureDemo = new FaceCaptureUdp { Port = 7902 };
        AddChild(_capture);
        AddChild(_captureDemo);
        _faceRig = new FaceRig();
        AddChild(_faceRig);
        _faceRig.Attach(_actor, _capture);

        var faceEnv = System.Environment.GetEnvironmentVariable("YUZUPET_FACECAP");
        if (faceEnv == "1" || faceEnv == "demo")
            ToggleFaceCapture(demo: faceEnv == "demo");

        _liftSim = System.Environment.GetEnvironmentVariable("YUZUPET_LIFT") == "1";
    }

    // ---- native platform integrations (addons/) ---------------------------

    private GodotObject _tray;
    private GodotObject _dwm;

    /// <summary>
    /// godot-native-tray: system tray icon + menu. godot_dwm: hide the pet
    /// window from the taskbar and alt-tab (WS_EX_TOOLWINDOW). Both are
    /// optional at runtime — the pet still works if an addon is missing.
    /// </summary>
    private void SetupPlatformIntegrations()
    {
        if (ClassDB.ClassExists("DWM"))
        {
            _dwm = ClassDB.Instantiate("DWM").AsGodotObject();
            const long WsExToolwindow = 0x00000080;
            var ok = _dwm?.Call("set_window_style_bits", GetWindow(),
                WsExToolwindow, true, true, true).AsBool() ?? false;
            GD.Print("DWM toolwindow (hide from taskbar): ", ok);
        }

        if (Engine.HasSingleton("NativeTray"))
        {
            _tray = Engine.GetSingleton("NativeTray");
            _tray.Call("init_tray_ico", "柚松屋桌宠",
                ProjectSettings.GlobalizePath("res://assets/icon.ico"));
            _tray.Call("add_menu_item", 1, "捏脸换装", false, false);
            _tray.Call("add_menu_item", 2, "投喂", false, false);
            _tray.Call("add_menu_item", 3, "跳舞", false, false);
            _tray.Call("add_menu_item", 4, "站好", false, false);
            _tray.Call("add_menu_item", 5, "2D 面捕", false, false);
            _tray.Call("add_menu_separator");
            _tray.Call("add_menu_item", 9, "退出", false, false);
            _tray.Connect("on_menu_item_selected", Callable.From<long>(OnTrayMenu));
            _tray.Connect("on_tray_left_clicked", Callable.From(OnTrayLeftClick));
            GD.Print("tray ready: ", _tray.Call("is_tray_active").AsBool());
        }
    }

    private void OnTrayMenu(long id)
    {
        switch (id)
        {
            case 1:
                OpenWardrobe();
                break;
            case 2:
                Say(_brain.OnFeed());
                _actor.ReactHappy(1.6f);
                _actor.Squash(0.7f);
                SpawnHearts();
                break;
            case 3:
                Say(_brain.OnDance());
                var pool = PartCatalog.Motions
                    .Select(m => m.Id)
                    .Where(mid => mid != "still" && mid != "breath")
                    .ToList();
                _danceIndex = (_danceIndex + 1) % pool.Count;
                _actor.PlayMotion(pool[_danceIndex]);
                break;
            case 4:
                _brain.SetIdle();
                _actor.PlayMotion("still");
                break;
            case 5:
                ToggleFaceCapture();
                break;
            case 9:
                _brain.Save();
                _tray?.Call("remove_tray");
                GetTree().Quit();
                break;
        }
    }

    private bool _petHidden;

    private void OnTrayLeftClick()
    {
        if (GetNodeOrNull<WardrobeWindow>("WardrobeWindow") != null)
            return; // while the studio is open the pet's visibility is its business
        _petHidden = !_petHidden;
        SetPetWindowVisible(!_petHidden);
    }

    // ---- face capture (v2) --------------------------------------------------

    public bool FaceCaptureActive => _faceRig?.Active ?? false;

    /// <summary>
    /// Root for on-disk sidecar files (python tools). In the editor that's
    /// the project folder; in an exported build it's next to the exe — the
    /// tools stay unpacked (res:// inside a pck can't be spawned).
    /// </summary>
    private static string SidecarRoot() =>
        OS.HasFeature("editor")
            ? ProjectSettings.GlobalizePath("res://")
            : OS.GetExecutablePath().GetBaseDir();

    /// <summary>
    /// Locate the OpenSeeFace launcher: env override, then the repo checkout
    /// (tools/OpenSeeFace — see README setup), then a sibling folder next to
    /// the project, and finally next to an exported exe.
    /// </summary>
    private static string FindTrackerBat()
    {
        string[] candidates =
        {
            System.Environment.GetEnvironmentVariable("YUZUPET_OSF") ?? "",
            ProjectSettings.GlobalizePath("res://tools/OpenSeeFace/run_tracker.bat"),
            ProjectSettings.GlobalizePath("res://../_sidecar/OpenSeeFace/run_tracker.bat"),
            OS.GetExecutablePath().GetBaseDir() + "/tools/OpenSeeFace/run_tracker.bat",
        };
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && FileAccess.FileExists(c))
                return c;
        return "";
    }

    /// <summary>
    /// Resolve a python executable Godot can spawn: env override first, then
    /// common install locations (the WindowsApps "python" stub is unreliable
    /// from CreateProcess), then bare "python" as a last resort.
    /// </summary>
    private static string ResolvePython()
    {
        var env = System.Environment.GetEnvironmentVariable("YUZUPET_PYTHON");
        if (!string.IsNullOrEmpty(env))
            return env;
        string[] candidates =
        {
            System.Environment.ExpandEnvironmentVariables(
                @"%LOCALAPPDATA%\Programs\Python\Python314\python.exe"),
            System.Environment.ExpandEnvironmentVariables(
                @"%LOCALAPPDATA%\Programs\Python\Python313\python.exe"),
            System.Environment.ExpandEnvironmentVariables(
                @"%LOCALAPPDATA%\Programs\Python\Python312\python.exe"),
            @"C:\Python314\python.exe",
            @"C:\Python313\python.exe",
        };
        foreach (var c in candidates)
            if (FileAccess.FileExists(c))
                return c;
        return "python";
    }

    /// <summary>
    /// Start/stop 2D face capture. Real mode spawns the OpenSeeFace tracker
    /// (tools/OpenSeeFace/facetracker.py, BSD-2) streaming binary packets;
    /// demo=true runs the old mediapipe sidecar's sine stream so the rig can
    /// be verified without a camera. Python path: YUZUPET_PYTHON or PATH.
    /// </summary>
    public void ToggleFaceCapture(bool demo = false)
    {
        if (FaceCaptureActive)
        {
            _faceRig.Deactivate();
            _capture.Stop();
            _captureDemo.Stop();
            _faceRig.Attach(_actor, _capture); // back to the OpenSeeFace receiver
            if (_sidecarPid > 0)
            {
                OS.Kill(_sidecarPid);
                _sidecarPid = -1;
            }
            Say("面捕已关闭");
            return;
        }

        var python = ResolvePython();
        GD.Print("face capture python: ", python);

        if (!demo)
        {
            // OpenSeeFace tracker (model 3: good expression/blink quality)
            _capture.Port = 7901;
            _capture.Start();
            if (!_capture.IsRunning)
                return;
            var bat = FindTrackerBat();
            if (string.IsNullOrEmpty(bat))
            {
                Say("未找到 OpenSeeFace，请按 README 获取面捕工具");
                _capture.Stop();
                return;
            }
            _sidecarPid = OS.CreateProcess(bat, new string[0], false);
            if (_sidecarPid == -1)
            {
                _capture.Stop();
                Say("面捕启动失败：请确认已安装 python（或设置 YUZUPET_PYTHON）");
                return;
            }
        }
        else
        {
            _faceRig.Attach(_actor, _captureDemo);
            _captureDemo.Port = 7902;
            _captureDemo.Start();
            if (!_captureDemo.IsRunning)
                return;
            var args = new List<string>
            {
                SidecarRoot() + "/tools/face_capture.py",
                "--demo", "--port", "7902",
            };
            _sidecarPid = OS.CreateProcess(python, args.ToArray(), false);
            if (_sidecarPid == -1)
            {
                _captureDemo.Stop();
                Say("演示启动失败：请确认已安装 python（或设置 YUZUPET_PYTHON）");
                return;
            }
        }
        _faceRig.Activate();
        Say(demo ? "面捕演示模式" : "面捕开启，看着我～");
    }

    public override void _ExitTree()
    {
        if (_sidecarPid > 0)
            OS.Kill(_sidecarPid);
    }

    /// <summary>
    /// Show/hide the pet's OS window. Window.Visible is unreliable for the
    /// root window (assign-and-read-back returns true), so use minimize —
    /// the OS reliably pulls it off the desktop — and re-assert the pet's
    /// window flags when restoring.
    /// </summary>
    private void SetPetWindowVisible(bool visible)
    {
        var win = GetWindow();
        if (visible)
        {
            win.Mode = Window.ModeEnum.Windowed;
            win.Borderless = true;
            win.AlwaysOnTop = true;
        }
        else
        {
            win.Mode = Window.ModeEnum.Minimized;
        }
    }

    private void OpenWardrobe()
    {
        if (GetNodeOrNull<WardrobeWindow>("WardrobeWindow") != null)
            return;
        var w = new WardrobeWindow(_appearance, _actor, _brain, () =>
        {
            Say("好看！");
            _petHidden = false;
            SetPetWindowVisible(true); // bring the pet back
        });
        w.Name = "WardrobeWindow";
        AddChild(w);
        w.Show();
        w.GrabFocus();
        SetPetWindowVisible(false); // hide the pet while dressing up
    }

    /// <summary>Dev self-check: render one frame of the pet, save a png, exit.</summary>
    private async System.Threading.Tasks.Task CaptureSelfCheck()
    {
        var at = 1.2f;
        var atEnv = System.Environment.GetEnvironmentVariable("YUZUPET_SHOT_AT");
        if (float.TryParse(atEnv, out var parsed) && parsed is > 0f and < 30f)
            at = parsed;
        await ToSignal(GetTree().CreateTimer(at), SceneTreeTimer.SignalName.Timeout);
        if (GetNodeOrNull<WardrobeWindow>("WardrobeWindow") == null)
            GetViewport().GetTexture().GetImage().SavePng("user://selfcheck.png");
        else
            GD.Print("selfcheck: studio open, skipping pet shot");
        var wardrobe = GetNodeOrNull<WardrobeWindow>("WardrobeWindow");
        if (wardrobe != null)
            wardrobe.GetTexture().GetImage().SavePng("user://selfcheck_wardrobe.png");
        GD.Print("selfcheck saved: ", ProjectSettings.GlobalizePath("user://selfcheck.png"));
        GD.Print("pose @ capture: ", _actor.DebugPose());
        GetTree().Quit();
    }

    // ---- UI bits ----------------------------------------------------------

    private void BuildBubble()
    {
        _bubble = new PanelContainer
        {
            // the pet's sprites carry positive ZIndex values (canvas layer
            // order) — UI must sit above that band or the pet hides it
            ZIndex = 2000,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, 0.96f),
            CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12,
            CornerRadiusBottomRight = 12, CornerRadiusBottomLeft = 12,
            ContentMarginLeft = 12, ContentMarginRight = 12,
            ContentMarginTop = 8, ContentMarginBottom = 8,
            ShadowColor = new Color(0, 0, 0, 0.15f), ShadowSize = 4,
            BorderWidthBottom = 1, BorderWidthTop = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(0, 0, 0, 0.06f),
        };
        _bubble.AddThemeStyleboxOverride("panel", style);
        _bubbleLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // dark ink on the light bubble (the theme default is white-on-white)
        _bubbleLabel.AddThemeColorOverride("font_color", new Color(0.24f, 0.19f, 0.21f));
        _bubbleLabel.AddThemeFontSizeOverride("font_size", 19);
        _bubble.AddChild(_bubbleLabel);
        _bubble.Visible = false;
        AddChild(_bubble);

        _bubbleTimer = new Timer { OneShot = true };
        _bubbleTimer.Timeout += () => _bubble.Visible = false;
        AddChild(_bubbleTimer);
    }

    private void Say(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        _bubbleLabel.Text = line;
        _bubble.Visible = true;
        _bubble.ResetSize();
        // park the bubble above the head
        var head = _actor.HeadCanvasRect();
        var headTopWin = _actor.CanvasToWindow(
            new Vector2(head.Position.X + head.Size.X / 2f, head.Position.Y), _actorPos);
        _bubble.Position = new Vector2(
            Mathf.Clamp(headTopWin.X - _bubble.Size.X / 2f, 6, GetWindow().Size.X - _bubble.Size.X - 6),
            Mathf.Max(headTopWin.Y - _bubble.Size.Y - 14, 6));
        _bubbleTimer.WaitTime = 2.6f;
        _bubbleTimer.Start();
    }

    private void BuildControlBar()
    {
        var bar = new VBoxContainer
        {
            Position = new Vector2(GetWindow().Size.X - 46, 24),
            ZIndex = 2000, // above the pet's positive-ZIndex sprite band
        };
        bar.AddThemeConstantOverride("separation", 6);

        Button MkButton(string label, string tip)
        {
            var b = new Button
            {
                Text = label,
                TooltipText = tip,
                CustomMinimumSize = new Vector2(38, 38),
                FocusMode = Control.FocusModeEnum.None,
            };
            b.AddThemeFontSizeOverride("font_size", 13);
            return b;
        }

        var wardrobe = MkButton("衣", "捏脸换装");
        wardrobe.Pressed += () =>
        {
            Say(_brain.OnDress());
            OpenWardrobe();
        };
        bar.AddChild(wardrobe);

        var feed = MkButton("喂", "投喂");
        feed.Pressed += () =>
        {
            Say(_brain.OnFeed());
            _actor.ReactHappy(1.6f);
            _actor.Squash(0.7f);
            SpawnHearts();
        };
        bar.AddChild(feed);

        var dance = MkButton("舞", "跳舞");
        dance.Pressed += () =>
        {
            Say(_brain.OnDance());
            var pool = PartCatalog.Motions
                .Select(m => m.Id)
                .Where(id => id != "still" && id != "breath")
                .ToList();
            _danceIndex = (_danceIndex + 1) % pool.Count;
            _actor.PlayMotion(pool[_danceIndex]);
        };
        bar.AddChild(dance);

        var still = MkButton("站", "站好");
        still.Pressed += () =>
        {
            _brain.SetIdle();
            _actor.PlayMotion("still");
        };
        bar.AddChild(still);

        var face = MkButton("捕", "2D 面捕");
        face.Pressed += () => ToggleFaceCapture();
        bar.AddChild(face);

        var quit = MkButton("✕", "退出（保存）");
        quit.Pressed += () =>
        {
            _brain.Save();
            GetTree().Quit();
        };
        bar.AddChild(quit);

        AddChild(bar);
    }

    private void SpawnHearts()
    {
        var heart = new Label { Text = "♥", ZIndex = 2000 };
        heart.AddThemeFontSizeOverride("font_size", 22);
        heart.AddThemeColorOverride("font_color", new Color(0.95f, 0.5f, 0.6f));
        var head = _actor.HeadCanvasRect();
        heart.Position = _actor.CanvasToWindow(
            new Vector2(head.Position.X + head.Size.X / 2f + GD.Randf() * 260 - 130,
                        head.Position.Y), _actorPos);
        AddChild(heart);
        var tw = CreateTween();
        tw.Parallel().TweenProperty(heart, "position:y", heart.Position.Y - 70, 1.1)
            .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        tw.Parallel().TweenProperty(heart, "modulate:a", 0f, 1.1);
        tw.TweenCallback(Callable.From(heart.QueueFree));
    }

    // ---- input ------------------------------------------------------------

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed && !_dragging && !_headPetting && OverCharacter(mb.Position))
            {
                var canvas = _actor.WindowToCanvas(mb.Position, _actorPos);
                var head = _actor.HeadCanvasRect();
                if (head.HasPoint(canvas))
                {
                    // press on the head: start a pat — click = bounce,
                    // scrub = petting with the head leaning to the cursor
                    _headPetting = true;
                    _petScrub = 0f;
                    _petLastX = mb.Position.X;
                    _petSatisfied = false;
                }
                else
                {
                    _dragging = true;
                    _dragOffset = DisplayServer.MouseGetPosition() - GetWindow().Position;
                    _actor.SetLiftInput(LiftPoint(DisplayServer.MouseGetPosition()),
                                        true, LiftSize);
                }
            }
            else if (!mb.Pressed && _headPetting)
            {
                _headPetting = false;
                _actor.HeadPetTilt = 0f;
                if (_petScrub < 24f)
                {
                    // plain click on the head: bounce
                    Say(_brain.OnPat());
                    _actor.ReactHappy();
                    _actor.Squash(0.5f);
                }
                else
                {
                    // petting done
                    Say(_petSatisfied ? "呼噜呼噜……好舒服" : "再摸一会儿嘛");
                    _actor.Squash(0.35f);
                }
            }
            else if (!mb.Pressed && _dragging)
            {
                _dragging = false;
                _actor.SetLiftInput(null, false);
                _actor.Squash(0.9f);
                _actor.ReactWow();
                Say(_brain.OnDrop());
                _brain.SetIdle();
                UpdatePassthrough();
            }
        }
        else if (@event is InputEventMouseMotion mm)
        {
            if (_dragging)
            {
                GetWindow().Position = DisplayServer.MouseGetPosition() - _dragOffset;
                _actor.SetLiftInput(LiftPoint(DisplayServer.MouseGetPosition()),
                                    true, LiftSize);
                DisplayServer.CursorSetShape(DisplayServer.CursorShape.Drag);
            }
            else if (_headPetting)
            {
                // scrub: the head leans toward wherever the cursor is, and
                // enough back-and-forth earns the content reaction
                _petScrub += Mathf.Abs(mm.Position.X - _petLastX);
                _petLastX = mm.Position.X;
                var head = _actor.HeadCanvasRect();
                var headCenterX = _actor.CanvasToWindow(
                    new Vector2(head.Position.X + head.Size.X * 0.5f, 0f), _actorPos).X;
                _actor.HeadPetTilt = Mathf.Clamp((mm.Position.X - headCenterX) * 0.12f, -9f, 9f);
                DisplayServer.CursorSetShape(DisplayServer.CursorShape.PointingHand);
                if (!_petSatisfied && _petScrub > 110f)
                {
                    _petSatisfied = true;
                    _actor.ReactHappy(2.5f);
                    Say(_brain.OnPat());
                    SpawnHearts();
                }
            }
            else
            {
                // hover feedback: a little hand over the character
                DisplayServer.CursorSetShape(OverCharacter(mm.Position)
                    ? DisplayServer.CursorShape.PointingHand
                    : DisplayServer.CursorShape.Arrow);
            }
        }
    }

    // While dragging, the window chases the cursor and the pet stays at a
    // fixed spot inside it — so the pet's motion through the world equals the
    // cursor's *screen* motion. The pendulum therefore feeds on screen-space
    // deltas (window-local coordinates would look frozen).
    private const float LiftSize = 320f;

    private static Vector2 LiftPoint(Vector2I screenPos) => new(screenPos.X, screenPos.Y);

    public override void _Process(double delta)
    {
        // self-check lift simulation: shake the pet side to side
        if (_liftSim)
        {
            _simTime += (float)delta;
            var canvas = new Vector2(762f + Mathf.Sin(_simTime * 4.2f) * 430f, 820f);
            _actor.SetLiftInput(canvas, true);
        }
    }

    private bool OverCharacter(Vector2 windowPos)
    {
        var canvas = _actor.WindowToCanvas(windowPos, _actorPos);
        return _actor.CanvasBounds.HasPoint(canvas);
    }

    private void UpdatePassthrough()
    {
        // Disabled: Godot implements MousePassthroughPolygon via SetWindowRgn,
        // which CLIPS the window into an irregular shape (cutting off bubbles,
        // the control bar and swinging limbs). Keep the window rectangular;
        // clicks on the transparent margin land harmlessly on the window.
        // ComputeSilhouette() is kept for a future WS_EX_TRANSPARENT toggle.
    }
}
