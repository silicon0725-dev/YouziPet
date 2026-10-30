using System;
using Godot;

namespace YuzuPet.Capture;

/// <summary>
/// IFaceCapture over OpenSeeFace's binary UDP protocol (BSD-2, emilianavt).
/// Packet layout (little endian), from facetracker.py's send code:
///   f64 time, i32 faceId, f32 camW, f32 camH, f32 eyeOpenR, f32 eyeOpenL,
///   u8 got3D, f32 fit3DError, 4xf32 quaternion, 3xf32 rawEuler,
///   3xf32 translation, 68xf32 confidence, 68x(2xf32) points,
///   70x(3xf32) points3D, 14xf32 features
///     (EyeL, EyeR, BrStL, BrUDL, BrQL, BrStR, BrUDR, BrQR,
///      MCUDL, MCIL, MCUDR, MCIR, MouthOpen, MouthWide)
/// Mapped into the pet's FaceFrame; roll comes from rawEuler.Z (OpenCV
/// convention: x=pitch, y=yaw, z=roll).
/// </summary>
public partial class FaceCaptureOpenSee : Node, IFaceCapture
{
    public event Action<FaceFrame> FrameReceived;
    public bool IsRunning { get; private set; }

    [Export] public int Port { get; set; } = 7901;

    private PacketPeerUdp _udp;
    private float _camW = 640f, _camH = 360f;
    private int _debugCounter;

    // startup zero-point calibration state (see Parse)
    private readonly System.Collections.Generic.List<float> _calibRoll = new();
    private readonly System.Collections.Generic.List<float> _calibPitch = new();
    private float _biasRoll, _biasPitch;
    private bool _calibrated;
    private int _calibSeen; // frames inspected (bounded calibration window)
    private float _lastBlinkL, _lastBlinkR; // eye-region low-confidence freeze

    private const int FeaturesOffset = 73 + 68 * 4 + 68 * 8 + 70 * 12;
    private const int EulerOffset = 8 + 4 + 8 + 4 + 4 + 1 + 4 + 16; // -> 3 floats
    private const int PointsOffset = EulerOffset + 12 + 12 + 68 * 4;

    public void Start()
    {
        if (IsRunning)
            return;
        _udp = new PacketPeerUdp();
        var err = _udp.Bind(Port, "127.0.0.1");
        if (err != Error.Ok)
        {
            GD.PushError($"OpenSeeFace capture: cannot bind udp {Port} ({err})");
            return;
        }
        IsRunning = true;
        GD.Print($"OpenSeeFace capture: listening on udp {Port}");
    }

    public void Stop()
    {
        IsRunning = false;
        _udp?.Close();
        _udp = null;
        // next session recalibrates to the then-current posture
        _calibrated = false;
        _calibRoll.Clear();
        _calibPitch.Clear();
        _calibSeen = 0;
        _biasRoll = _biasPitch = 0f;
    }

    public override void _Process(double delta)
    {
        if (!IsRunning || _udp == null)
            return;
        while (_udp.GetAvailablePacketCount() > 0)
        {
            var packet = _udp.GetPacket();
            var f = Parse(packet);
            if (f.HasValue)
                FrameReceived?.Invoke(f.Value);
        }
    }

    private static float ReadF(byte[] b, int o) => BitConverter.ToSingle(b, o);

