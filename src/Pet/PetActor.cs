using System;
using System.Collections.Generic;
using Godot;
using YuzuPet.Character;

namespace YuzuPet.Pet;

/// <summary>Pose parameters driving bone transforms (mirrors the original app).</summary>
public struct Pose
{
    public float Root, X, Hop, Head, Hair;
    public float ArmL, ArmR, ForeL, ForeR;
    public float LegL, LegR, KneeL, KneeR;
    public float ScaleX, ScaleY;

    public static Pose Empty => new() { ScaleX = 1, ScaleY = 1 };
}

/// <summary>
/// Character renderer with the original app's skeletal animation system:
/// 21 dance poses computed per frame (88 BPM phase), bone chains with PSD
/// pivot points (head/hair/tail/arms/legs+knees), dynamic z-ordering
/// (arm-up brings hands/sleeves forward; dresses tuck sleeves behind) and
/// temporary expression overrides (eyes/mouth swaps for reactions & blink).
/// </summary>
public partial class PetActor : Node2D
{
    public const string BlinkEyesId = "u141_closed"; // 轻轻闭眼
    private const float TintStrength = 0.55f;
    private const float SkinTintStrength = 0.65f;
    private const float Bpm = 88f / 60f; // pose phase beats-per-second
    private const float Tau = Mathf.Tau;

    // PSD canvas anchors (1525x2048 space)
    private static readonly Vector2 Anchor = new(762f, 1684f); // floor between feet
    private static readonly Vector2 HeadPivot = new(762f, 985f);
    private static readonly Vector2 HairPivot = new(762f, 680f);
    private static readonly Vector2 RootPivot = new(762f, 1305f);
    private static readonly Vector2 ArmLPivot = new(708f, 1086f);
    private static readonly Vector2 ArmRPivot = new(817f, 1086f);
    private static readonly Vector2 HipLPivot = new(700f, 1280f);
    private static readonly Vector2 HipRPivot = new(825f, 1280f);
    private static readonly Vector2 KneeLPivot = new(675f, 1500f);
    private static readonly Vector2 KneeRPivot = new(851f, 1500f);

    private class Layer
    {
        public Sprite2D Node;
        public Vector2 BasePos;     // relative to floor anchor
        public string Bone = "";
        public ItemDef Item;
        public int BaseZ;           // doubled order space for half-steps
        public int Side = -1;       // -1 full sprite; 0/1 half clone (left/right eye)
        public Vector2 GlintPivot;  // local pivot for highlight jelly (eye sprites)
        public Vector2 ScaleFix = Vector2.One; // canvas-size / texture-size (bleed padding fix)
    }

    /// <summary>Per-frame eye drive data written by FaceRig (live mode).</summary>
    public struct LiveEyeInput
    {
        public float HeadX, HeadY;  // smoothed normalized head position
        public float Roll;          // smoothed head tilt (deg), for gating
        public float BlinkL, BlinkR;
    }

    public LiveEyeInput? LiveEyes;

    private readonly Node2D _stage = new();    // x/hop offset + scale (origin at anchor)
    private readonly Node2D _rootRot = new();  // waist rotation
    private readonly Node2D _layers = new();   // sprite container
    private readonly List<Layer> _active = new();
    private readonly Dictionary<string, List<Layer>> _byCategory = new();

    private Appearance _appearance;
    private Timer _blinkTimer;
    private float _time;   // pose phase clock (reset when the dance changes)
    private float _clock;  // wall clock for expression expiry (never resets)
    private string _dance = "breath";
    private float _amplitude = 0.65f;

    // expression overrides: category -> (itemId, expiresAt) ; null itemId = clear pending
    private readonly Dictionary<string, (string Id, float Until)> _overrides = new();
    private bool _captureBlinkHeld;

    // squash-and-stretch feedback (multiplied into stage scale)
    private float _squashT = -1f;
    private float _squashPower;

    // lift-and-shake pendulum physics
    private readonly LiftPhysics.Bank _liftBank = new();
    private float? _liftX, _liftY;
    private float _liftSize = 225f;
    private bool _liftHeld;

    // eye dynamics (live mode): per-side eyelids, glint spring, opened-at stamps
    private const float EyeLineY = 868f; // canvas y of the eye line
    private readonly float[] _eyelidAmt = new float[2];
    private readonly float[] _eyeOpenedAt = { -10f, -10f };
    private readonly bool[] _eyeWasClosed = new bool[2];
    private readonly float[] _blinkSmooth = new float[2];
    private readonly float[] _eyeHighTime = new float[2]; // sustained-above timers
    private readonly float[] _eyeLowTime = new float[2];  // sustained-below timers
    private readonly float[] _eyeMidTime = new float[2];  // sustained-squint timers
    private bool _l54Shown; // static closed-eyes overlay active (jitter-free)
    private float _glintStretch, _glintVelocity, _glintLastX, _glintLastY;

    public float FitScale { get; private set; } = 0.3f;
    public Rect2 CanvasBounds { get; private set; }

    /// <summary>
    /// When face capture is active, FaceRig writes the live pose here every
    /// frame and it replaces the dance pose (original live mode behavior).
    /// </summary>
    public Pose? LivePose;

    /// <summary>
    /// Head-patting tilt (degrees): while the cursor scrubs across the head,
    /// Main sets this so the head leans toward the cursor's side.
    /// </summary>
    public float HeadPetTilt;

