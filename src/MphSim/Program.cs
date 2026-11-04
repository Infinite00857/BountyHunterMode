// MphSim - headless checks for the mod's core (no Unity, no game): ROM cache, Samus movement numbers, Power Beam
// cadence, gun model posing (software-rendered to PNG), HUD art, sounds.
// Usage: dotnet run -- <rom.nds> <cache dir> <out dir>
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphSamus.Core;
using OpenTK.Mathematics;

string rom = args[0], cache = args[1], outDir = args.Length > 2 ? args[2] : ".";
Directory.CreateDirectory(outDir);
string? err = RomCache.Prepare(rom, cache, Console.WriteLine);
if (err != null) { Console.WriteLine("ROM: " + err); return 1; }
// ---- story item count (MPH_ITEM_COUNT=1): every ItemSpawn's item type over the story rooms, then exit ----
if (Environment.GetEnvironmentVariable("MPH_ITEM_COUNT") != null)
{
    var counts = new SortedDictionary<string, int>();
    foreach (RoomMetadata room in Metadata.RoomList)
    {
        if (room.Multiplayer || room.FirstHunt || room.EntityPath == null) { continue; }
        IReadOnlyList<Entity> ents;
        try { ents = Read.GetEntities(room.EntityPath, -1, false); } catch (Exception ex) { Console.WriteLine($"  {room.Name}: {ex.Message}"); continue; }
        foreach (Entity e in ents)
        {
            string key = e is Entity<ItemSpawnEntityData> it ? it.Data.ItemType.ToString() : "type " + e.Type;
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            if (key.Contains("Expansion") || key.Contains("Tank")) { Console.WriteLine($"  {key} in {room.Name}"); }
        }
    }
    foreach (KeyValuePair<string, int> kv in counts) { Console.WriteLine($"  {kv.Key}: {kv.Value}"); }
    return 0;
}
int failures = 0;
void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");
    if (!ok) { failures++; }
}

// ---- movement ----
{
    var sim = new SamusSim();
    sim.Spawn();
    var input = new SamusInput { MoveY = 1, Facing = new Vector3(0, 0, -1) };
    for (int i = 0; i < 120; i++)
    {
        sim.Step(input);
        sim.AfterCollision(true, false, sim.Speed);
    }
    float hs = new Vector2(sim.Speed.X, sim.Speed.Z).Length * 30;
    Check("walk cap", Math.Abs(hs - 7.2f) < 0.5f, $"{hs:F2} u/s (MPH 0.24 u/frame = 7.2 u/s)");
    // stop: friction
    int stopSteps = 0;
    input.MoveY = 0;
    while (new Vector2(sim.Speed.X, sim.Speed.Z).Length > 0.01f && stopSteps < 600)
    {
        sim.Step(input);
        sim.AfterCollision(true, false, sim.Speed);
        stopSteps++;
    }
    Check("stop time", stopSteps < 60, $"{stopSteps} steps ({stopSteps / 60f:F2} s) to stop");
    // jump apex
    float y = 0, apex = 0;
    int air = 0;
    input = new SamusInput { Facing = new Vector3(0, 0, -1), Jump = true, JumpPressed = true };
    sim.Step(input);
    bool standing = false;
    sim.AfterCollision(false, false, sim.Speed);
    input.JumpPressed = false;
    while (air < 600)
    {
        y += sim.Speed.Y / 2;
        apex = Math.Max(apex, y);
        standing = y <= 0;
        if (standing) { y = 0; }
        sim.AfterCollision(standing, false, sim.Speed);
        air++;
        if (standing) { break; }
        sim.Step(input);
    }
    Check("jump apex", apex > 1.5f && apex < 3.5f, $"{apex:F2} units, airtime {air / 60f:F2} s");
}

// ---- Power Beam ----
{
    var sim = new SamusSim();
    sim.Spawn();
    sim.ReportGunAnimEnded(true);   // skip the spawn UpDown (one step to reach Idle)
    var input = new SamusInput { Facing = new Vector3(0, 0, -1) };
    sim.Step(input);
    var shots = new List<(int step, ShotEvent e)>();
    for (int i = 0; i < 180; i++)
    {
        input.Fire = i < 90;
        input.FirePressed = i == 0;
        sim.Step(input);
        sim.AfterCollision(true, false, sim.Speed);
        if (sim.GunAnim == GunAnimation.UpDown) { sim.ReportGunAnimEnded(true); }
        if (sim.ShotThisStep is ShotEvent s) { shots.Add((i, s)); }
    }
    string list = string.Join(", ", shots.ConvertAll(s => $"{s.step}:{s.e.Kind}/{s.e.Damage}"));
    bool hasCharged = shots.Exists(s => s.e.Kind == ShotKind.Charged && s.step == 90);
    // Samus's single-player Power Beam (affinity entry, 4-frame cooldowns): uncharged shots at DS frames 0, 4, 9, 15
    // = 60 Hz steps 0, 8, 18, 30, then the charge (the testbed gives the same steps)
    string uncharged = string.Join(",", shots.FindAll(s => s.e.Kind == ShotKind.Uncharged).ConvertAll(s => s.step));
    Check("power beam hold 1.5 s", hasCharged && uncharged == "0,8,18,30", list);
    int tapShots = 0;
    var sim2 = new SamusSim();
    sim2.Spawn();
    sim2.ReportGunAnimEnded(true);
    sim2.Step(new SamusInput { Facing = new Vector3(0, 0, -1) });
    for (int i = 0; i < 120; i++)
    {
        input.Fire = i % 6 < 3;
        input.FirePressed = i % 6 == 0;
        sim2.Step(input);
        if (sim2.GunAnim == GunAnimation.UpDown) { sim2.ReportGunAnimEnded(true); }
        if (sim2.ShotThisStep != null) { tapShots++; }
    }
    Check("power beam mashing", tapShots >= 5, $"{tapShots} shots in 2 s of 10 presses/s (ShotCooldown 4 DS frames)");
}

