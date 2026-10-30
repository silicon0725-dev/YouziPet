using System;

namespace YuzuPet.Capture;

/// <summary>
/// One frame of face tracking, mirroring the original app's protocol
/// {x, y, roll, pitch, expression:{blink, mouth, smile}}. Distances are
/// normalized (-1..1) around the capture calibration point.
/// </summary>
public readonly struct FaceFrame
{
    public readonly float X, Y, Roll, Pitch;
    public readonly float BlinkLeft, BlinkRight, Mouth, Smile;

    public FaceFrame(float x, float y, float roll, float pitch,
                     float blinkL, float blinkR, float mouth, float smile)
    {
        X = x; Y = y; Roll = roll; Pitch = pitch;
        BlinkLeft = blinkL; BlinkRight = blinkR; Mouth = mouth; Smile = smile;
    }
}

/// <summary>
/// Face capture abstraction, reserved for v2: a MediaPipe sidecar
/// (python process) will produce FaceFrame streams over UDP; the stub does
/// nothing for now. PetActor.SetCaptureBlink() already consumes blink state.
/// </summary>
public interface IFaceCapture
{
    event Action<FaceFrame> FrameReceived;
    bool IsRunning { get; }
    void Start();
    void Stop();
}

/// <summary>No-op implementation so the pet runs without a camera.</summary>
public sealed class FaceCaptureStub : IFaceCapture
{
    public event Action<FaceFrame> FrameReceived { add { } remove { } }
    public bool IsRunning => false;
    public void Start() { }
    public void Stop() { }
}
