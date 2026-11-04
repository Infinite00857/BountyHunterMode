// SamusSim (weapon afflictions) - MphRead PlayerEntity.TakeDamage (beam.Afflictions), PlayerInput
// (_frozenTimer, _disruptedTimer), PlayerHud (UpdateDisruptedState): the Judicator freezes (no input, an ice layer
// over the view), the Volt Driver disrupts (the screen's lines shift, Samus's own shots stray). Burn (Magmaul) is
// SamusSim.World's Ignite.
using System;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    public sealed partial class SamusSim
    {
        // ---- MPH values ----
        public const int FrozenSteps = 75 * 2;          // frozen by a player / bot's Judicator (30 * 2 by the world)
        public const int FrozenMinSteps = 15 * 2;       // frozen again soon after: at least this
        public const int FrozenRepeatSteps = 60 * 2;    // a full freeze only this long after the last one
        public const int DisruptedSteps = 60 * 2;
        public const float DisruptedSpray = 3;          // TryFireWeapon: the shot vector +-this per axis (units)

        public int FrozenTimer, FrozenGfxTimer, DisruptedTimer;
        public int TimeSinceFrozen = 0xFFFF;
        public bool FrozenThisStep, ThawedThisStep, DisruptedThisStep;
        // PlayerHud disrupted state: 1 ramp in, 2 hold, 3 ramp out; the screen shift factor 0..1
        public int HudDisruptedState;
        public float HudDisruptionFactor;
        private int _hudDisruptedTimer;

        public bool Frozen => FrozenTimer > 0;

        // PlayerEntity.TakeDamage: Affliction.Freeze
        public void Freeze()
        {
            if (Health <= 0) { return; }
            if (FrozenTimer == 0)
            {
                if (TimeSinceFrozen > FrozenRepeatSteps) { FrozenTimer = FrozenSteps; }
                else if (FrozenTimer < FrozenMinSteps) { FrozenTimer = FrozenMinSteps; }
                FrozenGfxTimer = FrozenTimer + 5 * 2;
                FrozenThisStep = true;
            }
        }

        // PlayerEntity.TakeDamage: Affliction.Disrupt (+ HudOnDisrupted)
        public void Disrupt()
        {
            if (Health <= 0) { return; }
            DisruptedTimer = DisruptedSteps;
            DisruptedThisStep = true;
            if (HudDisruptedState == 0)
            {
                HudDisruptedState = 1;
                _hudDisruptedTimer = DisruptedTimer;
            }
        }

        // the frozen player's input: nothing (the aim stays where it was)
        private SamusInput AfflictInput(in SamusInput input)
        {
            if (FrozenTimer == 0) { return input; }
            return new SamusInput { Facing = Facing, Heading = input.Heading, InWater = input.InWater };
        }

        // PlayerInput (timers) + PlayerHud.UpdateDisruptedState, once per step
        private void StepAfflictions()
        {
            FrozenThisStep = ThawedThisStep = DisruptedThisStep = false;
            if (FrozenTimer > 0)
            {
                FrozenTimer--;
                TimeSinceFrozen = 0;
                if (FrozenTimer == 0) { ThawedThisStep = true; }
            }
            if (FrozenGfxTimer > 0) { FrozenGfxTimer--; }
            if (TimeSinceFrozen < 0xFFFF) { TimeSinceFrozen++; }
            if (DisruptedTimer > 0) { DisruptedTimer--; }
            switch (HudDisruptedState)
            {
                case 1:
                    HudDisruptionFactor += 0.25f / 2;
                    if (HudDisruptionFactor >= 1) { HudDisruptionFactor = 1; HudDisruptedState = 2; }
                    break;
                case 2:
                    if (--_hudDisruptedTimer <= 0) { HudDisruptedState = 3; }
                    break;
                case 3:
                    HudDisruptionFactor -= 0.125f / 2;
                    if (HudDisruptionFactor <= 0) { HudDisruptionFactor = 0; HudDisruptedState = 0; }
                    break;
            }
        }

        // TryFireWeapon: a disrupted player's shot vector strays (+-3 units per axis at the aim point)
        public Vector3 DisruptAim(Vector3 shotVec, Random rng)
        {
            if (DisruptedTimer == 0) { return shotVec; }
            float R() => (float)(rng.NextDouble() * 2 - 1) * DisruptedSpray;
            return shotVec + new Vector3(R(), R(), R());
        }

        private void ResetAfflictions()
        {
            FrozenTimer = FrozenGfxTimer = DisruptedTimer = 0;
            TimeSinceFrozen = 0xFFFF;
            HudDisruptedState = 0;
            HudDisruptionFactor = 0;
            _hudDisruptedTimer = 0;
        }
    }
}