// ---- Missiles ----
{
    var sim = new SamusSim { HasMissiles = true };
    sim.Spawn();
    sim.ReportGunAnimEnded(true);
    var input = new SamusInput { Facing = new Vector3(0, 0, -1) };
    sim.Step(input);
    var log = new List<string>();
    void Run(int steps, Func<int, SamusInput> make, List<(int step, ShotEvent e)> shots)
    {
        for (int i = 0; i < steps; i++)
        {
            sim.Step(make(i));
            sim.AfterCollision(true, false, sim.Speed);
            // the gun animations end instantly here (GunModel advances them in game)
            sim.ReportGunAnimEnded(true);
            if (sim.ShotThisStep is ShotEvent s) { shots.Add((i, s)); }
        }
    }
    var none = new List<(int, ShotEvent)>();
    Run(1, i => new SamusInput { Facing = input.Facing, WeaponSlot = 2 }, none);
    bool switched = sim.CurrentWeapon == BeamType.Missile && sim.GunAnim == GunAnimation.MissileOpen && sim.WeaponIconSteps > 0;
    Run(10, i => input, none);
    int ammo0 = sim.MissileAmmo;
    var tap = new List<(int step, ShotEvent e)>();
    Run(30, i => new SamusInput { Facing = input.Facing, Fire = i < 3, FirePressed = i == 0 }, tap);
    Run(30, i => input, none);   // past the missile ShotCooldown
    int ammo1 = sim.MissileAmmo;
    var held = new List<(int step, ShotEvent e)>();
    Run(140, i => new SamusInput { Facing = input.Facing, Fire = i < 100, FirePressed = i == 0 }, held);
    int ammo2 = sim.MissileAmmo;
    ShotEvent? charged = held.Find(s => s.e.Kind == ShotKind.Charged).e is ShotEvent c && c.Weapon == BeamType.Missile ? c : null;
    // flight: distance after 0.5 s (speed decays toward FinalSpeed over SpeedDecayTime)
    float flown = 0;
    if (tap.Count > 0)
    {
        var p = new Projectile(tap[0].e, Vector3.Zero, new Vector3(0, 0, -1));
        for (int i = 0; i < 30; i++) { p.Step(null); }
        flown = p.Position.Length;
    }
    // empty: no shot, no switch to missiles
    sim.MissileAmmo = 5;
    var dry = new List<(int step, ShotEvent e)>();
    Run(30, i => new SamusInput { Facing = input.Facing, Fire = i < 3, FirePressed = i == 0 }, dry);
    Run(1, i => new SamusInput { Facing = input.Facing, WeaponSlot = 1 }, none);
    bool back = sim.CurrentWeapon == BeamType.PowerBeam && sim.GunAnim == GunAnimation.MissileClose;
    Run(1, i => new SamusInput { Facing = input.Facing, WeaponSlot = 2 }, none);
    bool refused = sim.CurrentWeapon == BeamType.PowerBeam && sim.SwitchedThisStep == WeaponSwitch.Fail;
    string tapInfo = tap.Count > 0 ? $"{tap[0].e.Kind} dmg {tap[0].e.Damage} splash {tap[0].e.SplashDamage}/{tap[0].e.SplashRadius:F2} homing {tap[0].e.Homing:F3} life {tap[0].e.Lifespan} radius {tap[0].e.Radius:F2} maxdist {tap[0].e.MaxDistance:F1} speed {tap[0].e.Speed:F2}->{tap[0].e.FinalSpeed:F2} decay {tap[0].e.SpeedDecayTime:F2} col {tap[0].e.ColEffect} draw {tap[0].e.DrawFuncId}" : "none";
    string chInfo = charged is ShotEvent ch ? $"dmg {ch.Damage} splash {ch.SplashDamage}/{ch.SplashRadius:F2} homing {ch.Homing:F3} at step {held.Find(s => s.e.Kind == ShotKind.Charged).step}" : "none";
    bool ok = switched && tap.Count == 1 && tap[0].e.Homing == 0 && ammo0 - ammo1 == 10
        && charged is ShotEvent cc && cc.Homing > 0 && cc.Damage > tap[0].e.Damage && ammo1 - ammo2 == 40
        && dry.Count == 0 && back && refused && flown > 1;
    Check("missiles", ok, $"switch {switched}, tap {tap.Count} ({tapInfo}), charged ({chInfo}), ammo {ammo0}->{ammo1}->{ammo2}, "
        + $"flew {flown:F2} u in 0.5 s, dry shots {dry.Count}, back {back}, empty refused {refused}");
}

// ---- underwater dampening + jump pads (MPH ActivateJumpPad) ----
{
    // jump from a floor at 0: peak height, air time, fastest fall; walking speed after 60 steps
    (float peak, int air, float minVy, float walk) Run(bool water, bool ball)
    {
        var sim = new SamusSim();
        sim.Spawn();
        sim.ReportGunAnimEnded(true);
        var f = new Vector3(0, 0, -1);
        float y = 0, peak = 0, minVy = 0, walk = 0;
        int air = 0;
        bool standing = true;
        if (ball)
        {
            sim.Step(new SamusInput { Facing = f, MorphPressed = true });
            for (int i = 0; i < 60; i++) { sim.AfterCollision(true, false, sim.Speed); sim.Step(new SamusInput { Facing = f, InWater = water }); }
        }
        for (int i = 0; i < 60; i++) { sim.AfterCollision(true, false, sim.Speed); sim.Step(new SamusInput { Facing = f, MoveY = 1, InWater = water }); }
        walk = MathF.Sqrt(sim.Speed.X * sim.Speed.X + sim.Speed.Z * sim.Speed.Z);
        if (ball) { return (0, 0, 0, walk); }
        sim.AfterCollision(true, false, sim.Speed);
        sim.Step(new SamusInput { Facing = f, Jump = true, JumpPressed = true, InWater = water });
        for (int i = 0; i < 600; i++)
        {
            y += sim.Speed.Y / 2;
            standing = y <= 0 && sim.Speed.Y <= 0;
            if (standing) { y = 0; break; }
            air++;
            peak = MathF.Max(peak, y);
            minVy = MathF.Min(minVy, sim.Speed.Y);
            sim.AfterCollision(false, false, sim.Speed);
            sim.Step(new SamusInput { Facing = f, InWater = water });
        }
        return (peak, air, minVy, walk);
    }
    var dry = Run(false, false);
    var wet = Run(true, false);
    var ballDry = Run(false, true);
    var ballWet = Run(true, true);
    bool ok = wet.peak < dry.peak && wet.peak > dry.peak * 0.6f && wet.air > dry.air && wet.minVy >= -SamusSim.WaterMaxFall - 1e-4f
        && wet.walk < dry.walk * 0.7f && ballWet.walk < ballDry.walk * 0.8f;
    // jump pad: the pad's speed, a fresh jump, the sound once per 5 DS frames
    var pad = new SamusSim();
    pad.Spawn();
    pad.UsedJump = true;
    bool sfx1 = pad.ActivateJumpPad(new Vector3(0, 2, 0));
    pad.AfterCollision(true, false, pad.Speed);
    bool launched = pad.Speed.Y == 2 && !pad.UsedJump && pad.Launched;
    pad.Step(new SamusInput { Facing = new Vector3(0, 0, -1) });
    bool sfx2 = pad.ActivateJumpPad(new Vector3(0, 2, 0));
    // burning (MPH affliction): 1 energy every 8 DS frames for 150 DS frames (the timer reaching 0 ticks too)
    var burn = new SamusSim();
    burn.Spawn();
    burn.Ignite();
    int ticks = 0, burnSteps = 0;
    while (burn.BurnTimer > 0 && burnSteps < 1000)
    {
        burn.AfterCollision(true, false, burn.Speed);
        burn.Step(new SamusInput { Facing = new Vector3(0, 0, -1) });
        burnSteps++;
        if (burn.BurnTickThisStep) { ticks++; burn.TakeBurnDamage(1); }
    }
    ok &= ticks == SamusSim.BurnSteps / 16 + 1 && burn.Health == SamusSim.StartHealthMax - ticks;
    Console.WriteLine($"     burn: {ticks} ticks over {burnSteps} steps, energy {burn.Health}");
    Check("water + jump pads", ok && sfx1 && !sfx2 && launched,
        $"jump peak {dry.peak:F2} -> {wet.peak:F2} under water, air {dry.air} -> {wet.air} steps, fall cap {wet.minVy:F3}; walk {dry.walk:F3} -> {wet.walk:F3}, roll {ballDry.walk:F3} -> {ballWet.walk:F3}; pad launch {launched}, sound {sfx1}/{sfx2}");
}

// ---- Battlehammer ----
{
    var sim = new SamusSim();
    sim.Carried[(int)BeamType.Battlehammer] = true;
    sim.Spawn();
    sim.ReportGunAnimEnded(true);
    var f = new Vector3(0, 0, -1);
    var shots = new List<(int step, ShotEvent e)>();
    int n = 0;
    void StepB(SamusInput i) { i.Facing = f; sim.Step(i); sim.AfterCollision(true, false, sim.Speed); sim.ReportGunAnimEnded(true); if (sim.ShotThisStep is ShotEvent e) { shots.Add((n, e)); } n++; }
    StepB(new SamusInput());
    StepB(new SamusInput { WeaponSlot = 5 });   // KeyOrder: 5 = Battlehammer (rocket launcher key)
    bool equipped = sim.CurrentWeapon == BeamType.Battlehammer && sim.SwitchedThisStep == WeaponSwitch.Beam;
    int ua0 = sim.UaAmmo;
    for (int i = 0; i < 60; i++) { StepB(new SamusInput { Fire = true, FirePressed = i == 0 }); }
    int ua1 = sim.UaAmmo;
    WeaponInfo w = SamusSim.WeaponFor(BeamType.Battlehammer);
    string info = shots.Count > 0 ? $"dmg {shots[0].e.Damage} splash {shots[0].e.SplashDamage}/{shots[0].e.SplashRadius:F2} speed {shots[0].e.Speed:F2} gravity {shots[0].e.Gravity:F3} life {shots[0].e.Lifespan} draw {shots[0].e.DrawFuncId} col {shots[0].e.ColEffect} radius {shots[0].e.Radius:F2}" : "none";
    Check("battlehammer", equipped && shots.Count > 1 && ua1 < ua0,
        $"equipped {equipped}, {shots.Count} shots in 1 s held (steps {string.Join(",", shots.ConvertAll(x => x.step))}), UA {ua0}->{ua1} (cost {w.AmmoCost}), flags {w.Flags}, {info}");
}

