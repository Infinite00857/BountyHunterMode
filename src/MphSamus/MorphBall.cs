// MorphBall - the Unity side of Samus's Morph Ball (the rules are in Core.SamusSim.Ball): collider shrink/grow,
// the ball camera (MphRead PlayerCamera.UpdateCameraThird1 via the testbed's updateCameraThird, not mouse-steerable),
// the biped (morph/unmorph animations) and ball models, bombs + bomb jumps, Boost Ball rams, the Morph Ball trail,
// effects (samusFurl, samusDash, bombStart, bombBlue) and sounds.
//
// Collider: the ball uses V1's slide collider (height 1.25, radius 0.5) so it fits wherever V1 slides or crouches;
// the ball model is drawn at that size (BallRadius) instead of MPH's 0.5 * k, which wouldn't fit (owner to confirm).
using System.Collections.Generic;
using MphSamus.Core;
using UnityEngine;
using Matrix4 = OpenTK.Mathematics.Matrix4;
using OV3 = OpenTK.Mathematics.Vector3;
using OV2 = OpenTK.Mathematics.Vector2;

namespace MphSamus
{
    public sealed class MorphBall
    {
        // ---- TUNABLE CONSTANTS (ULTRAKILL adapter) ----
        private const float StandHeight = 3.5f;        // V1's collider height (NewMovement)
        private const float SlideHeight = 1.25f;       // V1's slide/crouch collider height
        private const float BallRadius = SlideHeight / 2;   // drawn ball radius, Unity units
        private const float HeadroomUp = 3.5f;         // NewMovement uncrouch checks: ray up 3.5, sphere 0.5 up 2
        private const float BoostReachExtra = 0.3f;    // Boost Ball ram reach beyond the ball, in MPH units (x k)
        // ---- TUNABLE CONSTANTS (MPH; testbed mph_player.cpp) ----
        private const int FxSamusFurl = 30, FxSamusDash = 136, FxBombStart = 9, FxBombBlue = 145;
        private const float BombFxDrop = 1000 / 4096f; // bombs sit this far below the ball center (MPH units)
        private const float TunnelCamDistance = 1.5f, TunnelCamHeight = 0.7f, TunnelCamTargetY = 0.5f;
        private const float CamResetDist = 6;          // farther than this = always follow (MPH units)
        private const float CamMoveSpeedSqr = 36 / 4096f;   // (Speed/2)^2 above this counts as rolling
        private const int CamBlockSteps = 15 * 2;      // view blocked this long before the camera snaps in
        private const int TrailSegments = 9 * 2;
        private const float TrailFade = 3 / 31f / 2;   // alpha lost per 60 Hz step
        private const float TrailStartAlpha = 25 / 31f;
        private const float TrailMinSpeed = 1269 / 4096f;
        private const float TrailMaxSeamUp = 0.5f;
        private const float TrailHalfWidth = 0.375f;   // MPH units for an MPH-sized ball (scaled to ours)
        private static readonly OV3 TrailColor = new OV3(1, 27 / 31f, 11 / 31f);

        private readonly SamusController _c;
        private readonly NewMovement _nm;
        private readonly CapsuleCollider _capsule;
        private readonly CameraController _cc;
        private BipedModel _body;
        private ModelView _ballView, _bipedView;
        private bool _shrunk;
        private Vector3 _lastBallPos;

        // camera (Unity world)
        public bool CameraActive { get; private set; }
        private Vector3 _camPos, _camTarget, _camFrom, _lastCamBall;
        private Quaternion _switchFromRot;
        private float _f551, _f552;
        private bool _noUnmorph;

        private sealed class Bomb
        {
            public Vector3 Pos;
            public int Countdown;
            public FxHandle Fx;
            public readonly HashSet<EnemyIdentifier> Hit = new HashSet<EnemyIdentifier>();
        }
        private readonly List<Bomb> _bombs = new List<Bomb>();
        private FxHandle _furlFx, _boostFx;
        private readonly Matrix4[] _trail = new Matrix4[TrailSegments];
        private readonly float[] _trailAlpha = new float[TrailSegments];
        private int _trailIndex;

