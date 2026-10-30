using System;
using Godot;

namespace YuzuPet.Capture;

/// <summary>
/// IFaceCapture over UDP: receives the sidecar's compact JSON frames
/// {"t","x","y","roll","pitch","blinkL","blinkR","mouth","smile"}.
/// </summary>
public partial class FaceCaptureUdp : Node, IFaceCapture
{
    public event Action<FaceFrame> FrameReceived;
    public bool IsRunning { get; private set; }

    [Export] public int Port { get; set; } = 7901;

    private PacketPeerUdp _udp;

    public void Start()
    {
        if (IsRunning)
            return;
        _udp = new PacketPeerUdp();
        var err = _udp.Bind(Port, "127.0.0.1");
        if (err != Error.Ok)
        {
            GD.PushError($"face capture: cannot bind udp {Port} ({err})");
            return;
        }
        IsRunning = true;
        GD.Print($"face capture: listening on udp {Port}");
    }

    public void Stop()
    {
        IsRunning = false;
        _udp?.Close();
        _udp = null;
    }

    public override void _Process(double delta)
    {
        if (!IsRunning || _udp == null)
            return;
        while (_udp.GetAvailablePacketCount() > 0)
        {
            var packet = _udp.GetPacket();
            var frame = Parse(packet.GetStringFromUtf8());
            if (frame.HasValue)
                FrameReceived?.Invoke(frame.Value);
        }
    }

    private static float F(Godot.Collections.Dictionary d, string key) =>
        d.ContainsKey(key) ? d[key].AsSingle() : 0f;

    private static FaceFrame? Parse(string json)
    {
        var root = Json.ParseString(json).AsGodotDictionary();
        if (root == null || root.Count == 0)
            return null;
        return new FaceFrame(
            F(root, "x"), F(root, "y"), F(root, "roll"), F(root, "pitch"),
            F(root, "blinkL"), F(root, "blinkR"), F(root, "mouth"), F(root, "smile"));
    }
}