// ---- every weapon: equip, tap, hold (charge), projectile rules, effects, sounds ----
{
    var lines = new List<string>();
    bool ok = true;
    var rng = new Random(1);
    for (int k = 1; k <= SamusSim.KeyOrder.Length; k++)
    {
        BeamType b = SamusSim.KeyOrder[k - 1];
        var sim = new SamusSim();
        foreach (BeamType c in SamusSim.WeaponOrder) { sim.Carried[(int)c] = true; }
        sim.Spawn();
        sim.ReportGunAnimEnded(true);
        var f = new Vector3(0, 0, -1);
        var shots = new List<(int step, ShotEvent e)>();
        int n = 0;
        void St(SamusInput i) { i.Facing = f; sim.Step(i); sim.AfterCollision(true, false, sim.Speed); sim.ReportGunAnimEnded(true); if (sim.ShotThisStep is ShotEvent e) { shots.Add((n, e)); } n++; }
        St(new SamusInput());
        St(new SamusInput { WeaponSlot = k });
        bool equipped = sim.CurrentWeapon == b;
        for (int i = 0; i < 40; i++) { St(new SamusInput()); }
        int ammo0 = sim.AmmoRaw;
        for (int i = 0; i < 150; i++) { St(new SamusInput { Fire = i < 130, FirePressed = i == 0 }); }
        int ammo1 = sim.AmmoRaw;
        int charged = shots.FindAll(x => x.e.Kind == ShotKind.Charged).Count;
        string extra = "";
        if (shots.Count > 0)
        {
            ShotEvent e = shots[shots.Count - 1].e;
            var proj = new Projectile(e, Vector3.Zero, f);
            if (e.Continuous)
            {
                proj.Step(null); proj.Step(null);
                extra += $" continuous: gone after 2 steps {proj.Lifespan <= 0}";
                ok &= proj.Lifespan <= 0;
            }
            if (e.Ricochet)
            {
                proj.Step(null);
                float before = proj.Speed;
                proj.Ricochet(new Vector3(0, 0, -1), new Vector3(0, 0, 1));
                extra += $" ricochet speed {before:F2}->{proj.Speed:F2} dir z {proj.Direction.Z:F2}";
                ok &= proj.Speed < before && proj.Direction.Z > 0;
            }
            if (e.Spread > 0)
            {
                Vector3 d1 = Projectile.SpreadDirection(f, e.Spread, rng), d2 = Projectile.SpreadDirection(f, e.Spread, rng);
                float ang = MathF.Acos(Math.Clamp(Vector3.Dot(d1, f), -1, 1)) * 180 / MathF.PI;
                extra += $" spread x{e.Projectiles} {ang:F1} deg";
                ok &= d1 != d2 && ang <= e.Spread / 4096f + 0.01f;
            }
            if (e.RicochetWeapon != null) { extra += $" ricochet weapon {e.RicochetWeapon.Name}"; }
        }
        bool fired = shots.Count > 0 && (ammo1 < ammo0 || ammo0 < 0);
        ok &= equipped && fired;
        lines.Add($"{k}={b}: {shots.Count} shots ({charged} charged), ammo {ammo0}->{ammo1}{extra}");
    }
    // their effects + sounds load
    var fxSys = new EffectSystem();
    var missing = new List<string>();
    foreach (BeamType b in SamusSim.KeyOrder)
    {
        WeaponInfo w = SamusSim.WeaponFor(b);
        foreach (int col in w.CollisionEffects) { if (col != 255 && !fxSys.Spawn(col - 3, Matrix4.Identity).Valid) { missing.Add($"{b} col {col - 3}"); } }
        WeaponSounds ws = WeaponSounds.ByBeam[(int)b];
        foreach (int id in new[] { ws.Shot, ws.ChargeShot, ws.Charge, ws.Hit, ws.ChargeHit })
        {
            if (id >= 0 && SoundBank.Get(SoundBank.NameAt(id) ?? "?") == null) { missing.Add($"{b} sound {id}"); }
        }
    }
    foreach (int id in new[] { 163, 166, 167 }) { lines.Add($"{id}={SoundBank.NameAt(id)}"); }
    foreach (string m in new[] { "iceShard", "sniperBeam" })
    {
        try { Read.GetModelInstance(m); } catch (Exception ex) { missing.Add($"{m}: {ex.Message}"); }
    }
    for (int dgn = 43; dgn <= 47; dgn += 1) { if (dgn != 45 && dgn != 46) { lines.Add($"DGN {dgn}: {DgnSounds.EntryCount(dgn)} samples"); } }
    ok &= missing.Count == 0;
    Check("all weapons", ok, string.Join("; ", lines) + (missing.Count > 0 ? "; MISSING " + string.Join(", ", missing) : ""));
}

// ---- weapon keys + wheel (testbed) ----
{
    var sim = new SamusSim { HasMissiles = true };
    sim.Spawn();
    sim.ReportGunAnimEnded(true);
    var f = new Vector3(0, 0, -1);
    void StepW(SamusInput i) { i.Facing = f; sim.Step(i); sim.AfterCollision(true, false, sim.Speed); sim.ReportGunAnimEnded(true); }
    StepW(new SamusInput());
    StepW(new SamusInput { WeaponSlot = 3 });   // Volt Driver: not carried
    bool refused = sim.CurrentWeapon == BeamType.PowerBeam && sim.SwitchedThisStep == WeaponSwitch.Fail;
    StepW(new SamusInput { WeaponCycle = 1 });   // next carried weapon: Missiles
    bool cycled = sim.CurrentWeapon == BeamType.Missile;
    StepW(new SamusInput { WeaponCycle = 1 });   // wraps past the rest back to the Power Beam
    bool wrapped = sim.CurrentWeapon == BeamType.PowerBeam;
    // wheel: open, push the cursor right (Volt Driver at 90 degrees) -> refused; up-right (Missiles at 45) -> equipped
    StepW(new SamusInput { WheelHeld = true });
    bool opened = sim.WheelOpen && sim.WheelEventThisStep == WheelEvent.Open;
    StepW(new SamusInput { WheelHeld = true, LookDX = 60 });
    int sel1 = sim.WheelSelection;
    StepW(new SamusInput());
    bool fail = sim.WheelEventThisStep == WheelEvent.CloseFail && sim.CurrentWeapon == BeamType.PowerBeam && !sim.WheelOpen;
    StepW(new SamusInput { WheelHeld = true });
    StepW(new SamusInput { WheelHeld = true, LookDX = 40, LookDY = 40 });
    int sel2 = sim.WheelSelection;
    StepW(new SamusInput());
    bool picked = sim.CurrentWeapon == BeamType.Missile;
    Check("weapon keys + wheel", refused && cycled && wrapped && opened && sel1 == 2 && fail && sel2 == 1 && picked,
        $"slot 3 refused {refused}, cycle {cycled}/{wrapped}, wheel open {opened}, right -> slot {sel1} refused {fail}, up-right -> slot {sel2} picked {picked}");
}

// ---- model materials (MPH_MODEL_DUMP=name,name): render modes / texture formats ----
if (Environment.GetEnvironmentVariable("MPH_MODEL_DUMP") is string dumpModels)
{
    foreach (string name in dumpModels.Split(','))
    {
        ModelInstance inst = Read.GetModelInstance(name);
        Model m = inst.Model;
        Console.WriteLine($"  {name}: {m.Materials.Count} materials, {m.Nodes.Count} nodes");
        foreach (Material mat in m.Materials)
        {
            var texs = m.Recolors[0].Textures; string tex = mat.TextureId >= 0 && mat.TextureId < texs.Count ? $"{texs[mat.TextureId].Format} {texs[mat.TextureId].Width}x{texs[mat.TextureId].Height} opaque={texs[mat.TextureId].Opaque}" : "none";
            Console.WriteLine($"     {mat.Name}: render {mat.RenderMode} poly {mat.PolygonMode} alpha {mat.Alpha} light {mat.Lighting} cull {mat.Culling} tex {tex}");
        }
        foreach (Node node in m.Nodes) { Console.WriteLine($"     node {node.Name} billboard {node.BillboardMode} parent {node.ParentIndex} child {node.ChildIndex} next {node.NextIndex} meshes {node.MeshCount} pos {node.Position} rest {node.Transform.Row3.Xyz} scale {m.Scale}"); }
    }
}

