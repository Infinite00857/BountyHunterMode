// BeamSystem - Samus's shots in ULTRAKILL (every MPH weapon). Flight is MPH's at a fixed 60 Hz (Core.Projectile: speed
// decay, homing steer, continuous beams, ricochets, past positions); positions live in MPH world units
// (SamusController.ToFxWorld). Ported from the testbed's playtested hunters/mph_weapons.cpp BeamSystem (spawn,
// checkHomingTargets, processOne, checkCollision, processRicochet, onCollision, checkSplashDamage, draw), itself
// MphRead (MIT) BeamProjectileEntity. Collision, damage and splash use ULTRAKILL's physics and damage calls:
// - direct hits: EnemyIdentifier.DeliverDamage, MPH damage x DamageDealtScale, headshot bonus as the crit multiplier,
//   damage falling off by distance where the weapon has a max distance; ULTRAKILL hitter per weapon (Power Beam /
//   Volt Driver "revolver", Shock Coil "zapper", Imperialist "railcannon", Judicator "nail", Magmaul "cannonball"),
//   electricity for the Volt Driver / Shock Coil, fire for the Magmaul;
// - splash: enemies within the radius take MPH's falloff (SplashDmgType) unless a wall is in between (CheckSplashDamage
//   / CheckBetweenPoints); the explosive weapons (Missiles, Battlehammer, Magmaul) follow ULTRAKILL's Explosion rules
//   (immunities, breakables, explosion knockback); MPH's splash hits Samus too (no owner check), and SelfDamage shots
//   (Judicator, Magmaul) can hit her directly after 4 frames;
// - missiles follow ULTRAKILL's rocket rules: explosion immunities (Malicious Face, Idol, blocking Soldiers),
//   Cerberus/Providence bonuses, a bigger blast on airborne enemies, explosion knockback, they glance off armor /
//   Malicious Faces and still splash on terrain; charged missiles hit like the Knuckleblaster ("heavypunch"); her own
//   blast costs Samus a little energy;
// - electric shots (Volt Driver, Shock Coil) electrocute the water they pass through (RevolverBeam.CheckWater) and the
//   charged Volt Driver powers electric switches (AttributeChecker, like the electric railcannon);
// - shots pass through ULTRAKILL's portals (Fraud);
// - the charged Power Beam has a small splash (not MPH).
// Visuals are MPH's (testbed BeamSystem::draw): Draw00 Power Beam, Draw01/02 Volt Driver (electro trails), Draw03
// Judicator (ice shard model + trail), Draw04/05 Magmaul (flying effects), Draw07 Missile, Draw08 Imperialist (sniper
// beam model), Draw09 Shock Coil (arc), Draw10 Battlehammer, plus the flying effects and collision effects.
using System.Collections.Generic;
using MphRead;
using MphSamus.Core;
using ULTRAKILL.Portal;
using UnityEngine;
using OV3 = OpenTK.Mathematics.Vector3;
using OV2 = OpenTK.Mathematics.Vector2;
using Matrix4 = OpenTK.Mathematics.Matrix4;
using MphProjectile = MphSamus.Core.Projectile;
using MphBeam = MphRead.BeamType;

namespace MphSamus
{
    public sealed class BeamSystem
    {
        // ---- TUNABLE CONSTANTS (testbed BeamSystem::draw / spawnCollisionEffect / BEAM_SOUNDS) ----
        private const float FuzzHalfSize = 0.25f;       // AddSingleParticle scale (MPH units)
        private const float Trail1Height = 122 / 4096f; // Draw00 DrawTrail1 height
        private const float Trail2Height = 204 / 4096f; // Draw03 / Draw07 DrawTrail2 height
        private const float VoltTrailHeight = 614 / 4096f, VoltChargedTrailHeight = 1024 / 4096f;
        private const float HammerTrailHeight = 81 / 4096f;   // Draw10 (Battlehammer): DrawTrail2, 2 segments
        // BEAM_DRAW_EFFECTS: the effect that flies with the shot, by draw function (0 = none)
        private static readonly int[] DrawEffects = { 0, 237, 137, 0, 211, 130, 0, 0, 0, 0, 134, 209, 64, 0, 102, 94, 96, 0, 116, 138, 183, 238, 246 };
        private const float HitEffectLift = 0.125f;     // spawn the impact this far off the surface (MPH units)
        private const float StepDt = 1 / 60f;
        private const int MaxSteps = 8;
        private const float HomingBeepVolume = 16 / 127f;   // MISSILE_CHARGE3
        private const float ContinuousToleranceMax = 4094 / 4096f;   // continuous homing: the cone narrows to this at range
        private const int ShockCoilDgn = 47, MagmaulBounceDgn = 43, JudicatorRicochetDgn = 44;

        private readonly SamusController _owner;
        private readonly List<Shot> _shots = new List<Shot>();
        private readonly List<SniperBeam> _sniper = new List<SniperBeam>();
        private readonly System.Random _rng = new System.Random();
        private static Particle _fuzz;
        private static bool _fuzzLooked;
        private float _acc;
        private bool _beep;
        private int _frame;
        // Shock Coil: the hit timer (Message.Impact) drives its DGN sound; reset when nothing was hit last step
        private EnemyIdentifier _shockTarget;
        private int _shockTimer, _timeSinceHit = 0xFFFF;
        private float _zapCharge;   // owner: the Shock Coil works like the red nailgun's cable - held on one enemy, it charges a zap
        private const float ZapChargeTime = 5;            // Zapper: charge rises 1/s to 5, then Zap()
        private const float BranchRadius = 8;             // Shock Coil branching off machines (MPH units)
        private const int BranchCount = 3;
        private const int BranchDepth = 4;                // owner: machines it reaches branch on, up to 4 hops
        private readonly string[] _shockSounds = new string[3];

        private sealed class Shot
        {
            public MphProjectile P;
            public EnemyIdentifier Target;
            public readonly HashSet<EnemyIdentifier> Hit = new HashSet<EnemyIdentifier>();
            public OV3 Color;
            public OV3 Up;          // spawn up vector (ice shard pose)
            public bool Super;      // direct hit on an airborne enemy: bigger blast (rocket rule)
            public FxHandle Fx;     // the effect flying with the shot (DrawEffects)
            public ModelView Model; // Judicator ice shard
            public WeaponInfo Rico; // ricochet weapon still to fire (Judicator), cleared once used
            public readonly List<(OV3 from, OV3 to)> Branches = new List<(OV3, OV3)>();   // Shock Coil arcs off a machine
            public Collider BounceCol;          // what a missile just glanced off (ignored for a few steps)
            public EnemyIdentifier BounceEid;
            public int BounceSteps;
            public readonly HashSet<Water> Zapped = new HashSet<Water>();   // waters this electric shot electrocuted
            public bool CheckedStartWater;
            public MphBeam Weapon => P.Shot.Weapon;
        }

        private sealed class SniperBeam
        {
            public ModelView View;
            public int Life;
        }

        public BeamSystem(SamusController owner)
        {
            _owner = owner;
        }

        public bool ContinuousAlive { get; private set; }

        // BeamSystem::spawn: one or more projectiles (spread); continuous homing picks its target now.
        // Returns whether a continuous beam locked on (the Shock Coil sound).
        public bool Spawn(ShotEvent e, Vector3 muzzle, Vector3 dir)
        {
            OV3 d = SamusController.ToMph(dir);
            d = d.LengthSquared > 0 ? d.Normalized() : OV3.UnitZ;
            bool locked = false;
            int count = Mathf.Max(1, e.Projectiles);
            for (int i = 0; i < count; i++)
            {
                OV3 di = MphProjectile.SpreadDirection(d, e.Spread, _rng);
                Shot s = Add(e, _owner.ToFxWorld(muzzle), di);
                if (s.P.Homing)
                {
                    s.Target = e.Continuous ? FindContinuousTarget(s.P) : FindHomingTarget(s.P);
                    locked |= e.Continuous && s.Target != null;
                    if (s.Target != null && !e.Continuous) { Plugin.Event($"{e.Weapon} homing on {s.Target.enemyType}"); }
                }
            }
            if (e.Continuous) { UpdateShockSound(true, locked); }
            return locked;
        }

        private Shot Add(ShotEvent e, OV3 pos, OV3 dir)
        {
            var s = new Shot { P = new MphProjectile(e, pos, dir), Color = Bgr555(e.Color), Rico = e.RicochetWeapon };
            OV3 right = dir.X != 0 || dir.Z != 0 ? new OV3(dir.Z, 0, -dir.X).Normalized() : OV3.UnitX;
            s.Up = OV3.Cross(dir, right).Normalized();
            int fx = e.DrawFuncId >= 0 && e.DrawFuncId < DrawEffects.Length ? DrawEffects[e.DrawFuncId] : 0;
            if (fx != 0)
            {
                s.Fx = _owner.WorldFx.Spawn(fx, FlightTransform(s.P));
                _owner.WorldFx.SetElementExtension(s.Fx, true);
            }
            if (e.DrawFuncId == 3) { s.Model = NewModel("iceShard"); }
            _shots.Add(s);
            return s;
        }