        public MorphBall(SamusController c, NewMovement nm, CapsuleCollider capsule, CameraController cc)
        {
            _c = c;
            _nm = nm;
            _capsule = capsule;
            _cc = cc;
            try
            {
                _body = new BipedModel();
                _ballView = new ModelView(_body.BallPosed, "MPH Morph Ball", null, 0) { Scale = BallScale };
                _bipedView = new ModelView(_body.BipedPosed, "MPH Samus", null, 0);
                _ballView.SetVisible(false);
                _bipedView.SetVisible(false);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Samus biped/ball models: {ex}");
                _body = null;
            }
        }

        private SamusSim Sim => _c.Sim;
        private float K => _c.K;
        // MPH -> Unity scale of the drawn ball (MPH's ball is AltColRadius across the radius)
        // MPH -> Unity scale of the drawn ball (MPH's ball radius is AltColRadius 0.5); the ball's effects use it too
        public static float BallScale => BallRadius / (2048 / 4096f);
        private static float Fixed(int v) => v / 4096f;

        // the biped's arm cannon muzzle (world), from the last pose; null when the biped isn't drawn
        public Vector3? ThirdMuzzle { get; private set; }

        public Vector3 BallCenter => _capsule != null ? _capsule.transform.TransformPoint(_capsule.center) : _nm.transform.position;

        // NewMovement's uncrouch test: room above the ball for V1's standing collider
        public bool CanUnmorph()
        {
            if (_capsule == null) { return true; }
            Vector3 up = _nm.transform.up;
            var bottom = new Vector3(_capsule.bounds.center.x, _capsule.bounds.min.y, _capsule.bounds.center.z);
            int env = LayerMaskDefaults.Get(LMD.Environment);
            return !Physics.Raycast(bottom, up, HeadroomUp, env, QueryTriggerInteraction.Ignore)
                && !Physics.SphereCast(new Ray(bottom + up * 0.25f, up), 0.5f, 2f, env, QueryTriggerInteraction.Ignore);
        }

        // ---- per 60 Hz step, after Sim.Step ----
        public void Step()
        {
            if (_body != null)
            {
                _body.Apply(Sim);
            }
            else
            {
                Sim.ReportBipedAnimEnded(true);   // no model: switch forms at once
                Sim.ReportTorso(true, 0);
            }
            Vector3 ball = BallCenter;
            if (Sim.MorphedThisStep)
            {
                Shrink(true);
                ball = BallCenter;
                _c.Audio.Play("MORPH_BALL", 96 / 127f);
                _c.BallFx.Unlink(ref _furlFx);
                _furlFx = _c.BallFx.Spawn(FxSamusFurl, EffectSystem.Transform(Sim.Left, Sim.BallFacing, _c.ToBallFx(ball)));
                StartBallCamera();
                System.Array.Clear(_trailAlpha, 0, TrailSegments);
                Plugin.Event("morph");
            }
            if (Sim.UnmorphedThisStep)
            {
                Shrink(false);
                ball = BallCenter;
                _c.Audio.Play("MORPH_BALL", 96 / 127f);
                ReturnCameraToBiped();
                Plugin.Event("unmorph");
            }
            // roll (UpdateAltTransform) from this step's movement
            if (Sim.AltForm)
            {
                Vector3 moved = ball - _lastBallPos;
                if (moved.sqrMagnitude < 4) { Sim.UpdateBallRoll(_c.ToFxWorld(moved), BallRadius / K); }
            }
            _lastBallPos = ball;
            if (Sim.BombPlacedThisStep) { PlaceBomb(ball); }
            if (Sim.BoostedThisStep)
            {
                _c.Audio.Play("BOOST_BALL2", 80 / 127f);
                _c.BallFx.Unlink(ref _boostFx);
                _boostFx = _c.BallFx.Spawn(FxSamusDash, EffectSystem.Transform(Sim.Left, Sim.BallFacing, _c.ToBallFx(ball)));
                _c.BallFx.SetElementExtension(_boostFx, true);
                Plugin.Event($"boost, damage {Sim.BoostDamage}");
            }
            UpdateBombs(ball);
            if (Sim.Boosting && Sim.AltForm) { BoostHits(ball); }
            UpdateEffects(ball);
            UpdateTrail(ball);
            // Morph Ball roll loop (testbed updateLoopSounds): the terrain's roll DGN at SamusSim.RollAmount
            UpdateRollSound();
            // the camera keeps driving until the switch back to first person has finished
            if (CameraActive && !Sim.InBall && Sim.CamSwitchTimer >= Sim.CamSwitchSteps)
            {
                CameraActive = false;
                _cc.ApplyRotations();
            }
        }