    public override void _Ready()
    {
        // stage origin = floor anchor; rootRot pivots at the waist
        _rootRot.Position = RootPivot - Anchor;
        _layers.Position = Anchor - RootPivot;
        _stage.AddChild(_rootRot);
        _rootRot.AddChild(_layers);
        AddChild(_stage);

        _blinkTimer = new Timer { WaitTime = 3.5, Autostart = true };
        _blinkTimer.Timeout += OnAutoBlink;
        AddChild(_blinkTimer);
    }

    public void Setup(Appearance appearance, Vector2 windowSize)
    {
        _appearance = appearance;
        // the original fits the *content* box (1430x1730), not the full
        // 1525x2048 canvas — the margins are headroom for limb swings
        FitScale = Mathf.Min(windowSize.X / 1430f, windowSize.Y / 1730f);
        Rebuild();
    }

    // ---- outfit building ---------------------------------------------------

    public void Rebuild()
    {
        foreach (var layer in _active)
            layer.Node.QueueFree();
        _active.Clear();
        _byCategory.Clear();

        var equipped = new List<ItemDef>();
        foreach (var sid in PartCatalog.SkinIds)
            if (PartCatalog.Items.TryGetValue(sid, out var skinItem))
                equipped.Add(skinItem);
        foreach (var (category, itemId) in _appearance.Selected)
        {
            if (category == "skin" || string.IsNullOrEmpty(itemId))
                continue;
            if (PartCatalog.Items.TryGetValue(itemId, out var item))
                equipped.Add(item);
        }
        // stable tie-break: some items share the same PSD order (e.g. the
        // vest dress and the long boots are both 49.0); the original relies
        // on stable catalog-order sorting, so match that instead of letting
        // the dictionary's equip order decide who stacks on top
        equipped.Sort((a, b) =>
            PartCatalog.ItemIndex.GetValueOrDefault(a.Id, 0)
          - PartCatalog.ItemIndex.GetValueOrDefault(b.Id, 0));

        bool first = true;
        CanvasBounds = new Rect2();
        int dressOrder = -1;
        if (_appearance.Selected.TryGetValue("dress", out var dressId)
            && !string.IsNullOrEmpty(dressId)
            && PartCatalog.Items.TryGetValue(dressId, out var dressItem))
            dressOrder = dressItem.Sprites.Count > 0
                && PartCatalog.Sprites.TryGetValue(dressItem.Sprites[0], out var ds)
                ? ds.Order : -1;

        foreach (var item in equipped)
        {
            var tint = item.Skin
                ? HtmlColor(_appearance.ColorOf("skin"))
                : _appearance.TintOf(item.Category);
            var strength = item.Skin ? SkinTintStrength : TintStrength;
            foreach (var sid in item.Sprites)
            {
                if (!PartCatalog.Sprites.TryGetValue(sid, out var sp))
                    continue;
                AddSpriteLayer(sp, item, tint, strength, dressOrder);
                var r = new Rect2(sp.X, sp.Y, sp.W, sp.H);
                CanvasBounds = first ? r : CanvasBounds.Merge(r);
                first = false;
            }
        }

        ApplyPendingOverrides();
    }

    /// <summary>
    /// Build one drawable layer; eyes/eyeHighlight sprites contain both eyes
    /// centered on the canvas midline, so they are split into left/right
    /// AtlasTexture halves (like the original's half-screen clipping) for
    /// per-side eyelids, glint jelly and eyeball follow.
    /// </summary>
    private void AddSpriteLayer(SpriteDef sp, ItemDef item, Godot.Color tint,
                                float strength, int dressOrder)
    {
        int z = sp.Order * 2;
        if (dressOrder >= 0 && item.Category == "sleeves")
            z = dressOrder * 2 - 1;
        var basePos = new Vector2(sp.X + sp.W / 2f - Anchor.X,
                                  sp.Y + sp.H / 2f - Anchor.Y);
        var texture = LoadTexture(sp);
        bool eyePair = item.Category is "eyes" or "eyeHighlight"
                       && texture != null && texture.GetWidth() > 4;

        void Register(Layer layer)
        {
            _layers.AddChild(layer.Node);
            _active.Add(layer);
            _byCategory.TryGetValue(item.Category, out var cat);
            if (cat == null) _byCategory[item.Category] = cat = new List<Layer>();
            cat.Add(layer);
        }

        // custom-imported parts ship PNGs with bleed padding; normalize the
        // drawn size to the catalog's canvas rectangle either way
        Vector2 scaleFix = Vector2.One;
        if (texture != null && texture.GetWidth() > 0 && texture.GetHeight() > 0)
            scaleFix = new Vector2(sp.W / texture.GetWidth(), sp.H / texture.GetHeight());

        if (eyePair)
        {
            float halfW = texture.GetWidth() / 2f;
            for (int side = 0; side <= 1; side++)
            {
                var half = new AtlasTexture
                {
                    Atlas = texture,
                    Region = new Rect2(side * halfW, 0, halfW, texture.GetHeight()),
                };
                var layer = new Layer
                {
                    Node = new Sprite2D
                    {
                        Texture = half,
                        Position = basePos + new Vector2(side == 0 ? -sp.W / 4f : sp.W / 4f, 0),
                        Modulate = Godot.Colors.White.Lerp(tint, strength),
                        Scale = scaleFix,
                    },
                    BasePos = basePos + new Vector2(side == 0 ? -sp.W / 4f : sp.W / 4f, 0),
                    Bone = sp.Bone,
                    Item = item,
                    BaseZ = z,
                    Side = side,
                    ScaleFix = scaleFix,
                    GlintPivot = new Vector2(sp.X + sp.W * (side == 0 ? 0.09f : 0.91f) - Anchor.X,
                                             sp.Y + sp.H * 0.5f - Anchor.Y),
                };
                Register(layer);
            }
            return;
        }

        Register(new Layer
        {
            Node = new Sprite2D
            {
                Texture = texture,
                Position = basePos,
                Modulate = Godot.Colors.White.Lerp(tint, strength),
                Scale = scaleFix,
            },
            BasePos = basePos,
            Bone = sp.Bone,
            Item = item,
            BaseZ = z,
            ScaleFix = scaleFix,
        });
    }