        private ModelView NewModel(string name)
        {
            try
            {
                var inst = Read.GetModelInstance(name);
                if (inst.Model.AnimationGroups.Node.Count > 0 || inst.Model.AnimationGroups.Material.Count > 0)
                {
                    inst.SetAnimation(0, AnimFlags.NoLoop);
                }
                return new ModelView(new PosedModel(inst), "MPH " + name, null, 0) { Scale = _owner.K };
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"{name}: {ex.Message}");
                return null;
            }
        }

        public void Update(float dt)
        {
            if (!_fuzzLooked)
            {
                _fuzzLooked = true;
                _fuzz = EffectSystem.FindParticle("particles", "fuzzBall");
            }
            _acc = Mathf.Min(_acc + dt, StepDt * MaxSteps);
            while (_acc >= StepDt)
            {
                _acc -= StepDt;
                _frame++;
                if (_timeSinceHit != 0xFFFF && ++_timeSinceHit > 1)
                {
                    _shockTimer = 0;
                    _shockTarget = null;
                    _zapCharge = 0;
                }
                for (int i = _shots.Count - 1; i >= 0; i--)
                {
                    Shot s = _shots[i];
                    bool alive = StepShot(s);
                    if (s.Fx.Valid)
                    {
                        // OnCollision detaches the flying effect (it finishes on its own); otherwise it follows the shot
                        if (s.P.Collided || !alive) { _owner.WorldFx.Detach(ref s.Fx, true); }
                        else { _owner.WorldFx.SetTransform(s.Fx, FlightTransform(s.P)); }
                    }
                    if (!alive)
                    {
                        s.Model?.Destroy();
                        _shots.RemoveAt(i);
                    }
                }
                StepSniperBeams();
            }
            bool beep = false, continuous = false;
            foreach (Shot s in _shots)
            {
                Draw(s, _acc / StepDt);
                beep |= s.Weapon == MphBeam.Missile && s.P.Charged && s.P.Homing && !s.P.Collided;
                continuous |= s.P.Shot.Continuous;
            }
            ContinuousAlive = continuous;
            if (!continuous) { UpdateShockSound(false, false); }   // StopContinuousBeamSfx
            // BeamSfx Homing: the charged homing missile's beep loops while one is flying
            if (beep != _beep)
            {
                if (beep) { _owner.Audio.StartLoop("MISSILE_CHARGE3", HomingBeepVolume); }
                else { _owner.Audio.StopLoop("MISSILE_CHARGE3"); }
                _beep = beep;
            }
        }

        private Vector3 U(OV3 mph) => _owner.FromFxWorld(mph);

        private static Matrix4 FlightTransform(MphProjectile p) =>
            EffectSystem.Transform(EffectSystem.CrossVector(p.Direction), p.Direction, p.Position);

        // one 60 Hz step (BeamProjectileEntity.Process + CheckCollision); false = remove
        private bool StepShot(Shot s)
        {
            MphProjectile p = s.P;
            if (p.Collided)
            {
                p.Step(null);
                return p.Lifespan > 0;
            }
            if (s.Target != null && (s.Target.dead || !s.Target.isActiveAndEnabled)) { s.Target = null; }
            if (s.BounceSteps > 0) { s.BounceSteps--; }
            bool firstFrame = p.Age == 0;
            OV3? continuousTarget = !p.Shot.Continuous ? (OV3?)null
                : s.Target != null ? _owner.ToFxWorld(TargetPoint(s.Target)) : (OV3?)null;
            p.Step(continuousTarget);
            if (p.Shot.Continuous && !firstFrame) { return p.Lifespan > 0; }   // continuous beams collide once
            Vector3 from = U(p.BackPosition), to = U(p.Position);
            bool outOfRange = false;
            if (p.MaxDistanceHit() is float pct)
            {
                to = Vector3.Lerp(from, to, pct);
                outOfRange = true;
            }
            ZapWater(s, from, to);
            if (ThroughPortal(s, from, ref to, out bool portalHit)) { return true; }
            if (portalHit) { return true; }
            if (Collide(s, from, to)) { return true; }
            if (outOfRange)
            {
                // CheckCollision MaxDistance: ends there without a collision effect (unless ForceEffect)
                if (p.Shot.ForceEffect) { Impact(s, to, -SamusController.ToUnity(p.Direction), entity: true); }
                p.Collide(_owner.ToFxWorld(to));
                Detonate(s, to, null, false);
                if (p.Shot.DrawFuncId == 8) { SpawnSniperBeam(p); }
                return true;
            }
            if (!p.Shot.Continuous && s.Target != null && !p.Steer(_owner.ToFxWorld(TargetPoint(s.Target)))) { s.Target = null; }
            if (p.Lifespan <= 0 && !p.Shot.Continuous)
            {
                // lifespan over in the air: the effect plays without a splat, and the shot's splash goes off
                Impact(s, to, -SamusController.ToUnity(p.Direction), entity: true);
                Detonate(s, to, null, false);
                if (p.Shot.DrawFuncId == 8) { SpawnSniperBeam(p); }
                p.Collide(p.Position);
            }
            return true;
        }