        // V1's StartSlide / uncrouch: keep the feet in place while the collider changes height
        private void Shrink(bool shrink)
        {
            if (_capsule == null || _shrunk == shrink) { return; }
            _shrunk = shrink;
            Transform t = _nm.transform;
            float delta = (StandHeight - SlideHeight) / 2;
            Vector3 groundCheck = Traverse(_nm);
            if (shrink)
            {
                _capsule.height = SlideHeight;
                t.position -= t.up * delta;
                _nm.gc?.SetLocalPosition(groundCheck + Vector3.up * 1.125f);
            }
            else
            {
                _capsule.height = StandHeight;
                _nm.gc?.SetLocalPosition(groundCheck);
                if (Physics.Raycast(t.position, -t.up, 2.25f, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore))
                {
                    t.position += t.up * delta;
                }
            }
        }

        private static Vector3 Traverse(NewMovement nm)
        {
            return HarmonyLib.Traverse.Create(nm).Field("groundCheckPos").GetValue<Vector3>();
        }

        // respawn / leaving the level: back to the standing collider and first person at once
        public void Reset()
        {
            Shrink(false);
            foreach (Bomb b in _bombs) { _c.BallFx.Unlink(ref b.Fx); }
            _bombs.Clear();
            _c.BallFx.Unlink(ref _furlFx);
            _c.BallFx.Unlink(ref _boostFx);
            System.Array.Clear(_trailAlpha, 0, TrailSegments);
            if (_rollSound != null) { _c.Audio.StopLoop(_rollSound); _rollSound = null; }
            if (CameraActive)
            {
                CameraActive = false;
                _cc.ApplyRotations();
            }
            _ballView?.SetVisible(false);
            _bipedView?.SetVisible(false);
        }

        // ---- bombs (BombEntity + PlayerEntity.CheckHitByBomb) ----
        private void PlaceBomb(Vector3 ball)
        {
            var b = new Bomb { Pos = ball, Countdown = SamusSim.BombFuseFrames * 2 };
            OV3 p = _c.ToBallFx(ball) - new OV3(0, BombFxDrop, 0);
            b.Fx = _c.BallFx.Spawn(FxBombStart, EffectSystem.Transform(OV3.UnitZ, OV3.UnitY, p));
            _c.BallFx.SetElementExtension(b.Fx, true);
            _bombs.Add(b);
            _c.Audio.Play("MORPH_BALL_BOMB_PLACE", 80 / 127f);
            Plugin.Event($"bomb placed, ammo {Sim.BombAmmo}");
        }

        private void UpdateBombs(Vector3 ball)
        {
            for (int i = _bombs.Count - 1; i >= 0; i--)
            {
                Bomb b = _bombs[i];
                if (b.Countdown > 0) { b.Countdown--; }
                bool exploding = b.Countdown == 0;
                // an enemy within BombRadius sets the bomb off
                if (!exploding)
                {
                    foreach (Collider col in Physics.OverlapSphere(b.Pos, Sim.BombRadius * K, LayerMaskDefaults.Get(LMD.Enemies), QueryTriggerInteraction.Collide))
                    {
                        if (col.TryGetComponent(out EnemyIdentifierIdentifier eidid) && eidid.eid != null && !eidid.eid.dead)
                        {
                            exploding = true;
                            break;
                        }
                    }
                }
                if (!exploding) { continue; }
                SlamImpact(b);
                _bombs.RemoveAt(i);
                _c.BallFx.Unlink(ref b.Fx);
                OV3 p = _c.ToBallFx(b.Pos) - new OV3(0, BombFxDrop, 0);
                _c.BallFx.SpawnOneShot(FxBombBlue, EffectSystem.Transform(OV3.UnitZ, OV3.UnitY, p));
                _c.Audio.PlayAt("MORPH_BALL_BOMB", b.Pos, 1);
                BreakThings(b.Pos, Sim.BombRadius * K);
                // own bomb: bomb jump when the ball is close and not far below it
                Vector3 between = ball - b.Pos;
                float self = Sim.BombSelfRadius * K;
                if (Sim.AltForm && between.sqrMagnitude <= self * self && between.y > -BallRadius)
                {
                    Sim.BombJump();
                    _cc.CameraShake((1 - between.sqrMagnitude / (self * self)) * 0.1f * 4);
                    Plugin.Event("bomb jump");
                }
            }
        }

