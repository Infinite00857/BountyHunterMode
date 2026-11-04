// SamusSim (Morph Ball) - ported from the testbed's playtested Morph Ball (hunters/mph_player.cpp processAlt,
// enterAltForm, tryExitAltForm, updateBallRoll, processMovement alt parts, bomb ammo), which ports MphRead (MIT)
// PlayerInput.ProcessAlt / PlayerProcess (EnterAltForm, UpdateForm, InitAltTransform, UpdateAltTransform).
// The engine owns positions: it places bombs, finds headroom (CanUnmorph), runs the ball camera and reports the
// ball's movement for the roll (UpdateBallRoll). Biped Morph/Unmorph animations are requested here and played by
// the engine (BipedModel), which reports when they end.
//
// Not MPH (owner / testbed decisions): the camera is not mouse-steerable in the ball (emulator check); the bomb
// cooldown is the testbed's estimate (Samus's BombCooldown value didn't decode cleanly).
using System;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    // MphRead PlayerAnimation (Samus_Anim + SamusSharedAnim)
    public enum BipedAnimation
    {
        Morph = 0, WalkForward = 2, Unmorph = 3, Idle = 8, LandNeutral = 9, JumpNeutral = 12, JumpBack = 13,
        JumpForward = 14, JumpLeft = 15, JumpRight = 16, WalkBackward = 18, WalkLeft = 20, WalkRight = 21,
        Charge = 23, ChargeShoot = 24, Shoot = 25
    }

    public sealed partial class SamusSim
    {
        // ---- TUNABLE CONSTANTS (Morph Ball; testbed SamusValues / mph_player.cpp) ----
        public const int MaxBombs = 3;
        public const int BombFuseFrames = 43;          // BombEntity countdown (DS frames)
        private const float BombCooldownFrames = 8;    // ESTIMATE (testbed): Samus's BombCooldown didn't decode cleanly
        public const int AltAttackCooldownFrames = 15;
        public const int AltAttackDamage = 28;         // Boost Ball ram damage at full charge
        public const int BoostHudFlashSteps = 11 * 2;  // boost icon flash after a boost (11 DS frames)
        private const float SeamSnapLimit = 0.125f;    // UpdateAltTransform: misalignment below this is corrected at once
        private const float SeamFullSpeedAngle = 3216 / 4096f;   // roll angle per step for the full seam ease rate
        private const float SeamEaseDivisor = 8;       // ... 1/8 of the misalignment per step

        // ---- state ----
        public bool AltForm, Morphing, Unmorphing;
        public bool InBall => AltForm || Morphing;
        public int BoostCharge, AltAttackCooldown, BoostDamage, BoostHudSteps;
        public bool Boosting;
        public int BombAmmo = MaxBombs, BombCooldown, BombRefillTimer;
        public Matrix4 AltRot = Matrix4.Identity;     // ball orientation (rows: seam axis, up, forward), no translation
        public Vector3 BallFacing = new Vector3(0, 0, -1);   // _field70/_field74 in the ball (follows the roll)
        private Vector3 _rollFb = new Vector3(0, 0, -1), _rollLr = new Vector3(-1, 0, 0);
        private bool _altGravity;

        // camera switch (PlayerCamera.SwitchCamera): the engine blends its camera over CamSwitchSteps
        public bool ThirdCam;
        public int CamSwitchTimer = 1000;
        public int CamSwitchSteps => V.CamSwitchTime * 2;
        public float CamSwitchFraction => Math.Min(1f, CamSwitchTimer / (float)CamSwitchSteps);
        public bool FirstPerson => !ThirdCam && !InBall && CamSwitchTimer >= CamSwitchSteps;
        public bool ShowBiped => !AltForm && (ThirdCam || CamSwitchTimer < CamSwitchSteps);
        // F5 third-person view (testbed V view; not in the DS game): over-the-shoulder camera, kept across morphs
        public bool ThirdPersonView;
        public bool ThirdView => ThirdCam && !InBall;
        public bool ViewToggledThisStep;

        // biped animation requests (played by the engine's BipedModel)
        public BipedAnimation BipedAnim { get; private set; } = BipedAnimation.Idle;
        public AnimFlags BipedAnimFlags { get; private set; } = AnimFlags.None;
        // torso (MphRead _bipedModel2): shooting / charging poses over the legs' animation
        public BipedAnimation TorsoAnim { get; private set; } = BipedAnimation.Idle;
        public AnimFlags TorsoAnimFlags { get; private set; } = AnimFlags.None;
        public bool TorsoAnimChanged { get; set; }
        public bool TorsoSyncFrame { get; private set; }
        private bool _torsoEnded, _torsoCharge;
        private int _torsoFrame;
        public void ReportTorso(bool ended, int frame) { _torsoEnded = ended; _torsoFrame = frame; }
        public bool BipedAnimChanged { get; set; }
        private bool _bipedEnded;
        public void ReportBipedAnimEnded(bool ended) { _bipedEnded = ended; }

        // events this step
        public bool MorphedThisStep, UnmorphedThisStep, BallFormedThisStep, BombPlacedThisStep, BoostedThisStep;

        // values in MPH units (the engine scales them)
        public float AltColRadius => Fixed.ToFloat(V.AltColRadius);
        public float BombRadius => Fixed.ToFloat(V.BombRadius);
        public float BombSelfRadius => Fixed.ToFloat(V.BombSelfRadius);
        public int BombEnemyDamage => V.BombEnemyDamage;
        public float BoostChargeFraction => BoostCharge / (float)(V.BoostChargeMax * 2);
        public float ThirdCamDistance => Fixed.ToFloat(V.Field78);
        public float ThirdCamHeight => Fixed.ToFloat(V.Field7C);
        public float ThirdCamTargetY => Fixed.ToFloat(V.Field80);
        public float ThirdCamFollow => Fixed.ToFloat(V.Field84);
        public float ThirdCamRadius => Fixed.ToFloat(V.Field90);

        // back on her feet at once (no unmorph animation): where the engine can't have the Morph Ball
        public void ForceBiped() { ResetForm(); }

        private void ResetForm()
        {
            AltForm = Morphing = Unmorphing = false;
            BoostCharge = AltAttackCooldown = BoostHudSteps = 0;
            Boosting = false;
            BombAmmo = MaxBombs;
            BombCooldown = BombRefillTimer = 0;
            ThirdCam = ThirdPersonView;
            CamSwitchTimer = 1000;
            _altGravity = false;
            BipedAnim = BipedAnimation.Idle;
            BipedAnimChanged = true;
            TorsoAnim = BipedAnimation.Idle;
            TorsoAnimFlags = AnimFlags.None;
            TorsoAnimChanged = true;
        }

        private void BeginBallStep()
        {
            MorphedThisStep = UnmorphedThisStep = BallFormedThisStep = BombPlacedThisStep = BoostedThisStep = false;
            ViewToggledThisStep = false;
        }

        // PlayerInput.ProcessAlt (Samus): camera-relative rolling, bombs, Boost Ball, unmorph
        private void ProcessAlt(in SamusInput input)
        {
            Vector3 speedDelta = Vector3.Zero;
            UsedJump = true;
            // roll directions follow the camera (CameraInfo.Field48..54)
            var camFwd = new Vector3(input.Facing.X, 0, input.Facing.Z);
            if (camFwd.LengthSquared > 1e-8f)
            {
                camFwd.Normalize();
                _rollFb = camFwd;
                _rollLr = new Vector3(camFwd.Z, 0, -camFwd.X);   // left = (fz, -fx)
            }
            float traction = Fixed.ToFloat(V.RollAltTraction) * WaterScale;   // (under water)
            if (input.MoveY > 0.5f) { speedDelta += _rollFb * traction; }
            else if (input.MoveY < -0.5f) { speedDelta -= _rollFb * traction; }
            if (input.MoveX < -0.5f) { speedDelta += _rollLr * traction; }
            else if (input.MoveX > 0.5f) { speedDelta -= _rollLr * traction; }

            // bombs and boost wait until the ball has formed (!IsMorphing)
            if (!Morphing && input.FirePressed && BombAmmo > 0 && BombCooldown == 0)
            {
                BombPlacedThisStep = true;
                if (BombAmmo >= 2) { BombRefillTimer = V.BombRefillTime * 2; }
                BombAmmo--;
                BombCooldown = (int)(BombCooldownFrames * 2);
            }

            if (Morphing)
            {
                BoostCharge = 0;
            }
            else if (input.Boost)
            {
                if (BoostCharge < V.BoostChargeMax * 2) { BoostCharge++; }
            }
            else
            {
                // releasing past BoostChargeMin boosts; the cooldown only drives the HUD icon (MphRead has no check here)
                if (BoostCharge > V.BoostChargeMin * 2)
                {
                    float max = V.BoostChargeMax * 2;
                    BoostDamage = (int)(AltAttackDamage * BoostCharge / max);
                    float boostHCap = Fixed.ToFloat(V.BoostSpeedCap) * BoostCharge / max;
                    if (HSpeedCap < boostHCap) { HSpeedCap = boostHCap; }
                    float factor = Fixed.ToFloat(V.BoostSpeedMin)
                        + BoostCharge * (Fixed.ToFloat(V.BoostSpeedMax) - Fixed.ToFloat(V.BoostSpeedMin)) / max;
                    speedDelta.X += BallFacing.X * factor;
                    speedDelta.Z += BallFacing.Z * factor;
                    AltAttackCooldown = AltAttackCooldownFrames * 2;
                    Boosting = true;
                    BoostHudSteps = BoostHudFlashSteps;
                    BoostedThisStep = true;
                }
                BoostCharge = 0;
            }

            // (under water the roll cap is lower too)
            float cap = HSpeedCap * WaterScale;
            float magBefore = MathF.Sqrt(Speed.X * Speed.X + Speed.Z * Speed.Z);
            Speed += speedDelta;
            float magAfter = MathF.Sqrt(Speed.X * Speed.X + Speed.Z * Speed.Z);
            if (magAfter > magBefore && magAfter > cap)
            {
                float factor = magBefore <= cap ? cap / magAfter : magBefore / magAfter;
                Speed.X *= factor;
                Speed.Z *= factor;
            }

            // TrySwitchForms refuses while the morph animation is still playing
            if (AltForm && input.MorphPressed && input.CanUnmorph)
            {
                ExitAltForm();
            }
        }

        // ProcessMovement (alt parts): the ball faces where it rolls; its speed cap decays toward AltMinHSpeed
        private void BallMovement(Vector3 hSpeed)
        {
            if (HSpeedMag <= 0)
            {
                return;
            }
            Vector3 h = hSpeed / HSpeedMag;
            if (HSpeedMag > Fixed.ToFloat(V.Field58))
            {
                BallFacing = h;
            }
            if (!AltForm)
            {
                return;
            }
            float minH = Fixed.ToFloat(V.AltMinHSpeed);
            if (HSpeedCap <= minH) { HSpeedCap = minH; }
            else if (HSpeedMag >= HSpeedCap) { HSpeedCap -= Fixed.ToFloat(V.AltHSpeedCapIncrement) / 2; }
            else { HSpeedCap = HSpeedMag; }
        }

        private void EnterAltForm()
        {
            var lateral = new Vector3(Facing.X, 0, Facing.Z);
            BallFacing = lateral.LengthSquared > 1e-8f ? lateral.Normalized() : new Vector3(0, 0, -1);
            _rollFb = BallFacing;
            _rollLr = new Vector3(BallFacing.Z, 0, -BallFacing.X);
            Morphing = true;
            InitAltTransform();
            SwitchCamera(true);
            if (_chargeSfxOn)
            {
                _chargeSfxOn = false;
                ChargeStopped = true;
            }
            ChargeLevel = 0;
            SmokeLevel = 0;
            Shooting = false;
            SetGunAnimation(GunAnimation.Idle, AnimFlags.NoLoop);
            SetBipedAnimation(BipedAnimation.Morph, AnimFlags.NoLoop, torso: true);
            MorphedThisStep = true;
        }

        private void ExitAltForm()
        {
            AltForm = false;
            Unmorphing = true;
            BoostCharge = 0;
            Boosting = false;
            Facing = BallFacing;   // aim straight ahead along the roll (gunVec1 = facing, aimY = 0)
            SwitchCamera(ThirdPersonView);   // back to the view Samus morphed from
            SetBipedAnimation(BipedAnimation.Unmorph, AnimFlags.NoLoop, torso: true);   // biped2 = biped1
            UnmorphedThisStep = true;
        }

        private void SwitchCamera(bool third)
        {
            ThirdCam = third;
            int steps = CamSwitchSteps;
            CamSwitchTimer = steps - Math.Min(CamSwitchTimer, steps);
        }

        private void ToggleView()
        {
            ThirdPersonView = !ThirdPersonView;
            SwitchCamera(ThirdPersonView);
            ViewToggledThisStep = true;
        }

        // biped animation for the third-person view (one animation for the whole body; MphRead plays separate
        // legs/torso animations): jumps, landing, walk/strafe by input, idle
        private void UpdateBipedAnimation(in SamusInput input)
        {
            bool fwd = input.MoveY > 0.5f, back = input.MoveY < -0.5f, right = input.MoveX > 0.5f, leftIn = input.MoveX < -0.5f;
            if (JumpedThisStep)
            {
                SetBipedAnimation(fwd ? BipedAnimation.JumpForward : back ? BipedAnimation.JumpBack
                    : leftIn ? BipedAnimation.JumpLeft : right ? BipedAnimation.JumpRight : BipedAnimation.JumpNeutral);
                return;
            }
            if (!Standing)
            {
                return;   // keep the jump pose in the air
            }
            if (LandedThisStep)
            {
                SetBipedAnimation(BipedAnimation.LandNeutral);
                return;
            }
            BipedAnimation anim = fwd ? BipedAnimation.WalkForward : back ? BipedAnimation.WalkBackward
                : leftIn ? BipedAnimation.WalkLeft : right ? BipedAnimation.WalkRight : BipedAnimation.Idle;
            if (anim == BipedAnimation.Idle && BipedAnimFlags.HasFlag(AnimFlags.NoLoop) && !_bipedEnded)
            {
                return;   // let a landing / the unmorph play out (moving cuts in: the legs run during the unmorph)
            }
            if (anim != BipedAnim || BipedAnimFlags.HasFlag(AnimFlags.NoLoop))
            {
                SetBipedAnimation(anim, AnimFlags.None);
            }
        }

        // PlayerInput.ProcessBiped (torso): Charge while the fire button holds a charge (once the torso is free), Shoot
        // for each shot (restarted), ChargeShoot for a charged release; otherwise the torso joins the legs' animation
        private void UpdateTorsoAnimation()
        {
            // the unmorph completes when the torso's Unmorph ends (testbed: biped2), the legs may already be walking
            if (Unmorphing && TorsoAnim == BipedAnimation.Unmorph && _torsoEnded && !TorsoAnimChanged)
            {
                Unmorphing = false;
            }
            BipedAnimation? req = null;
            bool torsoFree = _torsoEnded || TorsoAnim == BipedAnimation.Charge || (TorsoAnim == BipedAnimation.Shoot && _torsoFrame > 8);
            if (_torsoCharge && torsoFree) { req = BipedAnimation.Charge; }
            if (ShotCharged) { req = BipedAnimation.ChargeShoot; }
            else if (ShotUncharged) { req = BipedAnimation.Shoot; }
            if (req == null)
            {
                if ((!TorsoAnimFlags.HasFlag(AnimFlags.NoLoop) || _torsoEnded) && TorsoAnim != BipedAnim)
                {
                    SetTorso(BipedAnim, BipedAnimFlags, sync: true);
                }
            }
            else if (req != TorsoAnim || req == BipedAnimation.Shoot)
            {
                SetTorso(req.Value, AnimFlags.NoLoop, sync: false);
            }
        }

        private void SetTorso(BipedAnimation anim, AnimFlags flags, bool sync)
        {
            TorsoAnim = anim;
            TorsoAnimFlags = flags;
            TorsoAnimChanged = true;
            TorsoSyncFrame = sync;
            _torsoEnded = false;
        }

        private void SetBipedAnimation(BipedAnimation anim, AnimFlags flags = AnimFlags.NoLoop, bool torso = false)
        {
            if (torso) { SetTorso(anim, flags, sync: false); }
            BipedAnim = anim;
            BipedAnimFlags = flags;
            BipedAnimChanged = true;
            _bipedEnded = false;
        }

        // InitAltTransform: ball basis = (left, up, facing), orthonormalized
        private void InitAltTransform()
        {
            var x = new Vector3(Left.X, 0, Left.Z);
            Vector3 y = Vector3.UnitY;
            Vector3 z = Vector3.Cross(x, y).Normalized();
            y = Vector3.Cross(z, x).Normalized();
            x = x.Normalized();
            AltRot = Rows(x, y, z);
        }

        private static Matrix4 Rows(Vector3 x, Vector3 y, Vector3 z)
        {
            var m = Matrix4.Identity;
            m.Row0 = new Vector4(x, 0);
            m.Row1 = new Vector4(y, 0);
            m.Row2 = new Vector4(z, 0);
            return m;
        }

        // PlayerProcess.UpdateAltTransform (Samus): roll about the horizontal axis across this step's movement by
        // distance / radius, then turn the ball so its seam (row 0) eases toward that axis. moved = this step's
        // movement and radius = the drawn ball's radius, both in MPH units.
        public void UpdateBallRoll(Vector3 moved, float radius)
        {
            if (!AltForm || radius <= 0)
            {
                return;
            }
            float r = radius;
            var axis = new Vector3(r * moved.Z, 0, -r * moved.X);
            float mag = axis.Length;
            if (mag <= 0)
            {
                return;
            }
            axis /= mag;
            float angle = mag / (r * r);
            Matrix4 t = AltRot * AxisAngle(axis, angle);
            Vector3 row0 = t.Row0.Xyz;
            if (Vector3.Dot(row0, axis) < 0) { axis = -axis; }
            Vector3 turnAxis = Vector3.Cross(row0, axis);
            float mbAngle = turnAxis.Length;
            if (mbAngle > 0)
            {
                if (mbAngle > SeamSnapLimit)
                {
                    float div = Math.Min(angle / SeamFullSpeedAngle, 1);
                    mbAngle *= div / SeamEaseDivisor;
                }
                t *= AxisAngle(turnAxis.Normalized(), mbAngle);
            }
            // re-orthonormalize as MPH does: row2 = row0 x row1, row1 = row2 x row0, normalize all
            Vector3 x = t.Row0.Xyz, y = t.Row1.Xyz;
            Vector3 z = Vector3.Cross(x, y).Normalized();
            y = Vector3.Cross(z, x).Normalized();
            AltRot = Rows(x.Normalized(), y, z);
        }

        // rotation by angle (radians) about a unit axis, row-vector convention (v * M)
        private static Matrix4 AxisAngle(Vector3 axis, float angle)
        {
            return Matrix4.CreateFromAxisAngle(axis, angle);
        }

        // PlayerEntity.CheckHitByBomb (own bomb): bomb jump
        public void BombJump()
        {
            if (!AltForm)
            {
                return;
            }
            float jump = Fixed.ToFloat(V.BombJumpSpeed);
            if (Speed.Y < jump) { Speed.Y = jump; }
        }

        // a boosting ball that hit something stops boosting (EndAltAttack)
        public void EndBoost() { Boosting = false; }

        private void UpdateBallTimers()
        {
            if (AltAttackCooldown > 0) { AltAttackCooldown--; }
            if (BombCooldown > 0) { BombCooldown--; }
            if (BoostHudSteps > 0) { BoostHudSteps--; }
            if (BombRefillTimer > 0)
            {
                if (--BombRefillTimer == 0 && BombAmmo < MaxBombs)
                {
                    BombAmmo++;
                    if (BombAmmo < MaxBombs) { BombRefillTimer = V.BombRefillTime * 2; }
                }
            }
            else if (BombAmmo < MaxBombs && AltAttackCooldown == 0)
            {
                BombRefillTimer = V.BombRefillTime * 2;
            }
            if (Boosting && HSpeedMag <= Fixed.ToFloat(V.AltMinHSpeed))
            {
                Boosting = false;
            }
        }

        private void EndBallStep()
        {
            // morph / unmorph complete when the biped animation ends (PlayerProcess)
            if (_bipedEnded && !BipedAnimChanged)
            {
                if (Morphing && BipedAnim == BipedAnimation.Morph)
                {
                    Morphing = false;
                    AltForm = true;
                    InitAltTransform();   // UpdateForm(altForm: true)
                    BallFormedThisStep = true;
                }
            }

            if (CamSwitchTimer < CamSwitchSteps)
            {
                CamSwitchTimer++;
                if (!AltForm && !ThirdCam && CamSwitchTimer == CamSwitchSteps)
                {
                    SetGunAnimation(GunAnimation.UpDown, AnimFlags.NoLoop);
                }
            }
        }
    }
}