        // sphere sweep along this step's segment (+ Samus herself for SelfDamage shots); true = hit something
        private bool Collide(Shot s, Vector3 from, Vector3 to)
        {
            Vector3 step = to - from;
            float dist = step.magnitude;
            if (dist <= 0) { return false; }
            float radius = Mathf.Max(s.P.Shot.Radius * _owner.K, 0.05f);
            float selfAt = SelfHitDistance(s, from, to, radius);
            // CheckCollision: the room by a thin ray along the segment (MPH), targets by the shot's radius - a sphere
            // against walls caught bounced Magmaul shells on the floor they had just left
            var hits = new List<RaycastHit>(Physics.SphereCastAll(from, radius, step / dist, dist, LayerMaskDefaults.Get(LMD.Enemies),
                QueryTriggerInteraction.Collide));
            hits.AddRange(Physics.RaycastAll(from, step / dist, dist, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Collide));
            if (DestroysProjectiles(s))
            {
                hits.AddRange(Physics.SphereCastAll(from, radius, step / dist, dist, 1 << ProjectileLayer, QueryTriggerInteraction.Collide));
            }
            if (s.Weapon == MphBeam.Missile)
            {
                // armor on any layer (Grenade.Collision only skips the projectile layers 14 / 20): the Hideous Mass's
                // shell isn't on the enemy or environment layers
                int armorMask = ~((1 << 2) | (1 << ProjectileLayer) | (1 << 20) | LayerMaskDefaults.Get(LMD.Enemies) | LayerMaskDefaults.Get(LMD.Environment));
                foreach (RaycastHit a in Physics.SphereCastAll(from, radius, step / dist, dist, armorMask, QueryTriggerInteraction.Collide))
                {
                    if (a.collider != null && a.collider.CompareTag("Armor")) { hits.Add(a); }
                }
            }
            hits.Sort((a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit h in hits)
            {
                if (h.collider == null) { continue; }
                if (selfAt >= 0 && selfAt < h.distance) { break; }
                // SphereCastAll reports overlaps at the start with distance 0 and no point/normal
                Vector3 point = h.distance > 0 ? h.point : from;
                Vector3 normal = h.distance > 0 ? h.normal : -step / dist;
                if (h.collider.gameObject.layer == ProjectileLayer)
                {
                    // an attack V1's punch parries: a charged Power Beam shot parries it too (owner: no heal - pickups
                    // knocked out instead). Punch.TryParryProjectile's targets: the rigidbody's object, or a ParryHelper's.
                    Transform target = h.collider.attachedRigidbody != null ? h.collider.attachedRigidbody.transform : h.collider.transform;
                    if (target.TryGetComponent(out ParryHelper helper) && helper.target != null) { target = helper.target; }
                    // the Insurrectionist's boulder mid swing (a launchable Cannonball): sent back along the shot
                    if (s.Weapon == MphBeam.PowerBeam && s.P.Charged && target.TryGetComponent(out Cannonball ball) && ball.launchable && !ball.launched)
                    {
                        EnemyIdentifier owner = ball.sisy != null ? ball.sisy.GetComponent<EnemyIdentifier>() : null;
                        if (MonoSingleton<TimeController>.TryGetInstance(out TimeController tc) && tc != null) { tc.ParryFlash(); }
                        ball.transform.forward = step / dist;
                        ball.Launch();
                        Plugin.Event($"charged shot parried {(owner != null ? owner.enemyType + "'s " : "")}{ball.name}");
                        if (owner != null) { MphDrops.OnParried(owner, point); }
                        Impact(s, point, normal, entity: true);
                        s.P.Collide(_owner.ToFxWorld(point));
                        return true;
                    }
                    if (s.Weapon == MphBeam.PowerBeam && s.P.Charged && target.TryGetComponent(out ParryReceiver receiver) && receiver.enabled)
                    {
                        EnemyIdentifier owner = h.collider.GetComponentInParent<EnemyIdentifier>();
                        if (MonoSingleton<TimeController>.TryGetInstance(out TimeController tc) && tc != null) { tc.ParryFlash(); }
                        receiver.Parry();
                        Plugin.Event($"charged shot parried {(owner != null ? owner.enemyType.ToString() : h.collider.name)}");
                        if (owner != null) { MphDrops.OnParried(owner, point); }
                        Impact(s, point, normal, entity: true);
                        s.P.Collide(_owner.ToFxWorld(point));
                        return true;
                    }
                    // Power Beam shots don't parry projectiles, they destroy the ones V1 could parry
                    global::Projectile proj = h.collider.GetComponentInParent<global::Projectile>();
                    if (proj == null || proj.friendly || proj.playerBullet || proj.unparryable || proj.undeflectable) { continue; }
                    Plugin.Event($"charged shot destroyed {proj.name}");
                    proj.Break();
                    Impact(s, point, normal, entity: true);
                    s.P.Collide(_owner.ToFxWorld(point));
                    return true;
                }
                h.collider.TryGetComponent(out EnemyIdentifierIdentifier eidid);
                EnemyIdentifier hitEid = eidid != null ? eidid.eid : null;
                // electric switches (AttributeChecker: the electric railcannon's targets, e.g. in 1-2) take the charged
                // Volt Driver
                if (hitEid == null && TryPowerSwitch(s, h.collider, point)) { continue; }
                if (s.BounceSteps > 0 && (h.collider == s.BounceCol || hitEid != null && hitEid == s.BounceEid)) { continue; }
                // armor (the Hideous Mass's shell...) bounces missiles like ULTRAKILL rockets (Grenade: any Armor-tagged
                // collider, trigger or not, on an enemy or not)
                bool armor = h.collider.CompareTag("Armor");
                bool face = hitEid != null && !hitEid.dead && hitEid.enemyType == EnemyType.MaliciousFace && !hitEid.isGasolined;
                if (s.Weapon == MphBeam.Missile && (armor || face))
                {
                    Bounce(s, h.collider, hitEid, point, normal, step / dist, radius);
                    return true;
                }
                if (hitEid != null)
                {
                    if (hitEid.dead || s.Hit.Contains(hitEid)) { continue; }
                    s.Hit.Add(hitEid);
                    HitEnemy(s, eidid, h.collider, point);
                    Impact(s, point, normal, entity: true);
                    if (s.Weapon == MphBeam.Imperialist) { continue; }   // owner: pierces enemies like the railcannon
                    // OnCollision: continuous beams don't stop (the arc keeps drawing to the target until replaced)
                    if (!s.P.Shot.Continuous) { s.P.Collide(_owner.ToFxWorld(point)); }
                    Detonate(s, point, eidid.eid, false);
                    if (s.P.Shot.DrawFuncId == 8) { SpawnSniperBeam(s.P); }
                    return true;
                }
                if (h.collider.isTrigger) { continue; }
                if (LayerMaskDefaults.IsMatchingLayer(h.collider.gameObject.layer, LMD.Environment))
                {
                    HitSurface(s, h);
                    if (s.P.Shot.Ricochet && s.P.Shot.DrawFuncId != 8)
                    {
                        // ProcessRicochet: bounce off with speed loss (Magmaul)
                        OV3 n = SamusController.ToMph(normal);
                        n = n.LengthSquared > 0 ? n.Normalized() : -s.P.Direction;
                        s.P.Ricochet(_owner.ToFxWorld(point), n);
                        PlayRicochetSfx(s, point);
                        Plugin.Event($"{s.Weapon} bounced off {h.collider.name}, speed {s.P.Speed * 60:F1} u/s");
                        return true;
                    }
                    if (s.P.Shot.Ricochet || s.Rico == null) { Impact(s, point, normal, entity: false); }
                    else { PlayRicochetSfx(s, point); }   // the ricochet weapon's bounce sound instead of a hit
                    if (!s.P.Shot.Continuous) { s.P.Collide(_owner.ToFxWorld(point)); }
                    Detonate(s, point, null, false);
                    FireRicochetWeapon(s, point, normal);
                    if (s.P.Shot.DrawFuncId == 8) { SpawnSniperBeam(s.P); }
                    return true;
                }
            }
            if (selfAt >= 0)
            {
                Vector3 p = from + step / dist * selfAt;
                int energy = s.P.Shot.Damage;
                Plugin.Event($"own {s.Weapon} shot hit Samus");
                if (energy > 0) { _owner.SelfDamage(energy, $"own {s.Weapon} shot"); }
                Impact(s, p, -step / dist, entity: true);
                // (continuous beams don't stop: the Shock Coil's arc keeps drawing to Samus, as on enemies)
                if (!s.P.Shot.Continuous) { s.P.Collide(_owner.ToFxWorld(p)); }
                Detonate(s, p, null, true);
                return true;
            }
            return false;
        }

        // CheckCollision (players): SelfDamage shots can hit their owner after SelfDamageAge; distance along the
        // segment where it reaches Samus's capsule, or -1
        private float SelfHitDistance(Shot s, Vector3 from, Vector3 to, float radius)
        {
            if (!s.P.Shot.SelfDamage || s.P.Age < MphProjectile.SelfDamageAge || _owner.IsDead) { return -1; }
            if (!(_owner.BodyCollider is CapsuleCollider cap)) { return -1; }
            Transform t = cap.transform;
            float scale = t.lossyScale.y;
            float half = Mathf.Max(0, cap.height * scale / 2 - cap.radius * scale);
            Vector3 c = t.TransformPoint(cap.center);
            Vector3 a = c - t.up * half, b = c + t.up * half;
            float reach = cap.radius * scale + radius;
            // sample the segment (short at 60 Hz) against the capsule axis
            const int n = 8;
            for (int i = 0; i <= n; i++)
            {
                Vector3 q = Vector3.Lerp(from, to, i / (float)n);
                Vector3 ab = b - a;
                float k = Mathf.Clamp01(Vector3.Dot(q - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                if ((q - (a + ab * k)).sqrMagnitude <= reach * reach) { return Vector3.Distance(from, q); }
            }
            return -1;
        }

        // the closest enemy to the flight direction within range and tolerance (CheckHomingTargets)
        private EnemyIdentifier FindHomingTarget(MphProjectile p)
        {
            if (!MonoSingleton<EnemyTracker>.TryGetInstance(out EnemyTracker tracker) || tracker == null) { return null; }
            EnemyIdentifier best = null;
            float bestDot = p.Shot.HomingTolerance;
            foreach (EnemyIdentifier eid in tracker.GetCurrentEnemies())
            {
                if (eid == null || eid.dead || eid.blessed) { continue; }
                float d = p.HomingScore(_owner.ToFxWorld(TargetPoint(eid)), bestDot);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = eid;
                }
            }
            return best;
        }

        // CheckHomingTargets (continuous): the cone narrows with distance (tolerance at the muzzle, 4094/4096 at range),
        // and a wall in between blocks it
        private EnemyIdentifier FindContinuousTarget(MphProjectile p)
        {
            if (!MonoSingleton<EnemyTracker>.TryGetInstance(out EnemyTracker tracker) || tracker == null) { return null; }
            if (p.Velocity.LengthSquared < 1e-12f) { return null; }
            OV3 velDir = p.Velocity.Normalized();
            float tol = p.Shot.HomingTolerance, range = p.Shot.HomingRange, cur = tol;
            EnemyIdentifier best = null;
            int env = LayerMaskDefaults.Get(LMD.Environment);
            foreach (EnemyIdentifier eid in tracker.GetCurrentEnemies())
            {
                if (eid == null || eid.dead || eid.blessed) { continue; }
                Vector3 tpU = TargetPoint(eid);
                OV3 between = _owner.ToFxWorld(tpU) - p.Position;
                float dist = between.Length;
                if (dist <= 0 || dist > range) { continue; }
                float div1 = OV3.Dot(between, velDir) / dist;
                if (div1 < cur) { continue; }
                float div2 = Mathf.Min(dist / range, 1);
                if (div1 < tol + div2 * (ContinuousToleranceMax - tol)) { continue; }
                if (Physics.Linecast(U(p.Position), tpU, env, QueryTriggerInteraction.Ignore)) { continue; }
                cur = div1;
                best = eid;
            }
            return best;
        }

        private static Vector3 TargetPoint(EnemyIdentifier eid)
        {
            Transform c = eid.GetCenter();
            return c != null ? c.position : eid.transform.position;
        }

        // BeamProjectileEntity.SpawnCollisionEffect: up = the surface normal, lifted off the surface; entity hits and
        // expiry use the NoSplat versions (BeamEffectEntity.Create); the Imperialist's points back along the shot
        private void Impact(Shot s, Vector3 point, Vector3 normal, bool entity)
        {
            int col = s.P.Shot.ColEffect;
            if (col != 255)
            {
                int id = col - 3;
                if (entity)
                {
                    if (id == 1) { id = 2; }
                    else if (id == 92) { id = 98; }
                }
                OV3 up = s.Weapon == MphBeam.Imperialist ? -s.P.Direction : SamusController.ToMph(normal);
                up = up.LengthSquared > 0 ? up.Normalized() : OV3.UnitY;
                OV3 pos = _owner.ToFxWorld(point) + up * HitEffectLift;
                _owner.WorldFx.SpawnOneShot(id, EffectSystem.Transform(EffectSystem.CrossVector(up), up, pos));
            }
            if (!s.P.Shot.Continuous) { PlayHitSfx(s, point); }
        }

        // PlayHitSfx (BEAM_SOUNDS)
        private void PlayHitSfx(Shot s, Vector3 point)
        {
            WeaponSounds ws = WeaponSounds.ByBeam[(int)s.Weapon];
            int hitId = s.P.Charged ? ws.ChargeHit : ws.Hit;
            string name = SamusController.SoundName(hitId);
            if (name != null) { _owner.Audio.PlayAt(name, point, (s.P.Charged ? ws.ChargeHitVol : ws.HitVol) / 127f); }
        }

        // PlayRicochetSfx: Judicator = DGN 44 (amount A random), Magmaul = DGN 43 (amount A = speed), charged Magmaul = hit
        private void PlayRicochetSfx(Shot s, Vector3 point)
        {
            if (s.P.Charged && s.Weapon == MphBeam.Magmaul) { PlayHitSfx(s, point); return; }
            int dgn;
            float amountA;
            if (s.Weapon == MphBeam.Judicator || s.Rico != null)
            {
                dgn = JudicatorRicochetDgn;
                amountA = _rng.Next(0xFFFF);
            }
            else if (s.Weapon == MphBeam.Magmaul)
            {
                dgn = MagmaulBounceDgn;
                amountA = Mathf.Min(0xFFFF * (s.P.Speed * 2) / (3300 / 4096f), 0xFFFF);
            }
            else
            {
                return;
            }
            for (int i = 0; i < DgnSounds.EntryCount(dgn); i++)
            {
                DgnResult d = DgnSounds.Eval(dgn, i, amountA, 0);
                string name = SamusController.SoundName(d.Sample);
                if (name != null && d.Volume > 0) { _owner.Audio.PlayAt(name, point, d.Volume); }
            }
        }

        // PlayBeamShotSfx (continuous): the Shock Coil's DGN 47 loops - amount A grows with the time on one target
        private void UpdateShockSound(bool fired, bool homing)
        {
            float amountA = homing ? Mathf.Min(65535, 0x3FFF + 0x3FFF * _shockTimer / 60f) : 0;
            int n = Mathf.Min(DgnSounds.EntryCount(ShockCoilDgn), _shockSounds.Length);
            for (int i = 0; i < _shockSounds.Length; i++)
            {
                DgnResult d = fired && i < n ? DgnSounds.Eval(ShockCoilDgn, i, amountA, 0) : default;
                string want = d.Volume > 0 ? SamusController.SoundName(d.Sample) : null;
                if (_shockSounds[i] != null && _shockSounds[i] != want) { _owner.Audio.StopLoop(_shockSounds[i]); }
                _shockSounds[i] = want;
                if (want != null) { _owner.Audio.SetLoop(want, d.Volume, d.Pitch, raw: true); }
            }
        }

        // ULTRAKILL hitter / attributes for each MPH weapon (style + enemy reactions)
        private static string HitterFor(MphBeam b) => b switch
        {
            MphBeam.ShockCoil => "zapper",
            MphBeam.Imperialist => "railcannon",
            MphBeam.Judicator => "nail",
            MphBeam.Magmaul => "cannonball",
            _ => "revolver",
        };

        private const int ProjectileLayer = 14;   // ULTRAKILL's projectile layer

        private static bool DestroysProjectiles(Shot s) => s.Weapon == MphBeam.PowerBeam;   // owner: charged or not

        // the enemy is mid melee attack (Machine / Zombie / Statue parry window), and V1's punch exists to run the parry
        private static bool CanParry(EnemyIdentifier eid, Collider col)
        {
            if (!MonoSingleton<FistControl>.TryGetInstance(out FistControl fc) || fc == null || fc.currentPunch == null) { return false; }
            Enemy en = eid.GetComponent<Enemy>() ?? eid.GetComponentInParent<Enemy>();
            if (en == null) { return false; }
            return en.parryable || en.partiallyParryable && en.parryables != null && en.parryables.Contains(col.transform);
        }

        private void HitEnemy(Shot s, EnemyIdentifierIdentifier eidid, Collider col, Vector3 point)
        {
            ShotEvent e = s.P.Shot;
            bool charged = s.P.Charged;
            EnemyIdentifier eid = eidid.eid;
            // CheckCollision: damage falls off over the max distance (DamageInterp), headshot bonus as the crit
            float baseDmg = e.Damage;
            if (e.MaxDistance > 0)
            {
                float traveled = (s.P.Position - s.P.SpawnPosition).Length;
                baseDmg = MphProjectile.Interpolate(e.DamageInterp, e.Damage, 0, traveled / e.MaxDistance);
            }
            float dmg = baseDmg * Plugin.DamageDealtScale.Value;
            float crit = e.Damage > 0 ? (float)e.HeadshotDamage / e.Damage - 1 : 0;
            Vector3 dir = SamusController.ToUnity(s.P.Direction).normalized;
            if (s.Weapon == MphBeam.ShockCoil)
            {
                // Message.Impact: the Shock Coil's timer counts steps on the same target
                _timeSinceHit = 0;
                if (eid == _shockTarget) { _shockTimer++; }
                else { _shockTimer = 0; _shockTarget = eid; _zapCharge = 0; }
                _zapCharge += StepDt;
            }
            if (s.Weapon == MphBeam.Battlehammer || s.Weapon == MphBeam.Magmaul)
            {
                // explosive shells: the explosion rules (immunities)
                if (s.Weapon == MphBeam.Magmaul) { eid.hitterAttributes.Add(HitterAttribute.Fire); }
                ExplosionHit(eid, col, point, point, dmg, s.Weapon == MphBeam.Magmaul ? "cannonball" : "explosion", BlastForce(dir) * 0.25f);
                if (s.Weapon == MphBeam.Magmaul) { EnemyEffects.Ignite(eid); }   // owner: the Magmaul ignites
                Plugin.Event($"hit {eid.enemyType} ({col.name}) with the {s.Weapon} for {dmg:F2}, health {eid.health:F2}{(eid.dead ? " - killed" : "")}");
                return;
            }
            if (s.Weapon == MphBeam.Missile)
            {
                // a rocket's direct hit: the explosion rules (immunities), bigger blast on an airborne enemy
                s.Super = !eid.flying && (eid.gce != null && !eid.gce.onGround || eid.timeSinceSpawned <= 0.15f);
                // knockback like a rocket's explosion (Explosion.Collide: away from the blast, lifted, 50000)
                ExplosionHit(eid, col, point, point, dmg, charged ? "heavypunch" : "explosion", BlastForce(dir));
                Plugin.Event($"hit {eid.enemyType} ({col.name}) with a{(charged ? " charged" : "")} missile for {dmg:F2}{(s.Super ? " (airborne: big blast)" : "")}, health {eid.health:F2}{(eid.dead ? " - killed" : "")}");
                return;
            }
            if (s.Weapon == MphBeam.Judicator) { JudicatorChill.Apply(eid, Plugin.JudicatorChill.Value * (charged ? 1.5f : 1)); }
            if (s.Weapon == MphBeam.VoltDriver)
            {
                // owner: electric damage like the red nailgun's Zapper; charged shots also set off an aftershock
                EnemyEffects.Electrocute(eid, col, point, dmg);
                if (charged) { EnemyEffects.Aftershock(eid); }   // owner: aftershocks on charged shots only
                Plugin.Event($"hit {eid.enemyType} ({col.name}) with the Volt Driver{(charged ? " charged (zapper + aftershock)" : " (zapper)")} for {dmg:F2}, health {eid.health:F2}{(eid.dead ? " - killed" : "")}");
                return;
            }
            if (s.Weapon == MphBeam.ShockCoil)
            {
                ShockCoilHit(s, eid, col, point, dmg);
                return;
            }
            if (dmg <= 0) { return; }
            // (like the Feedbacker) a charged Power Beam shot landing in an enemy's parry window (a melee attack) is
            // a parry - ULTRAKILL's own ("punch" during Enemy.parryable: flash, style, the attack cancelled); V1's
            // full heal is off for Samus, the hit knocks pickups out instead (owner)
            bool parry = s.Weapon == MphBeam.PowerBeam && charged && CanParry(eid, col);
            eid.hitter = parry ? "punch" : HitterFor(s.Weapon);
            Vector3 force = s.Weapon == MphBeam.PowerBeam && charged
                ? dir * Plugin.ChargedKnockback.Value * Mathf.Max(0.25f, e.ChargeFraction)
                : dir * (e.Knockback > 0 ? 1000f + e.Knockback * 4000f : 1000f);
            eid.DeliverDamage(col.gameObject, force, point, dmg, false, crit, null);
            if (parry)
            {
                Plugin.Event($"charge beam parried {eid.enemyType}");
                MphDrops.OnParried(eid, point);
            }
            if (s.Weapon != MphBeam.ShockCoil)
            {
                Plugin.Event($"hit {eid.enemyType} ({col.name}) with {e.Weapon}{(charged ? " charged" : "")} for {dmg:F2} ({eid.hitter}), health {eid.health:F2}{(eid.dead ? " - killed" : "")}");
            }
            if (s.Weapon == MphBeam.PowerBeam && charged && !eid.dead && col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic)
            {
                col.attachedRigidbody.AddForce(force * 0.02f, ForceMode.VelocityChange);
            }
        }

        // Shock Coil on an enemy: MPH's per-frame damage (electric), the red nailgun's cable charge (held on one enemy for
        // ZapChargeTime it electrocutes it, or sooner if it dies), and on machines it branches into the enemies around
        private void ShockCoilHit(Shot s, EnemyIdentifier eid, Collider col, Vector3 point, float dmg)
        {
            bool wasAlive = !eid.dead;
            if (dmg > 0)
            {
                eid.hitter = "zapper";
                eid.hitterAttributes.Add(HitterAttribute.Electricity);
                eid.DeliverDamage(col.gameObject, Vector3.zero, point, dmg, false, 0, null);
            }
            if (_zapCharge >= ZapChargeTime || wasAlive && eid.dead && _zapCharge > 0.5f)
            {
                _zapCharge = 0;
                EnemyEffects.Electrocute(eid, col, point, Plugin.ShockCoilZapDamage.Value);
                Plugin.Event($"Shock Coil zapped {eid.enemyType} for {Plugin.ShockCoilZapDamage.Value:F1}, health {eid.health:F2}{(eid.dead ? " - killed" : "")}");
            }
            // owner: on a machine the beam branches into up to BranchCount enemies around it, and every machine it reaches
            // branches on again, up to BranchDepth hops
            s.Branches.Clear();
            if (eid.enemyClass != EnemyClass.Machine || !MonoSingleton<EnemyTracker>.TryGetInstance(out EnemyTracker tracker) || tracker == null) { return; }
            float reach = BranchRadius * _owner.K;
            int env = LayerMaskDefaults.Get(LMD.Environment);
            var hit = new HashSet<EnemyIdentifier> { eid };
            var frontier = new List<EnemyIdentifier> { eid };
            List<EnemyIdentifier> enemies = tracker.GetCurrentEnemies();
            for (int depth = 0; depth < BranchDepth && frontier.Count > 0; depth++)
            {
                var next = new List<EnemyIdentifier>();
                foreach (EnemyIdentifier src in frontier)
                {
                    Vector3 from = TargetPoint(src);
                    var near = new List<(float d, EnemyIdentifier e, Vector3 p)>();
                    foreach (EnemyIdentifier other in enemies)
                    {
                        if (other == null || hit.Contains(other) || other.dead || other.blessed) { continue; }
                        Vector3 tp = TargetPoint(other);
                        float d = Vector3.Distance(from, tp);
                        if (d > reach || Physics.Linecast(from, tp, env, QueryTriggerInteraction.Ignore)) { continue; }
                        near.Add((d, other, tp));
                    }
                    near.Sort((a, b) => a.d.CompareTo(b.d));
                    for (int i = 0; i < near.Count && i < BranchCount; i++)
                    {
                        EnemyIdentifier other = near[i].e;
                        hit.Add(other);
                        if (dmg > 0)
                        {
                            other.hitter = "zapper";
                            other.hitterAttributes.Add(HitterAttribute.Electricity);
                            other.DeliverDamage(other.gameObject, Vector3.zero, near[i].p, dmg, false, 0, null);
                        }
                        s.Branches.Add((_owner.ToFxWorld(from), _owner.ToFxWorld(near[i].p)));
                        if (other.enemyClass == EnemyClass.Machine && !other.dead) { next.Add(other); }
                    }
                }
                frontier = next;
            }
        }

        // the charged Magmaul's wider ignition (owner): sets enemies around the blast on fire, no extra damage
        private void IgniteAround(Vector3 at, float radius, EnemyIdentifier direct)
        {
            if (!MonoSingleton<EnemyTracker>.TryGetInstance(out EnemyTracker tracker) || tracker == null) { return; }
            int env = LayerMaskDefaults.Get(LMD.Environment);
            foreach (EnemyIdentifier eid in tracker.GetCurrentEnemies())
            {
                if (eid == null || eid.dead || eid == direct) { continue; }
                Vector3 tp = TargetPoint(eid);
                if (Vector3.Distance(at, tp) > radius || Physics.Linecast(at, tp, env, QueryTriggerInteraction.Ignore)) { continue; }
                EnemyEffects.Ignite(eid);
            }
        }

        // ---- TUNABLE CONSTANTS (ULTRAKILL rocket rules) ----
        private const float SuperBlastScale = 1.5f;   // Grenade.Explode "big": maxSize x 1.5

        // Grenade.Collision: rockets glance off Malicious Faces (unless gasolined) and armor, with the ineffective sound
        // (pushed clear by its radius and ignoring what it hit for a few steps: a missile still touching a Malicious Face
        // bounced back into it every step and stuck)
        private const int BounceIgnoreSteps = 10;
        private void Bounce(Shot s, Collider col, EnemyIdentifier eid, Vector3 point, Vector3 normal, Vector3 dir, float radius)
        {
            Vector3 n = normal.sqrMagnitude > 1e-6f && Vector3.Dot(normal, dir) < 0 ? normal.normalized : -dir;
            if (eid != null)
            {
                // off the enemy's surface, away from its center
                Vector3 away = point - col.bounds.center;
                if (away.sqrMagnitude > 1e-6f && Vector3.Dot(away, n) < 0) { n = away.normalized; }
            }
            Vector3 r = Vector3.Reflect(dir, n).normalized;
            if (Vector3.Dot(r, n) < 0.2f) { r = (r + n * 0.5f).normalized; }
            s.P.Redirect(_owner.ToFxWorld(point + n * (radius + 0.25f)), SamusController.ToMph(r));
            s.BounceCol = col;
            s.BounceEid = eid;
            s.BounceSteps = BounceIgnoreSteps;
            s.Target = null;
            GameObject snd = MonoSingleton<DefaultReferenceManager>.Instance != null ? MonoSingleton<DefaultReferenceManager>.Instance.ineffectiveSound : null;
            if (snd != null && Object.Instantiate(snd, point, Quaternion.identity).TryGetComponent(out AudioSource a)) { a.volume = 0.75f; }
            Plugin.Event("missile bounced off");
        }

        // Explosion.Collide (enemy part): who takes explosion damage and how (rocketExplosion = true)
        private static void ExplosionHit(EnemyIdentifier eid, Collider col, Vector3 point, Vector3 origin, float dmg, string hitter, Vector3 force)
        {
            switch (eid.enemyType)
            {
                case EnemyType.MaliciousFace when !eid.isGasolined:
                    GameObject snd = MonoSingleton<DefaultReferenceManager>.Instance != null ? MonoSingleton<DefaultReferenceManager>.Instance.ineffectiveSound : null;
                    if (snd != null) { Object.Instantiate(snd, point, Quaternion.identity); }
                    return;
                case EnemyType.Idol:
                case EnemyType.Deathcatcher:
                    if (!Physics.Linecast(origin, col.bounds.center, LayerMaskDefaults.Get(LMD.Environment)))
                    {
                        eid.hitter = hitter;
                        eid.DeliverDamage(col.gameObject, Vector3.zero, point, 1f, false, 0, null, false, true);
                    }
                    return;
                case EnemyType.Cerberus: dmg *= 1.5f; break;
                case EnemyType.Providence: dmg *= 2; break;
            }
            eid.hitter = hitter;
            // grounded Soldiers block explosions on harder difficulties
            ZombieProjectiles zp = eid.GetComponent<ZombieProjectiles>();
            Enemy en = eid.GetComponent<Enemy>();
            if (eid.enemyType == EnemyType.Soldier && !eid.isGasolined && !ULTRAKILL.Cheats.BlindEnemies.Blind && en != null && en.grounded
                && zp != null && zp.difficulty >= 2)
            {
                eid.hitter = "blocked";
                if (zp.difficulty <= 3) { eid.DeliverDamage(col.gameObject, Vector3.zero, point, dmg * 0.25f, false, 0, null, false, true); }
                zp.Block(origin);
                return;
            }
            eid.DeliverDamage(col.gameObject, force, point, dmg, false, 0, null, false, true);
        }

        // Explosion: push mostly away, lifted (vector5 rule), at MissileForce
        private static Vector3 BlastForce(Vector3 dir)
        {
            if (dir.y <= 0.5f) { dir.y += 0.5f; }
            else if (dir.y < 1) { dir.y = 1; }
            return dir * Plugin.MissileForce.Value;
        }

        // breakables a shot breaks by hitting them: the charged Power Beam (any non-special), the Imperialist (like the
        // railcannon: precision-only ones too), weak ones on any shot; glass on charged / precise shots
        private void HitSurface(Shot s, RaycastHit h)
        {
            bool charged = s.P.Charged && s.Weapon == MphBeam.PowerBeam;
            bool precise = s.Weapon == MphBeam.Imperialist;
            if (h.transform.TryGetComponent(out Breakable br) && !br.unbreakable && !br.specialCaseOnly && !br.broken
                && (charged || precise || br.weak && !br.precisionOnly))
            {
                br.Break();
                Plugin.Event($"broke {h.transform.name}");
            }
            if ((charged || precise) && h.transform.TryGetComponent(out Glass glass))
            {
                glass.Shatter();
            }
        }

        // OnCollision with a surface: the ricochet weapon (Judicator) fires off it. Owner: like ULTRAKILL's ricochets
        // (RevolverBeam: reflect off the surface, then RicochetAimAssist turns it to the closest visible enemy within a
        // 5-unit-wide cast along the bounce, at its weak point when that's in sight)
        private void FireRicochetWeapon(Shot s, Vector3 point, Vector3 normal)
        {
            if (s.Rico == null) { return; }
            WeaponInfo rico = s.Rico;
            s.Rico = null;
            Vector3 n = normal.sqrMagnitude > 1e-6f ? normal.normalized : -SamusController.ToUnity(s.P.Direction).normalized;
            Vector3 dirU = Vector3.Reflect(SamusController.ToUnity(s.P.Direction).normalized, n).normalized;
            Vector3 start = point + n * 0.05f;
            dirU = RicochetAimAssist(start, dirU);
            OV3 dir = SamusController.ToMph(dirU).Normalized();
            ShotEvent e = SamusSim.MakeShot(rico, s.P.Charged, s.P.Charged ? 1 : 0, _frame, true);
            OV3 pos = _owner.ToFxWorld(start);
            int count = Mathf.Max(1, e.Projectiles);
            for (int i = 0; i < count; i++) { Add(e, pos, MphProjectile.SpreadDirection(dir, e.Spread, _rng)); }
        }

        // RevolverBeam.RicochetAimAssist (ULTRAKILL, without V1's coins)
        private static Vector3 RicochetAimAssist(Vector3 from, Vector3 dir)
        {
            RaycastHit[] hits = Physics.SphereCastAll(from, 5f, dir, float.PositiveInfinity, LayerMaskDefaults.Get(LMD.Enemies));
            int env = LayerMaskDefaults.Get(LMD.Environment);
            float best = float.PositiveInfinity;
            Vector3? target = null;
            EnemyIdentifier hitEid = null;
            foreach (RaycastHit h in hits)
            {
                if (h.distance > best || h.distance < 0.1f) { continue; }
                if (!h.transform.TryGetComponent(out EnemyIdentifierIdentifier eidid) || eidid.eid == null || eidid.eid.dead) { continue; }
                if (Physics.Raycast(from, h.point - from, h.distance, env)) { continue; }
                best = h.distance;
                target = h.point;
                hitEid = eidid.eid;
            }
            if (target == null) { return dir; }
            Vector3 aim = target.Value;
            if (hitEid.weakPoint != null)
            {
                Vector3 wp = hitEid.weakPoint.transform.position;
                if (!Physics.Raycast(from, wp - from, Vector3.Distance(wp, from), env)) { aim = wp; }
            }
            Plugin.Event($"ricochet aims at {hitEid.enemyType}");
            return (aim - from).normalized;
        }

        // the shot's splash in MPH units: Missiles = MPH's x MissileSplashScale; charged Power Beam = a small one
        // (owner, not MPH: ChargeBeamSplash, smaller for partial charges, ChargeBeamSplashDamage of the shot's damage)
        private static bool SplashOf(Shot s, out float damage, out float radius, out int type)
        {
            ShotEvent e = s.P.Shot;
            damage = radius = 0;
            type = 0;
            if (s.Weapon == MphBeam.Missile)
            {
                damage = e.SplashDamage;
                radius = e.SplashRadius * Mathf.Max(0, Plugin.MissileSplashScale.Value) * (s.Super ? SuperBlastScale : 1);
                type = e.SplashDmgType;
            }
            else if (s.Weapon != MphBeam.PowerBeam)
            {
                damage = e.SplashDamage;   // MPH's own splash
                radius = e.SplashRadius;
                type = e.SplashDmgType;
            }
            else if (s.P.Charged)
            {
                damage = e.Damage * Mathf.Max(0, Plugin.ChargeBeamSplashDamage.Value);
                radius = Mathf.Max(0, Plugin.ChargeBeamSplash.Value) * Mathf.Lerp(0.5f, 1, e.ChargeFraction);
            }
            return damage > 0 && radius > 0;
        }

        private static bool Explosive(MphBeam b) => b == MphBeam.Missile || b == MphBeam.Battlehammer || b == MphBeam.Magmaul;

        // OnCollision: splash damage, breakables in the blast, rocket jump / self splash
        private void Detonate(Shot s, Vector3 at, EnemyIdentifier direct, bool hitSelf)
        {
            if (!SplashOf(s, out float splashDamage, out float splashRadius, out int splashType)) { return; }
            bool missile = s.Weapon == MphBeam.Missile;
            bool explosive = Explosive(s.Weapon);   // explosion rules, breakables
            float radius = splashRadius * _owner.K;
            int DamageAt(float dist) => (int)MphProjectile.Interpolate(splashType, splashDamage, 0, dist / radius);
            bool charged = s.P.Charged;
            int envMask = LayerMaskDefaults.Get(LMD.Environment);
            var closest = new Dictionary<EnemyIdentifier, (float dist, Collider col, Vector3 point)>();
            var broken = new HashSet<Breakable>();
            foreach (Collider c in Physics.OverlapSphere(at, radius, ~0, QueryTriggerInteraction.Collide))
            {
                if (c == null) { continue; }
                Vector3 cp = c.bounds.ClosestPoint(at);
                float dist = Vector3.Distance(at, cp);
                if (c.TryGetComponent(out EnemyIdentifierIdentifier eidid) && eidid.eid != null)
                {
                    EnemyIdentifier eid = eidid.eid;
                    if (eid.dead || eid == direct) { continue; }
                    if (!closest.TryGetValue(eid, out var best) || dist < best.dist) { closest[eid] = (dist, c, cp); }
                    continue;
                }
                if (c.isTrigger || !explosive) { continue; }
                Breakable br = null;
                if (!c.TryGetComponent(out br) && c.attachedRigidbody != null) { c.attachedRigidbody.TryGetComponent(out br); }
                // Explosion's breakable rule; a charged missile counts as the Knuckleblaster (heavy punch)
                // (heavy punch: anything but precision / special-case breakables, explosion-proof ones included)
                bool heavy = missile && charged;
                if (br != null && !br.broken && !br.unbreakable && !br.precisionOnly && !br.specialCaseOnly
                    && (heavy || !br.ignoreExplosions) && broken.Add(br))
                {
                    br.Break(heavy ? 999999f : DamageAt(dist));
                    if (br.broken) { Plugin.Event($"blast broke {br.name}"); }
                }
                else if (c.TryGetComponent(out Glass glass))
                {
                    glass.Shatter();
                }
            }
            foreach (KeyValuePair<EnemyIdentifier, (float dist, Collider col, Vector3 point)> kv in closest)
            {
                (float dist, Collider col, Vector3 point) = kv.Value;
                // CheckBetweenPoints: walls block splash
                if (dist > 0.01f && Physics.Linecast(at, point, envMask, QueryTriggerInteraction.Ignore)) { continue; }
                int mph = DamageAt(dist);
                if (mph <= 0) { continue; }
                EnemyIdentifier eid = kv.Key;
                float dmg = mph * Plugin.DamageDealtScale.Value;
                if (explosive)
                {
                    if (s.Weapon == MphBeam.Magmaul) { eid.hitterAttributes.Add(HitterAttribute.Fire); }
                    // missiles push like ULTRAKILL's explosions; the shells (Battlehammer, Magmaul) a quarter of that
                    ExplosionHit(eid, col, point, at, dmg, missile && charged && eid.enemyType == EnemyType.Gutterman ? "heavypunch" : "explosion",
                        BlastForce((point - at).normalized) * (missile ? 1 : 0.25f));
                }
                else
                {
                    eid.hitter = HitterFor(s.Weapon);
                    if (s.Weapon == MphBeam.VoltDriver)
                    {
                        eid.hitter = "zapper";
                        eid.hitterAttributes.Add(HitterAttribute.Electricity);
                    }
                    eid.DeliverDamage(col.gameObject, (point - at).normalized * Plugin.ChargedKnockback.Value * 0.25f, point, dmg, false, 0, null);
                    if (s.Weapon == MphBeam.Judicator) { JudicatorChill.Apply(eid, Plugin.JudicatorChill.Value * 0.5f); }
                }
                if (s.Weapon == MphBeam.Magmaul) { EnemyEffects.Ignite(eid); }
                Plugin.Event($"splash {eid.enemyType} for {dmg:F2} at {dist:F1}, health {eid.health:F2}{(eid.dead ? " - killed" : "")}");
            }
            if (s.Weapon == MphBeam.Magmaul)
            {
                EnemyEffects.IgniteGround(at);
                if (charged) { IgniteAround(at, radius * Mathf.Max(1, Plugin.MagmaulChargedIgnite.Value), direct); }
            }
            if (missile) { MissileSelfHit(at, radius, charged); }
            else if (s.Weapon != MphBeam.PowerBeam && !hitSelf) { SelfSplash(s, at, radius, DamageAt); }
        }

        // CheckSplashDamage hits the shooter too (MPH: no owner check; the SelfDamage flags only cover direct hits):
        // Samus loses the splash's energy at her distance, walls block it, no launch
        private void SelfSplash(Shot s, Vector3 at, float radius, System.Func<float, int> damageAt) =>
            SplashSamus(s, at, radius, damageAt, Plugin.SplashSelfDamage.Value, $"own {s.Weapon} splash");

        private void SplashSamus(Shot s, Vector3 at, float radius, System.Func<float, int> damageAt, float scale, string what)
        {
            Collider body = _owner.BodyCollider;
            if (scale <= 0 || body == null || _owner.Sim == null || _owner.IsDead) { return; }
            Vector3 cp = body.bounds.ClosestPoint(at);
            float dist = Vector3.Distance(at, cp);
            if (dist >= radius) { return; }
            if (dist > 0.01f && Physics.Linecast(at, cp, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore)) { return; }
            int energy = Mathf.RoundToInt(damageAt(dist) * scale);
            if (energy <= 0) { return; }
            _owner.SelfDamage(energy, what);
        }

        // her own missile blast catching Samus costs a little energy (Balance/MissileSelfDamage; no launch)
        private void MissileSelfHit(Vector3 at, float radius, bool charged)
        {
            Collider body = _owner.BodyCollider;
            int self = Plugin.MissileSelfDamage.Value * (charged ? 2 : 1);
            if (self <= 0 || body == null || _owner.Sim == null || _owner.IsDead) { return; }
            Vector3 cp = body.bounds.ClosestPoint(at);
            float dist = Vector3.Distance(at, cp);
            if (dist >= radius) { return; }
            if (dist > 0.01f && Physics.Linecast(at, cp, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore)) { return; }
            _owner.SelfDamage(self, "own missile");
        }

        // ---- electricity (ULTRAKILL) ----
        private static bool Electric(Shot s) => s.Weapon == MphBeam.VoltDriver || s.Weapon == MphBeam.ShockCoil;
        private const int WaterMask = 1 << 4;

        // RevolverBeam.CheckWater: an electric shot starting in or passing through water electrocutes it - every enemy in
        // that water is zapped (and Samus too, if she's in it, as V1 would be). Once per water per shot.
        private void ZapWater(Shot s, Vector3 from, Vector3 to)
        {
            if (!Electric(s)) { return; }
            if (!s.CheckedStartWater)
            {
                s.CheckedStartWater = true;
                foreach (Collider c in Physics.OverlapSphere(from, 0.01f, WaterMask, QueryTriggerInteraction.Collide)) { Zap(s, c, from); }
            }
            Vector3 step = to - from;
            float dist = step.magnitude;
            if (dist <= 0) { return; }
            foreach (RaycastHit h in Physics.RaycastAll(from, step / dist, dist, WaterMask, QueryTriggerInteraction.Collide)) { Zap(s, h.collider, h.point); }
        }

        private void Zap(Shot s, Collider c, Vector3 at)
        {
            if (c == null) { return; }
            Water w = c.attachedRigidbody != null ? c.attachedRigidbody.GetComponent<Water>() : c.GetComponent<Water>();
            if (w == null || w.visualsOnly || !s.Zapped.Add(w)) { return; }
            try
            {
                EnemyIdentifier.Zap(at, 2f, new List<GameObject>(), null, null, w, waterOnly: true);
                Plugin.Event($"{s.Weapon} electrocuted {w.name}");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"water zap: {ex.Message}"); }
        }

        // AttributeChecker (electric switches): activated by the charged Volt Driver, as by the electric railcannon
        private bool TryPowerSwitch(Shot s, Collider col, Vector3 point)
        {
            if (s.Weapon != MphBeam.VoltDriver || !s.P.Charged) { return false; }
            AttributeChecker ac = col.GetComponent<AttributeChecker>() ?? col.GetComponentInParent<AttributeChecker>();
            if (ac == null || ac.targetAttribute != HitterAttribute.Electricity || !ac.isActiveAndEnabled) { return false; }
            ac.DelayedActivate();
            Plugin.Event($"charged Volt Driver powered {ac.name}");
            Impact(s, point, -SamusController.ToUnity(s.P.Direction), entity: true);
            s.P.Collide(_owner.ToFxWorld(point));
            return true;
        }

        // ---- portals (ULTRAKILL.Portal, Fraud) ----
        // the step's segment crosses a portal: the part before it collides as usual, then the shot continues from the
        // exit with its position and direction carried through (PortalUtils travel matrix). True = the shot hit something.
        private bool ThroughPortal(Shot s, Vector3 from, ref Vector3 to, out bool hitBefore)
        {
            hitBefore = false;
            if (!MonoSingleton<PortalManagerV2>.TryGetInstance(out PortalManagerV2 pm) || pm == null) { return false; }
            Vector3 step = to - from;
            float dist = step.magnitude;
            if (dist <= 1e-4f) { return false; }
            PortalTraversalV2[] traversals;
            try
            {
                PortalPhysicsV2.Raycast(from, step / dist, dist, LayerMaskDefaults.Get(LMD.Environment), out _, out traversals, out _,
                    QueryTriggerInteraction.Ignore);
            }
            catch { return false; }
            if (traversals == null || traversals.Length == 0) { return false; }
            Vector3 entrance = traversals[0].entrancePoint;
            // up to the portal (a little short: the surface behind a wall portal must not catch it)
            Vector3 dir = step / dist;
            float before = Vector3.Distance(from, entrance) - 0.02f;
            if (before > 0 && Collide(s, from, from + dir * before))
            {
                hitBefore = true;
                return true;
            }
            Matrix4x4 m = PortalUtils.GetTravelMatrix(traversals);
            Vector3 newTo = m.MultiplyPoint3x4(to);
            Vector3 newDir = m.MultiplyVector(dir).normalized;
            Vector3 exit = traversals[traversals.Length - 1].exitPoint + newDir * 0.02f;
            s.P.Redirect(_owner.ToFxWorld(newTo), SamusController.ToMph(newDir), clearTrail: true);
            s.Target = null;
            Plugin.Event($"{s.Weapon} went through a portal");
            if (Collide(s, exit, newTo)) { return true; }
            to = newTo;
            return true;   // (this step is done; the shot flies on from the exit)
        }

        // SpawnSniperBeam: the Imperialist's beam model, stretched from 8 steps back to where the shot ended, animated once
        private void SpawnSniperBeam(MphProjectile p)
        {
            OV3 start = p.Past[8];
            OV3 up = p.Position - start;
            float mag = up.Length;
            if (mag <= 0) { return; }
            up /= mag;
            ModelView v = NewModel("sniperBeam");
            if (v == null) { return; }
            Matrix4 xf = Matrix4.CreateScale(v.Model.Instance.Model.Scale) * Matrix4.CreateScale(1, mag, 1)
                * EffectSystem.Transform(EffectSystem.CrossVector(up), up, start);
            var light = MphLighting.Default;
            v.Model.Pose(xf, light);
            int frames = v.Model.Instance.AnimInfo.FrameCount[0];
            _sniper.Add(new SniperBeam { View = v, Life = Mathf.Max(1, frames - 1) * 2 });
            _sniperXf[v] = xf;
            Upload(v);
        }
        private readonly Dictionary<ModelView, Matrix4> _sniperXf = new Dictionary<ModelView, Matrix4>();

        private void StepSniperBeams()
        {
            for (int i = _sniper.Count - 1; i >= 0; i--)
            {
                SniperBeam b = _sniper[i];
                if (b.Life-- <= 0)
                {
                    _sniperXf.Remove(b.View);
                    b.View.Destroy();
                    _sniper.RemoveAt(i);
                    continue;
                }
                if (_frame % 2 == 0) { b.View.Model.Instance.UpdateAnimFrames(); }
                b.View.Model.Pose(_sniperXf[b.View], MphLighting.Default);
                Upload(b.View);
            }
        }

        private void Upload(ModelView v)
        {
            Camera cam = _owner.Cam;
            v.Upload(cam != null ? cam.transform.position : Vector3.zero, 20);
        }

        // DrawTrail alpha + the per-weapon draw functions (testbed BeamSystem::draw)
        private void Draw(Shot s, float t)
        {
            MphProjectile p = s.P;
            EffectSystem fx = _owner.WorldFx;
            OV3 pos = p.Collided ? p.Position : OV3.Lerp(p.BackPosition, p.Position, t);
            float alpha = p.TrailAlpha;
            switch (p.Shot.DrawFuncId)
            {
                case 0:   // Power Beam
                {
                    if (!p.Collided && _fuzz != null) { fx.AddSingle(_fuzz, pos, s.Color, 1, FuzzHalfSize); }
                    Trail1(s, pos, "trail", Trail1Height, alpha);
                    break;
                }
                case 1: Trail1(s, pos, "electroTrail", VoltTrailHeight, alpha); break;              // uncharged Volt Driver
                case 2: Trail2(s, pos, "electroTrail", VoltChargedTrailHeight, 5, alpha); break;    // charged Volt Driver
                case 3:   // Judicator: ice shard + trail
                {
                    if (s.Model != null)
                    {
                        s.Model.SetVisible(!p.Collided);
                        if (!p.Collided)
                        {
                            s.Model.Model.Pose(Matrix4.CreateScale(s.Model.Model.Instance.Model.Scale)
                                * EntityBase.GetTransformMatrix(p.Direction, s.Up, pos), MphLighting.Default);
                            Upload(s.Model);
                        }
                    }
                    Trail2(s, pos, "trail", Trail2Height, 5, alpha);
                    break;
                }
                case 7:   // Missile
                {
                    if (!p.Collided && _fuzz != null) { fx.AddSingle(_fuzz, pos, OV3.One, 1, FuzzHalfSize); }
                    Trail2(s, pos, "trail", Trail2Height, 5, alpha);
                    break;
                }
                case 9:   // Shock Coil
                {
                    if (!p.Collided)
                    {
                        if (s.Target != null) { Trail4(p, 0.15f, 0.5f, 10); }   // locked on
                        else { Trail4(p, 0.025f, 0.35f, 5); }
                        foreach ((OV3 from, OV3 to) in s.Branches) { Arc(from, to, OV3.Zero, 0.1f, 0.5f, 8); }
                    }
                    break;
                }
                case 10: Trail2(s, pos, "trail", HammerTrailHeight, 2, alpha); break;   // Battlehammer
                default: break;                                                          // Magmaul / Imperialist: effect / model only
            }
        }

        // DrawTrail1: one quad from one step back to the beam, not behind the muzzle
        private void Trail1(Shot s, OV3 pos, string model, float height, float alpha)
        {
            MphProjectile p = s.P;
            OV3 back = p.Collided ? p.BackPosition : pos - p.Velocity;
            if (OV3.Dot(back - p.SpawnPosition, p.Direction) < 0) { back = p.SpawnPosition; }
            _owner.WorldFx.AddTrail(model, back, pos, OV3.UnitY, height, s.Color, alpha);
        }

        // DrawTrail2: a strip through every other past position (newest first)
        private void Trail2(Shot s, OV3 pos, string model, float height, int segments, float alpha)
        {
            if (!EffectSystem.TrailTexture(model, out _, out int tw, out int th) || segments < 2) { return; }
            MphProjectile p = s.P;
            float uvT = (th - 1 / 16f) / th;
            OV3 h = new OV3(0, height, 0);
            for (int i = 0; i + 1 < segments; i++)
            {
                OV3 a = i == 0 ? pos : p.Past[i * 2], b = p.Past[(i + 1) * 2];
                float u0 = UvS(i, tw, segments), u1 = UvS(i + 1, tw, segments);
                _owner.WorldFx.AddTrailQuad(model, a - h, a + h, b + h, b - h,
                    new OV2(u0, 0), new OV2(u0, uvT), new OV2(u1, uvT), new OV2(u1, 0), s.Color, alpha);
            }
        }

        // DrawTrail4: the Shock Coil arc - a jittered strip from the muzzle to the beam end, bowed by the velocity
        private void Trail4(MphProjectile p, float height, float range, int segments) =>
            Arc(p.Past[8], p.Position, p.Velocity, height, range, segments);

        private void Arc(OV3 start, OV3 end, OV3 bow, float height, float range, int segments)
        {
            if (!EffectSystem.TrailTexture("arcWelder", out _, out int tw, out int th)) { return; }
            uint frames = (uint)(Time.frameCount / 2);
            uint rng = frames + (uint)(int)(end.X * 4096);
            int index = (int)(frames & 15);
            float halfRange = range / 2;
            uint rangeInt = (uint)(range * 4096);
            OV3 vec = end - start;
            float uvT = (th - 1 / 16f) / th;
            var pts = new OV3[10];
            var us = new float[10];
            for (int i = 0; i < segments && i < 10; i++)
            {
                int factor = index + i;
                us[i] = factor > 0 ? (2f * tw / (segments - 1) * factor - 1 / 16f) / tw : 0;
                float pct = i / (float)(segments - 1);
                OV3 q = vec * pct + bow * (0.25f * pct * (1 - pct));
                if (i > 0 && i < segments - 1)
                {
                    q.X += CallRng(ref rng, rangeInt) / 4096f - halfRange;
                    q.Y += CallRng(ref rng, rangeInt) / 4096f - halfRange;
                    q.Z += CallRng(ref rng, rangeInt) / 4096f - halfRange;
                }
                pts[i] = start + q;
            }
            OV3 h = new OV3(0, height, 0);
            for (int i = 0; i + 1 < segments && i + 1 < 10; i++)
            {
                _owner.WorldFx.AddTrailQuad("arcWelder", pts[i] - h, pts[i] + h, pts[i + 1] + h, pts[i + 1] - h,
                    new OV2(us[i], 0), new OV2(us[i], uvT), new OV2(us[i + 1], uvT), new OV2(us[i + 1], 0), OV3.One, 1);
            }
        }

        // Rng.CallGetRandomInt (testbed callRng)
        private static uint CallRng(ref uint rng, uint value)
        {
            rng = rng * 0x7FF8A3ED + 0x2AA01D31;
            return (uint)(((ulong)(rng >> 16) * value) >> 16);
        }

        private static float UvS(int k, int tw, int segments) => k == 0 ? 0 : (tw / (float)(segments - 1) * k - 1 / 16f) / tw;

        private static OV3 Bgr555(int c) => new OV3((c & 31) / 31f, ((c >> 5) & 31) / 31f, ((c >> 10) & 31) / 31f);

        public void Clear()
        {
            foreach (Shot s in _shots)
            {
                if (s.Fx.Valid) { _owner.WorldFx.Unlink(ref s.Fx); }
                s.Model?.Destroy();
            }
            _shots.Clear();
            foreach (SniperBeam b in _sniper) { b.View.Destroy(); }
            _sniper.Clear();
            _sniperXf.Clear();
            UpdateShockSound(false, false);
            if (_beep)
            {
                _owner.Audio?.StopLoop("MISSILE_CHARGE3");
                _beep = false;
            }
        }
    }
}