        // Not MPH (owner): a bomb blast is also a ground-slam impact (GroundCheck heavyFall): every enemy in the blast
        // with nothing in between takes BombEnemyDamage as "ground slam" (slam reactions: dead Guttermen explode, ...),
        // Idols die, Bleeders bleed
        private void SlamImpact(Bomb b)
        {
            float radius = Sim.BombRadius * K;
            int env = LayerMaskDefaults.Get(LMD.Environment);
            foreach (Collider col in Physics.OverlapSphere(b.Pos, radius, ~0, QueryTriggerInteraction.Collide))
            {
                if (col == null) { continue; }
                if ((col.gameObject.layer == 10 || col.gameObject.layer == 11) && col.TryGetComponent(out EnemyIdentifierIdentifier eidid) && eidid.eid != null)
                {
                    EnemyIdentifier eid = eidid.eid;
                    if (b.Hit.Contains(eid) || Physics.Linecast(b.Pos, col.bounds.center, env, QueryTriggerInteraction.Ignore)) { continue; }
                    b.Hit.Add(eid);
                    bool wasDead = eid.dead;
                    float dmg = Sim.BombEnemyDamage * Plugin.DamageDealtScale.Value;
                    eid.hitter = "ground slam";
                    eid.DeliverDamage(col.gameObject, (col.bounds.center - b.Pos).normalized * 5000, col.ClosestPoint(b.Pos), dmg, true, 0, null);
                    Plugin.Event($"bomb slammed {eid.enemyType} for {dmg:F2}, health {eid.health:F2}{(eid.dead && !wasDead ? " - killed" : "")}");
                    continue;
                }
                if (col.TryGetComponent(out Bleeder bleeder)) { bleeder.GetHit(col.transform.position, GoreType.Body); }
                if (col.transform.TryGetComponent(out Idol idol)) { idol.Death(); }
            }
        }

        // bombs break what a charged Power Beam shot breaks (non-special breakables, glass)
        private static void BreakThings(Vector3 pos, float radius)
        {
            foreach (Collider col in Physics.OverlapSphere(pos, radius, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Collide))
            {
                if (col.TryGetComponent(out Breakable br) && !br.unbreakable && !br.specialCaseOnly && !br.broken)
                {
                    br.Break();
                    Plugin.Event($"bomb broke {col.name}");
                }
                if (col.TryGetComponent(out Glass glass)) { glass.Shatter(); }
            }
        }

        private string _rollSound;
        private void UpdateRollSound()
        {
            if (Sim.AltForm) { _c.UpdateTerrain(); }
            DgnResult d = Sim.RollAmount > 0 ? DgnSounds.Terrain(_c.Terrain, TerrainSound.Roll, Sim.RollAmount, 0) : default;
            string want = d.Volume > 0 ? SamusController.SoundName(d.Sample) : null;
            if (want != _rollSound && _rollSound != null) { _c.Audio.StopLoop(_rollSound); }
            _rollSound = want;
            if (want != null) { _c.Audio.SetLoop(want, d.Volume, d.Pitch, raw: true); }
        }

        // PlayerCollision: a boosting ball that touches an enemy deals BoostDamage and stops boosting
        private void BoostHits(Vector3 ball)
        {
            float reach = BallRadius + BoostReachExtra * K;
            foreach (Collider col in Physics.OverlapSphere(ball, reach, LayerMaskDefaults.Get(LMD.Enemies), QueryTriggerInteraction.Collide))
            {
                if (!col.TryGetComponent(out EnemyIdentifierIdentifier eidid) || eidid.eid == null || eidid.eid.dead)
                {
                    continue;
                }
                float dmg = Sim.BoostDamage * Plugin.DamageDealtScale.Value;
                Vector3 dir = SamusController.ToUnity(Sim.BallFacing);
                eidid.eid.DeliverDamage(col.gameObject, dir * Plugin.ChargedKnockback.Value * 0.5f, col.ClosestPoint(ball), dmg, false, 0, null);
                Plugin.Event($"boost hit {eidid.eid.enemyType} for {dmg:F2}, health {eidid.eid.health:F2}{(eidid.eid.dead ? " - killed" : "")}");
                Sim.EndBoost();
                break;
            }
        }

