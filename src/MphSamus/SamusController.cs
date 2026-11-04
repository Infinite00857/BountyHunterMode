// SamusController - runs Samus on ULTRAKILL's player object (NewMovement) while Samus mode is on.
// The player GameObject stays the game's (triggers, checkpoints, death planes, enemy targeting keep working); its
// movement/weapons/arms are switched off (Patches.cs) and the MPH simulation (Core.SamusSim, fixed 60 Hz) drives the
// Rigidbody's velocity. Unity physics does the collision. ULTRAKILL's CameraController keeps mouse look (CENTER
// CROSSHAIR style: Samus aims where the camera looks); the camera height is moved to Samus's eye height.
// Coordinates: the sim uses MPH axes (right-handed); Unity is left-handed, so X is mirrored at this boundary. The sim
// runs in the player's gravity frame (ULTRAKILL turns the player when gravity changes: Fraud's gravity volumes and
// portals), so MPH's +Y is always the player's up.
using System.Collections.Generic;
using MphRead.Entities;
using MphSamus.Core;
using UnityEngine;
using Matrix4 = OpenTK.Mathematics.Matrix4;
using OV3 = OpenTK.Mathematics.Vector3;

namespace MphSamus
{
    public sealed class SamusController : MonoBehaviour
    {
        // ---- TUNABLE CONSTANTS (ULTRAKILL adapter) ----
        private const float MphHeight = 1.6f;          // MPH player volume: feet 0.5 below the position, head 1.1 above
        private const float MphFeetBelow = 0.5f;
        private const float StepDt = 1 / 60f;
        private const int MaxStepsPerFrame = 4;
        private const float GroundProbe = 0.12f;       // Unity units below the capsule for the floor check
        private const float GroundStick = 3f;          // Unity units/s pressed into the floor (along its normal) while standing
        private const float StepSkin = 0.02f;          // stepping up / snapping down: clearance left above the floor
        private const float WeaponScrollCooldown = 0.15f;   // mouse wheel: one weapon per notch
        private const int FxSortingOrder = 1000;       // effects draw after the models' batches
        // F5 third-person view (testbed V view, mph_player.cpp updateCameraThird): MPH units, scaled by k
        private const float ThirdViewDistance = 3.2f;  // behind Samus
        private const float ThirdViewHeight = 0.9f;    // above her position (0.5 above the feet)
        private const float ThirdViewSide = 0.6f;      // to her right (over the shoulder)
        private const float ThirdViewEye = 0.3f;       // the wall check starts this far above her position
        private const float ThirdViewWallMargin = 0.25f, ThirdViewMinDistance = 0.2f;

        public static SamusController Current { get; private set; }

        public SamusSim Sim { get; private set; }
        public float K { get; private set; } = 2;      // MPH units -> Unity units
        public MphAudio Audio { get; private set; }
        public Camera Cam => _cc != null ? _cc.cam : null;
        // MPH effects in world space (MPH units = Unity / k, X mirrored): beams, impacts
        public EffectSystem WorldFx { get; } = new EffectSystem();
        // Morph Ball effects (furl, boost dash, bombs) at the drawn ball's scale, so they fit the ball as in MPH
        public EffectSystem BallFx { get; } = new EffectSystem();
        public OV3 ToBallFx(Vector3 unity) => ToMph(unity) / MorphBall.BallScale;
        private EffectView _ballFxView;
        public OV3 ToFxWorld(Vector3 unity) => ToMph(unity) / K;
        public Vector3 FromFxWorld(OV3 mph) => ToUnity(mph) * K;
        public Collider BodyCollider => _capsule;

        private NewMovement _nm;
        private Rigidbody _rb;
        private CapsuleCollider _capsule;
        private CameraController _cc;
        private GunModel _gun;
        private ModelView _gunView;
        private SamusHud _hud;
        private BeamSystem _beams;
        public BeamSystem Beams => _beams;
        private float _acc;
        private bool _jumpLatch, _fireLatch, _morphLatch, _viewLatch;
        private bool _fxThird;   // the muzzle/charge effects live in WorldFx (third person) instead of _gunFx
        private MorphBall _ball;
        private LeftHand _hand;
        private HeldItems _items;
        private bool _useLatch;
        public bool BallCameraActive => _ball != null && _ball.CameraActive;
        public HudState Hud { get; } = new HudState();
        public bool IsDead => _nm == null || _nm.dead;
        private float _lastVelY;
        private bool _origGravity;
        private int _hudLayer = -1;
        private bool _reentry;
        // arm cannon effects run in the gun's camera space (MPH axes: +X left, +Y up, +Z forward)
        private readonly EffectSystem _gunFx = new EffectSystem();
        private EffectView _worldFxView, _gunFxView;
        private FxHandle _muzzleFx, _chargeFx, _burnFx;
        private bool _chargeLoopFx;
        private float _fxAcc, _ballFxAcc;

        public static void TryAttach()
        {
            if (Current != null || !Plugin.SamusMode || !MphAssets.Loaded)
            {
                return;
            }
            if (SceneHelper.CurrentScene == "Main Menu" || SceneHelper.CurrentScene == "Bootstrap" || SceneHelper.CurrentScene == "Intro")
            {
                return;
            }
            if (!MonoSingleton<NewMovement>.TryGetInstance(out NewMovement nm) || nm == null)
            {
                return;
            }
            if (nm.GetComponent<SamusController>() == null)
            {
                nm.gameObject.AddComponent<SamusController>();
            }
        }

        public static bool IsActive => Current != null && Current.isActiveAndEnabled;

        private void Awake()
        {
            Current = this;
            _nm = GetComponent<NewMovement>();
            _rb = _nm.rb != null ? _nm.rb : GetComponent<Rigidbody>();
            _capsule = _nm.playerCollider != null ? _nm.playerCollider : GetComponent<CapsuleCollider>();
            _cc = _nm.cc != null ? _nm.cc : MonoSingleton<CameraController>.Instance;
        }

        private void Start()
        {
            float h = _capsule != null ? _capsule.height * transform.lossyScale.y : 3.5f;
            K = Plugin.PlayerScale.Value > 0 ? Plugin.PlayerScale.Value : Mathf.Clamp(h / MphHeight, 0.5f, 5f);
            Plugin.Log.LogInfo($"Samus attached to {name}: capsule height {h:F2}, radius {(_capsule ? _capsule.radius : 0):F2}, scale k = {K:F2}");
            _origGravity = _rb.useGravity;
            _rb.useGravity = false;
            Sim = new SamusSim();
            Sim.Spawn();
            Audio = gameObject.AddComponent<MphAudio>();
            _hud = gameObject.AddComponent<SamusHud>();
            _hud.Controller = this;
            if (_cc != null && _cc.cam != null)
            {
                // the Volt Driver's screen shift: on the camera that puts the finished frame on screen (ULTRAKILL's
                // PostProcessV2 renders the main camera into its own buffers, so OnRenderImage never runs there)
                Transform virt = _cc.cam.transform.Find("Virtual Camera");
                GameObject screenCam = virt != null && virt.GetComponent<Camera>() != null ? virt.gameObject : _cc.cam.gameObject;
                if (screenCam.GetComponent<DisruptScreen>() == null) { screenCam.AddComponent<DisruptScreen>().Controller = this; }
                Plugin.Log.LogInfo($"disruption effect on {screenCam.name}");
            }
            _beams = new BeamSystem(this);
            FindHudLayer();
            try
            {
                MphAssets.RefreshShader();
                _gun = new GunModel();
                _gunView = new ModelView(_gun.Posed, "MPH Arm Cannon", _cc.cam.transform, _hudLayer) { Scale = K };
                _gunFxView = new EffectView("MPH Arm Cannon FX", _cc.cam.transform, _hudLayer, FxSortingOrder);
                // the ball's effects (boost glow, furl, bombs) draw after the ball's sorted batches (sortingOrder 0..n),
                // so the glow shows over the ball (owner); the world effects (beams, Morph Ball trail) keep sorting with it
                _worldFxView = new EffectView("MPH World FX", null, 0, 0);
                _ballFxView = new EffectView("MPH Ball FX", null, 0, FxSortingOrder);
                _ball = new MorphBall(this, _nm, _capsule, _cc);
                _hand = new LeftHand(this, _cc.cam.transform, _hudLayer);
                _items = new HeldItems(this, _cc.cam.transform, _hudLayer);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Arm cannon: {ex}");
            }
            Audio.Play("PLAYER_SPAWN", 96 / 127f);
            if (Plugin.DebugDump.Value)
            {
                Plugin.Log.LogInfo("Player:\n" + UiUtil.Dump(transform, 0, 3));
                Plugin.Log.LogInfo("Camera:\n" + UiUtil.Dump(_cc.transform, 0, 3));
            }
            HideVanilla();
        }

        private void OnDestroy()
        {
            if (Current == this) { Current = null; }
            RestoreFov();
            if (_rb != null) { _rb.useGravity = _origGravity; }
            _gunView?.Destroy();
            _gunFxView?.Destroy();
            _worldFxView?.Destroy();
            _ballFxView?.Destroy();
            _ball?.Destroy();
            _beams?.Clear();
            _hand?.Destroy();
            _items?.Destroy();
            foreach (DualGun d in _duals) { d.View?.Destroy(); }
            _duals.Clear();
            // (Samus Mode off: the HUD, audio and screen effect go with Samus)
            if (_hud != null) { Destroy(_hud); }
            if (Audio != null) { Destroy(Audio); }
            if (_cc != null && _cc.cam != null)
            {
                foreach (DisruptScreen d in _cc.cam.GetComponentsInChildren<DisruptScreen>(true)) { Destroy(d); }
            }
        }

        // the layer ULTRAKILL draws its first-person weapons on (rendered by the HUD camera over the world)
        private void FindHudLayer()
        {
            GunControl gc = MonoSingleton<GunControl>.TryGetInstance(out GunControl g) ? g : null;
            if (gc != null)
            {
                foreach (Renderer r in gc.GetComponentsInChildren<Renderer>(true))
                {
                    _hudLayer = r.gameObject.layer;
                    break;
                }
            }
            if (_hudLayer < 0)
            {
                int l = LayerMask.NameToLayer("AlwaysOnTop");
                _hudLayer = l >= 0 ? l : _cc.gameObject.layer;
            }
            Plugin.Log.LogInfo($"Arm cannon layer: {_hudLayer} ({LayerMask.LayerToName(_hudLayer)})");
        }