// ---- story enemy drops (MPH_DROP_DUMP=1): every EnemySpawn's ItemType / ItemChance, per enemy type ----
if (Environment.GetEnvironmentVariable("MPH_DROP_DUMP") != null)
{
    var rows = new SortedDictionary<string, List<string>>();
    foreach (RoomMetadata room in Metadata.RoomList)
    {
        if (room.Multiplayer || room.FirstHunt || room.EntityPath == null) { continue; }
        IReadOnlyList<Entity> ents;
        try { ents = Read.GetEntities(room.EntityPath, -1, false); } catch { continue; }
        foreach (Entity e in ents)
        {
            if (e is not Entity<EnemySpawnEntityData> es) { continue; }
            EnemySpawnEntityData d = es.Data;
            string key = d.EnemyType.ToString();
            if (!rows.TryGetValue(key, out List<string>? list)) { rows[key] = list = new List<string>(); }
            list.Add($"{d.ItemType}@{d.ItemChance}x{d.SpawnTotal}");
            if (d.EnemyType == EnemyType.Hunter)
            {
                Console.WriteLine($"  hunter {(Hunter)d.Fields.S09.HunterId} in {room.Name}: health {d.Fields.S09.HunterHealth}/{d.Fields.S09.HunterHealthMax}, weapon {d.Fields.S09.HunterWeapon}");
            }
        }
    }
    foreach (KeyValuePair<string, List<string>> kv in rows)
    {
        var counts = new SortedDictionary<string, int>();
        foreach (string r in kv.Value) { counts[r] = counts.TryGetValue(r, out int n) ? n + 1 : 1; }
        Console.WriteLine($"  {kv.Key} ({kv.Value.Count} spawners): {string.Join(", ", System.Linq.Enumerable.Select(counts, c => $"{c.Key} [{c.Value}]"))}");
    }
}

// ---- weapon table dump (MPH_WEAPON_DUMP=1) ----
if (Environment.GetEnvironmentVariable("MPH_WEAPON_DUMP") != null)
{
    foreach (BeamType b in SamusSim.KeyOrder)
    {
        WeaponInfo w = SamusSim.WeaponFor(b);
        Console.WriteLine($"  {b}: flags {w.Flags}; ammo type {w.AmmoType} cost {w.AmmoCost}/{w.MinChargeCost}/{w.ChargeCost}; charge {w.MinCharge}/{w.FullCharge}; cooldown {w.ShotCooldown} auto {w.AutofireCooldown}");
        Console.WriteLine($"     dmg {w.UnchargedDamage}/{w.MinChargeDamage}/{w.ChargedDamage} hs {w.HeadshotDamage}/{w.ChargedHeadshotDamage} splash {w.SplashDamage}/{w.ChargedSplashDamage} r {w.UnchargedSplashRadius}/{w.ChargedSplashRadius} types {string.Join(",", w.SplashDamageTypes)}");
        Console.WriteLine($"     speed {w.UnchargedSpeed}/{w.ChargedSpeed} final {w.UnchargedFinalSpeed}/{w.ChargedFinalSpeed} decay {string.Join(",", w.SpeedDecayTimes)} grav {w.UnchargedGravity}/{w.ChargedGravity} life {w.UnchargedLifespan}/{w.ChargedLifespan} dist {w.UnchargedDistance}/{w.ChargedDistance} cyl {w.UnchargedCylRadius}/{w.ChargedCylRadius}");
        Console.WriteLine($"     homing {w.UnchargedHoming}/{w.ChargedHoming} range {w.HomingRange} tol {w.HomingTolerance}; spread {w.UnchargedSpread}/{w.ChargedSpread}; projectiles {w.Projectiles}/{w.ChargedProjectiles}; rico H {w.UnchargedRicochetLossH}/{w.ChargedRicochetLossH} V {w.UnchargedRicochetLossV}/{w.ChargedRicochetLossV} weapons {(w.UnchargedRicochetWeapon?.Name ?? "-")}/{(w.ChargedRicochetWeapon?.Name ?? "-")}");
        Console.WriteLine($"     draw {string.Join(",", w.DrawFuncIds)} col {string.Join(",", w.CollisionEffects)} muzzle {string.Join(",", w.MuzzleEffects)} dmgdir {string.Join(",", w.DmgDirTypes)} mag {w.UnchargedDmgDirMag}/{w.ChargedDmgDirMag} interp {string.Join(",", w.DamageInterpolations)} afflictions {string.Join(",", w.Afflictions)} zoom {w.ZoomFov} colors {string.Join(",", w.Colors)} priority {w.Priority}");
    }
}

// ---- terrain DGN sounds (footsteps, landing, roll) ----
{
    var parts = new List<string>();
    bool ok = true;
    foreach (MphTerrain t in Enum.GetValues(typeof(MphTerrain)))
    {
        DgnResult step = DgnSounds.Terrain(t, TerrainSound.Step1, 0xFFFF, 0x4000);
        DgnResult land = DgnSounds.Terrain(t, TerrainSound.Land, 0xFFFF * 60 / 180f, 0);
        DgnResult roll = DgnSounds.Terrain(t, TerrainSound.Roll, 0xFFFF * 0.8f, 0);
        DgnResult rollSlow = DgnSounds.Terrain(t, TerrainSound.Roll, 2000, 0);
        parts.Add($"{t}: step {SoundBank.NameAt(step.Sample)} {step.Volume:F2}@{step.Pitch:F2}, land {SoundBank.NameAt(land.Sample)} {land.Volume:F2}, roll {(roll.Sample >= 0 ? SoundBank.NameAt(roll.Sample) : "-")} {rollSlow.Volume:F2}->{roll.Volume:F2}@{roll.Pitch:F2}");
        ok &= step.Volume > 0 && land.Volume > 0 && (t == MphTerrain.Lava || roll.Volume > 0);
    }
    Check("terrain sounds", ok, string.Join("; ", parts) + (DgnSounds.LastError != null ? " error " + DgnSounds.LastError : ""));
}