    private static Godot.Color HtmlColor(string hex) =>
        string.IsNullOrEmpty(hex) ? Godot.Colors.White : Godot.Color.FromHtml(hex);

    private static Texture2D LoadTexture(SpriteDef sp)
    {
        if (string.IsNullOrEmpty(sp.Src))
            return null;
        var name = sp.Src.Substring(sp.Src.LastIndexOf('/') + 1);
        return ResourceLoader.Load<Texture2D>($"res://assets/parts/{name}");
    }

    // ---- pose (ported 1:1 from the original basePose) ----------------------

    private static float Smooth(float v)
    {
        v = Mathf.Clamp(v, 0f, 1f);
        return v * v * (3f - 2f * v);
    }

    private static float Mix(float a, float b, float k) => a + (b - a) * k;

    private Pose ComputePose(float time)
    {
        var p = Pose.Empty;
        if (_dance == "still")
            return p;

        float ph = time * Bpm * Tau;
        float a = _amplitude;
        float s = Mathf.Sin(ph);
        float c = Mathf.Cos(ph);
        bool step = _dance == "step";

        // default groove (sway / step)
        p.Root = Mathf.Sin(ph / 2) * 1.3f * a;
        p.X = Mathf.Sin(ph / 2) * (step ? 6 : 4) * a;
        p.Hop = -Mathf.Abs(s) * (step ? 2.8f : 1.5f) * a;
        p.Head = -Mathf.Sin(ph / 2 - 0.3f) * 2.2f * a;
        p.Hair = Mathf.Sin(ph / 2 - 0.6f) * 0.9f * a;
        p.ArmL = Mathf.Sin(ph / 2 + 0.3f) * 8 * a;
        p.ArmR = -Mathf.Sin(ph / 2 - 0.3f) * 8 * a;
        p.ForeL = -Mathf.Max(0, s) * 3 * a;
        p.ForeR = Mathf.Max(0, -s) * 3 * a;
        p.LegL = s * (step ? 3 : 1.5f) * a;
        p.LegR = -s * (step ? 3 : 1.5f) * a;
        p.KneeL = Mathf.Max(0, -s) * 1.5f * a;
        p.KneeR = -Mathf.Max(0, s) * 1.5f * a;

        switch (_dance)
        {
            case "wave":
            {
                float beat = time * Bpm % 16f;
                float k = Smooth((beat - 8) / 1.5f) * Smooth((16 - beat) / 1.5f);
                p.ArmL = Mix(p.ArmL, 22 + Mathf.Sin(ph) * 5 * a, k);
                p.ForeL = Mix(p.ForeL, -8, k);
                break;
            }
            case "twist":
                p.Root = Mathf.Sin(ph / 2) * 6 * a;
                p.Head = -p.Root * 0.7f;
                p.ArmL = 12 + s * 10 * a;
                p.ArmR = -12 - s * 10 * a;
                p.X = Mathf.Sin(ph / 2) * 7 * a;
                break;
            case "swing":
                p.ArmL = 20 + s * 18 * a;
                p.ArmR = -20 + s * 18 * a;
                p.ForeL = -12 * (0.5f + 0.5f * c);
                p.ForeR = 12 * (0.5f - 0.5f * c);
                p.Root = -s * 3 * a;
                break;
            case "hop":
                p.Hop = -Mathf.Abs(s) * 10 * a;
                p.ScaleY = 1 + Mathf.Sin(ph * 2) * 0.025f * a;
                p.ScaleX = 1 - Mathf.Sin(ph * 2) * 0.018f * a;
                p.ArmL = 12 + Mathf.Abs(s) * 14 * a;
                p.ArmR = -p.ArmL;
                p.LegL = s * 5 * a;
                p.LegR = -p.LegL;
                break;
            case "elastic":
                p.ScaleX = 1 + Mathf.Sin(ph / 2) * 0.075f * a;
                p.ScaleY = 1 - Mathf.Sin(ph / 2) * 0.07f * a;
                p.Head = Mathf.Sin(ph / 2 - 0.7f) * 4 * a;
                p.ArmL = 15 + s * 16 * a;
                p.ArmR = -15 - s * 16 * a;
                p.Hair = Mathf.Sin(ph / 2 - 1) * 2 * a;
                break;
            case "flip":
            {
                float turn = Mathf.Cos(ph / 8);
                p.ScaleX = Mathf.Abs(turn) < 0.035f ? (turn < 0 ? -0.035f : 0.035f) : turn;
                p.Root = Mathf.Sin(ph / 4) * 3 * a;
                p.Head = Mathf.Sin(ph / 2) * 2 * a;
                p.ArmL = 18;
                p.ArmR = -18;
                break;
            }
            case "breath":
                p.Root = 0;
                p.X = p.Hop = p.LegL = p.LegR = p.KneeL = p.KneeR = 0;
                p.ScaleY = 1 + Mathf.Sin(ph / 4) * 0.012f;
                p.ArmL = p.ArmR = 0;
                p.Head = Mathf.Sin(ph / 4) * 1.5f;
                p.Hair = Mathf.Sin(ph / 4 - 0.5f) * 0.5f;
                break;
            case "sidestep":
                p.X = Mathf.Sin(ph / 2) * 15 * a;
                p.Root = -Mathf.Sin(ph / 2) * 3 * a;
                p.LegL = Mathf.Sin(ph / 2) * 7 * a;
                p.LegR = p.LegL;
                p.ArmL = 12 + s * 6 * a;
                p.ArmR = -12 + s * 6 * a;
                p.Hop = -Mathf.Abs(s) * 3 * a;
                break;
            case "paw":
                p.ArmL = 49 + Mathf.Sin(ph / 2) * 8 * a;
                p.ArmR = -49 + Mathf.Sin(ph / 2) * 8 * a;
                p.ForeL = -36 - Mathf.Max(0, s) * 16 * a;
                p.ForeR = 36 + Mathf.Max(0, -s) * 16 * a;
                p.Head = Mathf.Sin(ph / 2) * 4 * a;
                p.Root = -Mathf.Sin(ph / 2) * 2 * a;
                break;
            case "disco":
            {
                float q = Mathf.Sin(ph / 2);
                p.ArmL = 42 + q * 30 * a;
                p.ArmR = -42 + q * 30 * a;
                p.ForeL = -10;
                p.ForeR = 10;
                p.Root = -q * 5 * a;
                p.Head = q * 3 * a;
                p.LegL = s * 4 * a;
                p.LegR = -s * 4 * a;
                break;
            }
            case "waltz":
            {
                float q = ph / 3;
                p.X = Mathf.Sin(q) * 12 * a;
                p.Root = Mathf.Sin(q) * 4 * a;
                p.Head = -Mathf.Sin(q - 0.4f) * 3 * a;
                p.ArmL = 25 + Mathf.Sin(q + 0.6f) * 9 * a;
                p.ArmR = -25 + Mathf.Sin(q - 0.6f) * 9 * a;
                p.LegL = Mathf.Sin(ph) * 5 * a;
                p.LegR = -Mathf.Sin(ph) * 5 * a;
                p.Hop = -Mathf.Pow(0.5f + 0.5f * Mathf.Sin(q * 3), 2) * 4 * a;
                break;
            }
            case "penguin":
            {
                float q = Mathf.Sin(ph / 2);
                p.Root = q * 5 * a;
                p.Head = -q * 2 * a;
                p.X = q * 8 * a;
                p.ArmL = 24 + Mathf.Abs(s) * 7 * a;
                p.ArmR = -p.ArmL;
                p.ForeL = p.ForeR = 0;
                p.LegL = q * 6 * a;
                p.LegR = q * 6 * a;
                p.Hop = -Mathf.Abs(s) * 2 * a;
                break;
            }
            case "nod":
                p.Head = Mathf.Sin(ph) * 5 * a;
                p.Root = Mathf.Sin(ph / 2) * 2 * a;
                p.ArmL = 14 + s * 9 * a;
                p.ArmR = -14 - s * 9 * a;
                p.ForeL = -6;
                p.ForeR = 6;
                p.Hop = -Mathf.Abs(s) * 4 * a;
                p.LegL = s * 5 * a;
                p.LegR = -s * 5 * a;
                break;
            case "starjump":
            {
                float k = 0.5f + 0.5f * s;
                p.ArmL = 14 + 78 * k * a;
                p.ArmR = -p.ArmL;
                p.LegL = -(2 + 13 * k) * a;
                p.LegR = -p.LegL;
                p.Hop = -Mathf.Abs(c) * 15 * a;
                p.Root = Mathf.Sin(ph / 2) * 3 * a;
                p.Head = -p.Root * 0.6f;
                break;
            }
            case "windmill":
                p.ArmL = 65 + Mathf.Sin(ph / 2) * 70 * a;
                p.ArmR = -65 + Mathf.Cos(ph / 2) * 70 * a;
                p.Root = Mathf.Sin(ph / 2) * 7 * a;
                p.Head = -p.Root * 0.5f;
                p.X = Mathf.Sin(ph / 2) * 16 * a;
                p.Hop = -Mathf.Abs(s) * 6 * a;
                p.LegL = s * 8 * a;
                p.LegR = -s * 8 * a;
                break;
            case "rocket":
            {
                float k = Mathf.Abs(s);
                p.ArmL = 70 + 34 * k * a;
                p.ArmR = -p.ArmL;
                p.Hop = -k * 20 * a;
                p.Root = Mathf.Sin(ph / 2) * 5 * a;
                p.Head = -Mathf.Sin(ph / 2) * 4 * a;
                p.LegL = -k * 9 * a;
                p.LegR = k * 9 * a;
                break;
            }
            case "shuffle":
                p.ArmL = 30 + s * 35 * a;
                p.ArmR = -30 + s * 35 * a;
                p.LegL = s * 18 * a;
                p.LegR = -s * 18 * a;
                p.KneeL = Mathf.Max(0, -s) * 10 * a;
                p.KneeR = -Mathf.Max(0, s) * 10 * a;
                p.Root = -s * 7 * a;
                p.X = Mathf.Sin(ph / 2) * 20 * a;
                p.Hop = -Mathf.Abs(s) * 7 * a;
                break;
            case "groove":
            {
                float q = Mathf.Sin(ph / 2);
                p.Root = q * 9 * a;
                p.Head = -q * 5 * a;
                p.X = q * 22 * a;
                p.ArmL = 35 + Mathf.Sin(ph / 2 + 0.7f) * 50 * a;
                p.ArmR = -35 + Mathf.Sin(ph / 2 - 0.7f) * 50 * a;
                p.LegL = s * 9 * a;
                p.LegR = -s * 9 * a;
                p.Hop = -Mathf.Abs(s) * 8 * a;
                break;
            }
        }
        p.ForeL = p.ForeR = 0; // forearms are fused in this build (as upstream)
        return p;
    }