        // ---- pointer-style aim (MPH stylus aiming; PlayerInput.UpdateAimFacing, testbed updateAimFacing) ----
        // The mouse turns the aim (ULTRAKILL's camera rotation); the view trails it - at most ~15 degrees behind, easing
        // 10% of the gap per DS step - so the arm cannon and the target circle swing toward a turn and settle back.
        // First person only; F4 toggles it.
        private const float AimLeadMaxCos = 3956 / 4096f, AimLeadMaxSin = 1060 / 4096f, AimLeadEase = 0.1f;
        private bool _pointerAim = true;
        private Vector3 _viewDir;
        private bool _haveView;
        public Vector3 AimDir { get; private set; } = Vector3.forward;   // world: where Samus aims
        public bool PointerAimActive => _pointerAim && Sim != null && Sim.FirstPerson && !_nm.dead && !Sim.Zoomed;   // zoomed: the view is the aim (MPH)

        // after CameraController.LateUpdate: its rotation is the aim; the camera turns to the trailing view
        public void ApplyPointerView(CameraController cc)
        {
            Transform cam = cc.transform;
            Vector3 aim = cam.forward;
            AimDir = aim;
            FlushShots();
            if (!PointerAimActive || !_haveView)
            {
                _viewDir = aim;
                _haveView = true;
                return;
            }
            float d = Vector3.Dot(aim, _viewDir);
            if (d < AimLeadMaxCos)
            {
                Vector3 t = _viewDir - aim * d;
                if (t.sqrMagnitude > 1e-8f) { _viewDir = aim * AimLeadMaxCos + t.normalized * AimLeadMaxSin; }
            }
            float k = 1 - Mathf.Pow(1 - AimLeadEase, Time.deltaTime * 60);   // 10% per 60 Hz step at any frame rate
            _viewDir = Vector3.Normalize(_viewDir + (aim - _viewDir) * k);
            cam.rotation = Quaternion.LookRotation(_viewDir, transform.up);
        }

        // the aim target in the camera's MPH space (+X left, +Y up, +Z forward), AimDistance away
        private OV3 AimTargetLocal()
        {
            if (_cc == null) { return new OV3(0, 0, Sim.AimDistance); }
            Vector3 local = _cc.cam.transform.InverseTransformDirection(AimDir);
            return ToMph(local.normalized) * Sim.AimDistance;
        }

        // the target circle's spot on the screen (0..1 viewport, y up)
        public Vector2 ReticleViewport()
        {
            if (_cc == null || !PointerAimActive) { return new Vector2(0.5f, 0.5f); }
            Transform cam = _cc.cam.transform;
            Vector3 vp = _cc.cam.WorldToViewportPoint(cam.position + AimDir * Sim.AimDistance * K);
            return vp.z > 0 ? new Vector2(vp.x, vp.y) : new Vector2(0.5f, 0.5f);
        }

        // ---- Hideous Mass harpoon ----
        // MassSpear sticks in the player (hitPlayer) until its spearHealth runs out (it drains over time; V1 shoots it).
        // Samus pulls it: looking down, each E press takes 1 / HarpoonPulls of the health it stuck with, with the left
        // hand's throw motion each time.
        private const int HarpoonPulls = 5;
        private const float HarpoonLookDown = -0.6f;   // camera forward.y at most this (looking down at it)
        private readonly Dictionary<MassSpear, float> _harpoonHealth = new Dictionary<MassSpear, float>();

        private bool TryPullHarpoon()
        {
            if (_cc == null || _cc.cam == null || _cc.cam.transform.forward.y > HarpoonLookDown) { return false; }
            MassSpear spear = null;
            foreach (MassSpear s in GetComponentsInChildren<MassSpear>())
            {
                if (s != null && s.hitPlayer && s.spearHealth > 0) { spear = s; break; }
            }
            if (spear == null) { return false; }
            if (!_harpoonHealth.TryGetValue(spear, out float start))
            {
                start = spear.spearHealth;
                _harpoonHealth[spear] = start;
            }
            // (MassSpear.GetHurt divides damage by 1.5 on Brutal and up: the same number of pulls on every difficulty)
            float pull = start / HarpoonPulls * (spear.difficulty >= 4 ? 1.5f : 1) + 0.001f;
            spear.GetHurt(pull);
            _hand?.Arm?.Throw();   // (no throw sound)
            Plugin.Event($"harpoon pulled: {Mathf.Max(0, spear.spearHealth):F2} / {start:F2} left");
            if (spear.spearHealth <= 0) { _harpoonHealth.Remove(spear); }
            return true;
        }

        // ---- Samus Mode off (the cheat): V1's movement, weapons, arms, HUD, crosshair and FOV come back ----
        public void Detach()
        {
            Plugin.Event("Samus Mode off: back to V1");
            StopChargeSound();
            Audio?.StopAll();
            _ball?.Reset();   // standing collider, first person
            try { ShowVanilla(); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"V1 HUD: {ex.Message}"); }
            Destroy(this);
        }

        private static void ShowVanilla()
        {
            if (MonoSingleton<GunControl>.TryGetInstance(out GunControl gc) && gc != null && gc.slots.Count > 0)
            {
                gc.YesWeapon();
                if (SceneHelper.CurrentScene == "uk_construct" && gc.slot6 != null && gc.slot6.Count > 0) { gc.SwitchWeapon(6); }   // Sandbox: the spawner arm
            }
            if (MonoSingleton<FistControl>.TryGetInstance(out FistControl fc) && fc != null) { fc.YesFist(); }
            if (MonoSingleton<StatsManager>.TryGetInstance(out StatsManager sm) && sm != null && sm.crosshair != null) { sm.crosshair.SetActive(true); }
            HudController hc = HudController.Instance;
            if (hc != null && hc.gunCanvas != null) { hc.gunCanvas.SetActive(true); }
        }

        private static void HideVanilla()
        {
            if (MonoSingleton<GunControl>.TryGetInstance(out GunControl gc) && gc != null && gc.currentWeapon != null && gc.currentWeapon.activeSelf)
            {
                gc.NoWeapon();
            }
            if (MonoSingleton<FistControl>.TryGetInstance(out FistControl fc) && fc != null && fc.activated)
            {
                fc.NoFist();
            }
            // ULTRAKILL's crosshair (Samus has the MPH target circle)
            if (MonoSingleton<StatsManager>.TryGetInstance(out StatsManager sm) && sm != null && sm.crosshair != null && sm.crosshair.activeSelf)
            {
                sm.crosshair.SetActive(false);
            }
            HudController hc = HudController.Instance;
            if (hc != null && hc.gunCanvas != null && hc.gunCanvas.activeSelf)
            {
                hc.gunCanvas.SetActive(false);
            }
        }