// ---- gun model ----
{
    var gun = new GunModel();
    var sim = new SamusSim();
    sim.Spawn();
    var light = MphLighting.Default;
    light.ViewDir = Vector3.UnitZ;
    light.ViewRight = -Vector3.UnitX;
    light.ViewUp = Vector3.UnitY;
    var input = new SamusInput { Facing = new Vector3(0, 0, -1) };
    for (int i = 0; i < 200; i++)
    {
        sim.Step(input);
        gun.Apply(sim);
    }
    Vector3 off = sim.GunOffset();
    Vector3 aim = (new Vector3(0, 0, sim.AimDistance) - off).Normalized();
    gun.Pose(off, aim, light);
    int tris = 0;
    var min = new Vector3(1e9f); var max = new Vector3(-1e9f);
    for (int b = 0; b < gun.Posed.BatchCount; b++)
    {
        var batch = gun.Posed.Batches[b];
        tris += batch.Count / 3;
        for (int i = 0; i < batch.Count; i++) { min = Vector3.ComponentMin(min, batch.Positions[i]); max = Vector3.ComponentMax(max, batch.Positions[i]); }
    }
    Check("gun pose", tris > 100, $"{gun.Posed.BatchCount} batches, {tris} tris, scale {gun.Instance.Model.Scale.X}, bounds {min} .. {max}, anim {sim.GunAnim}");
    SoftRender.Draw(gun.Posed, Path.Combine(outDir, "gun_idle.png"), 320, 240, 78);
    if (Environment.GetEnvironmentVariable("MPH_GUN_MATS") != null)
    {
        var mdl = gun.Instance.Model;
        for (int i = 0; i < mdl.Materials.Count; i++)
        {
            var mt = mdl.Materials[i];
            Console.WriteLine($"     mat {i} {mt.Name}: tex {mt.TextureId}/{mt.CurrentTextureId} pal {mt.PaletteId}/{mt.CurrentPaletteId} dif {mt.CurrentDiffuse} amb {mt.CurrentAmbient} light {mt.Lighting} alpha {mt.CurrentAlpha} render {mt.RenderMode}");
        }
        var ai = gun.Instance.AnimInfo;
        Console.WriteLine($"     anim: index {ai.Index[0]}/{ai.Index[1]} mat group {(ai.Material.Group != null)} slot {ai.Material.Slot} tex group {(ai.Texture.Group != null)} slot {ai.Texture.Slot} texcoord group {(ai.Texcoord.Group != null)}");
        for (int bi = 0; bi < gun.Posed.BatchCount; bi++)
        {
            var bb = gun.Posed.Batches[bi];
            float u0 = 1e9f, u1 = -1e9f, v0 = 1e9f, v1 = -1e9f;
            for (int k = 0; k < bb.Count; k++) { u0 = Math.Min(u0, bb.Uvs[k].X); u1 = Math.Max(u1, bb.Uvs[k].X); v0 = Math.Min(v0, bb.Uvs[k].Y); v1 = Math.Max(v1, bb.Uvs[k].Y); }
            var mt = mdl.Materials[bb.MaterialId];
            Console.WriteLine($"     batch {bi} mat {mt.Name} tex {bb.TextureKey} texgen {mt.TexgenMode} uv {u0:F3}..{u1:F3} x {v0:F3}..{v1:F3} scale {mt.ScaleS},{mt.ScaleT} tr {mt.TranslateS},{mt.TranslateT} tris {bb.Count / 3}");
        }
        Png.Write(Path.Combine(outDir, "gun_tex1.png"), gun.Posed.Textures[1]);
        {
            var info = gun.Instance.AnimInfo;
            var grp = info.Texcoord.Group;
            if (grp != null && grp.Animations.TryGetValue("lambert10", out var an))
            {
                for (int f = 0; f < grp.FrameCount; f += Math.Max(1, grp.FrameCount / 6))
                {
                    var tm = mdl.AnimateTexcoords(grp, an, f);
                    Console.WriteLine($"     uvanim f{f}/{grp.FrameCount}: s {tm.M11:F3} {tm.M12:F3} t {tm.M21:F3} {tm.M22:F3} tr {tm.M41:F3} {tm.M42:F3} (current frame {info.TexcoordFrame})");
                }
            }
            else { Console.WriteLine("     no texcoord anim for lambert10"); }
        }
        Console.WriteLine($"     groups: node {mdl.AnimationGroups.Node.Count} mat {mdl.AnimationGroups.Material.Count} tex {mdl.AnimationGroups.Texture.Count} texcoord {mdl.AnimationGroups.Texcoord.Count}");
    }
    // charge + shot frames
    input.Fire = true; input.FirePressed = true;
    for (int i = 0; i < 70; i++) { sim.Step(input); gun.Apply(sim); input.FirePressed = false; }
    gun.Pose(off, aim, light);
    SoftRender.Draw(gun.Posed, Path.Combine(outDir, "gun_charged.png"), 320, 240, 78);
    Console.WriteLine($"     charged anim {sim.GunAnim}, frame {gun.Instance.AnimInfo.Frame[0]}/{gun.Instance.AnimInfo.FrameCount[0]}");
}

// ---- left arm (carrying items, terminals) ----
{
    var arm = new LeftArm();
    var shots = new List<string>();
    int tris = 0;
    foreach ((string name, ArmState st, int steps) in new[] { ("arm_tucked", ArmState.Hidden, 0), ("arm_point", ArmState.Point, 20), ("arm_hold", ArmState.Hold, 20) })
    {
        arm.State = st;
        arm.Show = st == ArmState.Hidden ? 1 : arm.Show;   // tucked but visible, to see it
        for (int i = 0; i < steps; i++) { arm.Step(st == ArmState.Point, st == ArmState.Hold); }
        arm.Pose(new Vector3(0, 0, 1), MphLighting.Default);
        SoftRender.Draw(arm.Posed, Path.Combine(outDir, name + ".png"), 320, 240, 78);
        tris = 0;
        foreach (PosedModel.Batch bt in arm.Posed.Batches.GetRange(0, arm.Posed.BatchCount)) { tris += bt.Count / 3; }
        shots.Add($"{name}: {tris} tris, hand ({arm.Hand.X:F2}, {arm.Hand.Y:F2}, {arm.Hand.Z:F2})");
    }
    Check("left arm", arm.Loaded && tris > 0, string.Join("; ", shots));
}

// ---- pickup models ----
{
    var got = new List<string>();
    foreach (string m in new[] { "pick_health_A", "pick_health_B", "pick_health_C" })
    {
        var inst = Read.GetModelInstance(m);
        var pm = new PosedModel(inst);
        inst.SetAnimation(0, AnimFlags.None);
        pm.Pose(Matrix4.Identity, MphLighting.Default);
        int tris = 0;
        for (int b = 0; b < pm.BatchCount; b++) { tris += pm.Batches[b].Count / 3; }
        got.Add($"{m}: {tris} tris, {inst.Model.AnimationGroups.Node.Count} anims");
        if (m == "pick_health_A") { SoftRenderAt(pm, Path.Combine(outDir, "health_A.png")); }
    }
    Check("pickup models", got.Count == 3, string.Join("; ", got));
}
static void SoftRenderAt(PosedModel pm, string path)
{
    // move in front of the camera
    for (int b = 0; b < pm.BatchCount; b++)
    {
        var batch = pm.Batches[b];
        for (int i = 0; i < batch.Count; i++) { batch.Positions[i] += new Vector3(0, 0, 2.5f); }
    }
    SoftRender.Draw(pm, path, 160, 160, 40);
}