    private FaceFrame? Parse(byte[] b)
    {
        // 1785 bytes: 8+4+8+8+1+4+16+12+12+272+544+840+56
        if (b.Length < 1785)
            return null;
        int o = 8 + 4;
        _camW = ReadF(b, o);
        _camH = ReadF(b, o + 4);
        float eyeOpenR = ReadF(b, o + 8);
        float eyeOpenL = ReadF(b, o + 12);
        // got3D at o+16; fit3D at o+17; quaternion at o+21..37; euler at +37
        int eo = EulerOffset;
        float rawX = ReadF(b, eo);
        float rawYaw = ReadF(b, eo + 4);
        float rawZ = ReadF(b, eo + 8);
        if (!Mathf.IsFinite(rawZ) || !Mathf.IsFinite(rawX))
            return null;

        // OpenSeeFace's raw euler zero-point is NOT a straight face: upstream
        // converts with rotation.z = (z-90)%360, rotation.x = -(x+180)%360
        // (OpenSee.cs). Rebase around that so a level face reads ~0.
        float Roll() { float d = Mathf.PosMod(rawZ - 90f, 360f); return d > 180f ? d - 360f : d; }
        float Pitch() { float d = Mathf.PosMod(rawX + 180f, 360f); return d > 180f ? d - 360f : d; }
        float roll = Roll();
        float pitch = Pitch();

        // nose tip (landmark 30) normalized around the frame center
        int po = PointsOffset + 30 * 8;
        float nx = ReadF(b, po);
        float ny = ReadF(b, po + 4);
        float x = Mathf.Clamp((nx / Mathf.Max(1f, _camW) - 0.5f) * 2f, -1f, 1f);
        float y = Mathf.Clamp((ny / Mathf.Max(1f, _camH) - 0.5f) * 2f, -1f, 1f);

        // features block
        int fo = FeaturesOffset;
        float eyeFeatL = ReadF(b, fo);
        float eyeFeatR = ReadF(b, fo + 4);
        float mouthCornerUpDownL = ReadF(b, fo + 8);
        float mouthCornerUpDownR = ReadF(b, fo + 40);
        float mouthOpen = ReadF(b, fo + 48);

        // eye feature is -1(closed)..0(normal); blink score = clamp(-v, 0..1),
        // blended with the eyeOpen probability for robustness
        float blinkL = Mathf.Clamp(Mathf.Max(1f - eyeOpenL, -eyeFeatL), 0f, 1f);
        float blinkR = Mathf.Clamp(Mathf.Max(1f - eyeOpenR, -eyeFeatR), 0f, 1f);

        // eye-region landmark confidence (points 36..47). While the head is
        // tilted the tracker's eye features degrade and report "open" for a
        // visibly closed eye — hold the last known blink scores until the
        // region is trustworthy again, instead of feeding the flap
        float confSum = 0f;
        for (int i = 36; i <= 47; i++)
            confSum += ReadF(b, 73 + i * 4); // confidence block follows the header
        float eyeConf = confSum / 12f;
        if (eyeConf < 0.55f)
        {
            blinkL = _lastBlinkL;
            blinkR = _lastBlinkR;
        }
        else
        {
            _lastBlinkL = blinkL;
            _lastBlinkR = blinkR;
        }

        // smile combines both mouth corners the way the original does:
        // 70% of the stronger side + 30% of the weaker (needs both to agree)
        float smileStrong = Mathf.Max(mouthCornerUpDownL, mouthCornerUpDownR);
        float smileWeak = Mathf.Min(mouthCornerUpDownL, mouthCornerUpDownR);
        float smile = Mathf.Clamp(smileStrong * 0.7f + smileWeak * 0.3f, -1f, 1f);
        float mouth = Mathf.Clamp(mouthOpen, 0f, 1f);

        // startup calibration: collect a neutral-face median as this setup's
        // zero point. Only frames with eyes open and a quiet mouth count —
        // blinking/talking at switch-on would poison the baseline and every
        // later pose would read as tilted. Bounded window so it always ends.
        if (!_calibrated)
        {
            _calibSeen++;
            float blinkMax = Mathf.Max(blinkL, blinkR);
            // "neutral" just means not clearly blinking/talking — the blink
            // score's resting level drifts with lighting, so keep this loose
            bool neutral = blinkMax < 0.55f && mouth < 0.30f;
            if (neutral)
            {
                _calibRoll.Add(roll);
                _calibPitch.Add(pitch);
            }
            if (_calibRoll.Count >= 50 || _calibSeen >= 150)
            {
                if (_calibRoll.Count == 0)
                {
                    _calibrated = true; // degenerate: no baseline, raw values
                    GD.Print("OSF calibrated: no neutral samples, zero bias");
                }
                else
                {
                    _calibRoll.Sort();
                    _calibPitch.Sort();
                    _biasRoll = _calibRoll[_calibRoll.Count / 2];
                    _biasPitch = _calibPitch[_calibPitch.Count / 2];
                    _calibrated = true;
                    GD.Print($"OSF calibrated ({_calibRoll.Count} samples): " +
                             $"roll bias {_biasRoll:F1}, pitch bias {_biasPitch:F1}");
                }
            }
        }

        // hold zero while calibrating so the pet doesn't lurch on startup;
        // positive roll tilts the same way in OpenCV-style euler and Godot's
        // y-down 2D space (flip the sign if the direction ever feels mirrored)
        float outRoll = _calibrated ? roll - _biasRoll : 0f;
        float outPitch = _calibrated ? pitch - _biasPitch : 0f;

        // throttle debug print (~once per 1s) so the live values can be read
        // from the log while calibrating
        if (++_debugCounter % 30 == 0)
            GD.Print($"OSF roll={outRoll:F1} pitch={outPitch:F1} yaw={rawYaw:F1} " +
                     $"blink=({blinkL:F2},{blinkR:F2}) mouth={mouth:F2} smile={smile:F2} " +
                     $"eyeConf={eyeConf:F2}");

        return new FaceFrame(
            x, y,
            outRoll,
            Mathf.Clamp(outPitch / 60f, -1f, 1f),
            blinkL, blinkR,
            mouth,
            Mathf.Max(0f, smile));
    }
}
