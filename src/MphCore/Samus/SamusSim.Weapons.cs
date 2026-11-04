// SamusSim (weapons) - equipping, ammo and the shot values for every weapon Samus carries, ported from the testbed's
// playtested hunters/mph_player.cpp (tryEquipWeapon, processWeaponFire, tryFireWeapon, updateGunAnimation) and
// mph_weapons.cpp (BeamSystem::spawn value interpolation), which port MphRead (MIT) PlayerEntity / PlayerInput /
// BeamProjectileEntity.Spawn. Values: Metadata/Weapons.cs (Samus uses the affinity table for her Missiles).
//
// Not MPH (owner decisions): only charged missiles home (emulator check; the affinity table gives uncharged ones 12).
using System;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    public enum WeaponSwitch { None, Beam, MissileOpen, MissileClose, Fail }
    public enum WheelEvent { None, Open, Hover, CloseEmpty, CloseFail }

    public sealed partial class SamusSim
    {
        // ---- TUNABLE CONSTANTS (MPH single player; testbed mph_player.h) ----
        public const int MissileAmmoStart = 50;        // MISSILE_MAX: the launcher's starting capacity (raw: 10 per shot)
        public const int UaAmmoStart = 400;            // UA_MAX: Universal Ammo (affinity weapons)
        public const int WeaponIconAnimSteps = 19 * 2; // HudOnWeaponSwitch icon spin
        public static readonly WeaponInfo MissileInfo = Weapons.Weapons1P[(int)BeamType.Missile + 9];   // affinity

        // ---- state ----
        public BeamType CurrentWeapon = BeamType.PowerBeam;
        public BeamType WeaponSelection = BeamType.PowerBeam;
        public BeamType PreviousWeapon = BeamType.PowerBeam;
        public readonly bool[] Carried = new bool[9];  // by BeamType; the engine grants them (MPH's own set, not V1's)
        public bool HasMissiles { get => Carried[(int)BeamType.Missile]; set => Carried[(int)BeamType.Missile] = value; }
        public int MissileAmmo = MissileAmmoStart, MissileAmmoMax = MissileAmmoStart;
        public int UaAmmo = UaAmmoStart, UaAmmoMax = UaAmmoStart;
        public int WeaponIconSteps;
        public WeaponSwitch SwitchedThisStep;
        public bool ShotMissile;

        public WeaponInfo Weapon => WeaponFor(CurrentWeapon);
        // samusWeapon: the Missiles' affinity entry, the Power Beam, else the weapon's 1P entry
        public static WeaponInfo WeaponFor(BeamType b) => b == BeamType.Missile ? MissileInfo
            : b == BeamType.PowerBeam ? PowerBeam : Weapons.Weapons1P[(int)b];
        // ammo pools (EquipInfo.Ammo): AmmoType 0 = UA, 1 = missiles; -1 = the weapon uses none
        public int AmmoOf(WeaponInfo w) => w.AmmoCost == 0 && w.ChargeCost == 0 ? -1 : w.AmmoType == 1 ? MissileAmmo : UaAmmo;
        public int AmmoMaxOf(WeaponInfo w) => w.AmmoCost == 0 && w.ChargeCost == 0 ? -1 : w.AmmoType == 1 ? MissileAmmoMax : UaAmmoMax;
        private void SpendAmmo(WeaponInfo w, int cost)
        {
            if (AmmoOf(w) < 0) { return; }
            if (w.AmmoType == 1) { MissileAmmo = Math.Max(0, MissileAmmo - cost); }
            else { UaAmmo = Math.Max(0, UaAmmo - cost); }
        }
        public int AmmoRaw => AmmoOf(Weapon);
        public int AmmoShots => AmmoRaw < 0 ? -1 : AmmoRaw / Math.Max(1, (int)Weapon.AmmoCost);
        public int AmmoMax => AmmoMaxOf(Weapon);

        // Metadata.MuzzleEffectIds / ChargeEffectIds / ChargeLoopEffectIds (testbed table), by beam
        private static readonly int[] MuzzleEffects = { 62, 57, 62, 60, 63, 59, 61, 58, 62 };
        private static readonly int[] ChargeEffects = { 169, 165, 170, 169, 169, 166, 168, 169, 169 };
        private static readonly int[] ChargeLoopEffects = { 198, 194, 197, 198, 198, 195, 196, 198, 198 };
        public int MuzzleEffectId => MuzzleEffects[(int)CurrentWeapon];
        public int ChargeEffectId => ChargeEffects[(int)CurrentWeapon];
        public int ChargeLoopEffectId => ChargeLoopEffects[(int)CurrentWeapon];

        // PlayerEntity.TryEquipWeapon
        public bool TryEquipWeapon(BeamType beam, bool silent = false)
        {
            WeaponInfo info = WeaponFor(beam);
            bool available = Has(beam);
            bool hasAmmo = AmmoOf(info) < 0 || AmmoOf(info) >= info.AmmoCost;
            if (!silent && (!hasAmmo || !available || GunAnim == GunAnimation.UpDown))
            {
                SwitchedThisStep = WeaponSwitch.Fail;
                return false;
            }
            if (_chargeSfxOn)
            {
                _chargeSfxOn = false;
                ChargeStopped = true;
            }
            UpdateZoom(false);
            PreviousWeapon = CurrentWeapon;
            CurrentWeapon = WeaponSelection = beam;
            ChargeLevel = 0;
            SmokeLevel = 0;
            TimeSinceInput = 0;
            if (silent) { return true; }
            WeaponIconSteps = WeaponIconAnimSteps;
            if (beam == BeamType.Missile)
            {
                SetGunAnimation(GunAnimation.MissileOpen, AnimFlags.NoLoop);
                SwitchedThisStep = WeaponSwitch.MissileOpen;
            }
            else if (PreviousWeapon == BeamType.Missile)
            {
                SetGunAnimation(GunAnimation.MissileClose, AnimFlags.NoLoop);
                SwitchedThisStep = WeaponSwitch.MissileClose;
            }
            else
            {
                // the Switch animation's material slot uses the weapon being switched away from
                SetGunAnimation(GunAnimation.Switch, AnimFlags.NoLoop, PreviousWeapon);
                SwitchedThisStep = WeaponSwitch.Beam;
            }
            return true;
        }

        // the weapon whose per-weapon gun animation column (slot 1) plays
        public BeamType GunAnimWeapon { get; private set; } = BeamType.PowerBeam;
        private bool _gunOpenAnim;

        private static bool IsOpenAnim(GunAnimation a) => a == GunAnimation.FullChargeMissile || a == GunAnimation.ChargingMissile
            || a == GunAnimation.MissileClose || a == GunAnimation.MissileOpen || a == GunAnimation.Unknown9 || a == GunAnimation.MissileShot;

        // BeamProjectileEntity.Spawn: charge -> (charged, fraction) and the shot's values (amount interpolation)
        private ShotEvent? BuildShot(WeaponInfo w, out int cost)
        {
            bool charged = false;
            float pct = 0;
            if (w.Flags.HasFlag(WeaponFlags.CanCharge))
            {
                if (w.Flags.HasFlag(WeaponFlags.PartialCharge))
                {
                    if (ChargeLevel >= w.MinCharge * 2)
                    {
                        charged = true;
                        pct = (ChargeLevel - w.MinCharge * 2) / (float)(w.FullCharge * 2 - w.MinCharge * 2);
                    }
                }
                else if (ChargeLevel >= w.FullCharge * 2)
                {
                    charged = true;
                    pct = 1;
                }
            }
            ShotEvent shot = MakeShot(w, charged, pct, _beamFrame, Zoomed);
            cost = shot.Cost;
            int ammo = AmmoOf(w);
            if (ammo >= 0 && cost > ammo)
            {
                return null;
            }
            if (w.Beam == BeamType.Missile && !charged)
            {
                shot.Homing = 0;   // owner: only charged missiles home
            }
            return shot;
        }

        // BeamProjectileEntity.Spawn (testbed BeamSystem::spawn): every value interpolated by the charge. frame: the beam
        // frame counter (continuous weapons spread cost/damage / 32 over 30 Hz frames); zoomed: the Imperialist does half
        // damage unzoomed. Also used for the ricochet weapon's shots (Judicator).
        public static ShotEvent MakeShot(WeaponInfo w, bool charged, float pct, int frame, bool zoomed)
        {
            float Amount(int un, int mid, int full) => pct <= 0 ? un : mid + (full - mid) * pct;
            int c = charged ? 1 : 0;
            int cost = (int)Amount(w.AmmoCost, w.MinChargeCost, w.ChargeCost);
            int damage = (int)Amount(w.UnchargedDamage, w.MinChargeDamage, w.ChargedDamage);
            int hsDamage = (int)Amount(w.HeadshotDamage, w.MinChargeHeadshotDamage, w.ChargedHeadshotDamage);
            int splash = (int)Amount(w.SplashDamage, w.MinChargeSplashDamage, w.ChargedSplashDamage);
            bool continuous = w.Flags.HasFlag(WeaponFlags.Continuous);
            if (continuous)
            {
                // cost and damage: 1/32 per 30 Hz frame, the fractions spread over the cycle
                int Spread(int v, bool inclusive)
                {
                    if (frame % 2 != 0) { return 0; }
                    int bits = v & 31;
                    v /= 32;
                    long cyc = (long)bits * (frame / 2) & 31;
                    if (bits != 0 && (inclusive ? cyc >= 32 - bits : cyc > 32 - bits)) { v++; }
                    return v;
                }
                cost = Spread(cost, false);
                damage = Spread(damage, true);
                hsDamage = damage;
            }
            if (w.Beam == BeamType.Imperialist && !zoomed)
            {
                damage /= 2;
                hsDamage /= 2;
                splash /= 2;
            }
            return new ShotEvent
            {
                Weapon = w.Beam,
                Info = w,
                Kind = charged ? ShotKind.Charged : ShotKind.Uncharged,
                ChargeFraction = pct,
                Cost = cost,
                Damage = damage,
                HeadshotDamage = hsDamage,
                SplashDamage = splash,
                SplashRadius = Amount(w.UnchargedSplashRadius, w.MinChargeSplashRadius, w.ChargedSplashRadius) * Fx,
                SplashDmgType = w.SplashDamageTypes[c],
                Speed = Amount(w.UnchargedSpeed, w.MinChargeSpeed, w.ChargedSpeed) * Fx,
                FinalSpeed = Amount(w.UnchargedFinalSpeed, w.MinChargeFinalSpeed, w.ChargedFinalSpeed) * Fx,
                SpeedDecayTime = w.SpeedDecayTimes[c] / 30f,
                SpeedInterp = w.SpeedInterpolations[c],
                Gravity = Amount(w.UnchargedGravity, w.MinChargeGravity, w.ChargedGravity) * Fx,
                Homing = Amount(w.UnchargedHoming, w.MinChargeHoming, w.ChargedHoming) * Fx / 2,
                HomingRange = w.HomingRange * Fx,
                HomingTolerance = w.HomingTolerance * Fx,
                Radius = Amount(w.UnchargedCylRadius, w.MinChargeCylRadius, w.ChargedCylRadius) * Fx,
                Lifespan = (int)Amount(w.UnchargedLifespan, w.MinChargeLifespan, w.ChargedLifespan),
                MaxDistance = Amount(w.UnchargedDistance, w.MinChargeDistance, w.ChargedDistance) * Fx,
                Knockback = Amount(w.UnchargedDmgDirMag, w.MinChargeDmgDirMag, w.ChargedDmgDirMag) * Fx,
                DmgDirType = w.DmgDirTypes[c],
                DamageInterp = w.DamageInterpolations[c],
                ColEffect = w.CollisionEffects[c],
                DrawFuncId = w.DrawFuncIds[c],
                Color = w.Colors[c],
                Continuous = continuous,
                Ricochet = w.Flags.HasFlag(charged ? WeaponFlags.RicochetCharged : WeaponFlags.RicochetUncharged),
                SelfDamage = w.Flags.HasFlag(charged ? WeaponFlags.SelfDamageCharged : WeaponFlags.SelfDamageUncharged),
                ForceEffect = w.Flags.HasFlag(charged ? WeaponFlags.ForceEffectCharged : WeaponFlags.ForceEffectUncharged),
                Projectiles = (int)Amount(w.Projectiles, w.MinChargeProjectiles, w.ChargedProjectiles),
                Spread = (int)Amount(w.UnchargedSpread, w.MinChargeSpread, w.ChargedSpread),
                RicoLossH = Amount(w.UnchargedRicochetLossH, w.MinChargeRicochetLossH, w.ChargedRicochetLossH) * Fx,
                RicoLossV = Amount(w.UnchargedRicochetLossV, w.MinChargeRicochetLossV, w.ChargedRicochetLossV) * Fx,
                RicochetWeapon = charged ? w.ChargedRicochetWeapon : w.UnchargedRicochetWeapon,
            };
        }

        private const float Fx = 1 / 4096f;

        private void UpdateWeaponTimers()
        {
            if (WeaponIconSteps > 0) { WeaponIconSteps--; }
            _beamFrame++;
        }
        private int _beamFrame;

        // ---- Imperialist zoom (PlayerEntity.UpdateZoom, PlayerInput: right mouse toggles, Fov eases) ----
        public const float ZoomFovStep = 2 * 2;         // Fov moves this many degrees per 60 Hz step
        public const float ZoomOutSnap = 0.1f * 2;      // zoom-out snaps to the normal Fov within this
        public const float ZoomSensitivityField70 = 106 / 4096f;
        public bool Zoomed;
        public bool ZoomChanged;                        // this step (zoom in/out sound)
        public float Fov = Fixed.ToFloat(V.NormalFov) * 2;   // degrees; the engine scales its own FOV by Fov / CameraFov
        private bool _prevZoom;

        private void UpdateZoom(bool zoom)
        {
            if (Zoomed != zoom) { ZoomChanged = true; }
            Zoomed = zoom;
        }

        private void ProcessZoom(in SamusInput input)
        {
            WeaponInfo w = Weapon;
            if (w.Flags.HasFlag(WeaponFlags.CanZoom) && !InBall)
            {
                if (input.Zoom && !_prevZoom) { UpdateZoom(!Zoomed); }
                if (Zoomed)
                {
                    float zoomFov = w.ZoomFov * Fx * 2;
                    if (zoomFov > Fov) { Fov = Math.Min(Fov + ZoomFovStep, zoomFov); }
                    else if (zoomFov < Fov) { Fov = Math.Max(Fov - ZoomFovStep, zoomFov); }
                }
            }
            else if (Zoomed)
            {
                UpdateZoom(false);
            }
            _prevZoom = input.Zoom;
            if (!Zoomed)
            {
                // PlayerProcess: back to normal, 1/4 of the gap per step, snapping close to it
                float normal = Fixed.ToFloat(V.NormalFov) * 2;
                float diff = normal - Fov;
                Fov = Math.Abs(diff) >= ZoomOutSnap ? Fov + diff / 4 : normal;
            }
        }

        // UpdateAimX/Y while zoomed: the mouse turns slower (testbed: capped at 1)
        public float ZoomSensitivity
        {
            get
            {
                float f = Fov - Fixed.ToFloat(V.NormalFov) * 2;
                return f != 0 ? Math.Min(1, 1 / (-ZoomSensitivityField70 * f)) : 1;
            }
        }

        // weapon keys: ULTRAKILL's slot keys 1-6 paired with V1's weapons (revolver Power Beam, shotgun Missiles,
        // nailgun Shock Coil, railcannon Imperialist, rocket launcher Battlehammer, then Volt Driver), 7 Judicator, 8 Magmaul
        public static readonly BeamType[] KeyOrder =
        {
            BeamType.PowerBeam, BeamType.Missile, BeamType.ShockCoil, BeamType.Imperialist,
            BeamType.Battlehammer, BeamType.VoltDriver, BeamType.Judicator, BeamType.Magmaul
        };

        // PlayerInput weapon order: the weapon wheel slots (Power Beam at the top, clockwise) and scrolling
        public static readonly BeamType[] WeaponOrder =
        {
            BeamType.PowerBeam, BeamType.Missile, BeamType.VoltDriver, BeamType.Battlehammer,
            BeamType.Imperialist, BeamType.Judicator, BeamType.Magmaul, BeamType.ShockCoil
        };

        // what Samus carries (MPH's own weapon set: the engine grants them - hunters drop theirs, later)
        public bool Has(BeamType b) => b == BeamType.PowerBeam || b >= 0 && (int)b < Carried.Length && Carried[(int)b];

        // PlayerInput.ProcessTouchInput (testbed processWeaponInput): weapon keys pick a weapon, scroll cycles through
        // every weapon (MphRead Controls.ScrollAllWeapons), skipping ones Samus doesn't have
        private void ProcessWeaponSelect(in SamusInput input)
        {
            if (Health <= 0) { return; }
            ProcessWeaponWheel(input);
            if (WheelOpen) { return; }
            if (input.WeaponSlot >= 1 && input.WeaponSlot <= KeyOrder.Length)
            {
                BeamType b = KeyOrder[input.WeaponSlot - 1];
                if (b != CurrentWeapon) { TryEquipWeapon(b); }
                return;
            }
            if (input.WeaponCycle == 0) { return; }
            int current = Math.Max(0, Array.IndexOf(WeaponOrder, CurrentWeapon));
            int i = current;
            do
            {
                i = (i + (input.WeaponCycle > 0 ? 1 : WeaponOrder.Length - 1)) % WeaponOrder.Length;
            }
            while (i != current && !Has(WeaponOrder[i]));
            if (WeaponOrder[i] != CurrentWeapon) { TryEquipWeapon(WeaponOrder[i]); }
        }

        // ---- weapon wheel (testbed processWeaponWheel, not MPH) ----
        // Hold the wheel key: the mouse moves a cursor instead of aiming; the slot it points at (8 slots in WeaponOrder,
        // Power Beam at the top, clockwise) is equipped on release.
        public const float WheelCounts = 60;       // mouse counts from the center to the rim
        public const float WheelDeadzone = 0.35f;  // the cursor must be this far out to pick a slot
        public bool WheelOpen;
        public float WheelX, WheelY;               // cursor, -1..1 (y up)
        public int WheelSelection = -1;
        public WheelEvent WheelEventThisStep;
        private bool _prevWheel;

        private void ProcessWeaponWheel(in SamusInput input)
        {
            WheelEventThisStep = WheelEvent.None;
            bool can = Health > 0 && !AltForm && !Morphing;
            if (input.WheelHeld && !_prevWheel && can && !WheelOpen)
            {
                WheelOpen = true;
                WheelX = WheelY = 0;
                WheelSelection = -1;
                WheelEventThisStep = WheelEvent.Open;
            }
            _prevWheel = input.WheelHeld;
            if (!WheelOpen) { return; }
            WheelX += input.LookDX / WheelCounts;
            WheelY += input.LookDY / WheelCounts;
            float len = MathF.Sqrt(WheelX * WheelX + WheelY * WheelY);
            if (len > 1)
            {
                WheelX /= len;
                WheelY /= len;
            }
            if (len > WheelDeadzone)
            {
                float ang = MathF.Atan2(WheelX, WheelY) * (180 / MathF.PI);   // 0 = up, clockwise
                if (ang < 0) { ang += 360; }
                int sel = (int)MathF.Round(ang / 45f) % 8;
                if (sel != WheelSelection) { WheelEventThisStep = WheelEvent.Hover; }
                WheelSelection = sel;
            }
            if (!input.WheelHeld || !can)
            {
                WheelOpen = false;
                BeamType? b = WheelSelection >= 0 ? WeaponOrder[WheelSelection] : (BeamType?)null;
                if (b == null) { WheelEventThisStep = WheelEvent.CloseEmpty; }
                else if (b == CurrentWeapon || !Has(b.Value)) { WheelEventThisStep = WheelEvent.CloseFail; }
                else
                {
                    WheelEventThisStep = WheelEvent.None;   // the arm cannon plays its own switch sound
                    TryEquipWeapon(b.Value);
                }
                WheelSelection = -1;
            }
        }

        // Not MPH: ULTRAKILL launches (jump pads, blasts, launch triggers) carry Samus with no air drag (or speed cap)
        // until she lands
        public static float JumpSpeed => Fixed.ToFloat(V.JumpSpeed);
        public bool Launched;
        private int _launchGrace, _bhopSteps;
        private const int LaunchGraceSteps = 6;
        public int BhopWindowSteps = 9;   // after a launch landing: jump within this many 60 Hz steps to keep the speed

        // jump pads, ULTRAKILL launches: this exact speed next step (units per DS frame), momentum kept after
        public void SetLaunchSpeed(Vector3 speed)
        {
            _pullSpeed = speed;
            _pullPending = true;
        }
        private Vector3 _pullSpeed;
        private bool _pullPending;

        private void ApplyLaunch()
        {
            if (_pullPending)
            {
                _pullPending = false;
                Speed = _pullSpeed;
                Launched = true;
                _launchGrace = LaunchGraceSteps;
            }
        }

        // ammo pickups / grants: the raw count (10 per missile), capped
        public void GiveMissiles(int raw) { MissileAmmo = Math.Min(MissileAmmo + raw, MissileAmmoMax); }
    }
}