        // PlayerProcess: keep the furl/dash effects on the ball, end them
        private void UpdateEffects(Vector3 ball)
        {
            EffectSystem fx = _c.BallFx;
            Matrix4 xf = EffectSystem.Transform(Sim.Left, Sim.BallFacing, _c.ToBallFx(ball));
            if (_boostFx.Valid)
            {
                fx.SetTransform(_boostFx, xf);
                if (!Sim.InBall) { fx.Unlink(ref _boostFx); }
                else if (!Sim.Boosting)
                {
                    if (fx.IsFinished(_boostFx)) { fx.Unlink(ref _boostFx); }
                    else { fx.SetElementExtension(_boostFx, false); }
                }
            }
            if (_furlFx.Valid)
            {
                fx.SetTransform(_furlFx, xf);
                if (!Sim.InBall || fx.IsFinished(_furlFx)) { fx.Unlink(ref _furlFx); }
            }
        }

        // PlayerEntity.UpdateMorphBallTrail: a ring of segments along the glowing seam
        private void UpdateTrail(Vector3 ball)
        {
            for (int i = 0; i < TrailSegments; i++) { _trailAlpha[i] = Mathf.Max(0, _trailAlpha[i] - TrailFade); }
            if (!Sim.AltForm) { return; }
            OV3 row0 = Sim.AltRot.Row0.Xyz;
            if (OV3.Dot(OV3.UnitY, row0) >= TrailMaxSeamUp || Sim.HSpeedMag < TrailMinSpeed) { return; }
            OV3 side = OV3.Cross(row0, OV3.UnitY).Normalized();
            OV3 row1 = OV3.Cross(side, row0);
            var m = Matrix4.Identity;
            m.Row0 = new OpenTK.Mathematics.Vector4(row0, 0);
            m.Row1 = new OpenTK.Mathematics.Vector4(row1, 0);
            m.Row2 = new OpenTK.Mathematics.Vector4(side, 0);
            m.Row3 = new OpenTK.Mathematics.Vector4(_c.ToFxWorld(ball), 1);
            _trail[_trailIndex] = m;
            _trailAlpha[_trailIndex] = TrailStartAlpha;
            _trailIndex = (_trailIndex + 1) % TrailSegments;
        }

        // ---- camera (PlayerCamera.SwitchCamera / UpdateCameraThird1, Unity world space) ----
        private void StartBallCamera()
        {
            Transform cam = _cc.cam.transform;
            Vector3 f = cam.forward;
            _camTarget = cam.position + f * K;
            _camPos = cam.position - f * (K / 64f);
            _camFrom = _camPos;
            _switchFromRot = cam.rotation;
            _lastCamBall = BallCenter;
            _f551 = 0;
            _f552 = 0;
            CameraActive = true;
        }

        private void ReturnCameraToBiped()
        {
            _camFrom = _camPos;
            _switchFromRot = Quaternion.LookRotation(_camTarget - _camPos, Vector3.up);
            // first person looks along the roll, level (gunVec1 = facing, aimY = 0)
            Vector3 f = SamusController.ToUnity(Sim.BallFacing);
            f.y = 0;
            if (f.sqrMagnitude > 1e-6f)
            {
                _cc.rotationY = Quaternion.LookRotation(f, Vector3.up).eulerAngles.y;
            }
            _cc.rotationX = 0;
        }

