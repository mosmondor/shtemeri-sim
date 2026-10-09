using Shtemeri.Api;

namespace Shtemeri.Sim;

/// <summary><see cref="ISelf"/> for one shtemer: reads the engine state, records commands for the end of the tick.</summary>
internal sealed class Agent : ISelf
{
    private readonly Match _m;
    private readonly Bot _b;

    public Agent(Match m, Bot b) { _m = m; _b = b; }

    public int Index => _b.Index;
    public int FleetSize => _m.Rules.FleetSize;
    public bool IsAdmiral => _b.Index == Fleet.Admiral;
    public bool IsAllyAlive(int index) =>
        index >= 0 && index < FleetSize && _m.Bots[_b.Slot * FleetSize + index].Alive;

    public int Tick => _m.Tick;
    public Vec2 Position => _b.Pos;
    public double Altitude => _m.Arena.Height(_b.Pos.X, _b.Pos.Y);
    public Vec2 Velocity => _b.Vel;
    public double Health => _b.Hp;
    public double Energy => _b.Energy;
    public double LookAngle => _b.Look;
    public int Ammo(Weapon weapon) => _b.Ammo[(int)weapon & 1];
    public int Cooldown(Weapon weapon) => _b.Cd[(int)weapon & 1];
    public IArena Arena => _m.FleetArena;
    public IRules Rules => _m.Rules;
    public IRandom Random => _b.Random;

    public void Thrust(Vec2 direction)
    {
        if (double.IsNaN(direction.X) || double.IsNaN(direction.Y)) return;
        _b.Thrust = direction.ClampLength(1.0);
    }

    public void Look(double angle)
    {
        if (double.IsNaN(angle) || double.IsInfinity(angle)) return;
        _b.LookTarget = Vec2.NormalizeAngle(angle);
    }

    public void LookAt(Vec2 point)
    {
        var d = point - _b.Pos;
        if (d.LengthSquared < 1e-12) return;
        _b.LookTarget = d.Angle;
    }

    public bool Fire(Weapon weapon, Vec2 target)
    {
        int w = (int)weapon;
        if (w < 0 || w > 1 || double.IsNaN(target.X) || double.IsNaN(target.Y)) return false;
        if (_b.Cd[w] > 0 || _b.Ammo[w] <= 0) return false;
        if (w == 0 ? _b.FiredPistol : _b.FiredRocket) return false;
        if (w == 0) _b.FiredPistol = true; else _b.FiredRocket = true;
        _b.Ammo[w]--;
        _b.Cd[w] = w == 0 ? _m.Rules.PistolCooldown : _m.Rules.RocketCooldown;
        _b.Shots.Add((weapon, target));
        _b.Tel?.Fires.Add((w, target));
        return true;
    }

    public bool Zoom(int blipId)
    {
        if (_b.ZoomsThisTick >= _m.Rules.MaxZoomsPerTick) return false;
        if (_b.Energy < _m.Rules.ZoomCost) return false;
        if (!_b.BlipKeyById.TryGetValue(blipId, out long key)) return false;
        _b.ZoomsThisTick++;
        _b.Energy -= _m.Rules.ZoomCost;
        _b.ZoomsRequested.Add((blipId, key));
        _b.Tel?.Zooms.Add(blipId);
        _m.RecordZoom(_b, key);
        return true;
    }

    public void Send(int toIndex, int type, params double[] data)
    {
        if (_b.MessagesThisTick >= _m.Rules.MaxMessagesPerTick) return;
        _b.MessagesThisTick++;
        if (toIndex < 0 || toIndex >= FleetSize || toIndex == _b.Index) return;
        _b.Tel?.MsgOut.Add((toIndex, type, data.Take(_m.Rules.MaxMessageData).ToArray()));
        _m.QueueMessage(_b, toIndex, type, data);
    }

    public void Broadcast(int type, params double[] data)
    {
        if (_b.MessagesThisTick >= _m.Rules.MaxMessagesPerTick) return;
        _b.MessagesThisTick++;
        _b.Tel?.MsgOut.Add((-1, type, (data ?? Array.Empty<double>()).Take(_m.Rules.MaxMessageData).ToArray()));
        for (int i = 0; i < FleetSize; i++)
            if (i != _b.Index) _m.QueueMessage(_b, i, type, data);
    }

    public void Log(string text)
    {
        Runtime.Meter.Charge(_m.Rules.CostLog);
        if (_b.LogLinesThisTick >= _m.Rules.MaxLogLinesPerTick) return;
        _b.LogLinesThisTick++;
        _b.Tel?.Log.Add(text);
        _m.LogLine?.Invoke(_b.Gid, _m.Tick, text.Length > _m.Rules.MaxLogLineLength ? text[.._m.Rules.MaxLogLineLength] : text);
    }

    public bool Shout(int message)
    {
        if (message < 0 || message >= WarCry.Count || _m.Tick < _b.NextShoutTick) return false;
        _b.NextShoutTick = _m.Tick + _m.Rules.WarCryCooldownTicks;
        _m.RecordShout(_b, message);
        return true;
    }
}

internal sealed class Senses : ISenses
{
    public IReadOnlyList<Blip> Blips { get; set; } = Array.Empty<Blip>();
    public IReadOnlyList<Contact> Contacts { get; set; } = Array.Empty<Contact>();
    public Zone Zone { get; set; }
}