// ---- Morph Ball ----
{
    var sim = new SamusSim();
    var body = new BipedModel();
    sim.Spawn();
    sim.ReportGunAnimEnded(true);
    var input = new SamusInput { Facing = new Vector3(0, 0, -1), CanUnmorph = true };
    void Run(int steps, SamusInput inp)
    {
        for (int i = 0; i < steps; i++)
        {
            sim.Step(inp);
            body.Apply(sim);
            if (sim.GunAnim == GunAnimation.UpDown) { sim.ReportGunAnimEnded(true); }
            Vector3 before = sim.Speed;
            sim.AfterCollision(true, false, sim.Speed);
            sim.UpdateBallRoll(new Vector3(sim.Speed.X, 0, sim.Speed.Z) / 2, sim.AltColRadius);
            inp.MorphPressed = inp.FirePressed = false;
        }
    }
    Run(5, input);
    var got = new List<string>
    {
        $"anim frames: morph {body.FrameCount(BipedAnimation.Morph)}, unmorph {body.FrameCount(BipedAnimation.Unmorph)}",
        $"ball anims {body.Ball.Model.AnimationGroups.Node.Count}/{body.Ball.Model.AnimationGroups.Material.Count}"
    };
    input.MorphPressed = true;
    int morphSteps = 0;
    sim.Step(input);
    body.Apply(sim);
    input.MorphPressed = false;
    bool morphStarted = sim.Morphing && sim.MorphedThisStep;
    while (!sim.AltForm && morphSteps < 600) { Run(1, input); morphSteps++; }
    got.Add($"morph took {morphSteps} steps ({morphSteps / 60f:F2} s)");
    // roll forward 2 s
    input.MoveY = 1;
    Run(120, input);
    float roll = new Vector2(sim.Speed.X, sim.Speed.Z).Length * 30;
    got.Add($"roll {roll:F2} u/s (AltMinHSpeed {1310 / 4096f * 30:F2})");
    Matrix4 rot = sim.AltRot;
    // boost: hold 0.5 s, release
    input.MoveY = 0;
    input.Boost = true;
    Run(30, input);
    input.Boost = false;
    Run(1, input);
    float boost = new Vector2(sim.Speed.X, sim.Speed.Z).Length * 30;
    got.Add($"boost {boost:F2} u/s, damage {sim.BoostDamage}");
    // bombs: 3 in a row, then refill
    int placed = 0;
    for (int i = 0; i < 4; i++)
    {
        input.FirePressed = true;
        sim.Step(input);
        body.Apply(sim);
        if (sim.BombPlacedThisStep) { placed++; }
        input.FirePressed = false;
        Run(20, input);
    }
    int ammoAfter = sim.BombAmmo;
    Run(300, input);
    got.Add($"bombs placed {placed}/4 tries, ammo {ammoAfter} -> {sim.BombAmmo} after 5 s");
    // ball + mid-morph renders
    var light = MphLighting.Default;
    light.ViewDir = Vector3.UnitZ; light.ViewRight = -Vector3.UnitX; light.ViewUp = Vector3.UnitY;
    body.PoseBall(sim.AltRot, 1, light);
    SoftRenderAt(body.BallPosed, Path.Combine(outDir, "ball.png"));
    // unmorph
    input.MorphPressed = true;
    Run(1, input);
    bool unmorphStarted = sim.Unmorphing;
    int unmorphSteps = 0;
    bool gunRaised = false;
    while ((sim.Unmorphing || !sim.FirstPerson) && unmorphSteps < 600)
    {
        Run(1, input);
        unmorphSteps++;
        gunRaised |= sim.GunAnim == GunAnimation.UpDown;
    }
    got.Add($"unmorph done in {unmorphSteps} steps, gun raised {gunRaised}");
    body.Biped.SetAnimation((int)BipedAnimation.Morph, AnimFlags.NoLoop);
    for (int i = 0; i < 6; i++) { body.Biped.UpdateAnimFrames(); }
    body.PoseBiped(new Vector3(0, 0, -1), 0, light, 1);
    for (int b = 0; b < body.BipedPosed.BatchCount; b++)
    {
        var batch = body.BipedPosed.Batches[b];
        for (int i = 0; i < batch.Count; i++) { batch.Positions[i] += new Vector3(0, -0.9f, 1.5f); }
    }
    SoftRender.Draw(body.BipedPosed, Path.Combine(outDir, "biped_morph.png"), 200, 200, 78);
    // standing pose (Idle) for comparison, plus bounds
    body.Biped.SetAnimation((int)BipedAnimation.Idle, AnimFlags.None);
    body.PoseBiped(new Vector3(0, 0, -1), 0, light, 1);
    var bmin = new Vector3(1e9f); var bmax = new Vector3(-1e9f);
    for (int b = 0; b < body.BipedPosed.BatchCount; b++)
    {
        var batch = body.BipedPosed.Batches[b];
        for (int i = 0; i < batch.Count; i++)
        {
            bmin = Vector3.ComponentMin(bmin, batch.Positions[i]); bmax = Vector3.ComponentMax(bmax, batch.Positions[i]);
            batch.Positions[i] += new Vector3(0, -0.9f, 2.5f);
        }
    }
    SoftRender.Draw(body.BipedPosed, Path.Combine(outDir, "biped_idle.png"), 200, 200, 78);
    got.Add($"biped idle bounds {bmin} .. {bmax}, {body.BipedPosed.BatchCount} batches");
    var names = new List<string>();
    foreach (int i in new[] { 37, 358, 359, 360, 361, 362, 367, 31, 35, 139, 140, 141, 142, 143, 144, 145, 154, 155, 156, 101, 102, 487 }) { names.Add($"{i}={SoundBank.NameAt(i)}"); }
    got.Add("sounds " + string.Join(" ", names));
    bool ok = morphStarted && morphSteps > 10 && morphSteps < 120 && Math.Abs(roll - 9.6f) < 1.5f && boost > roll + 3
        && placed == 3 && ammoAfter == 0 && sim.BombAmmo == 3 && unmorphStarted && unmorphSteps < 120 && gunRaised
        && rot != Matrix4.Identity;
    Check("morph ball", ok, string.Join("; ", got));
}

// ---- effects (camera space: +X left, +Y up, +Z forward; camera right = -X, back = -Z) ----
{
    Vector3 camR = -Vector3.UnitX, camU = Vector3.UnitY, camB = -Vector3.UnitZ;
    var got = new List<string>();
    bool ok = true;
    var sim = new SamusSim();
    Vector3 off = sim.GunOffset();
    Vector3 aim = (new Vector3(0, 0, sim.AimDistance) - off).Normalized();
    Vector3 muzzle = off + aim * sim.MuzzleOffset;
    Vector3 left = Vector3.Cross(Vector3.UnitY, aim).Normalized();
    Matrix4 gunXf = EffectSystem.Transform(left, aim, muzzle);
    Matrix4 hitXf = EffectSystem.Transform(EffectSystem.CrossVector(-Vector3.UnitZ), -Vector3.UnitZ, new Vector3(0, 0, 2));
    // (name, effect id, transform, steps, element extension)
    var cases = new (string Name, int Id, Matrix4 Xf, int Steps, bool Ext)[]
    {
        ("muzzle", 62, gunXf, 2, false), ("charge", 169, gunXf, 20, false), ("chargeLoop", 198, gunXf, 40, true),
        ("hit", 1, hitXf, 4, false), ("hitEnemy", 2, hitXf, 4, false), ("chargedHit", 92, hitXf, 6, false), ("chargedHitEnemy", 98, hitXf, 6, false),
        ("missileHit", 5, hitXf, 6, false), ("missileChargedHit", 190, hitXf, 6, false), ("missileCharge", 170, gunXf, 20, false), ("missileChargeLoop", 197, gunXf, 40, true),
        ("hammerFly", 134, hitXf, 20, true), ("hammerHit", 173, hitXf, 6, false)
    };
    foreach (var c in cases)
    {
        var fx = new EffectSystem();
        FxHandle h = fx.Spawn(c.Id, c.Xf);
        if (c.Ext) { fx.SetElementExtension(h, true); }
        int peak = 0;
        for (int i = 0; i < c.Steps; i++) { fx.Step(); peak = Math.Max(peak, fx.LiveParticles); }
        var list = new FxDrawList();
        fx.Draw(list, camR, camU, camB);
        FxRender.Draw(list, Path.Combine(outDir, $"fx_{c.Name}.png"), 240, 180, 78);
        // let it finish (one-shot impacts must clean up)
        fx.Detach(ref h, false);
        for (int i = 0; i < 600 && fx.LiveElements > 0; i++) { fx.Step(); }
        got.Add($"{c.Name}({c.Id}) {Read.GetEffect(c.Id)?.Name}: {peak} particles, {list.TriangleCount} tris, left {fx.LiveElements}");
        ok &= h.Valid == false && peak > 0 && list.TriangleCount > 0 && fx.LiveElements == 0;
    }
    // beam shot: fuzzBall sprite + trail ribbon
    {
        var fx = new EffectSystem();
        Particle? fuzz = EffectSystem.FindParticle("particles", "fuzzBall");
        var color = new Vector3(1f, 26 / 31f, 8 / 31f);
        if (fuzz != null) { fx.AddSingle(fuzz, new Vector3(0.1f, -0.1f, 2.5f), color, 1, 0.25f); }
        fx.AddTrail("trail", muzzle, new Vector3(0.1f, -0.1f, 2.5f), Vector3.UnitY, 122 / 4096f, color, 1);
        var list = new FxDrawList();
        fx.Draw(list, camR, camU, camB);
        FxRender.Draw(list, Path.Combine(outDir, "fx_beam.png"), 240, 180, 78);
        got.Add($"beam: fuzzBall {(fuzz != null ? "ok" : "missing")}, {list.TriangleCount} tris");
        ok &= fuzz != null && list.TriangleCount == 4;
    }
    Check("effects", ok, string.Join("; ", got));
}