        // every frame (LateUpdate). switchPct: camera switch progress incl. the part of a step since the last one.
        // eyeWorld / eyeRot: where the first-person camera would be.
        public void UpdateCamera(float dt, float switchPct, Vector3 eyeWorld, Quaternion eyeRot)
        {
            if (!CameraActive) { return; }
            Transform cam = _cc.transform;
            if (!Sim.InBall)
            {
                // back to Samus's view (UpdateCameraFirst while switching): blend to the eye / F5 camera
                cam.position = Vector3.Lerp(_camFrom, eyeWorld, switchPct);
                cam.rotation = Quaternion.Slerp(_switchFromRot, eyeRot, switchPct);
                return;
            }
            float steps = dt * 60;
            Vector3 ball = BallCenter;
            // camera distances in MPH units of the drawn ball, so it frames the ball as on the DS
            float k = BallScale;
            Vector3 camFacing = (_camTarget - _camPos).normalized;
            Vector3 prevCam = _camPos;
            float v5 = (_noUnmorph ? TunnelCamDistance : Sim.ThirdCamDistance) * k;
            float v6 = (_noUnmorph ? TunnelCamHeight : Sim.ThirdCamHeight) * k;
            float v7 = (_noUnmorph ? TunnelCamTargetY : Sim.ThirdCamTargetY) * k;
            bool switching = switchPct < 1;
            _camTarget.x = ball.x;
            _camTarget.y -= v7;
            _camTarget.y += (ball.y - _camTarget.y) * (1 - Mathf.Pow(0.5f, steps));   // eases halfway per step
            _camTarget.z = ball.z;
            Vector3 posVec;
            if (_f551 <= 1)
            {
                // not rolling yet: swing to the follow distance along the current view, keeping the camera's height
                Vector3 camVec = (_camPos - _camTarget);
                camVec.y = 0;
                camVec = camVec.sqrMagnitude > 1e-6f ? camVec.normalized : -camFacing;
                posVec = new Vector3(_camTarget.x + camVec.x * v5, _camPos.y, _camTarget.z + camVec.z * v5);
            }
            else
            {
                Vector3 camVec;
                if (!switching)
                {
                    camVec = _camPos - _camTarget;
                }
                else
                {
                    camVec = -camFacing;
                    _camFrom += ball - _lastCamBall;   // the blend start follows the ball
                }
                camVec.y = 0;
                camVec = camVec.sqrMagnitude > 1e-6f ? camVec.normalized : -camFacing;
                posVec = new Vector3(_camTarget.x + camVec.x * v5, _camTarget.y + v6, _camTarget.z + camVec.z * v5);
            }
            _camTarget.y += v7;
            if (switching)
            {
                _camPos = _camFrom + (posVec - _camFrom) * switchPct;
                Vector3 facingVec = _camPos + camFacing;
                _camTarget = facingVec + (_camTarget - facingVec) * switchPct;
            }
            else
            {
                _camPos += (posVec - _camPos) * (1 - Mathf.Pow(1 - Sim.ThirdCamFollow, steps));
            }
            // _field551: starts following once the ball moves; far away = always follow
            Vector3 fromBall = _camPos - ball;
            if (fromBall.sqrMagnitude >= CamResetDist * k * CamResetDist * k)
            {
                _f551 = 255;
            }
            else if (_f551 < 255 && Sim.HSpeedMag * Sim.HSpeedMag / 4 > CamMoveSpeedSqr)
            {
                _f551 += steps;
            }
            // CheckSphereBetweenPoints: slide along walls (up to 3 planes)
            int env = LayerMaskDefaults.Get(LMD.Environment);
            float radius = Sim.ThirdCamRadius * k;
            Vector3 from = prevCam, moved = _camPos - prevCam;
            for (int pass = 0; pass < 3; pass++)
            {
                float len = moved.magnitude;
                if (len <= 1e-5f) { break; }
                Vector3 dir = moved / len;
                if (!Physics.SphereCast(from, radius, dir, out RaycastHit hit, len, env, QueryTriggerInteraction.Ignore))
                {
                    from += moved;
                    break;
                }
                from += dir * hit.distance;
                Vector3 rest = moved - dir * hit.distance;
                moved = rest - hit.normal * Vector3.Dot(rest, hit.normal);
            }
            _camPos = from;
            // target -> camera blocked: tolerate it for a while, then pull the camera in front of the obstruction
            Vector3 toCam = _camPos - _camTarget;
            float toCamLen = toCam.magnitude;
            if (toCamLen > 1e-4f && Physics.Raycast(_camTarget, toCam / toCamLen, out RaycastHit block, toCamLen, env, QueryTriggerInteraction.Ignore))
            {
                if (_f552 < CamBlockSteps) { _f552 += steps; }
                else { _camPos = _camTarget + toCam * (Mathf.Max(0, block.distance - radius) / toCamLen); }
            }
            else
            {
                _f552 = 0;
            }
            _lastCamBall = ball;
            cam.position = _camPos;
            if ((_camTarget - _camPos).sqrMagnitude > 1e-6f)
            {
                cam.rotation = Quaternion.LookRotation(_camTarget - _camPos, Vector3.up);
            }
        }

