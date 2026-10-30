using System;
using Godot;
using YuzuPet.Capture;

namespace YuzuPet.Pet;

/// <summary>
/// Drives the pet from face capture frames, mirroring the original app's
/// live mode: 110ms smoothing, 190ms body follow, hair lag, the live pose
/// mapping (head=roll, body lean, arm counter-swing) and hysteresis on
/// blink / smile / mouth expressions.
/// </summary>
public partial class FaceRig : Node
{
    private PetActor _actor;
    private IFaceCapture _capture;
    private FaceFrame _lastFrame;
    private bool _hasFrame;
    private float _frameAge = 99f;

    // smoothed inputs (the original `smooth` bank)
    private float _sX, _sY, _sRoll, _sPitch, _sHeadX, _sHeadPitch;
    // slower body follow
    private float _bX, _bPitch, _bRoll;
    // hair lag
    private float _hRoll, _hX;

    private float _time;
    private bool _smiling;
    private bool _mouthOpen;
    private float _exprRefresh;

    public bool Active { get; private set; }

    public void Attach(PetActor actor, IFaceCapture capture)
    {
        _actor = actor;
        if (_capture != null)
            _capture.FrameReceived -= OnFrame;
        _capture = capture;
        _capture.FrameReceived += OnFrame;
    }

    public void Activate()
    {
        Active = true;
        _hasFrame = false;
        _frameAge = 99f;
    }

    public void Deactivate()
    {
        Active = false;
        if (_actor == null)
            return;
        _actor.LivePose = null;
        _actor.LiveEyes = null;
        _actor.SetCaptureEyes(null);
        _actor.SetOverride("eyes", null, 0f);
        _actor.SetOverride("mouth", null, 0f);
        _smiling = _mouthOpen = false;
    }

    private void OnFrame(FaceFrame f)
    {
        _lastFrame = f;
        _hasFrame = true;
        _frameAge = 0f;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _time += dt;
        if (!Active || _actor == null)
            return;

        _frameAge += dt;
        if (!_hasFrame || _frameAge > 0.7f)
        {
            // stream died: release the pet back to its dance
            _actor.LivePose = null;
            _actor.LiveEyes = null;
            return;
        }

        var f = _lastFrame;

        // ---- smoothing (k = 1 - exp(-dt/110ms)); roll clamps to ±12° ----
        float k = 1f - Mathf.Exp(-dt / 0.110f);
        // soft dead-zone on roll/pitch: tracker jitter within ±1.2° would
        // otherwise vibrate the (squashed) eye line while the head is tilted
        static float Dead(float v, float zone)
        {
            float a = Mathf.Abs(v);
            return a <= zone ? 0f : Mathf.Sign(v) * (a - zone);
        }
        _sX += (Mathf.Clamp(f.X, -1f, 1f) - _sX) * k;
        _sY += (Mathf.Clamp(f.Y, -1f, 1f) - _sY) * k;
        _sRoll += (Mathf.Clamp(Dead(f.Roll, 1.2f), -12f, 12f) - _sRoll) * k;
        _sPitch += (Mathf.Clamp(Dead(f.Pitch, 0.02f), -1f, 1f) - _sPitch) * k;
        _sHeadX += (Mathf.Clamp(Dead(f.X, 0.03f), -1f, 1f) - _sHeadX) * k;  // headX ?? x
        _sHeadPitch += (Mathf.Clamp(f.Pitch, -1f, 1f) - _sHeadPitch) * k;

        // ---- body follow (190ms) ----
        float kb = 1f - Mathf.Exp(-dt / 0.190f);
        _bX += (_sHeadX - _bX) * kb;
        _bPitch += (_sHeadPitch - _bPitch) * kb;
        _bRoll += (_sRoll - _bRoll) * kb;

        // ---- hair lag (0.09 per frame at ~60fps) ----
        float kh = Mathf.Clamp(0.09f * 60f * dt, 0f, 1f);
        _hRoll += (_sRoll - _hRoll) * kh;
        _hX += (_sHeadX - _hX) * kh;

        // ---- live pose (ported 1:1 from mode==='live') ----
        var p = Pose.Empty;
        p.Head = _sRoll;
        p.Hair = Mathf.Sin(_time * 1.7f) * 0.35f;
        p.ArmL = 2f;
        p.ArmR = -2f;
        p.Root = _bRoll * 0.18f + _bX * 1.6f;
        p.X = 0f;
        p.Hop = Mathf.Sin(_time * 1.5f) * 0.3f;
        p.ScaleY = 1f;
        p.ArmL -= _bPitch * 3f + _bX * 2f;
        p.ArmR += _bPitch * 3f - _bX * 2f;
        p.Hair = Mathf.Clamp((_hRoll - _sRoll) * 0.7f + (_hX - _sHeadX) * 2.4f, -4.5f, 4.5f);
        p.ArmL += Mathf.Sin(_time * 1.3f) * 0.5f;
        p.ArmR -= Mathf.Sin(_time * 1.3f) * 0.5f;
        _actor.LivePose = p;

        // ---- eyes: drive per-side eyelids + glint via LiveEyes (no sprite swaps) ----
        _actor.LiveEyes = new PetActor.LiveEyeInput
        {
            HeadX = _sX,
            HeadY = _sY,
            Roll = _sRoll,
            BlinkL = f.BlinkLeft,
            BlinkR = f.BlinkRight,
        };

        // ---- expressions with hysteresis ----
        _exprRefresh -= dt;
        // noise floors: a relaxed face still reads ~±0.3 smile / ~0.05 mouth;
        // cut anything below real-expression amplitude so the pet stays calm
        float smile = Mathf.Abs(f.Smile) < 0.30f ? 0f : f.Smile;
        float mouth = f.Mouth < 0.18f ? 0f : f.Mouth;
        bool wantSmile = _smiling ? smile > 0.12f : smile > 0.22f;
        bool wantMouth = _mouthOpen ? mouth > 0.25f : mouth > 0.50f;
        if (wantSmile != _smiling || wantMouth != _mouthOpen || _exprRefresh <= 0f)
        {
            _smiling = wantSmile;
            _mouthOpen = wantMouth;
            float blink = Mathf.Max(f.BlinkLeft, f.BlinkRight);
            if (blink < 0.64f)
                _actor.SetOverride("eyes", _smiling ? "l55" : null, 0.35f);
            _actor.SetOverride("mouth", _mouthOpen ? "l56" : null, 0.35f);
            _exprRefresh = 0.25f;
        }
    }
}