// ---- helmet HUD ----
{
    var errors = new List<string>();
    HudImages img = HudImages.Load(errors);
    var st = new HudState();
    var sim = new SamusSim();
    sim.Spawn();
    for (int i = 0; i < 60; i++) { st.Step(sim); }
    st.Damaged();
    st.AddAim(20, -10);   // turning left and down: the HUD lags the other way
    var draws = new List<HudDraw>();
    var d = new HudLayout.Input { Health = 76, HealthMax = 199, FirstPerson = true, BombAmmo = 2, BoostReady = true, Weapon = 2, AmmoShots = 3, AmmoRaw = 30, AmmoMax = 50 };
    HudLayout.Build(draws, img, st, d, 16 / 9f);
    HudRender.Draw(draws, img, Path.Combine(outDir, "hud.png"), 768, 432);
    d.WheelOpen = true; d.WheelSelection = 1; d.WheelX = 0.6f; d.WheelY = 0.6f;
    for (int i = 0; i < 8; i++) { d.WheelWeapon[i] = (int)SamusSim.WeaponOrder[i]; d.WheelOwned[i] = i < 2; }
    HudLayout.Build(draws, img, st, d, 16 / 9f);
    HudRender.Draw(draws, img, Path.Combine(outDir, "hud_wheel.png"), 768, 432);
    d.WheelOpen = false;
    d.InBall = true; d.FirstPerson = false;
    sim.AltForm = true;
    for (int i = 0; i < 120; i++) { st.Step(sim); }
    HudLayout.Build(draws, img, st, d, 16 / 9f);
    HudRender.Draw(draws, img, Path.Combine(outDir, "hud_ball.png"), 768, 432);
    Check("helmet hud", errors.Count == 0 && img.Helmet >= 0 && img.Font >= 0 && img.DamageQuads != null,
        $"{img.All.Count} images, {draws.Count} draws in the ball{(errors.Count > 0 ? ", errors: " + string.Join("; ", errors) : "")}");
}

// ---- HUD art ----
{
    RgbaImage box = HudArt.BuildMessageBox(5);
    Png.Write(Path.Combine(outDir, "msgbox.png"), box);
    // 9-slice preview at a popup-like size (each DS pixel drawn 3x, like the game)
    RgbaImage sl = HudArt.BuildSliceableMessageBox();
    int pw = 300, ph = 40, bd = HudArt.SliceBorder;
    var prev = new RgbaImage(pw * 3, ph * 3);
    for (int y = 0; y < ph * 3; y++)
    {
        for (int x = 0; x < pw * 3; x++)
        {
            int dx = x / 3, dy = y / 3;
            int sx = dx < bd ? dx : dx >= pw - bd ? sl.Width - (pw - dx) : bd;
            int sy = dy < bd ? dy : dy >= ph - bd ? sl.Height - (ph - dy) : bd;
            Buffer.BlockCopy(sl.Pixels, (sy * sl.Width + sx) * 4, prev.Pixels, (y * pw * 3 + x) * 4, 4);
        }
    }
    Png.Write(Path.Combine(outDir, "msgbox_slice_preview.png"), prev);
    RgbaImage? logo = HudArt.LoadModelTexture(HudArt.Files.TitleLogo);
    if (logo != null) { Png.Write(Path.Combine(outDir, "logo.png"), logo); }
    Check("hud art", box.Width > 100 && logo != null, $"message box {box.Width}x{box.Height}, logo {logo?.Width}x{logo?.Height}");
}

// ---- sounds ----
{
    var names = new[] { "BEAM", "BEAM_CHARGE1", "BEAM_CHARGE2", "BEAM_HIT", "JUMP", "LAND_METAL", "DAMAGE1", "POWER_UP1", "LETTER_BLIP", "PLAYER_SPAWN" };
    var got = new List<string>();
    foreach (string n in names)
    {
        PcmSound? s = SoundBank.Get(n);
        got.Add(s == null ? $"{n}=missing" : $"{n}={s.Samples.Length / (float)s.SampleRate:F2}s@{s.SampleRate}{(s.Loop ? $" loop@{s.LoopStart}" : "")}");
    }
    if (SoundBank.LastError != null) { Console.WriteLine("     last sound error: " + SoundBank.LastError); }
    Check("sounds", !got.Exists(g => g.EndsWith("missing")), string.Join(", ", got));
}
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static class Png
{
    public static void Write(string path, RgbaImage img)
    {
        using var ms = new MemoryStream();
        void Chunk(string tag, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); ms.Write(len);
            var t = System.Text.Encoding.ASCII.GetBytes(tag); ms.Write(t); ms.Write(data);
            uint crc = Crc(t, data); var c = BitConverter.GetBytes(crc); Array.Reverse(c); ms.Write(c);
        }
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        void Be(int v, int o) { ihdr[o] = (byte)(v >> 24); ihdr[o + 1] = (byte)(v >> 16); ihdr[o + 2] = (byte)(v >> 8); ihdr[o + 3] = (byte)v; }
        Be(img.Width, 0); Be(img.Height, 4); ihdr[8] = 8; ihdr[9] = 6;
        Chunk("IHDR", ihdr);
        using var raw = new MemoryStream();
        for (int y = 0; y < img.Height; y++) { raw.WriteByte(0); raw.Write(img.Pixels, y * img.Width * 4, img.Width * 4); }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) { raw.Position = 0; raw.CopyTo(zs); }
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        File.WriteAllBytes(path, ms.ToArray());
    }
    static uint Crc(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte[] arr in new[] { a, b })
        {
            foreach (byte x in arr) { c ^= x; for (int k = 0; k < 8; k++) { c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; } }
        }
        return ~c;
    }
}

// HUD rectangles in order (nearest sampling, tint, alpha blend) over a gray background.
static class HudRender
{
    public static void Draw(List<HudDraw> draws, HudImages imgs, string path, int w, int h)
    {
        var img = new RgbaImage(w, h);
        for (int i = 0; i < w * h; i++) { img.Pixels[i * 4] = 70; img.Pixels[i * 4 + 1] = 80; img.Pixels[i * 4 + 2] = 95; img.Pixels[i * 4 + 3] = 255; }
        foreach (HudDraw dr in draws)
        {
            RgbaImage src = imgs.All[dr.Image];
            int x0 = (int)Math.Floor(dr.X * w), y0 = (int)Math.Floor(dr.Y * h);
            int x1 = (int)Math.Ceiling((dr.X + dr.W) * w), y1 = (int)Math.Ceiling((dr.Y + dr.H) * h);
            for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            {
                for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
                {
                    float fu = ((x + 0.5f) / w - dr.X) / dr.W, fv = ((y + 0.5f) / h - dr.Y) / dr.H;
                    int sx = (int)Math.Floor(dr.SrcX + fu * dr.SrcW), sy = (int)Math.Floor(dr.SrcY + fv * dr.SrcH);
                    if (sx < 0 || sy < 0 || sx >= src.Width || sy >= src.Height) { continue; }
                    int si = (sy * src.Width + sx) * 4, o = (y * w + x) * 4;
                    float a = src.Pixels[si + 3] / 255f * dr.A;
                    if (a <= 0) { continue; }
                    float[] t = { dr.R, dr.G, dr.B };
                    for (int k = 0; k < 3; k++)
                    {
                        img.Pixels[o + k] = (byte)Math.Clamp(src.Pixels[si + k] * t[k] * a + img.Pixels[o + k] * (1 - a), 0, 255);
                    }
                }
            }
        }
        Png.Write(path, img);
    }
}