        // ---- drawing (LateUpdate, after the camera) ----
        public void Draw(MphLighting light)
        {
            if (_body == null) { return; }
            _noUnmorph = Sim.AltForm && !CanUnmorph();
            Vector3 camPos = _cc.cam.transform.position;
            bool ballOn = Sim.AltForm && !_nm.dead;
            _ballView.SetVisible(ballOn);
            if (ballOn)
            {
                // the furl / boost glow follow the ball where it is drawn (the step set them before physics moved it)
                Matrix4 fxNow = EffectSystem.Transform(Sim.Left, Sim.BallFacing, _c.ToBallFx(BallCenter));
                if (_boostFx.Valid) { _c.BallFx.SetTransformNow(_boostFx, fxNow); }
                if (_furlFx.Valid) { _c.BallFx.SetTransformNow(_furlFx, fxNow); }
                _body.PoseBall(Sim.AltRot, 1, light);
                _ballView.Root.transform.position = BallCenter;
                _ballView.Scale = BallScale;
                _ballView.Upload(camPos - BallCenter, 0);
            }
            bool bipedOn = Sim.ShowBiped && !_nm.dead;
            _bipedView.SetVisible(bipedOn);
            ThirdMuzzle = null;
            if (bipedOn)
            {
                // fades out while the camera returns to first person (PlayerDraw: Unmorph alpha)
                float alpha = Sim.TorsoAnim == BipedAnimation.Unmorph && Sim.Unmorphing && !Sim.ThirdCam ? Mathf.Clamp01(1 - _body.TorsoFraction) : 1;
                OV3 lateral = Sim.InBall || Sim.Unmorphing ? Sim.BallFacing : Sim.Facing;
                float aimY = Sim.InBall || Sim.Unmorphing ? 0 : Sim.Facing.Y;   // the spine follows the aim pitch
                _body.PoseBiped(lateral, aimY, light, alpha);
                Vector3 feet = BallCenter - Vector3.up * (_capsule != null ? _capsule.height / 2 * _capsule.transform.lossyScale.y : 1.75f);
                _bipedView.Root.transform.position = feet;
                _bipedView.Scale = K;
                _bipedView.Upload(camPos - feet, 0);
                if (_body.Muzzle(out OV3 m)) { ThirdMuzzle = feet + SamusController.ToUnity(m) * K; }
            }
            // DrawMorphBallTrail: quads between consecutive segments, newest first; the fade is in the texture column
            if (Sim.AltForm)
            {
                float hw = TrailHalfWidth * (BallRadius / K) / Fixed(2048);
                for (int i = 0; i < TrailSegments; i++)
                {
                    int id1 = (_trailIndex - 1 - i + TrailSegments * 2) % TrailSegments;
                    int id2 = (id1 - 1 + TrailSegments) % TrailSegments;
                    float a1 = _trailAlpha[id1], a2 = _trailAlpha[id2];
                    if (a1 <= 0 || a2 <= 0) { continue; }
                    float u1 = (31 - (int)(a1 * 31)) / 32f, u2 = (31 - (int)(a2 * 31)) / 32f;
                    Matrix4 m1 = _trail[id1], m2 = _trail[id2];
                    OV3 hi = new OV3(0, hw, 0), lo = new OV3(0, -hw, 0);
                    _c.WorldFx.AddTrailQuad("trail", OV3.TransformPosition(hi, m1), OV3.TransformPosition(lo, m1),
                        OV3.TransformPosition(lo, m2), OV3.TransformPosition(hi, m2),
                        new OV2(u1, 0), new OV2(u1, 1), new OV2(u2, 1), new OV2(u2, 0), TrailColor, 1);
                }
            }
        }

        public void Destroy()
        {
            _ballView?.Destroy();
            _bipedView?.Destroy();
        }
    }
}
