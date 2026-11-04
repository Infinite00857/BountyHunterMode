// Projectile - one beam/missile's flight in MPH units, engine-agnostic: the testbed's playtested BeamSystem::processOne
// (hunters/mph_weapons.cpp), a port of MphRead (MIT) BeamProjectileEntity.Process - velocity + half-step gravity, speed
// decay toward the final speed (GetInterpolatedValue), homing steer, past positions for the trail, max distance.
// The engine does collision (segment BackPosition -> Position each step) and calls Collide().
using System;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    public sealed class Projectile
    {
        public const float StepTime = 1 / 60f;
        public const float CollidedLinger = 4 / 30f;   // testbed COLLIDED_LINGER: the trail fades for 4 frames
        public const float SelfDamageAge = 4 / 30f;    // SelfDamage shots can hit their owner after 4 frames
        public const float RicochetPush = 0.01f;       // ProcessRicochet: re-placed this far off the plane

        public readonly ShotEvent Shot;
        public Vector3 Position, BackPosition, SpawnPosition, Velocity, Direction;
        public readonly Vector3[] Past = new Vector3[10];
        public float Speed, Age, Lifespan;
        public bool Collided;
        public bool Charged => Shot.Kind == ShotKind.Charged;
        public bool Homing => Shot.Homing > 0;

        private readonly float _initialSpeed, _finalSpeed;   // per 60 Hz step
        private readonly Vector3 _acceleration;
        private int _frame;

        public Projectile(in ShotEvent shot, Vector3 position, Vector3 direction)
        {
            Shot = shot;
            Direction = direction;
            _initialSpeed = Speed = shot.Speed / 2;
            _finalSpeed = shot.FinalSpeed / 2;
            _acceleration = new Vector3(0, shot.Gravity / 2, 0);
            Velocity = direction * Speed;
            SpawnPosition = BackPosition = Position = position;
            for (int i = 0; i < Past.Length; i++) { Past[i] = position; }
            Lifespan = shot.Lifespan / 30f;
        }

        // One 60 Hz step of flight. target: the homing target's position, if any (continuous beams snap to it).
        public void Step(Vector3? target)
        {
            Lifespan -= StepTime;
            if (Collided) { return; }
            if (Shot.Continuous && Age > 0)
            {
                Lifespan = 0;   // continuous beams live one step; the next shot replaces them
                return;
            }
            Age += StepTime;
            BackPosition = Position;
            if (_frame++ % 2 == 0)
            {
                for (int i = Past.Length - 1; i > 0; i--) { Past[i] = Past[i - 1]; }
                Past[0] = Position;
            }
            if (Homing && Shot.Continuous)
            {
                // the Shock Coil reaches its target; without one it stays at the muzzle (a short crackle)
                if (target is Vector3 tp) { Position = tp; }
                else { Velocity *= 0.25f; }
                return;
            }
            Position += Velocity;
            Velocity += _acceleration * 0.5f;
            if (Shot.SpeedDecayTime > 0 && Age <= Shot.SpeedDecayTime)
            {
                float mag = Velocity.Length;
                if (mag > 0)
                {
                    Speed = Interpolate(Shot.SpeedInterp, _initialSpeed, _finalSpeed, Age / Shot.SpeedDecayTime);
                    Velocity *= Speed / mag;
                }
            }
        }

        // the homing turn (BeamProjectileEntity.Process): accelerate toward the target, at most Homing per step;
        // a target behind the missile is dropped (returns false)
        public bool Steer(Vector3 target)
        {
            Vector3 accel = target - Position;
            accel = accel.LengthSquared > 0 ? accel.Normalized() : Vector3.UnitX;
            accel *= Speed;
            if (Vector3.Dot(accel, Velocity) < 0)
            {
                return false;
            }
            accel -= Velocity;
            float mag = accel.Length;
            if (mag > Shot.Homing) { accel *= Shot.Homing / mag; }
            Velocity += accel;
            return true;
        }

        // CheckHomingTargets (not continuous): the candidate closest to the flight direction, within range and
        // HomingTolerance (cosine). Returns the better dot, or tolerance when this one doesn't qualify.
        public float HomingScore(Vector3 targetPos, float best)
        {
            if (Velocity.LengthSquared < 1e-18f) { return best; }
            Vector3 between = targetPos - Position;
            float distSqr = between.LengthSquared;
            if (distSqr > Shot.HomingRange * Shot.HomingRange || distSqr <= 0) { return best; }
            float div = Vector3.Dot(between, Velocity.Normalized()) / MathF.Sqrt(distSqr);
            return div >= best ? div : best;
        }

        // CheckCollision MaxDistance: the fraction of this step's segment where the shot runs out of range (or null)
        public float? MaxDistanceHit()
        {
            if (Shot.MaxDistance <= 0) { return null; }
            Vector3 front = Position - SpawnPosition;
            float dist = front.Length;
            if (dist < Shot.MaxDistance) { return null; }
            float d = Vector3.Dot(front.Normalized(), BackPosition - SpawnPosition);
            float pct = MathF.Floor(dist * 4096) != MathF.Floor(d * 4096) ? (Shot.MaxDistance - d) / (dist - d) : 1;
            return Math.Clamp(pct, 0, 1);
        }

        // ProcessRicochet: reflect off the plane, losing speed (horizontal / vertical), re-placed just off it
        public void Ricochet(Vector3 hitPos, Vector3 normal)
        {
            float d1 = Vector3.Dot(Velocity, normal);
            Velocity = new Vector3((Velocity.X - 2 * normal.X * d1) * Shot.RicoLossH, (Velocity.Y - 2 * normal.Y * d1) * Shot.RicoLossV,
                (Velocity.Z - 2 * normal.Z * d1) * Shot.RicoLossH);
            Speed = Velocity.Length;
            Direction -= normal * (2 * Vector3.Dot(Direction, normal));
            BackPosition = Position = hitPos + normal * RicochetPush;
            for (int i = Past.Length - 1; i > 0; i--) { Past[i] = Past[i - 1]; }
            Past[0] = Position;
        }

        // TryFireWeapon spread (GetRandomInt2): up to maxSpread degrees off the aim, any way around it
        public static Vector3 SpreadDirection(Vector3 dir, int maxSpread, Random rng)
        {
            if (maxSpread <= 0) { return dir; }
            Vector3 right = dir.X != 0 || dir.Z != 0 ? new Vector3(dir.Z, 0, -dir.X).Normalized() : Vector3.UnitX;
            Vector3 up = Vector3.Cross(dir, right).Normalized();
            float a1 = rng.Next(maxSpread) / 4096f * MathF.PI / 180, a2 = rng.Next(0x168000) / 4096f * MathF.PI / 180;
            return (dir * MathF.Cos(a1) + (up * MathF.Cos(a2) + right * MathF.Sin(a2)) * MathF.Sin(a1)).Normalized();
        }

        // Not MPH: bounce off (ULTRAKILL armor deflects rockets) - same speed, new direction, from this point
        // clearTrail: the trail starts over here (through a portal: no streak across the room)
        public void Redirect(Vector3 position, Vector3 direction, bool clearTrail = false)
        {
            Position = BackPosition = position;
            Direction = direction;
            Velocity = direction * Speed;
            if (clearTrail) { for (int i = 0; i < Past.Length; i++) { Past[i] = position; } }
        }

        // OnCollision: stop where it hit; the trail lingers briefly
        public void Collide(Vector3 at)
        {
            Position = at;
            Collided = true;
            Lifespan = CollidedLinger;
            Velocity = Vector3.Zero;
        }

        // splash damage at a distance (CheckSplashDamage): SplashDmgType interpolation from full to 0 at the radius
        public int SplashDamageAt(float dist)
        {
            if (Shot.SplashRadius <= 0 || dist >= Shot.SplashRadius) { return 0; }
            return (int)Interpolate(Shot.SplashDmgType, Shot.SplashDamage, 0, dist / Shot.SplashRadius);
        }

        // DrawTrail alpha: fades over the last 4 DS frames of life
        public float TrailAlpha => Math.Clamp(Lifespan * 30 * 8, 0, 31) / 31f;

        // GetInterpolatedValue: 0 lerp, 1 sine, 2 sine (other half), 3 binary
        public static float Interpolate(int type, float v1, float v2, float ratio)
        {
            if (type == 3) { return ratio > 1 ? v2 : v1; }
            ratio = Math.Clamp(ratio, 0, 1);
            const float d2r = MathF.PI / 180;
            return type switch
            {
                0 => v1 + (v2 - v1) * ratio,
                1 => v1 + (v2 - v1) * ((MathF.Sin((270 - 180 * ratio) * d2r) + 1) / 2),
                2 => v1 + (v2 - v1) * (MathF.Sin((270 - 90 * ratio) * d2r) + 1),
                _ => 0
            };
        }
    }
}