    // ---- per-frame application ---------------------------------------------

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _time += dt;
        _clock += dt;

        // drop expired expression overrides and rebuild the ghost layers once
        bool expired = false;
        foreach (var kvp in _overrides)
            if (kvp.Value.Until < _clock)
                expired = true;
        if (expired)
        {
            var dead = new List<string>();
            foreach (var kvp in _overrides)
                if (kvp.Value.Until < _clock)
                    dead.Add(kvp.Key);
            foreach (var k in dead)
                _overrides.Remove(k);
            ApplyPendingOverrides();
        }

        var p = LivePose ?? ComputePose(_time);
        p.Head += HeadPetTilt; // head-pat lean rides on top of any pose

        // lift-and-shake physics blended on top of the dance / live pose
        if (_liftHeld || _liftBank.Weight > 0f)
        {
            LiftPhysics.Step(_liftBank, _liftX, _liftY, _liftSize, _clock, _liftHeld);
            LiftPhysics.Apply(_liftBank, ref p);
        }

        float sx = p.ScaleX;
        float sy = p.ScaleY;
        if (_squashT >= 0f)
        {
            _squashT += (float)delta;
            float t = _squashT / 0.5f;
            if (t >= 1f)
                _squashT = -1f;
            else
            {
                float decay = 1f - t;
                float wobble = Mathf.Sin(t * 18f) * 0.35f + 1f;
                sy *= 1f - 0.14f * _squashPower * decay * wobble;
                sx *= 1f + 0.10f * _squashPower * decay * wobble;
            }
        }

