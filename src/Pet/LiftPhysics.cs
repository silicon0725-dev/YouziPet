using System;
using System.Collections.Generic;

namespace YuzuPet.Pet;

/// <summary>
/// Pendulum physics for being picked up and shaken, ported 1:1 from the
/// original app's CottonLiftPhysics: seven driven/damped joints (root, both
/// arms, both legs, both knees) with parent coupling, a grab kick impulse,
/// weight blending and 120Hz sub-stepping.
/// </summary>
public static class LiftPhysics
{
    private readonly struct Config
    {
        public readonly float Length, Drive, Damping, Limit;

        public Config(float length, float drive, float damping, float limit)
        {
            Length = length; Drive = drive; Damping = damping; Limit = limit;
        }
    }

    public sealed class Joint
    {
        public float Angle;
        public float Velocity;
    }

    public sealed class Bank
    {
        public float Time = float.NaN;
        public bool Held;
        public float Weight;
        public float X, Y, Size = 225f;
        public float Vx, Vy, Ax, Ay;
        public readonly Dictionary<string, Joint> Joints = new();

        public Bank()
        {
            foreach (var key in Configs.Keys)
                Joints[key] = new Joint();
        }
    }

    private static readonly Dictionary<string, Config> Configs = new()
    {
        { "root",  new Config(1.3f, 0.46f, 4.4f,  0.18f) },
        { "armL",  new Config(0.42f, 1.2f,  3.1f,  1.03f) },
        { "armR",  new Config(0.48f, 1.2f,  2.95f, 1.03f) },
        { "legL",  new Config(0.68f, 1.05f, 2.55f, 0.72f) },
        { "legR",  new Config(0.75f, 1.05f, 2.45f, 0.72f) },
        { "kneeL", new Config(0.32f, 0.6f,  3.1f,  0.62f) },
        { "kneeR", new Config(0.37f, 0.6f,  3.0f,  0.62f) },
    };

    private static float Bound(float n, float a, float b) => Math.Max(a, Math.Min(b, n));

    /// <summary>Advance the bank: input is the grab point in canvas space.</summary>
    public static void Step(Bank bank, float? inputX, float? inputY, float inputSize,
                            float time, bool held)
    {
        if (float.IsNaN(time))
            return;
        float elapsed = time - bank.Time;
        if (float.IsNaN(bank.Time) || elapsed < 0f || elapsed > 0.25f)
        {
            // fresh bank after a gap
            bank.Time = time;
            bank.X = inputX ?? 0f;
            bank.Y = inputY ?? 0f;
            bank.Size = Math.Max(60f, inputSize > 0f ? inputSize : 225f);
            elapsed = 0f;
        }
        if (time == bank.Time && bank.Held == held)
            return;

        float dt = Bound(time - bank.Time, 1f / 240f, 0.05f);
        float size = Math.Max(60f, inputSize > 0f ? inputSize : bank.Size);
        float x = inputX ?? bank.X;
        float y = inputY ?? bank.Y;

        float vx = Bound((x - bank.X) / size / dt, -12f, 12f);
        float vy = Bound((y - bank.Y) / size / dt, -12f, 12f);
        float filter = 1f - MathF.Exp(-dt / 0.07f);
        float nextX = bank.Vx + (vx - bank.Vx) * filter;
        float nextY = bank.Vy + (vy - bank.Vy) * filter;
        bank.Ax += (Bound((nextX - bank.Vx) / dt, -45f, 45f) - bank.Ax) * filter;
        bank.Ay += (Bound((nextY - bank.Vy) / dt, -45f, 45f) - bank.Ay) * filter;
        bank.Time = time;
        bank.X = x;
        bank.Y = y;
        bank.Size = size;
        bank.Vx = nextX;
        bank.Vy = nextY;

        if (held && !bank.Held)
        {
            // grab kick: limbs spring outward the instant she is picked up
            bank.Joints["armL"].Velocity += -1.5f;
            bank.Joints["armR"].Velocity += 1.35f;
            bank.Joints["legL"].Velocity += 0.55f;
            bank.Joints["legR"].Velocity += -0.48f;
            bank.Joints["kneeL"].Velocity += 0.75f;
            bank.Joints["kneeR"].Velocity += -0.65f;
        }
        bank.Held = held;
        float weightTau = held ? 0.09f : 0.45f;
        bank.Weight += ((held ? 1f : 0f) - bank.Weight) * (1f - MathF.Exp(-dt / weightTau));

        int count = Math.Max(1, (int)Math.Ceiling(dt * 120f));
        float h = dt / count;
        float gravity = Bound(12f + bank.Ay * 0.5f, 4f, 24f);
        float drive = bank.Ax * (held ? 1f : 0.2f);

        foreach (var (key, cfg) in Configs)
        {
            var joint = bank.Joints[key];
            var parent = key == "kneeL" ? bank.Joints["legL"]
                       : key == "kneeR" ? bank.Joints["legR"] : null;
            for (int i = 0; i < count; i++)
            {
                float coupling = parent != null ? -parent.Velocity * 0.7f : 0f;
                float accel = drive * cfg.Drive / cfg.Length * MathF.Cos(joint.Angle)
                            - gravity / cfg.Length * MathF.Sin(joint.Angle)
                            - cfg.Damping * joint.Velocity
                            + coupling;
                joint.Velocity = Bound(joint.Velocity + accel * h, -8f, 8f);
                float next = joint.Angle + joint.Velocity * h;
                joint.Angle = Bound(next, -cfg.Limit, cfg.Limit);
                if (next != joint.Angle)
                    joint.Velocity *= 0.3f;
            }
        }

        if (!held && bank.Weight < 0.001f)
        {
            bank.Weight = 0f;
            foreach (var j in bank.Joints.Values)
            {
                j.Angle = 0f;
                j.Velocity = 0f;
            }
        }
    }

    /// <summary>Blend the pendulum pose into the current pose (weight-scaled).</summary>
    public static void Apply(Bank bank, ref Pose p)
    {
        float w = bank.Weight;
        if (w <= 0f)
            return;
        const float Deg = 180f / MathF.PI;
        var j = bank.Joints;

        p.Root += j["root"].Angle * Deg * w;
        p.Head -= j["root"].Angle * Deg * 0.7f * w;
        p.Hair -= j["root"].Angle * Deg * 0.25f * w;

        p.ArmL += (6f - p.ArmL) * w + j["armL"].Angle * Deg * w;
        p.ArmR += (-6f - p.ArmR) * w + j["armR"].Angle * Deg * w;
        p.LegL += (2f - p.LegL) * w + j["legL"].Angle * Deg * w;
        p.LegR += (-2f - p.LegR) * w + j["legR"].Angle * Deg * w;
        p.KneeL += (3f - p.KneeL) * w + (j["kneeL"].Angle - j["legL"].Angle * 0.45f) * Deg * w;
        p.KneeR += (-3f - p.KneeR) * w + (j["kneeR"].Angle - j["legR"].Angle * 0.45f) * Deg * w;
    }
}