        private void Update()
        {
            if (Time.deltaTime <= 0 || Sim == null)
            {
                return;
            }
            // effects step at 60 Hz whatever Samus is doing (impacts finish while dead)
            _fxAcc = Mathf.Min(_fxAcc + Time.deltaTime, StepDt * MaxStepsPerFrame);
            while (_fxAcc >= StepDt)
            {
                _fxAcc -= StepDt;
                WorldFx.Step();
                _gunFx.Step();
            }
            if (_nm.dead)
            {
                GunSys.Unlink(ref _muzzleFx);
                GunSys.Unlink(ref _chargeFx);
                // ULTRAKILL's death sequence owns the body now (until NewMovement.Respawn)
                StopChargeSound();
                _beams.Update(Time.deltaTime);
                return;
            }
            FlushShots();   // (any the camera didn't take last frame)
            global::PlayerInput src = MonoSingleton<InputManager>.Instance != null ? MonoSingleton<InputManager>.Instance.InputSource : null;
            bool canAct = _nm.activated && !GameStateManager.Instance.PlayerInputLocked && !_nm.dead;
            if (src != null && canAct)
            {
                if (src.Jump.WasPerformedThisFrame) { _jumpLatch = true; }
                if (src.Fire1.WasPerformedThisFrame) { _fireLatch = true; }
                if (src.Slide.WasPerformedThisFrame) { _morphLatch = true; }
                UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.f5Key.wasPressedThisFrame) { _viewLatch = true; }
                if (kb != null && kb.eKey.wasPressedThisFrame) { _useLatch = true; }   // grab / place / throw
                if (kb != null && kb.f4Key.wasPressedThisFrame)
                {
                    _pointerAim = !_pointerAim;
                    Plugin.Event($"pointer aim (F4): {(_pointerAim ? "on" : "off")}");
                }
                LatchWeaponKeys(src);
            }
            _acc = Mathf.Min(_acc + Time.deltaTime, StepDt * MaxStepsPerFrame);
            while (_acc >= StepDt)
            {
                _acc -= StepDt;
                Step(src, canAct);
            }
            FireDueDualShots();
            _beams.Update(Time.deltaTime);
            UpdateMusicDuck();
        }

        // ---- gravity frame: the player's up (ULTRAKILL rotates the player to its gravity: CameraController.gravityRotation) ----
        private Quaternion GravityFrame => (transform.parent != null ? transform.parent.rotation : Quaternion.identity)
            * (_cc != null ? _cc.gravityRotation : Quaternion.identity);
        public Vector3 Up => GravityFrame * Vector3.up;
        // world <-> sim (MPH axes in the gravity frame)
        public OV3 WorldToSim(Vector3 v) => ToMph(Quaternion.Inverse(GravityFrame) * v);
        public Vector3 SimToWorld(OV3 v) => GravityFrame * ToUnity(v);
        // gravity isn't ULTRAKILL's usual down (Fraud's gravity volumes / portals): the Morph Ball and its camera
        // assume world up, so Samus stays on her feet there
        // (the rigidbody's gravity: CameraController, which turns the player, is paused while the ball camera runs)
        public bool GravityTurned => _rb != null && Vector3.Dot(-_rb.GetGravityDirection(), Vector3.up) < 0.99f;

        // ---- terminals (ULTRAKILL ScreenZone: shops, level screens): the arm cannon lowers, the left hand reaches ----
        private const float GunLowerRate = 1 / 8f;   // per 60 Hz step
        private const float GunLowerDrop = 0.9f;     // MPH units the cannon drops when fully lowered
        private float _gunLower;                     // 0 = up, 1 = lowered (hidden)
        public bool AtTerminal => Patches.ScreenZones.Active;
        public bool GunLowered => _gunLower >= 0.999f;

        // ---- elevators: no helmet HUD or arm cannon while riding one, and V1's gravity (Samus's MPH gravity felt slow) ----
        private const float ElevatorProbe = 8;       // Unity units below the feet: still inside the elevator while airborne
        private Collider _elevatorFloor;
        private bool _inElevator;
        public bool InElevator => _inElevator;
        public bool HudHidden => _inElevator;

        private static bool IsElevator(Transform t)
        {
            for (int depth = 0; t != null && depth < 8; depth++, t = t.parent)
            {
                if (t.GetComponent<Elevator>() != null) { return true; }
                string n = t.name;
                if (n.IndexOf("elevator", System.StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                // the first elevator-named object decides: the floors' stops, shafts, wires and wrecks aren't a ride
                foreach (string not in new[] { "stop", "shaft", "wire", "broken", "hurtzone", "disabler", "set" })
                {
                    if (n.IndexOf(not, System.StringComparison.OrdinalIgnoreCase) >= 0) { return false; }
                }
                return true;
            }
            return false;
        }

        private void UpdateElevator(bool standing, RaycastHit floor)
        {
            bool inside;
            if (standing && floor.collider != null)
            {
                if (floor.collider != _elevatorFloor) { _elevatorFloor = IsElevator(floor.collider.transform) ? floor.collider : null; }
                inside = _elevatorFloor != null;
                if (!inside) { _elevatorFloor = null; }
            }
            else
            {
                // jumping or dropping with the car: still in it while its floor is right below
                BottomSphere(out Vector3 bottom, out _, out float r, out _);
                inside = _elevatorFloor != null && Physics.SphereCast(bottom + Up * 0.05f, r * 0.9f, -Up, out RaycastHit below,
                    ElevatorProbe, EnvMask, QueryTriggerInteraction.Ignore) && below.collider == _elevatorFloor;
            }
            if (inside != _inElevator)
            {
                _inElevator = inside;
                Plugin.Event(inside ? $"in an elevator ({_elevatorFloor.name}): HUD + arm cannon hidden, V1 gravity" : "left the elevator");
            }
            // V1's gravity (Physics.gravity, Unity units/s^2) in MPH units per DS frame^2: one 60 Hz step adds Gravity / 2
            // per DS frame = g / 60 Unity units/s
            Sim.GravityOverride = _inElevator ? -Physics.gravity.magnitude / (900 * K) : float.NaN;
        }

        // ---- ULTRAKILL's fake fall (8-4): FakeFallZone holds V1 near its height (jump rises, slide sinks) ----
        private void FakeFall(global::PlayerInput src, bool canAct)
        {
            FakeFallZone zone = _nm.fakeFallZones != null && _nm.fakeFallZones.Count > 0 ? _nm.fakeFallZones[_nm.fakeFallZones.Count - 1] : null;
            if (zone == null || Sim.Standing) { return; }
            // FakeFallZone.FixedUpdate (skipped for Samus: Patches.FakeFall): velocity.y = velocity.y / 2 + (zone height +
            // control - position.y), once per 60 Hz step here
            float control = 0;
            if (src != null && canAct && src.Jump.IsPressed) { control += zone.heightControlAmount; }
            if (src != null && canAct && src.Slide.IsPressed) { control -= zone.heightControlAmount; }
            float vy = _rb.velocity.y / 2 + (zone.transform.position.y + control - transform.position.y);
            Sim.Speed = new OV3(Sim.Speed.X, vy / (30 * K), Sim.Speed.Z);
        }

        private void Step(global::PlayerInput src, bool canAct)
        {
            // 1. collision results of the last physics steps (in the gravity frame)
            Vector3 up = Up;
            bool standing = FindFloor(0.05f + GroundProbe, out RaycastHit floor);
            // walking down stairs / off the top of a slope: back onto a floor within the step height (V1 TryFloorSnap)
            if (!standing && Sim.Standing && Sim.Speed.Y <= 0 && !Sim.Launched && SnapDown(out floor)) { standing = true; }
            _groundNormal = standing ? floor.normal : up;
            float upSpeed = Vector3.Dot(_rb.velocity, up);
            bool ceiling = _lastVelY > 0.5f && upSpeed < 0.05f;
            Vector3 v = _rb.velocity;
            Sim.AfterCollision(standing, ceiling, WorldToSim(v) / (30 * K));
            UpdateElevator(standing, floor);
            bool fakeFall = _nm.fakeFallRequests > 0;
            // terminals: the arm cannon goes down and can't fire; the left hand reaches for the screen
            bool terminal = AtTerminal && !Sim.InBall;
            _gunLower = Mathf.MoveTowards(_gunLower, terminal || _inElevator ? 1 : 0, GunLowerRate);
            bool gunDown = _gunLower > 0.01f;
            // 2. input + MPH step
            Vector3 heading = GravityFrame * (Quaternion.AngleAxis(_cc.rotationY, Vector3.up) * Vector3.forward);
            var input = new SamusInput
            {
                Facing = WorldToSim(_cc.cam.transform.forward),
                Heading = WorldToSim(heading),
            };
            input.InWater = CenterInWater();   // underwater dampening
            // a Hideous Mass harpoon in Samus: look down and press E to work it out
            if (_useLatch && canAct && !Sim.InBall && TryPullHarpoon()) { _useLatch = false; }
            // E: ULTRAKILL's carried items (skulls...) in the left hand
            if (_useLatch && _items != null && canAct && !Sim.InBall && !Sim.Morphing && !terminal) { _items.Use(_hand?.Arm); }
            _useLatch = false;
            Sim.Holding = _items != null && _items.Holding;
            // no Morph Ball in ULTRAKILL's fake fall (slide sinks there) or where gravity is turned
            bool noBall = fakeFall || GravityTurned;
            if (noBall && Sim.InBall)
            {
                Sim.ForceBiped();
                _ball?.Reset();
                Plugin.Event(fakeFall ? "fake fall: out of the Morph Ball" : "gravity turned: out of the Morph Ball");
            }
            if (src != null && canAct)
            {
                Vector2 move = src.Move.ReadValue<Vector2>();
                input.MoveX = move.x;
                input.MoveY = move.y;
                input.Jump = src.Jump.IsPressed && !fakeFall;
                input.JumpPressed = _jumpLatch && !fakeFall;   // (jump rises in the fake fall instead)
                input.Fire = (src.Fire1.IsPressed || _fireLatch) && !gunDown;
                input.FirePressed = _fireLatch && !gunDown;
                input.HasLookInput = src.Look.ReadValue<Vector2>().sqrMagnitude > 0.01f;
                input.MorphPressed = _morphLatch && !noBall;   // ULTRAKILL slide key = morph / unmorph
                input.Boost = src.Dodge.IsPressed;  // dash key = Boost Ball (hold to charge)
                input.CanUnmorph = !Sim.AltForm || _ball == null || _ball.CanUnmorph();
                input.ViewTogglePressed = _viewLatch;   // F5: third-person view
                input.Zoom = src.Fire2.IsPressed && !gunDown;   // right mouse: Imperialist zoom
                if (!gunDown)
                {
                    input.WeaponSlot = _slotLatch;          // weapon keys
                    input.WeaponCycle = _cycleLatch;
                    UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
                    input.WheelHeld = kb != null && kb.qKey.isPressed;
                }
                input.LookDX = _wheelDX;
                input.LookDY = _wheelDY;
            }
            _wheelDX = _wheelDY = 0;
            _jumpLatch = _fireLatch = _morphLatch = _viewLatch = false;
            _slotLatch = _cycleLatch = 0;
            SyncArsenal();
            SyncDuals();
            Sim.BhopWindowSteps = Mathf.Max(0, Mathf.RoundToInt(Plugin.BhopWindow.Value * 60));
            Sim.Step(input);
            if (fakeFall) { FakeFall(src, canAct); }
            if (Sim.ThawedThisStep) { Audio.Play("SHOTGUN_BREAK_FREEZE"); }
            _ball?.Step();
            _hand?.Step(terminal && _gunLower > 0.5f, Sim.Holding);
            Hud.Step(Sim);
            UpdateHudSway();
            UpdateAlarm();
            if (Sim.BurnTickThisStep) { Hurt(1, "burning", burn: true); }
            UpdateLiquidSounds();
            _gun?.Apply(Sim);
            // 3. velocity for Unity physics (sim frame -> world)
            Vector3 local = ToUnity(Sim.Speed) * (30 * K);
            _lastVelY = local.y;
            if (Sim.Standing && !Sim.JumpedThisStep && Sim.Speed.Y <= 0)
            {
                // along the floor: MPH keeps the horizontal speed on slopes (its collision slides along the floor plane),
                // so the climb doesn't eat it; pressed into the floor so downhill doesn't leave it
                Vector3 n = Quaternion.Inverse(GravityFrame) * _groundNormal;
                local.y = -(n.x * local.x + n.z * local.z) / Mathf.Max(n.y, 0.1f);
                local -= n * GroundStick;
            }
            Vector3 vel = GravityFrame * local;
            if (Sim.Standing && !Sim.JumpedThisStep && Sim.Speed.Y <= 0) { TryStepUp(vel); }
            _rb.velocity = vel;
            // keep ULTRAKILL's player flags sensible for systems that read them
            _nm.walking = false;
            _nm.jumping = !Sim.Standing;
            _nm.hp = Mathf.Clamp(Sim.Health, 1, 100);
            // 4. events
            if (Sim.JumpedThisStep) { Audio.Play("JUMP"); }
            if (Sim.LandedThisStep)
            {
                UpdateTerrain(force: true);
                // PlayerProcess landing: DGN amount A = air time (full at 180 steps)
                PlayTerrain(TerrainSound.Land, 0xFFFF * Mathf.Min(Sim.LandAirSteps, 180) / 180f, 0);
                Plugin.Event($"landed, view dip {Sim.Field44C * 2 * K:F2}{(Sim.LandShake > 0 ? ", shake" : "")}");
            }
            if (Sim.LandShake > 0 && _cc != null) { _cc.CameraShake(Sim.LandShake * K); }
            if (Sim.FootstepThisStep != 0)
            {
                // PlayerSound.UpdateWalkingSfx: DGN amount A = max, B = random (volume/pitch vary per step)
                UpdateTerrain(force: true);
                PlayTerrain(Sim.FootstepThisStep == 1 ? TerrainSound.Step1 : TerrainSound.Step2, 0xFFFF, Random.Range(0, 0x7FFF) * 2f);
            }
            if (Sim.ChargeStopped) { StopChargeSound(); }
            if (Sim.ChargeStarted) { StartChargeSound(); }
            PlaySwitchSounds();
            if (Sim.ZoomChanged) { Audio.Play(Sim.Zoomed ? "SNIPER_ZOOM_IN" : "SNIPER_ZOOM_OUT", 96 / 127f); }
            if (Sim.ShotThisStep is ShotEvent shot)
            {
                if (!shot.Continuous) { Plugin.Event($"shot {shot.Weapon} {shot.Kind} damage {shot.Damage}{(Sim.AmmoShots >= 0 ? $", {Sim.AmmoShots} shots left" : "")}"); }
                SyncGunFxSystem();
                if (!_muzzleFx.Valid || !shot.Continuous)
                {
                    GunSys.Unlink(ref _muzzleFx);
                    _muzzleFx = GunSys.Spawn(Sim.MuzzleEffectId, GunFxTransform());
                }
                // PlayBeamShotSfx (WEAPON_SOUNDS)
                bool charged = shot.Kind == ShotKind.Charged;
                WeaponSounds ws = WeaponSounds.ByBeam[(int)shot.Weapon];
                string sfx = SoundName(charged ? ws.ChargeShot : ws.Shot);
                if (sfx != null) { Audio.Play(sfx, (charged ? ws.ChargeShotVol : ws.ShotVol) / 127f); }
                _pendingShots.Add(shot);   // fired once the camera has this frame's aim (FlushShots)
                QueueDualShots(shot);
                // the Imperialist reloads when there's ammo for another shot
                if (shot.Weapon == MphRead.BeamType.Imperialist && Sim.AmmoRaw >= Sim.Weapon.AmmoCost) { Audio.Play("SNIPER_RELOAD", 110 / 127f); }
            }
            UpdateGunEffects();
        }

        // first person: muzzle/charge effects in the gun's camera space; third person: in the world at the biped's muzzle
        private EffectSystem GunSys => _fxThird ? WorldFx : _gunFx;

        private void SyncGunFxSystem()
        {
            bool third = Sim.ThirdView;
            if (third == _fxThird) { return; }
            GunSys.Unlink(ref _muzzleFx);
            GunSys.Unlink(ref _chargeFx);
            _fxThird = third;
        }

        // where shots aim: the camera's center ray
        private Vector3 AimPoint()
        {
            Transform cam = _cc.cam.transform;
            Vector3 aimDir = AimDir;
            Vector3 target = cam.position + aimDir * Sim.AimDistance * K;
            if (Physics.Raycast(cam.position, aimDir, out RaycastHit hit, Sim.AimDistance * K,
                LayerMaskDefaults.Get(LMD.EnemiesAndEnvironment), QueryTriggerInteraction.Ignore))
            {
                target = hit.point;
            }
            return target;
        }

        // muzzle in the gun's camera space, MPH axes (UpdateAimVecs: gun position + aim * MuzzleOffset);
        // effect transform = (facing _gunVec2 = left, up _gunVec1 = aim). Third person: the biped's muzzle, world space.
        private Matrix4 GunFxTransform()
        {
            if (_fxThird && _ball != null && _ball.ThirdMuzzle is Vector3 m)
            {
                OV3 aimW = ToMph((AimPoint() - m).normalized);
                OV3 leftW = OV3.Cross(OV3.UnitY, aimW);
                leftW = leftW.LengthSquared > 1e-8f ? leftW.Normalized() : OV3.UnitX;
                return EffectSystem.Transform(leftW, aimW, ToFxWorld(m));
            }
            OV3 off = Sim.GunOffset();
            OV3 aim = (AimTargetLocal() - off).Normalized();
            OV3 left = OV3.Cross(OV3.UnitY, aim).Normalized();
            return EffectSystem.Transform(left, aim, off + aim * Sim.MuzzleOffset);
        }

        // PlayerProcess (testbed SamusPlayer::updateEffects): keep the muzzle/charge effects on the gun, end them
        private void UpdateGunEffects()
        {
            SyncGunFxSystem();
            Matrix4 xf = GunFxTransform();
            if (_muzzleFx.Valid)
            {
                bool shockMuzzle = Sim.CurrentWeapon == MphRead.BeamType.ShockCoil && !Sim.ShotUncharged;
                if (GunSys.IsFinished(_muzzleFx) || shockMuzzle) { GunSys.Unlink(ref _muzzleFx); }
                else { GunSys.SetTransform(_muzzleFx, xf); }
            }
            // on fire: flamingGun on the arm cannon (PlayerProcess.CreateBurnEffect; the ball's flamingAltForm: later)
            if (Sim.BurnTimer > 0 && !Sim.InBall)
            {
                if (!_burnFx.Valid)
                {
                    _burnFx = GunSys.Spawn(BurnGunEffect, xf);
                    GunSys.SetElementExtension(_burnFx, true);
                }
                GunSys.SetTransform(_burnFx, xf);
            }
            else if (_burnFx.Valid) { GunSys.Unlink(ref _burnFx); }
            int level = Sim.ChargeLevel;
            MphRead.WeaponInfo w = Sim.Weapon;
            if (level < w.MinCharge * 2)
            {
                GunSys.Unlink(ref _chargeFx);
                return;
            }
            if (level == w.MinCharge * 2)
            {
                GunSys.Unlink(ref _chargeFx);
                _chargeFx = GunSys.Spawn(Sim.ChargeEffectId, xf);
                _chargeLoopFx = false;
            }
            else if (level == w.FullCharge * 2 && !_chargeLoopFx)
            {
                GunSys.Unlink(ref _chargeFx);
                _chargeFx = GunSys.Spawn(Sim.ChargeLoopEffectId, xf);
                GunSys.SetElementExtension(_chargeFx, true);
                _chargeLoopFx = true;
            }
            GunSys.SetTransform(_chargeFx, xf);
        }

        // ---- weapons: ULTRAKILL's weapon keys pick Samus's weapons (slot 1 = Power Beam, slot 2 = Missiles) ----
        private int _slotLatch, _cycleLatch;
        private float _scrollCooldown;
        private string _chargeSound;
        private bool _arsenalSeen;

        // weapon keys (owner): ULTRAKILL's own slot bindings 1-6 + the 7 and 8 keys pick SamusSim.KeyOrder; testbed
        // extras: F = Missiles, mouse wheel down/up = next/previous carried weapon, hold Q = weapon wheel
        private float _wheelDX, _wheelDY;

        public bool WheelOpen => Sim != null && Sim.WheelOpen;

        private void LatchWeaponKeys(global::PlayerInput src)
        {
            InputActionState[] slots = { src.Slot1, src.Slot2, src.Slot3, src.Slot4, src.Slot5, src.Slot6 };
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].WasPerformedThisFrame) { _slotLatch = i + 1; }
            }
            UnityEngine.InputSystem.Keyboard kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                if (kb.digit7Key.wasPressedThisFrame) { _slotLatch = 7; }
                if (kb.digit8Key.wasPressedThisFrame) { _slotLatch = 8; }
                if (kb.fKey.wasPressedThisFrame) { _slotLatch = 2; }
            }
            _scrollCooldown = Mathf.Max(0, _scrollCooldown - Time.deltaTime);
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0 && _scrollCooldown <= 0)
                {
                    _cycleLatch = scroll < 0 ? 1 : -1;
                    _scrollCooldown = WeaponScrollCooldown;
                }
                Vector2 d = mouse.delta.ReadValue();
                _wheelDX += d.x;
                _wheelDY += d.y;
            }
        }

        // Samus Mode: every MPH weapon and every story expansion (MphInventory); missile capacity from config
        private void SyncArsenal()
        {
            foreach (MphRead.BeamType b in SamusSim.WeaponOrder) { Sim.Carried[(int)b] = true; }
            Sim.HealthMax = SamusSim.StartHealthMax + MphInventory.EnergyTanks * MphInventory.EnergyTankAmount;
            Sim.UaAmmoMax = SamusSim.UaAmmoStart + MphInventory.UaExpansions * MphInventory.UaExpansionAmount;
            int max = Mathf.Max(1, Plugin.MissileCapacity.Value) * SamusSim.MissileInfo.AmmoCost
                + MphInventory.MissileExpansions * MphInventory.MissileExpansionAmount;
            Sim.MissileAmmoMax = max;
            if (!_arsenalSeen)
            {
                _arsenalSeen = true;
                Sim.Health = Sim.HealthMax;
                Sim.MissileAmmo = Sim.MissileAmmoMax;
                Sim.UaAmmo = Sim.UaAmmoMax;
                Plugin.Event($"arsenal: every weapon, energy {Sim.HealthMax}, missiles {max / SamusSim.MissileInfo.AmmoCost}, UA {Sim.UaAmmoMax / 10}");
            }
            Sim.UaAmmo = Mathf.Min(Sim.UaAmmo, Sim.UaAmmoMax);
            Sim.MissileAmmo = Mathf.Min(Sim.MissileAmmo, max);
            if (!Sim.Has(Sim.CurrentWeapon)) { Sim.TryEquipWeapon(MphRead.BeamType.PowerBeam, silent: true); }
        }

        // the weapon's charge loop (WEAPON_SOUNDS charge: BEAM_CHARGE1, MISSILE_CHARGE1, ...)
        private void StartChargeSound()
        {
            StopChargeSound();
            WeaponSounds ws = WeaponSounds.ByBeam[(int)Sim.CurrentWeapon];
            _chargeSound = SoundName(ws.Charge);
            if (_chargeSound != null) { Audio.StartLoop(_chargeSound, ws.ChargeVol / 127f); }
        }

        // ---- terrain sounds (MPH TerrainSfx + DGN curves) ----
        // ULTRAKILL's floor surface type (SceneHelper surface data, ground property overrides) -> MPH terrain set;
        // water -> MPH's wet (lava floor) set, as in the testbed's pools. Not MPH: ULTRAKILL has no terrain ids.
        public MphTerrain Terrain { get; private set; } = MphTerrain.Metal;
        private int _terrainSteps;

        public void UpdateTerrain(bool force = false)
        {
            if (!force && _terrainSteps-- > 0) { return; }
            _terrainSteps = 10;
            if ((MonoSingleton<UnderwaterController>.TryGetInstance(out UnderwaterController uw) && uw != null && uw.inWater)
                || _onLava)
            {
                Terrain = MphTerrain.Lava;
                return;
            }
            SurfaceType st = SurfaceType.Generic;
            CustomGroundProperties gp = _nm.groundProperties;
            if (gp != null && gp.overrideSurfaceType) { st = gp.surfaceType; }
            else if (MonoSingleton<SceneHelper>.TryGetInstance(out SceneHelper sh) && sh != null
                && sh.TryGetSurfaceData(transform.position, out SceneHelper.HitSurfaceData hit))
            {
                st = hit.surfaceType;
            }
            Terrain = TerrainFor(st);
        }

        private static MphTerrain TerrainFor(SurfaceType st)
        {
            switch (st)
            {
                case SurfaceType.Glass: return MphTerrain.Ice;
                case SurfaceType.Snow:
                case SurfaceType.Grass:
                case SurfaceType.Carpet: return MphTerrain.Snow;
                case SurfaceType.Sand:
                case SurfaceType.Dirt: return MphTerrain.Sand;
                case SurfaceType.Gravel:
                case SurfaceType.Rubble:
                case SurfaceType.Crunchy:
                case SurfaceType.Marble:
                case SurfaceType.Wood:
                case SurfaceType.WoodThin: return MphTerrain.Rock;
                case SurfaceType.Wet:
                case SurfaceType.Flesh: return MphTerrain.Lava;   // MPH's wet steps
                default: return MphTerrain.Metal;                 // generic, metal, hologram (MPH holo floors = metal)
            }
        }

        private void PlayTerrain(TerrainSound kind, float amountA, float amountB)
        {
            DgnResult d = DgnSounds.Terrain(Terrain, kind, amountA, amountB);
            string name = SoundName(d.Sample);
            if (name != null && d.Volume > 0) { Audio.Play(name, d.Volume, d.Pitch, raw: true); }
        }

        private static readonly Dictionary<int, string> _soundNames = new Dictionary<int, string>();
        internal static string SoundName(int sample)
        {
            if (sample < 0) { return null; }
            if (!_soundNames.TryGetValue(sample, out string n)) { _soundNames[sample] = n = SoundBank.NameAt(sample); }
            return n;
        }

        private void StopChargeSound()
        {
            if (_chargeSound == null) { return; }
            Audio.StopLoop(_chargeSound);
            _chargeSound = null;
        }

        // TryEquipWeapon sounds (not in the ball) + the testbed weapon wheel's
        private void PlaySwitchSounds()
        {
            switch (Sim.WheelEventThisStep)
            {
                case WheelEvent.Open: Audio.Play("HUD_WEAPON_SWITCH1", 64 / 127f); break;
                case WheelEvent.Hover: Audio.Play("HUD_WEAPON_SWITCH2", 64 / 127f); break;
                case WheelEvent.CloseEmpty: Audio.Play("RETURN_TO_SHIP_NO", 96 / 127f); break;
                case WheelEvent.CloseFail: Audio.Play("BEAM_SWITCH_FAIL", 72 / 127f); break;
            }
            WeaponSwitch sw = Sim.SwitchedThisStep;
            if (sw == WeaponSwitch.None) { return; }
            Plugin.Event($"weapon {sw}: {Sim.CurrentWeapon}");
            if (sw == WeaponSwitch.Fail) { Audio.Play("BEAM_SWITCH_FAIL", 72 / 127f); return; }
            if (Sim.AltForm) { return; }
            if (sw != WeaponSwitch.MissileOpen) { Audio.Play("BEAM_SWITCH", 72 / 127f); }
            if (sw == WeaponSwitch.MissileOpen) { Audio.Play("MISSILE_SWITCH", 88 / 127f); }
            else if (sw == WeaponSwitch.MissileClose) { Audio.Play("MISSILE_CLOSE", 56 / 127f); }
        }

        // ---- floors, slopes, stairs (the engine's collision; MPH's own is not used) ----
        private Vector3 _groundNormal = Vector3.up;   // world space
        private static float MinFloorY => Mathf.Cos(Mathf.Clamp(Plugin.SlopeLimit.Value, 0, 89) * Mathf.Deg2Rad);
        private static int EnvMask => LayerMaskDefaults.Get(LMD.Environment);

        // the capsule's bottom sphere (center, radius) and the feet height (along the player's up)
        private void BottomSphere(out Vector3 bottom, out Vector3 top, out float radius, out float feetY)
        {
            float scale = transform.lossyScale.y;
            radius = _capsule.radius * scale;
            Vector3 center = transform.TransformPoint(_capsule.center);
            float half = Mathf.Max(0, _capsule.height * scale / 2 - radius);
            Vector3 up = Up;
            bottom = center - up * half;
            top = center + up * half;
            feetY = Vector3.Dot(bottom, up) - radius;
        }

        // a floor within reach below the feet, no steeper than Movement/SlopeLimit (steeper: she slides off)
        private bool FindFloor(float reach, out RaycastHit floor)
        {
            floor = default;
            if (_capsule == null) { return false; }
            BottomSphere(out Vector3 bottom, out _, out float radius, out _);
            Vector3 up = Up;
            Vector3 origin = bottom + up * 0.05f;
            if (!Physics.SphereCast(origin, radius * 0.95f, -up, out floor, reach, EnvMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            // the face's own normal (on a step's edge the sphere reports the edge's slanted one)
            if (Physics.Raycast(floor.point + up * 0.1f, -up, out RaycastHit face, 0.2f, EnvMask, QueryTriggerInteraction.Ignore))
            {
                floor.normal = face.normal;
            }
            return Vector3.Dot(floor.normal, up) >= MinFloorY;
        }

        private bool SnapDown(out RaycastHit floor)
        {
            float step = Plugin.StepHeight.Value;
            if (step <= 0 || !FindFloor(0.05f + step, out floor)) { floor = default; return false; }
            float drop = floor.distance - 0.05f - StepSkin;
            if (drop > 0) { transform.position -= Up * drop; }
            return true;
        }

        // stairs: a ledge up to Movement/StepHeight high in the way, with room above it -> lift the body onto it
        private void TryStepUp(Vector3 vel)
        {
            Vector3 upV = Up;
            Vector3 h = Vector3.ProjectOnPlane(vel, upV);
            float speed = h.magnitude, step = Plugin.StepHeight.Value;
            if (_capsule == null || speed < 0.5f || step <= 0) { return; }
            Vector3 dir = h / speed;
            BottomSphere(out Vector3 bottom, out Vector3 top, out float r, out float feetY);
            float reach = speed * StepDt + 0.1f, rc = r * 0.95f;
            Vector3 lift = upV * (0.05f + StepSkin);
            if (!Physics.CapsuleCast(bottom + lift, top + lift, rc, dir, out RaycastHit wall, reach, EnvMask, QueryTriggerInteraction.Ignore))
            {
                return;
            }
            if (Vector3.Dot(wall.point, upV) - feetY > step) { return; }   // a wall, not a ledge
            Vector3 n = wall.normal;
            if (Physics.Raycast(wall.point - dir * 0.05f + upV * 0.01f, dir, out RaycastHit face, 0.15f, EnvMask, QueryTriggerInteraction.Ignore))
            {
                n = face.normal;
            }
            if (Vector3.Dot(n, upV) >= MinFloorY) { return; }   // a walkable slope: the along-the-floor speed climbs it
            Vector3 up = upV * (step + StepSkin);
            if (Physics.CheckCapsule(bottom + up, top + up, rc, EnvMask, QueryTriggerInteraction.Ignore)) { return; }   // no headroom
            if (Physics.CapsuleCast(bottom + up, top + up, rc, dir, reach, EnvMask, QueryTriggerInteraction.Ignore)) { return; }   // taller than a step
            Vector3 probe = bottom + up + dir * Mathf.Min(reach + r * 0.5f, r);
            if (!Physics.SphereCast(probe, rc, -upV, out RaycastHit ledge, step + StepSkin, EnvMask, QueryTriggerInteraction.Ignore)
                || Vector3.Dot(ledge.normal, upV) < MinFloorY)
            {
                return;
            }
            float rise = (Vector3.Dot(probe, upV) - ledge.distance) - Vector3.Dot(bottom, upV);   // how far the bottom sphere goes up
            if (rise <= 0.01f || rise > step) { return; }
            transform.position += upV * (rise + StepSkin);
        }

        // Shots wait for CameraController.LateUpdate: ULTRAKILL reads the mouse and turns the camera there, so a shot
        // fired in Update went where the view pointed a frame earlier - off the crosshair at low frame rates (owner:
        // the Imperialist beam missing its aim with many enemies around).
        private readonly List<ShotEvent> _pendingShots = new List<ShotEvent>();
        private void FlushShots()
        {
            for (int i = 0; i < _pendingShots.Count; i++) { FireBeam(_pendingShots[i]); }
            _pendingShots.Clear();
        }

        private void FireBeam(ShotEvent shot)
        {
            Transform cam = _cc.cam.transform;
            // muzzle: gun draw position + aim * MuzzleOffset (UpdateAimVecs), in Unity camera space
            OV3 off = Sim.GunOffset();
            Vector3 gunLocal = new Vector3(-off.X, off.Y, off.Z) * K;
            Vector3 aimLocal = (ToUnity(AimTargetLocal()) * K - gunLocal).normalized;
            Vector3 muzzle = cam.TransformPoint(gunLocal + aimLocal * Sim.MuzzleOffset * K);
            // third person: from the biped's arm cannon (testbed V view)
            if (Sim.ThirdView && _ball != null && _ball.ThirdMuzzle is Vector3 m) { muzzle = m; }
            // shots go where the crosshair points (TryFireWeapon: _aimPosition - _muzzlePos); disrupted: they stray
            Vector3 target = AimPoint();
            Vector3 dir = (target - muzzle).normalized;
            if (Sim.DisruptedTimer > 0)
            {
                OV3 vec = Sim.DisruptAim(ToMph(dir) * MphRead.Fixed.ToFloat(SamusSim.V.AimDistance), _afflictRng);
                dir = ToUnity(vec).normalized;
            }
            _beams.Spawn(shot, muzzle, dir);
        }

        // ---- Dual Wield power-up (owner: it works for Samus too) ----
        // ULTRAKILL's DualWieldPickup adds a DualWield copy under GunControl per pickup (firing 0.05 s later + 0.05 s per
        // earlier copy; the power-up meter ends them). Samus gets an MPH arm cannon per copy: copies alternate left /
        // right of the main cannon, further out as they stack; left ones are drawn mirrored (the model only has one side),
        // and each one points at the crosshair and repeats every shot after its delay.
        private const float DualSpacing = 0.6f;   // Unity units between stacked copies on one side
        private sealed class DualGun
        {
            public DualWield Dw;
            public ModelView View;
            public int Index;
        }
        private readonly List<DualGun> _duals = new List<DualGun>();
        private readonly List<(float time, ShotEvent shot, DualGun gun)> _dualShots = new List<(float, ShotEvent, DualGun)>();
        private int _dualCount;

        private void SyncDuals()
        {
            // the power-up meter lives on the weapon HUD canvas Samus mode hides: drain it here so the power-up ends
            if (MonoSingleton<PowerUpMeter>.TryGetInstance(out PowerUpMeter meter) && meter != null && !meter.isActiveAndEnabled) { meter.UpdateMeter(); }
            GunControl gc = MonoSingleton<GunControl>.TryGetInstance(out GunControl g) ? g : null;
            int count = gc != null ? gc.dualWieldCount : 0;
            if (count == _dualCount && _duals.TrueForAll(d => d.Dw != null)) { return; }
            _dualCount = count;
            foreach (DualGun d in _duals) { d.View?.Destroy(); }
            _duals.Clear();
            _dualShots.Clear();
            if (count <= 0 || gc == null || _gun == null) { return; }
            int i = 0;
            foreach (DualWield dw in gc.GetComponentsInChildren<DualWield>())
            {
                var view = new ModelView(_gun.Posed, "MPH Arm Cannon (Dual Wield)", _cc.cam.transform, _hudLayer) { Scale = K };
                _duals.Add(new DualGun { Dw = dw, View = view, Index = i++ });
            }
            Plugin.Event($"dual wield: {_duals.Count} extra arm cannon(s)");
        }

        // a copy's gun position in camera space (Unity units): 0 = left (mirror image of the main cannon), 1 = right
        // of it, 2 = further left, ...
        private Vector3 DualGunLocal(DualGun d)
        {
            OV3 off = Sim.GunOffset();
            Vector3 main = new Vector3(-off.X, off.Y, off.Z) * K;
            bool left = d.Index % 2 == 0;
            int level = left ? d.Index / 2 : (d.Index + 1) / 2;
            float x = left ? -main.x - level * DualSpacing : main.x + level * DualSpacing;
            return new Vector3(x, main.y, main.z);
        }

        private Vector3 DualAimLocal(Vector3 gunLocal) => (ToUnity(AimTargetLocal()) * K - gunLocal).normalized;

        private void DrawDuals(bool show, MphLighting light = default)
        {
            foreach (DualGun d in _duals)
            {
                d.View.SetVisible(show);
                if (!show) { continue; }
                Vector3 p = DualGunLocal(d), a = DualAimLocal(p);
                bool mirror = p.x < 0;
                // pose in the view's own space: a mirrored view flips X back
                Vector3 mp = mirror ? new Vector3(-p.x, p.y, p.z) : p, ma = mirror ? new Vector3(-a.x, a.y, a.z) : a;
                MphLighting l = light;
                if (mirror)
                {
                    l.Light1Dir = new OV3(-l.Light1Dir.X, l.Light1Dir.Y, l.Light1Dir.Z);
                    l.Light2Dir = new OV3(-l.Light2Dir.X, l.Light2Dir.Y, l.Light2Dir.Z);
                }
                _gun.Pose(ToMph(mp) / K, ToMph(ma), l);
                Transform root = d.View.Root.transform;
                root.localRotation = Quaternion.identity;
                root.localScale = new Vector3(mirror ? -1 : 1, 1, 1);
                root.localPosition = Vector3.zero;
                d.View.Scale = K;
                d.View.Upload(root.InverseTransformPoint(_cc.cam.transform.position), 0);
            }
        }

        private void QueueDualShots(ShotEvent shot)
        {
            foreach (DualGun d in _duals)
            {
                if (d.Dw != null) { _dualShots.Add((Time.time + d.Dw.delay, shot, d)); }
            }
        }

        private void FireDueDualShots()
        {
            for (int i = _dualShots.Count - 1; i >= 0; i--)
            {
                (float time, ShotEvent shot, DualGun gun) = _dualShots[i];
                if (Time.time < time) { continue; }
                _dualShots.RemoveAt(i);
                if (_nm.dead || Sim.Health <= 0) { continue; }
                Transform cam = _cc.cam.transform;
                Vector3 gunLocal = DualGunLocal(gun);
                Vector3 aimLocal = DualAimLocal(gunLocal);
                Vector3 muzzleLocal = gunLocal + aimLocal * Sim.MuzzleOffset * K;
                Vector3 muzzle = cam.TransformPoint(muzzleLocal);
                if (Sim.ThirdView && _ball != null && _ball.ThirdMuzzle is Vector3 m) { muzzle = m; }
                Vector3 target = AimPoint();
                _beams.Spawn(shot, muzzle, (target - muzzle).normalized);
                bool charged = shot.Kind == ShotKind.Charged;
                WeaponSounds ws = WeaponSounds.ByBeam[(int)shot.Weapon];
                string sfx = SoundName(charged ? ws.ChargeShot : ws.Shot);
                if (sfx != null && !shot.Continuous) { Audio.Play(sfx, (charged ? ws.ChargeShotVol : ws.ShotVol) / 127f); }
                if (!Sim.ThirdView)
                {
                    // muzzle flash on the copy (arm cannon camera space, MPH axes: +X left)
                    OV3 aim = ToMph(aimLocal);
                    OV3 left = OV3.Cross(OV3.UnitY, aim).Normalized();
                    _gunFx.SpawnOneShot(Sim.MuzzleEffectId, EffectSystem.Transform(left, aim, ToMph(muzzleLocal) / K));
                }
            }
        }

        // Samus Mode keeps ULTRAKILL's field of view setting; only the Imperialist's zoom narrows it (MPH's Fov change,
        // relative to MPH's normal FOV). CameraController eases cam.fieldOfView toward defaultFov, so that is what gets set.
        public const float GunCameraFov = 78f;   // the weapon camera (arm cannon): MPH's FOV (NormalFov x 2)
        private bool _zoomFov;

        private static float SettingsFov()
        {
            float f = MonoSingleton<PrefsManager>.TryGetInstance(out PrefsManager pm) && pm != null ? pm.GetFloat("fieldOfView") : 0;
            return f > 0 ? f : 105f;
        }

        private void ApplyFov()
        {
            if (_cc == null || _cc.cam == null) { return; }
            bool zoom = Sim != null && Sim.CameraFov > 0 && (Sim.Zoomed || Sim.Fov < Sim.CameraFov - 0.01f);
            if (zoom)
            {
                float fov = SettingsFov() * Sim.Fov / Sim.CameraFov;
                _cc.defaultFov = fov;
                _cc.cam.fieldOfView = fov;
                _zoomFov = true;
            }
            else if (_zoomFov)
            {
                RestoreFov();
            }
            if (BallCameraActive) { _cc.cam.fieldOfView = _cc.defaultFov; }   // CameraController is paused then
        }

        private void RestoreFov()
        {
            if (!_zoomFov || _cc == null) { return; }
            _zoomFov = false;
            float fov = SettingsFov();
            _cc.defaultFov = fov;
            if (_cc.cam != null) { _cc.cam.fieldOfView = fov; }
        }

        // ---- portals (Patches.TravellerPosition / Travel) ----
        // before NewMovement.OnTravel: the camera at Samus's eye (OnTravel places the body from the camera)
        public void CameraToEye()
        {
            if (_cc == null || _cc.transform.parent == null || EyeWorld == Vector3.zero) { return; }
            _cc.transform.localPosition = _cc.transform.parent.InverseTransformPoint(EyeWorld);
        }

        // after a portal: OnTravel turned the body's velocity (and maybe gravity) - the sim takes all of it, the vertical
        // part included (a fall into a floor portal comes out of a wall portal sideways)
        public void OnPortalTravel()
        {
            if (Sim == null) { return; }
            Sim.Speed = WorldToSim(_rb.velocity) / (30 * K);
            _haveView = false;
            _haveLastAim = false;
            Plugin.Event($"portal: {_rb.velocity.magnitude:F1} u/s, up {Up}");
        }

        private void LateUpdate()
        {
            if (Sim == null) { return; }
            HideVanilla();
            ApplyFov();
            // camera at Samus's eye height (+ landing dip), expressed in the camera parent's space
            if (_capsule != null && _cc != null && _cc.transform.parent != null)
            {
                float feet = _capsule.center.y - _capsule.height / 2;
                Vector3 eyeWorld = transform.TransformPoint(new Vector3(_capsule.center.x,
                    feet + (MphFeetBelow + Sim.EyeHeight + Sim.LandingDip(_acc / StepDt)) * K / transform.lossyScale.y, _capsule.center.z));
                EyeWorld = eyeWorld;
                Vector3 eye = _cc.transform.parent.InverseTransformPoint(eyeWorld);
                _cc.defaultPos = eye;
                _cc.defaultTarget = eye;
                _cc.originalPos = eye;
                float pct = Mathf.Clamp01((Sim.CamSwitchTimer + _acc / StepDt) / Sim.CamSwitchSteps);
                // F5 view: over the shoulder, blended with the eye while the camera switches
                Vector3 bipedCam = eyeWorld;
                if (Sim.ThirdCam || (Sim.ThirdPersonView == false && pct < 1 && !BallCameraActive))
                {
                    Vector3 third = ThirdViewPosition(feet);
                    float w = Sim.ThirdCam ? pct : 1 - pct;
                    bipedCam = Vector3.Lerp(eyeWorld, third, Sim.InBall ? 1 : w);
                }
                if (BallCameraActive)
                {
                    // the Morph Ball camera (and the blend back to Samus's view) owns the camera
                    Quaternion eyeRot = Quaternion.AngleAxis(_cc.rotationY, Vector3.up) * Quaternion.AngleAxis(-_cc.rotationX, Vector3.right);
                    Vector3 target = Sim.ThirdPersonView ? ThirdViewPosition(feet) : eyeWorld;
                    _ball.UpdateCamera(Time.deltaTime, pct, target, eyeRot);
                }
                else if (bipedCam != eyeWorld)
                {
                    Vector3 local = _cc.transform.parent.InverseTransformPoint(bipedCam);
                    _cc.defaultPos = local;
                    _cc.defaultTarget = local;
                    _cc.originalPos = local;
                    if (_cc.cameraShaking <= 0) { _cc.transform.localPosition = local; }
                }
                else if (_cc.cameraShaking <= 0)
                {
                    _cc.transform.localPosition = eye;
                }
            }
            DrawGun();
            _hand?.Draw(GunLight(), AimTargetLocal());
            if (_hand != null) { _items?.LateUpdate(_hand.Arm, Sim.FirstPerson, _hand.HandWorld); }
            DrawBody();      // (first: it moves the ball's effects to the drawn ball)
            DrawEffects();
        }

        // Samus's first-person eye (world): ULTRAKILL's portals track the player by it (Patches.TravellerPosition), also
        // in third person and in the Morph Ball, where the camera itself is elsewhere
        public Vector3 EyeWorld { get; private set; }

        // testbed updateCameraThird (V view): behind, above and right of Samus along the view's yaw, pulled in front of
        // walls. feetLocal: the collider's feet height in the player's local space.
        private Vector3 ThirdViewPosition(float feetLocal)
        {
            Vector3 pos = transform.TransformPoint(new Vector3(_capsule.center.x, feetLocal + MphFeetBelow * K / transform.lossyScale.y, _capsule.center.z));
            Transform cam = _cc.cam.transform;
            Vector3 up = Up;
            Vector3 fwd = Vector3.ProjectOnPlane(cam.forward, up);
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : transform.forward;
            Vector3 right = Vector3.Cross(up, fwd);
            // testbed V view distances, times the Samus/ThirdPersonDistance setting
            float scale = Plugin.ThirdPersonDistance.Value;
            float back = ThirdViewDistance * scale, side = ThirdViewSide * scale;
            Vector3 posVec = pos + up * ThirdViewHeight * K - fwd * back * K + right * side * K;
            Vector3 eye = pos + up * ThirdViewEye * K;
            Vector3 toCam = posVec - eye;
            float dist = toCam.magnitude;
            if (dist > 1e-3f && Physics.Raycast(eye, toCam / dist, out RaycastHit hit, dist + ThirdViewWallMargin * K,
                LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore))
            {
                posVec = eye + toCam * (Mathf.Max(ThirdViewMinDistance * K, hit.distance - ThirdViewWallMargin * K) / dist);
            }
            return posVec;
        }

        private void DrawBody()
        {
            if (_ball == null) { return; }
            Transform cam = _cc.cam.transform;
            MphLighting light = MphLighting.Default;   // world-space lights; view axes for billboards / specular
            light.ViewDir = ToMph(cam.forward);
            light.ViewRight = ToMph(cam.right);
            light.ViewUp = ToMph(cam.up);
            _ball.Draw(light);
        }

        private void DrawEffects()
        {
            Transform cam = _cc != null ? _cc.cam.transform : null;
            if (cam == null) { return; }
            // world: camera axes in MPH world space (ToMph keeps right x up = back)
            _worldFxView?.Draw(WorldFx, ToMph(cam.right), ToMph(cam.up), ToMph(-cam.forward), K, true);
            // the ball's effects step here, after MorphBall.Draw put them on the ball where it's drawn (physics moved it
            // after the 60 Hz step): new boost glow particles appear on the ball, not a move or two behind (owner)
            _ballFxAcc = Mathf.Min(_ballFxAcc + Time.deltaTime, StepDt * MaxStepsPerFrame);
            while (_ballFxAcc >= StepDt)
            {
                _ballFxAcc -= StepDt;
                BallFx.Step();
            }
            _ballFxView?.Draw(BallFx, ToMph(cam.right), ToMph(cam.up), ToMph(-cam.forward), MorphBall.BallScale, true);
            // arm cannon: camera space (right = -X, back = -Z)
            _gunFxView?.Draw(_gunFx, -OV3.UnitX, OV3.UnitY, -OV3.UnitZ, K, !_nm.dead && Sim.Health > 0 && Sim.FirstPerson && _gunLower <= 0 && !_inElevator);
        }

        private void DrawGun()
        {
            if (_gun == null || _gunView == null) { return; }
            // hidden in the ball, while switching views, fully lowered (terminals) and in elevators
            bool show = !_nm.dead && Sim.Health > 0 && Sim.FirstPerson && !GunLowered && !_inElevator;
            _gunView.SetVisible(show);
            if (!show) { DrawDuals(false); return; }
            OV3 off = Sim.GunOffset();
            OV3 aim = (AimTargetLocal() - off).Normalized();
            if (_gunLower > 0)
            {
                // lowering: down and tipped toward the floor (eased)
                float t = Mathf.SmoothStep(0, 1, _gunLower);
                off.Y -= GunLowerDrop * t;
                aim = (aim + new OV3(0, -1.2f * t, 0)).Normalized();
            }
            MphLighting light = GunLight();
            _gun.Pose(off, aim, light);
            _gunView.Scale = K;
            _gunView.Upload(Vector3.zero, 0);
            _gunView.Root.transform.localPosition = Vector3.zero;
            _gunView.Root.transform.localRotation = Quaternion.identity;
            DrawDuals(true, light);
        }

        // MPH camera-space lighting for the arm cannon / left arm: room lights are world-fixed, so rotate them into the
        // camera's frame
        private MphLighting GunLight()
        {
            Transform cam = _cc.cam.transform;
            MphLighting light = MphLighting.Default;
            light.Light1Dir = ToMph(cam.InverseTransformDirection(ToUnity(light.Light1Dir)));
            light.Light2Dir = ToMph(cam.InverseTransformDirection(ToUnity(light.Light2Dir)));
            light.ViewDir = OV3.UnitZ;
            light.ViewRight = -OV3.UnitX;
            light.ViewUp = OV3.UnitY;
            return light;
        }

        // ---- damage / healing (called from Patches) ----
        public void OnHurt(int ukDamage, bool invincible)
        {
            int dmg = Mathf.Max(1, Mathf.RoundToInt(ukDamage * Plugin.DamageTakenScale.Value));
            Hurt(dmg, $"ULTRAKILL damage {ukDamage}");
        }

        // energy damage Samus does to herself (her missiles), in MPH energy
        public void SelfDamage(int energy, string what) => Hurt(energy, what);

        private readonly System.Random _afflictRng = new System.Random();   // disrupted aim

        private void Hurt(int energy, string what, bool burn = false)
        {
            if (_nm.dead || ULTRAKILL.Cheats.Invincibility.Enabled) { return; }
            int taken = burn ? Sim.TakeBurnDamage(energy) : Sim.TakeDamage(energy);
            if (taken <= 0) { return; }
            if (!burn) { Plugin.Event($"hurt: {what} -> energy -{taken}, now {Sim.Health}"); }
            DamageIndicator();
            if (!burn) { Audio.Play("DAMAGE1"); }
            if (_cc != null) { _cc.CameraShake(Mathf.Max(taken * 0.01f, 0.05f) * 4); }
            if (Sim.Dead)
            {
                Audio.StopAll();
                Audio.Play("DIE");
                _ball?.Reset();   // ULTRAKILL's death camera needs its camera + the standing collider back
                // hand over to ULTRAKILL's death + checkpoint flow
                _reentry = true;
                _nm.hp = 1;
                _nm.GetHurt(9999, false, 0, false, false, 0.35f, true);
                _reentry = false;
            }
        }

        public bool Reentry => _reentry;

        // HUD damage indicator: every direction blinks on a hit (owner; ULTRAKILL damage has no direction)
        private void DamageIndicator()
        {
            Hud.Damaged();
        }

        // PlayerSound.UpdateHealthSfx: the low-energy alarm loops while energy is below 25
        private const int LowEnergy = 25;
        private static string _alarmSound;
        private void UpdateAlarm()
        {
            if (_alarmSound == null) { _alarmSound = SoundBank.NameAt(AlarmSample) ?? "ENERGY_ALARM"; }
            bool alarm = !_nm.dead && Sim.Health > 0 && Sim.Health < LowEnergy;
            if (alarm) { Audio.StartLoop(_alarmSound); }
            else { Audio.StopLoop(_alarmSound); }
        }
        private const int AlarmSample = 37;   // SfxId ENERGY_ALARM (testbed MphEnergyAlarm)

        // ---- water, lava, jump pads, ULTRAKILL launches ----
        // ---- TUNABLE CONSTANTS (MPH sounds; SfxId / DGN ids) ----
        private const int JumpPadSample = 61;      // SfxId JUMP_PAD
        private const int LavaDamageDgn = 25;
        private const int BurnGunEffect = 188;     // flamingGun      // SfxId DGN_LAVA_DAMAGE (PlayerSound.UpdateBurningSfx)
        private const float BurnDecay = 0.9354f;   // PlayerSound: x0.875 per DS frame -> per 60 Hz step
        private readonly Collider[] _probe = new Collider[16];
        private bool _onLava;
        private float _burnAmount;
        private readonly string[] _burnSounds = new string[4];

        // Samus's center (the ball's) inside an ULTRAKILL Water volume (layer 4; visuals-only water doesn't count)
        private bool CenterInWater()
        {
            if (_capsule == null) { return false; }
            Vector3 c = _capsule.transform.TransformPoint(_capsule.center);
            int n = Physics.OverlapSphereNonAlloc(c, 0.05f, _probe, 1 << 4, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                Water w = _probe[i].GetComponentInParent<Water>();
                if (w != null && !w.visualsOnly) { return true; }
            }
            return false;
        }

        // touching ULTRAKILL lava (a burning hurt zone) = MPH's lava floor: its burning sound, wet steps. ULTRAKILL's
        // zone does the damage and the bounce out (NewMovement.Launch -> OnLaunch).
        private bool TouchingLava()
        {
            if (_capsule == null) { return false; }
            Vector3 c = _capsule.transform.TransformPoint(_capsule.center);
            float r = _capsule.radius * _capsule.transform.lossyScale.y + 0.15f;
            float half = Mathf.Max(0, _capsule.height * _capsule.transform.lossyScale.y / 2 - r) + 0.15f;
            int n = Physics.OverlapCapsuleNonAlloc(c - Up * half, c + Up * half, r, _probe, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                Collider col = _probe[i];
                HurtZone hz = col.attachedRigidbody != null ? col.attachedRigidbody.GetComponent<HurtZone>() : null;
                if (hz == null) { hz = col.GetComponentInParent<HurtZone>(); }
                if (hz != null && hz.enabled && (hz.damageType == EnviroDamageType.Burn || hz.damageType == EnviroDamageType.WeakBurn))
                {
                    return true;
                }
            }
            return false;
        }

        // PlayerSound.UpdateBurningSfx (DGN loop at full amount while on lava, decaying after). Under water ULTRAKILL
        // muffles everything, MPH sounds included (they play through its mixer); the testbed's BUBBLES ambience was
        // dropped (owner: it sounds like lava).
        private void UpdateLiquidSounds()
        {
            bool lava = TouchingLava();
            if (lava != _onLava)
            {
                _onLava = lava;
                Plugin.Event(lava ? "touching lava" : "off lava");
            }
            _burnAmount = lava || Sim.BurnTimer > 0 ? 0xFFFF : _burnAmount * BurnDecay;   // (on fire too: UpdateBurningSfx)
            if (_burnAmount < 50) { _burnAmount = 0; }
            int count = Mathf.Min(DgnSounds.EntryCount(LavaDamageDgn), _burnSounds.Length);
            for (int i = 0; i < _burnSounds.Length; i++)
            {
                DgnResult d = _burnAmount > 0 && i < count ? DgnSounds.Eval(LavaDamageDgn, i, _burnAmount, 0) : default;
                string want = d.Volume > 0 ? SoundName(d.Sample) : null;
                if (_burnSounds[i] != null && _burnSounds[i] != want) { Audio.StopLoop(_burnSounds[i]); }
                _burnSounds[i] = want;
                if (want != null) { Audio.SetLoop(want, d.Volume, d.Pitch, raw: true); }
            }
        }

        // ULTRAKILL's JumpPad set the body's velocity (Unity units/s): MPH ActivateJumpPad with it
        public void OnJumpPad(Vector3 velocity)
        {
            if (Sim == null || _nm.dead || velocity.sqrMagnitude < 1) { return; }   // (a pad spawned on her: no push)
            if (Sim.ActivateJumpPad(WorldToSim(velocity) / (30 * K)))
            {
                string n = SoundName(JumpPadSample);
                if (n != null) { Audio.Play(n); }
                Plugin.Event($"jump pad: {velocity.magnitude:F1} u/s");
            }
        }

        // ULTRAKILL fire (FireZone: Streetcleaner flames, napalm) sets Samus on fire, like a Magmaul hit in MPH
        public void Ignite(string what)
        {
            if (Sim == null || _nm.dead) { return; }
            if (Sim.BurnTimer == 0) { Plugin.Event($"set on fire by {what}"); }
            Sim.Ignite();
        }

        // NewMovement.Launch (lava bounces, LaunchPlayer triggers, blast knockback, ...): the resulting velocity
        // (Unity units/s) becomes Samus's speed, carried like a rocket jump until she lands
        public void OnLaunch(Vector3 velocity, string what)
        {
            if (Sim == null || _nm.dead) { return; }
            Sim.SetLaunchSpeed(WorldToSim(velocity) / (30 * K));
            Plugin.Event($"launched by {what}: {velocity.magnitude:F1} u/s");
        }

        // PlayerPusher: a velocity change on top of the current speed
        public void OnPush(Vector3 velocityChange)
        {
            if (Sim == null || _nm.dead) { return; }
            OnLaunch(SimToWorld(Sim.Speed) * (30 * K) + velocityChange, "PlayerPusher");
        }

        // HUD sway input: how far the view turned this step (degrees left, up), first person only
        private float _lastYaw, _lastPitch;
        private bool _haveLastAim;
        private void UpdateHudSway()
        {
            float yaw = _cc.rotationY, pitch = _cc.rotationX;
            float aimX = 0, aimY = 0;
            if (_haveLastAim && Sim.FirstPerson && !BallCameraActive)
            {
                aimX = -Mathf.DeltaAngle(_lastYaw, yaw);
                aimY = Mathf.DeltaAngle(_lastPitch, pitch);
            }
            _lastYaw = yaw;
            _lastPitch = pitch;
            _haveLastAim = true;
            Hud.AddAim(aimX, aimY);
        }

        public void OnRespawn()
        {
            Plugin.Event("respawn");
            Sim.Health = Sim.HealthMax;
            _ball?.Reset();
            Sim.Spawn();
            Sim.MissileAmmo = Sim.MissileAmmoMax;   // checkpoints refill
            Sim.UaAmmo = Sim.UaAmmoMax;
            _beams.Clear();
            WorldFx.Clear();
            BallFx.Clear();
            _gunFx.Clear();
            _muzzleFx = _chargeFx = default;
            Audio.StopAll();
            Audio.Play("PLAYER_SPAWN", 96 / 127f);
        }

        // ---- items (PlayerProcess.PickUpItems / PickUpWeapon; MphItem) ----
        // ---- TUNABLE CONSTANTS (MPH pickups) ----
        private static readonly int[] HealthAmounts = { 30, 60, 100 };   // _healthPickupAmounts: HealthMedium, HealthSmall, HealthBig
        private const int AmmoSmall = 100, AmmoBig = 250;                 // story amounts (raw: 10 per missile)
        private const float DuckRecover = 3f;                             // music duck: fade back over 3 s

        public Vector3 BallCenter => _ball != null ? _ball.BallCenter : transform.position;
        // MPH's player position: 0.5 above the feet (MphRead PlayerEntity.Position)
        public Vector3 MphPosition
        {
            get
            {
                if (_capsule == null) { return transform.position; }
                BottomSphere(out _, out _, out _, out float feetY);
                Vector3 p = _capsule.transform.TransformPoint(_capsule.center);
                return new Vector3(p.x, feetY + MphFeetBelow * K, p.z);
            }
        }

        // true = picked up (MPH picks everything up, even at full)
        public bool PickUp(MphRead.ItemType t)
        {
            switch (t)
            {
                case MphRead.ItemType.HealthMedium:
                case MphRead.ItemType.HealthSmall:
                case MphRead.ItemType.HealthBig:
                    Heal(HealthAmounts[(int)t], t == MphRead.ItemType.HealthSmall ? "POWER_UP1" : "POWER_UP2");
                    return true;
                case MphRead.ItemType.UASmall:
                case MphRead.ItemType.UABig:
                case MphRead.ItemType.MissileSmall:
                case MphRead.ItemType.MissileBig:
                {
                    bool big = t == MphRead.ItemType.UABig || t == MphRead.ItemType.MissileBig;
                    int amount = big ? AmmoBig : AmmoSmall;
                    if (t == MphRead.ItemType.UASmall || t == MphRead.ItemType.UABig) { Sim.UaAmmo = Mathf.Min(Sim.UaAmmo + amount, Sim.UaAmmoMax); }
                    else { Sim.GiveMissiles(amount); }
                    Audio.Play(big ? "AMMO_POWER_UP2" : "AMMO_POWER_UP1");
                    Plugin.Event($"picked up {t}: UA {Sim.UaAmmo}/{Sim.UaAmmoMax}, missiles {Sim.MissileAmmo}/{Sim.MissileAmmoMax}");
                    return true;
                }
                default:
                    return true;
            }
        }

        private static void Message(string text)
        {
            if (MonoSingleton<HudMessageReceiver>.TryGetInstance(out HudMessageReceiver h) && h != null) { h.SendHudMessage(text); }
        }

        // music duck on major pickups: down to level, held, then back over DuckRecover seconds
        private float _duckBase = -1, _duckLevel = 1, _duckHold;
        private void DuckMusic(float level, float hold)
        {
            if (!MonoSingleton<AudioMixerController>.TryGetInstance(out AudioMixerController amc) || amc == null) { return; }
            if (_duckBase < 0) { _duckBase = amc.musicVolume; }
            _duckLevel = Mathf.Min(_duckLevel, level);
            _duckHold = Mathf.Max(_duckHold, hold);
            amc.SetMusicVolume(_duckBase * _duckLevel);
        }

        private void UpdateMusicDuck()
        {
            if (_duckBase < 0 || !MonoSingleton<AudioMixerController>.TryGetInstance(out AudioMixerController amc) || amc == null) { return; }
            if (_duckHold > 0) { _duckHold -= Time.unscaledDeltaTime; }
            else { _duckLevel = Mathf.MoveTowards(_duckLevel, 1, Time.unscaledDeltaTime / DuckRecover); }
            amc.SetMusicVolume(_duckBase * _duckLevel);
            if (_duckLevel >= 1) { _duckBase = -1; }
        }

        public void Heal(int amount, string sound)
        {
            Sim.GainHealth(amount);
            Plugin.Event($"healed +{amount}, now {Sim.Health}");
            Audio.Play(sound);
        }

        // ---- coordinate conversion (mirror X) ----
        public static OV3 ToMph(Vector3 v) => new OV3(-v.x, v.y, v.z);
        public static Vector3 ToUnity(OV3 v) => new Vector3(-v.X, v.Y, v.Z);
    }
}