        _stage.Position = new Vector2(p.X, p.Hop) * FitScale;
        _stage.Scale = new Vector2(FitScale * sx, FitScale * sy);
        _rootRot.Rotation = Mathf.DegToRad(p.Root);

        // ---- eye dynamics (live mode): eyelid amounts + glint spring ----
        if (LiveEyes is { } eyeIn)
        {
            // fully closed (both eyes): swap to the STATIC closed-eyes sheet
            // (upstream draws l54 at p>=0.66) — a static sheet rides the head
            // bone and cannot jitter, unlike animated squash. Release is
            // sticky: tilted heads make the tracker confidently report an
            // open eye for a closed one, so require BOTH eyes to stay clearly
            // open-worthy for a while before leaving the static sheet.
            bool bothClosed = _l54Shown
                ? !(_eyeLowTime[0] > 0.35f && _eyeLowTime[1] > 0.35f)
                : _eyelidAmt[0] > 0.66f && _eyelidAmt[1] > 0.66f;
            if (bothClosed != _l54Shown)
            {
                _l54Shown = bothClosed;
                // hold for as long as the state holds: a short-lived override
                // would expire mid-blink and flash the squashed eyes back
                if (bothClosed)
                    _overrides["eyes"] = ("l54", _clock + 999f);
                else
                    _overrides.Remove("eyes");
                ApplyPendingOverrides();
            }

            float blinkFilter = 1f - Mathf.Exp(-dt / 0.08f);
            // original app semantics: blink = MIN(both eyes) — an eye only
            // counts as closed when BOTH report closure. A tilted head makes
            // the tracker over-report one side; the healthy side vetoes it.
            float rawBinocular = Mathf.Min(eyeIn.BlinkL, eyeIn.BlinkR);
            if (rawBinocular < 0.35f)
                rawBinocular = 0f; // noise floor: real blinks shoot well past this
            for (int side = 0; side <= 1; side++)
            {
                _blinkSmooth[side] += (rawBinocular - _blinkSmooth[side]) * blinkFilter;
                float score = _blinkSmooth[side];

                // sustained thresholds: the score hovers around the edges
                // when the head is tilted (upstream's eye features weaken),
                // so a state flip requires the score to stay past its
                // threshold for a while — otherwise the squashed eye flaps
                // original thresholds (upstream eyelids): close ≥0.64, open ≤0.50
                if (score >= 0.64f) _eyeHighTime[side] += dt; else _eyeHighTime[side] = 0f;
                if (score <= 0.50f) _eyeLowTime[side] += dt; else _eyeLowTime[side] = 0f;
                if (score >= 0.45f && score < 0.64f) _eyeMidTime[side] += dt;
                else _eyeMidTime[side] = 0f;
                float target = _eyeWasClosed[side]
                    ? (_eyeLowTime[side] > 0.15f ? 0f : 1f)   // hold closed firmly
                    : (_eyeHighTime[side] > 0.06f ? 1f
                       : _eyeMidTime[side] > 0.25f ? 0.5f      // sustained squint only
                       : 0f);

                float rate = target > _eyelidAmt[side] ? 1f / 0.075f : 1f / 0.110f;
                _eyelidAmt[side] += Mathf.Clamp(target - _eyelidAmt[side], -rate * dt, rate * dt);
                if (_eyeWasClosed[side] && _eyelidAmt[side] <= 0.001f)
                {
                    _eyeOpenedAt[side] = _clock;
                    _eyeWasClosed[side] = false;
                }
                else if (_eyelidAmt[side] > 0.65f)
                    _eyeWasClosed[side] = true;
            }
            float kick = Mathf.Clamp((eyeIn.HeadX - _glintLastX) + (eyeIn.HeadY - _glintLastY),
                                     -0.15f, 0.15f);
            _glintLastX = eyeIn.HeadX;
            _glintLastY = eyeIn.HeadY;
            _glintVelocity += kick * 16f + (-_glintStretch * 180f - _glintVelocity * 16f) * dt;
            _glintStretch = Mathf.Clamp(_glintStretch + _glintVelocity * dt, -0.22f, 0.22f);
        }
        else
        {
            _eyelidAmt[0] = _eyelidAmt[1] = 0f;
            _eyeWasClosed[0] = _eyeWasClosed[1] = false;
        }