// Effect triangles in draw order, alpha blended, no depth (same camera as SoftRender).
static class FxRender
{
    public static void Draw(FxDrawList list, string path, int w, int h, float fovDeg)
    {
        var img = new RgbaImage(w, h);
        for (int i = 0; i < w * h; i++) { img.Pixels[i * 4] = 30; img.Pixels[i * 4 + 1] = 30; img.Pixels[i * 4 + 2] = 45; img.Pixels[i * 4 + 3] = 255; }
        float f = h / 2f / MathF.Tan(fovDeg * MathF.PI / 360f);
        for (int gi = 0; gi < list.GroupCount; gi++)
        {
            FxDrawList.Group g = list.Groups[gi];
            FxTexture tex = EffectSystem.Textures[g.Texture];
            for (int t = 0; t + 2 < g.Positions.Count; t += 3)
            {
                var sp = new Vector3[3];
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = g.Positions[t + k];
                    float z = Math.Max(p.Z, 0.01f);
                    sp[k] = new Vector3(w / 2f - p.X * f / z, h / 2f - p.Y * f / z, z);
                }
                float area = (sp[1].X - sp[0].X) * (sp[2].Y - sp[0].Y) - (sp[2].X - sp[0].X) * (sp[1].Y - sp[0].Y);
                if (Math.Abs(area) < 1e-6f) { continue; }
                int x0 = (int)Math.Max(0, Math.Floor(Math.Min(sp[0].X, Math.Min(sp[1].X, sp[2].X))));
                int x1 = (int)Math.Min(w - 1, Math.Ceiling(Math.Max(sp[0].X, Math.Max(sp[1].X, sp[2].X))));
                int y0 = (int)Math.Max(0, Math.Floor(Math.Min(sp[0].Y, Math.Min(sp[1].Y, sp[2].Y))));
                int y1 = (int)Math.Min(h - 1, Math.Ceiling(Math.Max(sp[0].Y, Math.Max(sp[1].Y, sp[2].Y))));
                for (int py = y0; py <= y1; py++)
                {
                    for (int px = x0; px <= x1; px++)
                    {
                        float qx = px + 0.5f, qy = py + 0.5f;
                        float w0 = ((sp[1].X - qx) * (sp[2].Y - qy) - (sp[2].X - qx) * (sp[1].Y - qy)) / area;
                        float w1 = ((sp[2].X - qx) * (sp[0].Y - qy) - (sp[0].X - qx) * (sp[2].Y - qy)) / area;
                        float w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) { continue; }
                        Vector2 uv = g.Uvs[t] * w0 + g.Uvs[t + 1] * w1 + g.Uvs[t + 2] * w2;
                        int tx = Wrap((int)MathF.Floor(uv.X * tex.Image.Width), tex.Image.Width, tex.RepeatS);
                        int ty = Wrap((int)MathF.Floor(uv.Y * tex.Image.Height), tex.Image.Height, tex.RepeatT);
                        int ti = (ty * tex.Image.Width + tx) * 4;
                        uint c = g.Colors[t];
                        float a = tex.Image.Pixels[ti + 3] / 255f * (c >> 24) / 255f;
                        int o = (py * w + px) * 4;
                        for (int k = 0; k < 3; k++)
                        {
                            float col = tex.Image.Pixels[ti + k] / 255f * ((c >> (8 * k)) & 255);
                            img.Pixels[o + k] = (byte)Math.Clamp(col * a + img.Pixels[o + k] * (1 - a), 0, 255);
                        }
                    }
                }
            }
        }
        Png.Write(path, img);
    }
    static int Wrap(int v, int n, RepeatMode mode)
    {
        if (mode == RepeatMode.Clamp) { return Math.Clamp(v, 0, n - 1); }
        if (mode == RepeatMode.Mirror)
        {
            int p = ((v % (2 * n)) + 2 * n) % (2 * n);
            return p < n ? p : 2 * n - 1 - p;
        }
        return ((v % n) + n) % n;
    }
}

// Minimal perspective rasterizer for posed batches (camera at origin, MPH camera space: +X left, +Y up, +Z forward).
static class SoftRender
{
    public static void Draw(PosedModel m, string path, int w, int h, float fovDeg)
    {
        var img = new RgbaImage(w, h);
        var depth = new float[w * h];
        Array.Fill(depth, float.MaxValue);
        for (int i = 0; i < w * h; i++) { img.Pixels[i * 4] = 40; img.Pixels[i * 4 + 1] = 40; img.Pixels[i * 4 + 2] = 60; img.Pixels[i * 4 + 3] = 255; }
        float f = h / 2f / MathF.Tan(fovDeg * MathF.PI / 360f);
        for (int bi = 0; bi < m.BatchCount; bi++)
        {
            var b = m.Batches[bi];
            RgbaImage? tex = b.TextureKey >= 0 ? m.Textures[b.TextureKey] : null;
            for (int t = 0; t + 2 < b.Count; t += 3)
            {
                var sp = new Vector3[3];
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = b.Positions[t + k];
                    float z = Math.Max(p.Z, 0.01f);
                    sp[k] = new Vector3(w / 2f - p.X * f / z, h / 2f - p.Y * f / z, z);   // +X is left on screen
                }
                int x0 = (int)Math.Max(0, Math.Floor(Math.Min(sp[0].X, Math.Min(sp[1].X, sp[2].X))));
                int x1 = (int)Math.Min(w - 1, Math.Ceiling(Math.Max(sp[0].X, Math.Max(sp[1].X, sp[2].X))));
                int y0 = (int)Math.Max(0, Math.Floor(Math.Min(sp[0].Y, Math.Min(sp[1].Y, sp[2].Y))));
                int y1 = (int)Math.Min(h - 1, Math.Ceiling(Math.Max(sp[0].Y, Math.Max(sp[1].Y, sp[2].Y))));
                float area = (sp[1].X - sp[0].X) * (sp[2].Y - sp[0].Y) - (sp[2].X - sp[0].X) * (sp[1].Y - sp[0].Y);
                if (Math.Abs(area) < 1e-6f) { continue; }
                for (int py = y0; py <= y1; py++)
                {
                    for (int px = x0; px <= x1; px++)
                    {
                        float qx = px + 0.5f, qy = py + 0.5f;
                        float w0 = ((sp[1].X - qx) * (sp[2].Y - qy) - (sp[2].X - qx) * (sp[1].Y - qy)) / area;
                        float w1 = ((sp[2].X - qx) * (sp[0].Y - qy) - (sp[0].X - qx) * (sp[2].Y - qy)) / area;
                        float w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) { continue; }
                        float z = w0 * sp[0].Z + w1 * sp[1].Z + w2 * sp[2].Z;
                        int di = py * w + px;
                        if (z >= depth[di]) { continue; }
                        float r = 1, g = 1, bl = 1, a = 1;
                        int c0 = (t) * 4, c1 = (t + 1) * 4, c2 = (t + 2) * 4;
                        float cr = (w0 * b.Colors[c0] + w1 * b.Colors[c1] + w2 * b.Colors[c2]) / 255f;
                        float cg = (w0 * b.Colors[c0 + 1] + w1 * b.Colors[c1 + 1] + w2 * b.Colors[c2 + 1]) / 255f;
                        float cb = (w0 * b.Colors[c0 + 2] + w1 * b.Colors[c1 + 2] + w2 * b.Colors[c2 + 2]) / 255f;
                        if (tex != null)
                        {
                            Vector2 uv = b.Uvs[t] * w0 + b.Uvs[t + 1] * w1 + b.Uvs[t + 2] * w2;
                            int tx = Wrap((int)MathF.Floor(uv.X * tex.Width), tex.Width, b.RepeatS);
                            int ty = Wrap((int)MathF.Floor(uv.Y * tex.Height), tex.Height, b.RepeatT);
                            int ti = (ty * tex.Width + tx) * 4;
                            r = tex.Pixels[ti] / 255f; g = tex.Pixels[ti + 1] / 255f; bl = tex.Pixels[ti + 2] / 255f; a = tex.Pixels[ti + 3] / 255f;
                        }
                        a *= b.Colors[c0 + 3] / 255f;
                        if (a < 0.1f) { continue; }
                        if (!b.Translucent) { depth[di] = z; }
                        int o = di * 4;
                        img.Pixels[o] = (byte)Math.Clamp(r * cr * 255 * a + img.Pixels[o] * (1 - a), 0, 255);
                        img.Pixels[o + 1] = (byte)Math.Clamp(g * cg * 255 * a + img.Pixels[o + 1] * (1 - a), 0, 255);
                        img.Pixels[o + 2] = (byte)Math.Clamp(bl * cb * 255 * a + img.Pixels[o + 2] * (1 - a), 0, 255);
                    }
                }
            }
        }
        Png.Write(path, img);
    }
    static int Wrap(int v, int n, RepeatMode mode)
    {
        if (mode == RepeatMode.Clamp) { return Math.Clamp(v, 0, n - 1); }
        if (mode == RepeatMode.Mirror)
        {
            int p = ((v % (2 * n)) + 2 * n) % (2 * n);
            return p < n ? p : 2 * n - 1 - p;
        }
        return ((v % n) + n) % n;
    }
}
