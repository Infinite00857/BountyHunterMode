// SamusSim - Samus's biped gameplay from MphRead, engine-agnostic and steppable headlessly (MphSim).
// Ported from MphRead (MIT): Entities/Players/PlayerInput.cs ProcessBiped + ProcessMovement (walk/strafe traction,
// speed caps, friction factors, gravity, jump), PlayerEntity.cs UpdateGunAnimation/SetGunAnimation, the Power Beam
// charge/fire rules (ProcessBiped shoot block + TryFireWeapon cooldowns), PlayerProcess.cs UpdateAimVecs (gun draw
// position + view bob), PlayerEntity.TakeDamage (energy, damage invulnerability). Values: Metadata/Player.cs
// (Samus PlayerValues) and Metadata/Weapons.cs ("Power Beam 1P").
//
// Units/timing as in MphRead: world units, speeds in units per DS (30 Hz) frame, one Step() = one 60 Hz frame with
// the "/ 2" half-steps and "* 2" timers MphRead uses. The engine scales units by its own factor k.
//
// Not MPH (mod decisions):
// - Aim comes straight from the engine camera (testbed's CENTER CROSSHAIR mode): no facing lag, no HUD shift.
// - Collision is the engine's: the adapter reports Standing / ceiling / lateral hits after its physics step.
using System;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    public struct SamusInput
    {
        public float MoveX, MoveY;        // strafe right / forward, -1..1 (MPH input is digital: |v| > 0.5 = held)
        public bool Jump, JumpPressed;
        public bool Fire, FirePressed;
        public Vector3 Facing;            // camera forward (any length > 0); in the ball: the ball camera's
        public Vector3 Heading;           // the camera's yaw as a horizontal direction (walking while looking straight down)
        public bool HasLookInput;
        public bool MorphPressed;         // morph / unmorph (ULTRAKILL slide key)
        public bool Boost;                // Boost Ball held (ULTRAKILL dash key)
        public bool CanUnmorph;           // the engine found room to stand up (PlayerFlags1.NoUnmorph clear)
        public bool ViewTogglePressed;    // F5: third-person view
        public bool Zoom;                 // zoom button held (Imperialist)
        public int WeaponSlot;            // 0 none, 1-8 = SamusSim.KeyOrder (weapon keys)
        public int WeaponCycle;           // +1 next / -1 previous
        public bool WheelHeld;            // testbed weapon wheel key (Q)
        public float LookDX, LookDY;      // mouse counts this step (the wheel cursor; y up)
        public bool InWater;              // Samus's center (the ball's) is under water
    }

    public enum ShotKind { Uncharged, Charged }

    // BeamProjectileEntity.Spawn values for one shot (already interpolated by charge)
    public struct ShotEvent
    {
        public BeamType Weapon;
        public ShotKind Kind;
        public float ChargeFraction;      // 0 = min charge, 1 = full (PartialCharge interpolation)
        public int Damage, HeadshotDamage, SplashDamage;
        public float SplashRadius;        // units
        public int SplashDmgType;         // interpolation of splash damage over distance (0 lerp .. 3 binary)
        public float Speed, FinalSpeed;   // units per DS frame
        public float SpeedDecayTime;      // seconds (0 = none)
        public int SpeedInterp;
        public float Gravity;             // units per DS frame^2
        public float Homing;              // max steer per 60 Hz step (units); 0 = no homing
        public float HomingRange, HomingTolerance;
        public float Radius;
        public int Lifespan;              // DS frames
        public float MaxDistance;         // units (0 = none)
        public WeaponInfo Info;           // the weapon this shot came from
        public int Cost;                  // ammo spent
        public bool Continuous, Ricochet, SelfDamage, ForceEffect;
        public int Projectiles, Spread;   // shots per trigger pull, max spread (degrees x 4096)
        public float RicoLossH, RicoLossV;
        public WeaponInfo? RicochetWeapon; // shot fired off the surface it hits (Judicator)
        public float Knockback;           // DmgDirMag (units)
        public int DmgDirType, DamageInterp;
        public int ColEffect;             // collision effect id + 3 (255 = none)
        public int DrawFuncId;
        public ushort Color;              // BGR555
    }

    public sealed partial class SamusSim
    {
        // ---- TUNABLE CONSTANTS (MPH Samus PlayerValues; Metadata/Player.cs) ----
        private const float NearVertical = 0.05f;   // (mod) facing this vertical: walk along the camera's yaw (Heading)
        public static readonly PlayerValues V = Metadata.PlayerValues[(int)Hunter.Samus];
        // single player: Samus's Power Beam is the table's affinity entry (MphRead PlayerEntity.TryEquipWeapon:
        // Weapons.Current[beam + 9] for Samus's Power Beam): 4-frame shot / autofire cooldowns, so holding fire gives
        // 4 shots before the charge, as in the DS game
        public static readonly WeaponInfo PowerBeam = Weapons.Weapons1P[(int)BeamType.PowerBeam + 9];
        public const int EnergyTank = 100;
        public const int StartHealthMax = 99;          // new story save: EnergyTank - 1

        // ---- state (MphRead PlayerEntity field names where it helps) ----
        public Vector3 Speed;                          // units per DS frame (engine writes back after collision)
        public bool Standing, StandingPrevious;
        public bool Walking, Strafing, MovingBiped, UsedJump;
        public float HSpeedCap = Fixed.ToFloat(V.WalkSpeedCap);
        public float HSpeedMag;
        public float Gravity;
        public int TimeSinceGrounded, TimeStanding;
        public float Field684, Field688;               // strafe / walk camera tilt accumulators
        public float GunViewBob = 270, WalkViewBob;
        public float Field44C;                         // landing camera dip (units)
        public int TimeBeforeLanding;
        public int LandAirSteps;                       // air time of the last landing (the landing sound's DGN amount)
        public float RollAmount;                       // Morph Ball roll loop DGN amount (PlayerSound.UpdateAltMovementSfx)
        private int _soundStep;
        public int TimeSinceInput, TimeIdle;
        public Vector3 Facing = new Vector3(0, 0, -1);
        public Vector3 Left = new Vector3(-1, 0, 0);   // _gunVec2 (MPH naming: left)
        public Vector3 Up = Vector3.UnitY;

        // weapon
        public int ChargeLevel;                        // 60 Hz steps held (EquipInfo.ChargeLevel)
        public int SmokeLevel;
        public int TimeSinceShot = 255;
        public int AutofireCooldown;
        public bool Shooting, NoShotsFired = true;
        public bool ShotUncharged, ShotCharged;

        // damage
        public int Health = StartHealthMax, HealthMax = StartHealthMax;
        public bool Holding;                           // an item in the left hand: no morphing
        // the engine's gravity (units per DS frame^2, negative = down) instead of MPH's while airborne (NaN = MPH's):
        // ULTRAKILL's elevators use V1's gravity
        public float GravityOverride = float.NaN;
        public int DamageInvulnTimer, SpawnInvulnTimer, TimeSinceDamage = 255, TimeSinceHeal = 255;
        public int DamageFlash;
        public bool Dead => Health <= 0;

        // gun model animation (driven here, posed by the adapter)
        public GunAnimation GunAnim { get; private set; } = GunAnimation.Idle;
        public AnimFlags GunAnimFlags { get; private set; } = AnimFlags.NoLoop;
        public bool GunAnimChanged { get; set; }

        // events this step (the adapter reads + clears)
        public bool JumpedThisStep, LandedThisStep;
        public ShotEvent? ShotThisStep;
        public bool ChargeStarted, ChargeStopped, FullChargeReached;
        private bool _chargeSfxOn, _fullChargeSeen;

        public float ChargeFraction => ChargeLevel <= Weapon.MinCharge * 2 ? 0
            : Math.Min(1f, (ChargeLevel - Weapon.MinCharge * 2f) / ((Weapon.FullCharge - Weapon.MinCharge) * 2f));
        public bool Charging => Weapon.Flags.HasFlag(WeaponFlags.CanCharge) && ChargeLevel >= Weapon.MinCharge * 2;

        public void Spawn()
        {
            Speed = Vector3.Zero;
            Standing = StandingPrevious = true;
            ChargeLevel = 0;
            ResetForm();
            SpawnInvulnTimer = 30 * 2;
            BurnTimer = 0;
            ResetAfflictions();
            SetGunAnimation(GunAnimation.UpDown, AnimFlags.NoLoop);
        }

        // One 60 Hz step: input -> Speed. The adapter moves the body with Speed, then calls AfterCollision.
        public void Step(in SamusInput rawInput)
        {
            StepAfflictions();
            SamusInput input = AfflictInput(rawInput);   // frozen: no input
            JumpedThisStep = false;   // (LandedThisStep is set by AfterCollision, which runs before Step)
            ShotThisStep = null;
            ChargeStarted = ChargeStopped = FullChargeReached = false;
            ShotUncharged = ShotCharged = ShotMissile = false;
            SwitchedThisStep = WeaponSwitch.None;
            ZoomChanged = false;
            InWater = input.InWater;

            BeginBallStep();
            ProcessWeaponSelect(input);
            Vector3 f = input.Facing;
            if (f.LengthSquared > 1e-6f && !InBall)
            {
                Facing = f.Normalized();
            }
            if (input.HasLookInput || Math.Abs(input.MoveX) > 0.5f || Math.Abs(input.MoveY) > 0.5f || input.Fire || input.Jump
                || input.Boost || input.MorphPressed)
            {
                TimeSinceInput = 0;
            }
            else if (TimeSinceInput < 100000)
            {
                TimeSinceInput++;
            }
            if (SmokeLevel < Weapon.SmokeDrain) { SmokeLevel = 0; } else { SmokeLevel -= Weapon.SmokeDrain; }

            Vector3 speedDelta = Vector3.Zero;
            Strafing = Walking = MovingBiped = false;
            // horizontal facing / left (ProcessMovement: _field70/_field74, _gunVec2)
            float hMag = MathF.Sqrt(Facing.X * Facing.X + Facing.Z * Facing.Z);
            float f70 = hMag > 1e-5f ? Facing.X / hMag : 0, f74 = hMag > 1e-5f ? Facing.Z / hMag : -1;
            if (hMag < NearVertical && input.Heading.LengthSquared > 1e-6f)
            {
                // looking (almost) straight up / down: forward is where the camera's yaw points
                float l = MathF.Sqrt(input.Heading.X * input.Heading.X + input.Heading.Z * input.Heading.Z);
                if (l > 1e-5f) { f70 = input.Heading.X / l; f74 = input.Heading.Z / l; }
            }
            Left = new Vector3(f74, 0, -f70);
            if (Health > 0 && InBall)
            {
                ProcessAlt(input);
            }
            else if (Health > 0)
            {
                bool right = input.MoveX > 0.5f, leftIn = input.MoveX < -0.5f, fwd = input.MoveY > 0.5f, back = input.MoveY < -0.5f;
                if (right || leftIn)
                {
                    int sign = right ? 1 : -1;
                    Strafing = true;
                    MovingBiped = true;
                    Walking = Standing;
                    float traction = Fixed.ToFloat(V.StrafeBipedTraction);
                    speedDelta.X -= Left.X * traction * sign;
                    speedDelta.Z -= Left.Z * traction * sign;
                    Field684 = Math.Clamp(Field684 + Fixed.ToFloat(V.Field114) * sign / 2, -180, 180);
                }
                if (Field684 < Fixed.ToFloat(500) && Field684 > Fixed.ToFloat(-500)) { Field684 = 0; } else { Field684 *= 0.9f; }
                if (fwd || back)
                {
                    int sign = fwd ? 1 : -1;
                    MovingBiped = true;
                    Walking = Standing;
                    float traction = Fixed.ToFloat(V.WalkBipedTraction);
                    speedDelta.X += f70 * traction * sign;
                    speedDelta.Z += f74 * traction * sign;
                    Field688 = Math.Clamp(Field688 + Fixed.ToFloat(V.Field114) * sign / 2, -180, 180);
                }
                if (Field688 < Fixed.ToFloat(500) && Field688 > Fixed.ToFloat(-500)) { Field688 = 0; } else { Field688 *= 0.9f; }
                if (input.JumpPressed && !UsedJump)
                {
                    UsedJump = true;
                    Speed.Y = Fixed.ToFloat(V.JumpSpeed) * WaterJump;
                    if (Launched)
                    {
                        _launchGrace = LaunchGraceSteps;   // jumped out of a launch landing: the speed carries on
                        _bhopSteps = 0;
                    }
                    TimeSinceGrounded = 8 * 2;
                    JumpedThisStep = true;
                }
                if (!Unmorphing)
                {
                    ProcessZoom(input);
                    ProcessWeapon(input);
                    if (input.MorphPressed && !Morphing && !Holding)
                    {
                        EnterAltForm();
                    }
                    else if (input.ViewTogglePressed && !Morphing)
                    {
                        ToggleView();
                    }
                }
                // apply input with the speed cap (ProcessBiped end); under water: less traction, lower cap
                float magBefore = MathF.Sqrt(Speed.X * Speed.X + Speed.Z * Speed.Z);
                Speed += speedDelta * WaterScale;
                float cap = HSpeedCap * WaterScale;
                float magAfter = MathF.Sqrt(Speed.X * Speed.X + Speed.Z * Speed.Z);
                if (magAfter > magBefore && magAfter > cap)
                {
                    float factor = magBefore <= cap ? cap / magAfter : magBefore / magAfter;
                    Speed.X *= factor;
                    Speed.Z *= factor;
                }
            }
            ProcessMovement();
            if (Health > 0 && !InBall)
            {
                // (while unmorphing too: the legs follow the movement, the torso finishes the unmorph)
                UpdateBipedAnimation(input);
                UpdateTorsoAnimation();
            }
            UpdateBob();
            UpdateRollAmount();
            UpdateGunAnimation();
            UpdateTimers();
            EndBallStep();
        }

        // ProcessMovement up to the position update (the engine does the move + collision).
        private void ProcessMovement()
        {
            var hSpeed = new Vector3(Speed.X, 0, Speed.Z);
            HSpeedMag = hSpeed.Length;
            if (InBall)
            {
                BallMovement(hSpeed);
            }
            else
            {
                HSpeedCap = Fixed.ToFloat(Strafing ? V.StrafeSpeedCap : V.WalkSpeedCap);
            }
            float speedFactor;
            if (InBall)
            {
                speedFactor = Fixed.ToFloat(Standing ? V.AltGroundSpeedFactor : V.AirSpeedFactor);
            }
            else if (Standing)
            {
                speedFactor = Fixed.ToFloat(Strafing ? V.StrafeSpeedFactor : (Walking ? V.WalkSpeedFactor : V.StandSpeedFactor));
            }
            else
            {
                speedFactor = Fixed.ToFloat(V.AirSpeedFactor);
            }
            if (Launched && (!Standing || _launchGrace > 0 || _bhopSteps > 0))
            {
                speedFactor = 1;   // not MPH (owner): blast launches keep their speed until she lands (no floor friction while leaving it)
            }
            Vector3 speedMul = new Vector3(Speed.X * speedFactor, Speed.Y, Speed.Z * speedFactor);
            Speed += (speedMul - Speed) / 2;
            if (Health > 0)
            {
                // ProcessMovement: the ball (and a biped still rising from the ball) uses the ball's gravity
                if (AltForm) { _altGravity = true; }
                else if (Speed.Y <= 0.01f) { _altGravity = false; }
                if (AltForm || _altGravity)
                {
                    Gravity = Fixed.ToFloat(Standing ? V.AltGroundGravity : V.AltAirGravity);
                }
                else
                {
                    Gravity = Standing ? 0 : Fixed.ToFloat(V.BipedGravity);
                }
                if (!float.IsNaN(GravityOverride) && !Standing) { Gravity = GravityOverride; }
                ApplyGravity();
            }
        }

        // After the engine's physics step: standing = on floor, ceiling = hit head, newSpeed = engine velocity
        // converted back to units per DS frame (wall slides, landings).
        public void AfterCollision(bool standing, bool ceiling, Vector3 newSpeed)
        {
            StandingPrevious = Standing;
            Standing = standing;
            LandedThisStep = false;
            if (_launchGrace > 0) { _launchGrace--; }
            LandShake = 0;
            float fallSpeed = Speed.Y;   // before the collision zeroes it
            Speed.X = newSpeed.X;
            Speed.Z = newSpeed.Z;
            if (standing && Speed.Y < 0) { Speed.Y = 0; }
            if (ceiling && Speed.Y > 0) { Speed.Y = 0; }
            ApplyLaunch();
            if (Standing)
            {
                if (!StandingPrevious)
                {
                    LandedThisStep = true;
                    // ProcessMovement landing: the view dips by up to 800/4096, deeper the faster the fall, and the
                    // camera shakes on hard landings (testbed processMovement / updateCameraFirst). Owner playtest:
                    // the old instant drop + slow recovery felt like clamping into the floor.
                    TimeStanding = 0;
                    Field44C = fallSpeed >= 0 ? 0 : Math.Min(-fallSpeed * LandDipFactor, LandDipMax);
                    if (fallSpeed < LandShakeSpeed) { LandShake = LandShakeAmount; }
                    LandAirSteps = TimeBeforeLanding;
                    TimeBeforeLanding = 0;
                }
                else
                {
                    TimeStanding = Math.Min(TimeStanding + 1, 65535);
                }
                if (Launched && _launchGrace == 0)   // (a launch from the floor leaves it first)
                {
                    // landing after a launch: a jump within BhopWindowSteps keeps the speed (owner), else it ends
                    if (!StandingPrevious) { _bhopSteps = BhopWindowSteps; }
                    else if (_bhopSteps > 0) { _bhopSteps--; }
                    if (_bhopSteps == 0) { Launched = false; }
                }
                UsedJump = false;
                TimeSinceGrounded = 0;
            }
            else
            {
                TimeStanding = 0;
                TimeBeforeLanding++;
                if (TimeSinceGrounded < 1000) { TimeSinceGrounded++; }
                // walked off a ledge: MPH lets you jump during the first frames (UsedJump stays clear)
            }
        }

        // ---- TUNABLE CONSTANTS (MPH landing; testbed mph_player.cpp) ----
        private const float LandDipFactor = 0.35f, LandDipMax = 800 / 4096f;
        private const float LandShakeSpeed = -0.65f, LandShakeAmount = 204 / 4096f;
        private const int LandDipSteps = 9 * 2;
        public float LandShake;   // camera shake this step (MPH units), 0 = none

        // UpdateCameraFirst: the eye eases down and back up over 9 DS frames after landing (cosine), in MPH units.
        // stepFraction: how far into the next 60 Hz step this frame is (smooth at high frame rates).
        public float LandingDip(float stepFraction = 0)
        {
            float t = TimeStanding + stepFraction;
            if (t >= LandDipSteps || Field44C <= 0) { return 0; }
            float angle = 2 * MathF.PI * t / LandDipSteps;
            return MathF.Cos(angle) * Field44C - Field44C;
        }

        // Charge + fire (PlayerInput.ProcessBiped shoot block; testbed processWeaponFire).
        private void ProcessWeapon(in SamusInput input)
        {
            WeaponInfo w = Weapon;
            _torsoCharge = false;
            if (!input.Fire)
            {
                Shooting = false;
            }
            else if (input.FirePressed || !NoShotsFired)
            {
                Shooting = true;
                NoShotsFired = false;
            }
            if (!w.Flags.HasFlag(WeaponFlags.CanCharge))
            {
                ChargeLevel = 0;
            }
            else
            {
                int ammo = AmmoRaw;
                bool releaseCharge = !Shooting || ammo >= 0 && ammo < w.ChargeCost;
                if (!releaseCharge)
                {
                    if (ChargeLevel > 0 && GunAnim != GunAnimation.MissileClose)
                    {
                        _torsoCharge = true;
                        // the Power Beam charge sound waits for MinCharge, the others start right away
                        if ((CurrentWeapon != BeamType.PowerBeam || ChargeLevel >= w.MinCharge * 2) && !_chargeSfxOn)
                        {
                            _chargeSfxOn = true;
                            ChargeStarted = true;
                        }
                    }
                    if (ChargeLevel >= w.FullCharge * 2)
                    {
                        SmokeLevel = Math.Min(SmokeLevel + w.SmokeChargeAmount, w.SmokeStart * 2);
                        if (!_fullChargeSeen)
                        {
                            _fullChargeSeen = true;
                            FullChargeReached = true;
                        }
                    }
                    else
                    {
                        ChargeLevel++;
                        int minCharge = w.MinCharge * 2;
                        if (ammo >= 0 && ChargeLevel > minCharge)
                        {
                            // not enough ammo to charge further
                            int fullCharge = w.FullCharge * 2, chargeCost = w.ChargeCost * 2, minCost = w.MinChargeCost * 2;
                            int cost = minCost + (chargeCost - minCost) * (ChargeLevel - minCharge) / (fullCharge - minCharge);
                            if (ammo < cost / 2) { ChargeLevel--; }
                        }
                    }
                }
                else
                {
                    if (_chargeSfxOn)
                    {
                        _chargeSfxOn = false;
                        ChargeStopped = true;
                    }
                    if (ChargeLevel >= w.MinCharge * 2)
                    {
                        TryFire(pressed: input.FirePressed);
                    }
                    ChargeLevel = 0;
                    _fullChargeSeen = false;
                }
            }
            // tap / repeat fire (RepeatFire while held, until the charge passes MinCharge). With Samus's single-player
            // Power Beam (the affinity entry, 4-frame cooldowns) holding fire gives 4 shots (0, 0.133, 0.3, 0.5 s),
            // then the charge starts at 0.583 s - as in the DS game and MphRead
            bool tap = input.FirePressed && ChargeLevel <= 1 * 2;
            bool repeat = w.Flags.HasFlag(WeaponFlags.RepeatFire) && Shooting
                && (!w.Flags.HasFlag(WeaponFlags.CanCharge) || ChargeLevel < w.MinCharge * 2);
            if (tap || repeat)
            {
                TryFire(pressed: input.FirePressed);
            }
        }

        // TryFireWeapon: cooldowns (Power Beam autofire slows down the longer it repeats), ammo, no shots during UpDown.
        private bool TryFire(bool pressed)
        {
            if (InBall || Morphing) { return false; }
            WeaponInfo w = Weapon;
            if (pressed || CurrentWeapon != BeamType.PowerBeam)
            {
                AutofireCooldown = w.AutofireCooldown * 2;
                _powerBeamAutofire = 0;
            }
            else
            {
                if (_powerBeamAutofire < ushort.MaxValue) { _powerBeamAutofire++; }
                int pbAuto = Math.Min(_powerBeamAutofire / 2, 90);
                pbAuto = (int)(pbAuto * 15 / 90f);
                AutofireCooldown = (pbAuto + w.AutofireCooldown) * 2;
            }
            if (TimeSinceShot < w.ShotCooldown * 2 || !pressed && TimeSinceShot < AutofireCooldown)
            {
                return false;
            }
            if (GunAnim == GunAnimation.UpDown)
            {
                return false;
            }
            if (BuildShot(w, out int cost) is not ShotEvent shot)
            {
                return false;   // out of ammo (BeamSfx Empty: silent for Samus)
            }
            SpendAmmo(w, cost);
            if (shot.Kind == ShotKind.Uncharged)
            {
                SmokeLevel = Math.Min(SmokeLevel + w.SmokeShotAmount, w.SmokeStart);
            }
            TimeSinceShot = 0;
            if (CurrentWeapon == BeamType.Missile) { ShotMissile = true; }
            if (ChargeLevel >= w.MinCharge * 2) { ShotCharged = true; } else { ShotUncharged = true; }
            if (_chargeSfxOn)
            {
                _chargeSfxOn = false;
                ChargeStopped = true;
            }
            ShotThisStep = shot;
            return true;
        }
        private int _powerBeamAutofire;

        // PlayerProcess.ProcessPlayer view bob (testbed updateBobAndSway) + PlayerSound.UpdateWalkingSfx timing.
        public int FootstepThisStep;   // 0 none, 1 = step 1, 2 = step 2
        private float _walkSfxTimer;
        private int _walkSfxIndex;
        private void UpdateBob()
        {
            float bobMax = Fixed.ToFloat(V.WalkBobMax);
            if (Walking)
            {
                GunViewBob += 14 / 2f;
                if (GunViewBob > 450) { GunViewBob -= 180; }
                WalkViewBob = Math.Min(WalkViewBob + 1 / 2f, bobMax);
            }
            else
            {
                WalkViewBob = Math.Max(WalkViewBob - bobMax / 32 / 2f, 0);
                GunViewBob = GunViewBob >= 360 ? Math.Min(GunViewBob + 14 / 2f, 450) : Math.Max(GunViewBob - 14 / 2f, 270);
            }
            FootstepThisStep = 0;
            if (!Standing || !MovingBiped || HSpeedMag <= 0)
            {
                _walkSfxTimer = 0;
                _walkSfxIndex = 0;
                return;
            }
            _walkSfxTimer += 1 / 60f;
            if (_walkSfxTimer >= 15 / 30f && _walkSfxIndex == 0)
            {
                FootstepThisStep = 1;
                _walkSfxIndex = 1;
            }
            if (_walkSfxTimer >= 25 / 30f)
            {
                FootstepThisStep = 2;
                _walkSfxTimer = 5 / 30f;
                _walkSfxIndex = 0;
            }
        }

        // testbed updateLoopSounds: the roll loop's DGN amount follows the ball's speed (rises fast, falls slower, on 30 Hz
        // frames), decays in the air, off below 1000
        private const float RollAmountOff = 1000, RollAirDecay = 0.5f;
        private void UpdateRollAmount()
        {
            float amount = 0;
            if (AltForm)
            {
                float target = 0xFFFF * HSpeedMag / Fixed.ToFloat(V.AltMinHSpeed);
                if (!Standing) { amount = RollAmount * MathF.Pow(MathF.Pow(RollAirDecay, 30), 1 / 60f); }
                else if (_soundStep % 2 == 0) { amount = RollAmount + (target - RollAmount) / (target < RollAmount ? 4 : 2); }
                else { amount = RollAmount; }
                if (amount < RollAmountOff) { amount = 0; }
            }
            RollAmount = amount;
            _soundStep++;
        }

        // Gun draw offset in camera space (UpdateAimVecs _gunDrawPos; x = left, y = up, z = forward), units.
        public Vector3 GunOffset()
        {
            float bobY = Fixed.ToFloat(20) * MathF.Cos(MathHelper.DegreesToRadians(GunViewBob));
            return new Vector3(Fixed.ToFloat(V.FieldB0), Fixed.ToFloat(V.FieldB4) + bobY, Fixed.ToFloat(V.FieldB8));
        }
        public float MuzzleOffset => Fixed.ToFloat(V.MuzzleOffset);
        public float AimDistance => Fixed.ToFloat(V.AimDistance);
        public float EyeHeight => Fixed.ToFloat(V.AimYOffset);          // camera above the player position
        public float CollisionRadius => Fixed.ToFloat(V.BipedColRadius);
        public float CameraFov => Fixed.ToFloat(V.NormalFov) * 2;

        // PlayerEntity.UpdateGunAnimation (testbed updateGunAnimation, without the idle lowering)
        private void UpdateGunAnimation()
        {
            bool ended = _gunEnded;
            if (GunAnim == GunAnimation.UpDown)
            {
                if (!ended) { return; }
                if (CurrentWeapon == BeamType.Missile) { SetGunAnimation(GunAnimation.MissileOpen, AnimFlags.NoLoop); }
            }
            if (ShotUncharged)
            {
                if (!ShotMissile) { SetGunAnimation(GunAnimation.Shot, AnimFlags.NoLoop); }
                else if (!_gunOpenAnim) { SetGunAnimation(GunAnimation.Unknown9, AnimFlags.NoLoop); }
                else { SetGunAnimation(GunAnimation.MissileShot, AnimFlags.NoLoop); }
                return;
            }
            if (ShotCharged)
            {
                SetGunAnimation(ShotMissile ? GunAnimation.MissileShot : GunAnimation.ChargeShot, AnimFlags.NoLoop);
                return;
            }
            if (Charging)
            {
                // (the Charging frame follows the charge level: GunModel)
                if (CurrentWeapon == BeamType.Missile)
                {
                    if (GunAnim != GunAnimation.ChargingMissile && GunAnim != GunAnimation.FullChargeMissile)
                    {
                        SetGunAnimation(GunAnimation.ChargingMissile, AnimFlags.NoLoop);
                    }
                    else if (GunAnim != GunAnimation.FullChargeMissile && ended)
                    {
                        SetGunAnimation(GunAnimation.FullChargeMissile, AnimFlags.None);
                    }
                }
                else if (GunAnim != GunAnimation.Charging && GunAnim != GunAnimation.FullCharge)
                {
                    SetGunAnimation(GunAnimation.Charging, AnimFlags.NoLoop);
                }
                else if (GunAnim != GunAnimation.FullCharge && ended)
                {
                    SetGunAnimation(GunAnimation.FullCharge, AnimFlags.None);
                }
                return;
            }
            if ((GunAnim == GunAnimation.Charging || GunAnim == GunAnimation.ChargingMissile) && !GunAnimFlags.HasFlag(AnimFlags.Reverse))
            {
                GunAnimFlags |= AnimFlags.Reverse;
                ReverseRequested = true;
                return;
            }
            if (GunAnim == GunAnimation.FullCharge)
            {
                SetGunAnimation(GunAnimation.Idle, AnimFlags.NoLoop);
                return;
            }
            if (GunAnim == GunAnimation.FullChargeMissile)
            {
                SetGunAnimation(GunAnimation.MissileOpen, AnimFlags.NoLoop);
                JumpToEndRequested = true;   // the launcher stays open (last frame)
                return;
            }
            if (ended)
            {
                if (!_gunOpenAnim || GunAnim == GunAnimation.MissileClose)
                {
                    if (GunAnim != GunAnimation.Idle) { SetGunAnimation(GunAnimation.Idle, AnimFlags.NoLoop); }
                }
                else if (CurrentWeapon == BeamType.Missile && WeaponSelection != BeamType.Missile)
                {
                    SetGunAnimation(GunAnimation.MissileClose, AnimFlags.NoLoop);
                }
            }
        }

        // the adapter owns the ModelInstance; it reports whether the gun animation ended and applies changes
        private bool _gunEnded;
        public bool ReverseRequested { get; set; }
        public bool JumpToEndRequested { get; set; }
        public void ReportGunAnimEnded(bool ended) { _gunEnded = ended; }

        // animWeapon: whose slot-1 (per-weapon material) column plays; the Switch animation uses the previous weapon's
        private void SetGunAnimation(GunAnimation anim, AnimFlags flags, BeamType? animWeapon = null)
        {
            GunAnim = anim;
            GunAnimFlags = flags;
            GunAnimWeapon = animWeapon ?? CurrentWeapon;
            GunAnimChanged = true;
            _gunEnded = false;
            _gunOpenAnim = IsOpenAnim(anim);
        }

        // Metadata.GunAnimationIds (slot 0 = node anim, slot 1 = per-weapon material anim)
        public static int GunAnimationId(GunAnimation anim, int slot)
        {
            return Metadata.GunAnimationIds[(int)Hunter.Samus, (int)anim, slot];
        }

        private void UpdateTimers()
        {
            if (TimeSinceShot < 255) { TimeSinceShot++; }
            if (DamageInvulnTimer > 0) { DamageInvulnTimer--; }
            if (SpawnInvulnTimer > 0) { SpawnInvulnTimer--; }
            if (TimeSinceDamage < 255) { TimeSinceDamage++; }
            if (TimeSinceHeal < 255) { TimeSinceHeal++; }
            if (DamageFlash > 0) { DamageFlash--; }
            UpdateWeaponTimers();
            UpdateBallTimers();
            UpdateWorldTimers();
        }

        // PlayerEntity.TakeDamage (energy + invulnerability). Returns the damage actually taken.
        public int TakeDamage(int damage, bool ignoreInvuln = false)
        {
            if (Health <= 0 || damage <= 0)
            {
                return 0;
            }
            if (!ignoreInvuln && (SpawnInvulnTimer > 0 || DamageInvulnTimer > 0))
            {
                return 0;
            }
            Health = Math.Max(0, Health - damage);
            DamageInvulnTimer = V.DamageInvuln * 2;
            TimeSinceDamage = 0;
            DamageFlash = 10 * 2;
            return damage;
        }

        public void GainHealth(int amount)
        {
            if (Health <= 0) { return; }
            Health = Math.Min(HealthMax, Health + amount);
            TimeSinceHeal = 0;
        }
    }
}