        bool armUp = Mathf.Max(p.ArmL, -p.ArmR) > 55f;
        foreach (var layer in _active)
        {
            if (!IsInstanceValid(layer.Node))
                continue; // guard against nodes freed this frame
            var n = layer.Node;
            n.Position = layer.BasePos;
            n.Rotation = 0f;
            n.Scale = layer.ScaleFix;
            ApplyBoneChain(n, layer.Bone, in p);
            if (layer.Side >= 0 && LiveEyes is { } eye)
                ApplyEyeTransform(layer, n, eye);
            n.ZIndex = PaintZ(layer, armUp);
        }
    }

    /// <summary>
    /// Per-side eye transform on top of the bone chain: eyelid squash, eyeball
    /// follow, and (for highlights) the glint jelly — ported from the
    /// original's eyeLayer + highlightJelly. The squash scales the sprite's
    /// LOCAL vertical axis (Sprite2D scale applies after rotation), so when
    /// the head is tilted both eyes squash along the same head-tilted line
    /// instead of a global horizontal one.
    /// </summary>
    private void ApplyEyeTransform(Layer layer, Sprite2D n, in LiveEyeInput eye)
    {
        int side = layer.Side;
        float amt = _eyelidAmt[side];
        float p = amt * amt * (3f - 2f * amt);
        float openness = 1f - p;

        // eyeball follow: small horizontal shift
        n.Position += new Vector2(eye.HeadX * 13f * openness, 0f);
        // eyelid squash along the local (head-rotated) vertical axis
        n.Scale = new Vector2(n.Scale.X, n.Scale.Y * (0.03f + 0.97f * openness));

        if (layer.Item.Category == "eyeHighlight")
        {
            float bounce = BounceAge(_clock - _eyeOpenedAt[side]);
            float a = Mathf.Clamp(_glintStretch + Mathf.Sin(_time * 3f) * 0.035f
                                  + bounce * 0.98f, -0.50f, 0.60f);
            a *= openness; // jelly off when closed (upstream hides highlights)
            n.Position += new Vector2(-a * 3f, 0f);
            // glint jelly along the same local axes
            n.Scale = new Vector2(n.Scale.X * (1f + a * 0.18f),
                                  n.Scale.Y * (1f - a * 1.08f));
        }
    }

    /// <summary>sin(age*18)*exp(-age*4.5) for age in [0,1.2), else 0 (jelly rebound).</summary>
    private static float BounceAge(float age) =>
        age >= 0f && age < 1.2f ? Mathf.Sin(age * 18f) * Mathf.Exp(-age * 4.5f) : 0f;

    private static void RotAround(Sprite2D n, Vector2 pivotLocal, float deg)
    {
        float rad = Mathf.DegToRad(deg);
        n.Rotation += rad;
        n.Position = pivotLocal + (n.Position - pivotLocal).Rotated(rad);
    }

    private static void ApplyBoneChain(Sprite2D n, string bone, in Pose p)
    {
        switch (bone)
        {
            case "head":
                RotAround(n, HeadPivot - Anchor, p.Head);
                break;
            case "hair":
                RotAround(n, HeadPivot - Anchor, p.Head);
                RotAround(n, HairPivot - Anchor, p.Hair);
                break;
            case "tail":
                RotAround(n, RootPivot - Anchor, p.Hair * 2f);
                break;
            case "armL":
                RotAround(n, ArmLPivot - Anchor, p.ArmL);
                break;
            case "armR":
                RotAround(n, ArmRPivot - Anchor, p.ArmR);
                break;
            case "legL":
                RotAround(n, HipLPivot - Anchor, p.LegL);
                break;
            case "legLLower":
                RotAround(n, HipLPivot - Anchor, p.LegL);
                RotAround(n, KneeLPivot - Anchor, p.KneeL);
                break;
            case "legR":
                RotAround(n, HipRPivot - Anchor, p.LegR);
                break;
            case "legRLower":
                RotAround(n, HipRPivot - Anchor, p.LegR);
                RotAround(n, KneeRPivot - Anchor, p.KneeR);
                break;
        }
    }

    /// <summary>
    /// Dynamic z-order (ported paintRank): when an arm lifts past 55°,
    /// hands/sleeves/top/dress/topAcc jump above everything else in that
    /// order; otherwise the base doubled order (with dress-sleeve rule) wins.
    /// </summary>
    private int PaintZ(Layer layer, bool armUp)
    {
        // Godot ZIndex must stay within [-4096, 4096]; base orders are <= ~1000,
        // arm-up bands live just below the ceiling.
        if (armUp)
        {
            if (layer.Item.Id is "l28" or "l29")
                return 3000 + layer.BaseZ / 4;
            switch (layer.Item.Category)
            {
                case "sleeves": return 3800;
                case "top":
                case "dress": return 3801;
                case "topAcc": return 3802;
            }
        }
        return layer.BaseZ;
    }

    // ---- controls -----------------------------------------------------------

    public void PlayMotion(string id)
    {
        _dance = id;
        _time = 0f;
    }

    public string Dance => _dance;

    /// <summary>Debug: current pose snapshot for self-check logs.</summary>
    public string DebugPose()
    {
        var p = ComputePose(_time);
        string armInfo = "";
        foreach (var l in _active)
        {
            if (l.Bone == "armL")
            {
                armInfo = $" | armL sprite rot={Mathf.RadToDeg(l.Node.Rotation):F1} " +
                          $"pos=({l.Node.Position.X:F0},{l.Node.Position.Y:F0}) base=({l.BasePos.X:F0},{l.BasePos.Y:F0})";
                break;
            }
        }
        return $"live={LivePose != null} dance={_dance} t={_time:F2} armL={p.ArmL:F1} armR={p.ArmR:F1} " +
               $"root={p.Root:F1} hop={p.Hop:F1} head={p.Head:F1} sx={p.ScaleX:F2}{armInfo}" +
               $" | lift w={_liftBank.Weight:F2} held={_liftHeld}" +
               $" jArmL={_liftBank.Joints["armL"].Angle:F2} jRoot={_liftBank.Joints["root"].Angle:F2}" +
               $" jLegL={_liftBank.Joints["legL"].Angle:F2} jLegR={_liftBank.Joints["legR"].Angle:F2}" +
               $" jKneeL={_liftBank.Joints["kneeL"].Angle:F2}" +
               $" | eyes lid=[{_eyelidAmt[0]:F2},{_eyelidAmt[1]:F2}] glint={_glintStretch:F3}";
    }

    public float Amplitude
    {
        get => _amplitude;
        set => _amplitude = Mathf.Clamp(value, 0.3f, 1f);
    }

    public void Squash(float power = 1f)
    {
        _squashT = 0f;
        _squashPower = power;
    }

    /// <summary>
    /// Feed the drag pointer while the pet is held; physics drives limbs and
    /// root from the pointer's acceleration. Any fixed coordinate space works
    /// (only deltas matter); size is the normalization scale of that space.
    /// Call with held=false on release, then stop calling.
    /// </summary>
    public void SetLiftInput(Vector2? point, bool held, float size = 225f)
    {
        _liftX = point?.X;
        _liftY = point?.Y;
        _liftSize = size;
        _liftHeld = held;
    }

    // ---- eyes / expressions -------------------------------------------------

    private void OnAutoBlink()
    {
        _blinkTimer.WaitTime = 2.4f + GD.Randf() * 3.6f;
        if (_captureBlinkHeld || LiveEyes != null)
            return; // in live mode blinking is driven by eyelids, not sprite swaps
        SetOverride("eyes", BlinkEyesId, 0.14f);
    }

    /// <summary>Temporarily swap one category (e.g. eyes) to another item.</summary>
    public void SetOverride(string category, string itemId, float duration)
    {
        _overrides[category] = (itemId, _clock + duration);
        ApplyPendingOverrides();
    }

    /// <summary>Happy face used for pats and treats: 笑眼 + 张嘴.</summary>
    public void ReactHappy(float duration = 1.4f)
    {
        SetOverride("eyes", "l55", duration);
        SetOverride("mouth", "l56", duration);
    }

    /// <summary>Surprised face used when dropped.</summary>
    public void ReactWow(float duration = 1.0f)
    {
        SetOverride("eyes", "l53", duration);
        SetOverride("mouth", "l57", duration);
    }

    /// <summary>v2 face capture: hold eyes closed while blink score is high.</summary>
    public void SetCaptureBlink(bool closed) =>
        SetCaptureEyes(closed ? BlinkEyesId : null);

    /// <summary>
    /// Face capture eye hold: itemId null = release (auto blink resumes);
    /// otherwise hold that eye item and suppress auto blinking.
    /// </summary>
    public void SetCaptureEyes(string itemId)
    {
        _captureBlinkHeld = itemId != null;
        if (itemId == null)
            _overrides.Remove("eyes");
        else
            _overrides["eyes"] = (itemId, _clock + 999f);
        ApplyPendingOverrides();
    }

    /// <summary>Re-apply live overrides: hide base layers, show override layers.</summary>
    private void ApplyPendingOverrides()
    {
        foreach (var list in _byCategory.Values)
            foreach (var l in list)
                l.Node.Visible = true;
        if (_byCategory.TryGetValue("_override", out var ghosts))
        {
            foreach (var g in ghosts)
            {
                g.Node.QueueFree();
                _active.Remove(g); // drop the reference so _Process never sees a freed node
            }
            _byCategory.Remove("_override");
        }

        var ghostLayers = new List<Layer>();
        foreach (var (category, (itemId, until)) in _overrides)
        {
            if (until < _clock || string.IsNullOrEmpty(itemId))
                continue;
            if (_byCategory.TryGetValue(category, out var baseLayers))
                foreach (var l in baseLayers)
                    l.Node.Visible = false;
            if (!PartCatalog.Items.TryGetValue(itemId, out var item))
                continue;
            foreach (var sid in item.Sprites)
            {
                if (!PartCatalog.Sprites.TryGetValue(sid, out var sp))
                    continue;
                var tex = LoadTexture(sp);
                Vector2 fix = Vector2.One;
                if (tex != null && tex.GetWidth() > 0 && tex.GetHeight() > 0)
                    fix = new Vector2(sp.W / tex.GetWidth(), sp.H / tex.GetHeight());
                var layer = new Layer
                {
                    Node = new Sprite2D
                    {
                        Texture = tex,
                        Position = new Vector2(sp.X + sp.W / 2f - Anchor.X,
                                               sp.Y + sp.H / 2f - Anchor.Y),
                        Scale = fix,
                        ZIndex = sp.Order * 2,
                    },
                    BasePos = new Vector2(sp.X + sp.W / 2f - Anchor.X,
                                          sp.Y + sp.H / 2f - Anchor.Y),
                    Bone = sp.Bone,
                    Item = item,
                    BaseZ = sp.Order * 2,
                    ScaleFix = fix,
                };
                _layers.AddChild(layer.Node);
                _active.Add(layer);
                ghostLayers.Add(layer);
            }
        }
        if (ghostLayers.Count > 0)
            _byCategory["_override"] = ghostLayers;
    }

    // ---- geometry helpers ----------------------------------------------------

    /// <summary>Convert PSD canvas coordinates to window content coordinates.</summary>
    public Vector2 CanvasToWindow(Vector2 canvasPoint, Vector2 actorWindowPos) =>
        actorWindowPos + (canvasPoint - Anchor) * FitScale;

    public Vector2 WindowToCanvas(Vector2 windowPoint, Vector2 actorWindowPos) =>
        (windowPoint - actorWindowPos) / FitScale + Anchor;

    public Rect2 HeadCanvasRect()
    {
        const float margin = 90f;
        if (PartCatalog.Items.TryGetValue("l49", out var face) && face.Sprites.Count > 0
            && PartCatalog.Sprites.TryGetValue(face.Sprites[0], out var sp))
            return new Rect2(sp.X - margin, sp.Y - margin, sp.W + margin * 2, sp.H + margin * 2);
        return new Rect2(PartCatalog.CanvasW / 2f - 400, 150, 800, 800);
    }

    public override void _Draw()
    {
        // debug helper kept minimal: nothing drawn by default
    }

    /// <summary>Strip-union silhouette polygon in window coordinates.</summary>
    public Godot.Collections.Array<Vector2> ComputeSilhouette(Vector2 actorWindowPos)
    {
        var poly = new Godot.Collections.Array<Vector2>();
        var bands = new List<(float y0, float y1, List<(float a, float b)>)>();
        float band = 48f;
        for (float y = CanvasBounds.Position.Y; y < CanvasBounds.End.Y; y += band)
        {
            var row = new List<(float, float)>();
            foreach (var layer in _active)
            {
                if (layer.Node.Texture == null || !layer.Node.Visible)
                    continue;
                var w = layer.Node.Texture.GetWidth();
                var h = layer.Node.Texture.GetHeight();
                var cx = layer.BasePos.X + Anchor.X;
                var cy = layer.BasePos.Y + Anchor.Y;
                var r = new Rect2(cx - w / 2f, cy - h / 2f, w, h);
                if (r.End.Y > y && r.Position.Y < y + band)
                    row.Add((r.Position.X, r.End.X));
            }
            if (row.Count == 0)
                continue;
            row.Sort((p, q) => p.Item1.CompareTo(q.Item1));
            var merged = new List<(float, float)> { row[0] };
            for (int i = 1; i < row.Count; i++)
            {
                var last = merged[^1];
                if (row[i].Item1 <= last.Item2)
                    merged[^1] = (last.Item1, Mathf.Max(last.Item2, row[i].Item2));
                else
                    merged.Add(row[i]);
            }
            bands.Add((y, y + band, merged));
        }

        Vector2 ToWindow(float x, float y) => CanvasToWindow(new Vector2(x, y), actorWindowPos);

        foreach (var (y0, _, merged) in bands)
            if (merged.Count > 0)
                poly.Add(ToWindow(merged[0].Item1, y0));
        for (int i = bands.Count - 1; i >= 0; i--)
        {
            var (y0, y1, merged) = bands[i];
            if (merged.Count > 0)
                poly.Add(ToWindow(merged[^1].Item2, y1));
        }
        return poly;
    }
}
