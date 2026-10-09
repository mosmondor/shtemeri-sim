using Shtemeri.Api;
using System;

// A minimal example fleet: every shtemer wanders on a circle inside the zone, looks around,
// zooms the nearest unknown blip and shoots the nearest blip a zoom has confirmed as an enemy.
public class Wanderer : Shtemer
{
    private int _enemyId = -1;
    private int _zoomTick = -1000;

    public override void OnTick(ISelf me, ISenses s)
    {
        Vec2 c = s.Zone.Center;
        double r = s.Zone.Radius;

        // move: a point on a circle at 60 % of the zone radius, a little ahead of where I am
        Vec2 fromC = me.Position - c;
        double ang = (fromC.Length < 0.5 ? me.Index * Math.PI / 2 : fromC.Angle) + 0.4;
        Vec2 goal = c + Vec2.FromAngle(ang, r * 0.6);
        me.Thrust(((goal - me.Position) * 0.5 - me.Velocity * 0.3).ClampLength(1.0));

        // remember what a zoom told me
        foreach (var ct in s.Contacts)
            if (ct.Kind == ContactKind.Shtemer && !ct.IsAlly) _enemyId = ct.BlipId;

        // nearest medium blip: zoom it if unknown, shoot it if it is the confirmed enemy
        Blip? nearest = null;
        foreach (var b in s.Blips)
            if (b.Size == BlipSize.Medium && (nearest == null || b.Distance < nearest.Distance)) nearest = b;

        if (nearest == null) { me.Look(me.Tick * 0.08 + me.Index * Math.PI / 2); return; }
        me.LookAt(nearest.Position);
        if (nearest.Id != _enemyId)
        {
            if (me.Energy >= me.Rules.ZoomCost && me.Tick - _zoomTick >= 10 && me.Zoom(nearest.Id)) _zoomTick = me.Tick;
        }
        else if (nearest.Distance < me.Rules.PistolRange && me.Ammo(Weapon.Pistol) > 0 && me.Cooldown(Weapon.Pistol) == 0)
            me.Fire(Weapon.Pistol, nearest.Position);
    }
}
