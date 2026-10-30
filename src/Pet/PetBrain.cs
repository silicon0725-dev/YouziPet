using System;
using System.Collections.Generic;
using Godot;

namespace YuzuPet.Pet;

/// <summary>Tiny behavior core: mood state, persisted affinity, and dialogue.</summary>
public partial class PetBrain : Node
{
    public enum Mood { Idle, Happy, Dragging, Eating, Dancing }

    private static readonly string[] PatLines =
    {
        "唔……头顶暖暖的", "再摸一下也可以哦", "呼噜呼噜……", "今天也辛苦啦",
        "头发要乱掉啦", "最喜欢你了",
    };
    private static readonly string[] FeedLines =
    {
        "哇，是小点心！", "啊呜——好吃！", "谢谢你投喂", "能量满满！",
    };
    private static readonly string[] DropLines = { "咚……头有点晕", "放轻一点嘛", "站稳啦" };
    private static readonly string[] IdleLines =
    {
        "陪你待一会儿吧", "窗外的云走得好慢", "要不要换套新衣服？", "嘿嘿，在看什么呀",
    };
    private static readonly string[] DressLines = { "要试试哪一件？", "换装时间！" };

    private const string ProgressPath = "user://progress.json";
    private readonly Random _rng = new();

    public Mood State { get; private set; } = Mood.Idle;
    public int Affinity { get; private set; }

    public override void _Ready()
    {
        if (FileAccess.FileExists(ProgressPath))
        {
            using var f = FileAccess.Open(ProgressPath, FileAccess.ModeFlags.Read);
            var d = Json.ParseString(f.GetAsText()).AsGodotDictionary();
            Affinity = d.ContainsKey("affinity") ? d["affinity"].AsInt32() : 0;
        }
    }

    public void Save()
    {
        var d = new Godot.Collections.Dictionary { { "affinity", Affinity } };
        using var f = FileAccess.Open(ProgressPath, FileAccess.ModeFlags.Write);
        f?.StoreString(Json.Stringify(d, "\t"));
    }

    private string Pick(IReadOnlyList<string> lines) => lines[_rng.Next(lines.Count)];

    public string OnPat()
    {
        Affinity += 1;
        State = Mood.Happy;
        return Pick(PatLines);
    }

    public string OnFeed()
    {
        Affinity += 3;
        State = Mood.Eating;
        return Pick(FeedLines);
    }

    public string OnDrop()
    {
        State = Mood.Idle;
        return _rng.NextSingle() < 0.4f ? Pick(DropLines) : null;
    }

    public string OnDance()
    {
        State = Mood.Dancing;
        return "一起跳舞吧！";
    }

    public string OnDress()
    {
        return Pick(DressLines);
    }

    public string MaybeIdleChat()
    {
        if (State != Mood.Idle || _rng.NextSingle() > 0.35f)
            return null;
        return Pick(IdleLines);
    }

    public void SetIdle() => State = Mood.Idle;
}
