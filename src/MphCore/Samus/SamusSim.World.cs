// SamusSim (world) - water, jump pads and burning.
// Underwater dampening (not MPH: MPH has no water). While Samus's center (the ball's, in the Morph Ball) is under
// water: gravity x0.35 with a slow sinking cap, extra horizontal drag, lower walk / strafe / roll traction and speed
// caps, weaker jumps. Jump pads: MphRead PlayerProcess.ActivateJumpPad (Speed = the
// pad's vector, a fresh jump, the JUMP_PAD sound unless one played in the last 5 DS frames); the engine supplies the
// vector (ULTRAKILL's JumpPad).
using System;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    public sealed partial class SamusSim
    {
        // ---- TUNABLE CONSTANTS (underwater dampening) ----
        public const float WaterGravityScale = 0.35f;   // gravity while Samus's center is under water
        public const float WaterSpeedScale = 0.6f;      // walk / strafe / roll traction + speed caps
        public const float WaterJumpScale = 0.55f;      // jump speed (with the low gravity: ~0.85x the height)
        public const float WaterMaxFall = 0.09f;        // sinking speed cap (units per DS frame)
        public const float WaterDrag = 0.96f;           // extra horizontal speed multiplier per DS frame
        // ---- TUNABLE CONSTANTS (MPH ActivateJumpPad) ----
        private const int JumpPadSfxSteps = 5 * 2;      // no second JUMP_PAD sound within this many 60 Hz steps

        // ---- TUNABLE CONSTANTS (MPH burn affliction: PlayerEntity.TakeDamage burn, PlayerProcess burn ticks) ----
        public const int BurnSteps = 150 * 2;          // on fire this long (Magmaul hits; here ULTRAKILL fire)
        private const int BurnTickSteps = 8 * 2;        // 1 energy every 8 DS frames, no invulnerability, no sound

        public bool InWater;
        public int BurnTimer;
        public bool BurnTickThisStep;

        public void Ignite() { BurnTimer = BurnSteps; }

        // burn damage: no damage invulnerability (DamageFlags.NoDmgInvuln), ignores it too
        public int TakeBurnDamage(int damage)
        {
            if (Health <= 0 || damage <= 0) { return 0; }
            Health = Math.Max(0, Health - damage);
            TimeSinceDamage = 0;
            return damage;
        }
        public int TimeSinceJumpPad = 1000;

        private float WaterScale => InWater ? WaterSpeedScale : 1;
        private float WaterJump => InWater ? WaterJumpScale : 1;

        // ProcessMovement gravity, under water: low gravity, slow sinking, drag
        private void ApplyGravity()
        {
            if (!InWater)
            {
                Speed.Y += Gravity / 2;
                return;
            }
            Speed.Y += Gravity * WaterGravityScale / 2;
            if (Speed.Y < -WaterMaxFall) { Speed.Y = -WaterMaxFall; }
            Speed.X += (Speed.X * WaterDrag - Speed.X) / 2;
            Speed.Z += (Speed.Z * WaterDrag - Speed.Z) / 2;
        }

        // speed: the pad's launch velocity (units per DS frame). Returns whether to play JUMP_PAD.
        public bool ActivateJumpPad(Vector3 speed)
        {
            bool sfx = TimeSinceJumpPad > JumpPadSfxSteps;
            SetLaunchSpeed(speed);
            TimeSinceJumpPad = 0;
            UsedJump = false;
            return sfx;
        }

        private void UpdateWorldTimers()
        {
            BurnTickThisStep = false;
            if (BurnTimer > 0 && --BurnTimer % BurnTickSteps == 0) { BurnTickThisStep = true; }
            if (TimeSinceJumpPad < 1000) { TimeSinceJumpPad++; }
        }
    }
}
