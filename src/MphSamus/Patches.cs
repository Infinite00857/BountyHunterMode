// Patches - Harmony hooks that switch V1 off while Samus is active. Kept few and narrow (game updates break them).
using HarmonyLib;
using UnityEngine;

namespace MphSamus
{
    internal static class Patches
    {
        // V1 movement: Samus's simulation drives the Rigidbody instead
        [HarmonyPatch(typeof(NewMovement), "Update")]
        private static class NmUpdate { private static bool Prefix() => !SamusController.IsActive; }

        [HarmonyPatch(typeof(NewMovement), "FixedUpdate")]
        private static class NmFixedUpdate { private static bool Prefix() => !SamusController.IsActive; }

        // V1 weapons, arms and whiplash
        [HarmonyPatch(typeof(GunControl), "Update")]
        private static class GunUpdate { private static bool Prefix() => !SamusController.IsActive; }

        [HarmonyPatch(typeof(FistControl), "Update")]
        private static class FistUpdate { private static bool Prefix() => !SamusController.IsActive; }

        [HarmonyPatch(typeof(HookArm), "Update")]
        private static class HookUpdate { private static bool Prefix() => !SamusController.IsActive; }

        // Morph Ball: the ball camera drives the camera (not mouse-steerable, as on the DS)
        [HarmonyPatch(typeof(CameraController), "LateUpdate")]
        private static class CamLateUpdate
        {
            private static bool Prefix(CameraController __instance, out Vector2 __state)
            {
                __state = new Vector2(__instance.rotationX, __instance.rotationY);
                return !(SamusController.Current?.BallCameraActive ?? false);
            }

            // the weapon camera (arm cannon, layer 13) draws at MPH's 78 degrees whatever ULTRAKILL's FOV setting is, so
            // the arm cannon looks as in MPH (ULTRAKILL's weapon camera sits at 90 and eases back when Samus leaves).
            // While the weapon wheel is open the mouse moves its cursor, not the view.
            private static void Postfix(CameraController __instance, Camera ___hudCamera, Vector2 __state)
            {
                SamusController cur = SamusController.Current;
                if (cur != null && !cur.WheelOpen && cur.Sim != null && cur.Sim.Zoomed)
                {
                    // UpdateAimX/Y while zoomed: the mouse turns slower (MPH zoom sensitivity)
                    float sens = cur.Sim.ZoomSensitivity;
                    __instance.rotationX = __state.x + Mathf.DeltaAngle(__state.x, __instance.rotationX) * sens;
                    __instance.rotationY = __state.y + Mathf.DeltaAngle(__state.y, __instance.rotationY) * sens;
                    __instance.ApplyRotations();
                }
                // (also frozen by the Judicator: MPH ignores all input, the aim included)
                if (SamusController.Current != null && (SamusController.Current.WheelOpen || (SamusController.Current.Sim?.Frozen ?? false)))
                {
                    __instance.rotationX = __state.x;
                    __instance.rotationY = __state.y;
                    __instance.ApplyRotations();
                }
                if (SamusController.Current != null && !SamusController.Current.BallCameraActive)
                {
                    SamusController.Current.ApplyPointerView(__instance);
                }
                if (SamusController.Current != null && ___hudCamera != null)
                {
                    ___hudCamera.fieldOfView = SamusController.GunCameraFov;
                }
            }
        }

        // Judicator chill: chilled enemies move slower (enemy scripts read totalSpeedModifier)
        [HarmonyPatch(typeof(EnemyIdentifier), "UpdateModifiers")]
        private static class EidModifiers
        {
            private static void Postfix(EnemyIdentifier __instance) { __instance.totalSpeedModifier *= JudicatorChill.SpeedFactor(__instance); }
        }

        // damage -> Samus's energy (MPH TakeDamage); lethal damage is passed back to ULTRAKILL's death flow
        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHurt))]
        private static class GetHurt
        {
            private static bool Prefix(NewMovement __instance, int damage, bool invincible)
            {
                SamusController c = SamusController.Current;
                if (c == null || c.Reentry || __instance.dead || __instance.levelOver) { return true; }
                if (ULTRAKILL.Cheats.Invincibility.Enabled) { return false; }
                if (Plugin.DebugEvents.Value) { Plugin.Event($"hurt by {Caller("GetHurt")}"); }
                c.OnHurt(damage, invincible);
                if (FireHurt.Active) { c.Ignite("fire"); }
                return false;
            }
        }

        // first game method up the stack that isn't the patched one itself (for the playtest log)
        private static string Caller(string patched)
        {
            foreach (System.Diagnostics.StackFrame f in new System.Diagnostics.StackTrace(2, false).GetFrames() ?? new System.Diagnostics.StackFrame[0])
            {
                System.Reflection.MethodBase m = f.GetMethod();
                if (m == null || m.Name.Contains(patched) || m.DeclaringType?.DeclaringType == typeof(Patches)) { continue; }
                return $"{m.DeclaringType?.Name}.{m.Name}";
            }
            return "?";
        }

        // ---- terminals: ULTRAKILL's screen zones (shops, level screens) lower V1's gun while you look at them ----
        // ScreenZone.UpdatePlayerState(active) runs NoWeapon / ShopMode; Samus lowers her arm cannon for the same zones.
        internal static class ScreenZones
        {
            private static readonly System.Collections.Generic.HashSet<ScreenZone> _active = new System.Collections.Generic.HashSet<ScreenZone>();
            public static bool Active
            {
                get
                {
                    _active.RemoveWhere(z => z == null || !z.isActiveAndEnabled);
                    return _active.Count > 0;
                }
            }
            public static void Set(ScreenZone z, bool on)
            {
                if (on) { _active.Add(z); } else { _active.Remove(z); }
            }
        }

        [HarmonyPatch(typeof(ScreenZone), nameof(ScreenZone.UpdatePlayerState))]
        private static class ScreenZoneState
        {
            private static void Postfix(ScreenZone __instance, bool ___touchMode) => ScreenZones.Set(__instance, ___touchMode);
        }

        // ---- portals (Fraud): ULTRAKILL tracks the player through portals by the camera's anchor ----
        // NewMovement.travellerPosition is the camera's default target, which is Samus's third-person / Morph Ball camera
        // when that's on: it crossed portals late (or not at all) and NewMovement.OnTravel then placed the body from the
        // camera. Samus is tracked by her eye instead, and the camera is put back at the eye before the travel.
        [HarmonyPatch(typeof(NewMovement), "travellerPosition", MethodType.Getter)]
        private static class TravellerPosition
        {
            private static bool Prefix(ref Vector3 __result)
            {
                SamusController c = SamusController.Current;
                if (!SamusController.IsActive || c == null || c.TravelPoint == Vector3.zero) { return true; }
                __result = c.TravelPoint;
                return false;
            }
        }

        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.OnTravel))]
        private static class Travel
        {
            private static void Prefix() => SamusController.Current?.CameraToEye();

            private static void Postfix(bool? __result, ULTRAKILL.Portal.PortalTravelDetails details)
            {
                if (__result == true) { SamusController.Current?.OnPortalTravel(details.enterToExit); }
            }
        }

        // ---- ULTRAKILL's fake fall (8-4): Samus's controller does FakeFallZone's height control itself ----
        [HarmonyPatch(typeof(FakeFallZone), "FixedUpdate")]
        private static class FakeFall
        {
            private static bool Prefix() => !SamusController.IsActive;
        }

        // ULTRAKILL fire zones (Streetcleaner flames, napalm): the damage they deal sets Samus on fire
        [HarmonyPatch(typeof(FireZone), "OnTriggerStay")]
        private static class FireHurt
        {
            public static bool Active;
            private static void Prefix() => Active = true;
            private static void Postfix() => Active = false;
        }

        // NewMovement.Launch (lava bounces, LaunchPlayer triggers, blast knockback, enemy launches; LaunchFromPoint and
        // LaunchFromPointAtSpeed end here): V1's resulting velocity, given to Samus's sim
        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.Launch))]
        private static class Launch
        {
            private static bool Prefix(NewMovement __instance, Vector3 direction, float multiplier, bool ignoreMass)
            {
                SamusController c = SamusController.Current;
                if (!SamusController.IsActive || c == null) { return true; }
                if (((bool)__instance.groundProperties && !__instance.groundProperties.launchable)
                    || (direction == Vector3.down && __instance.gc != null && __instance.gc.onGround)
                    || direction.sqrMagnitude <= 0)
                {
                    return false;
                }
                float mass = ignoreMass || __instance.rb == null ? 1 : Mathf.Max(0.01f, __instance.rb.mass);
                c.OnLaunch(Vector3.ClampMagnitude(direction, 1000f) * multiplier / mass, Caller("Launch"));
                return false;
            }
        }

        // ULTRAKILL jump pads: the pad sets the body's velocity, then MPH's jump pad takes it (+ the JUMP_PAD sound)
        [HarmonyPatch(typeof(JumpPad), "OnTriggerEnter")]
        private static class JumpPadEnter
        {
            private static void Postfix(JumpPad __instance, Collider other)
            {
                SamusController c = SamusController.Current;
                if (!SamusController.IsActive || c == null || other == null || other.gameObject.isStatic) { return; }
                NewMovement nm = MonoSingleton<NewMovement>.Instance;
                if (nm == null || nm.rb == null || other.attachedRigidbody != nm.rb) { return; }
                // (owner) MPH's JUMP_PAD sound replaces the pad's own for Samus
                AudioSource aud = Traverse.Create(__instance).Field("aud").GetValue<AudioSource>();
                if (aud != null) { aud.Stop(); }
                // only Samus's body (a bigger player trigger reaching the pad first played the sound while near it)
                if (other != c.BodyCollider || nm.rb.isKinematic) { return; }
                c.OnJumpPad(nm.rb.velocity);
            }
        }

        // PlayerPusher: an instant velocity change
        [HarmonyPatch(typeof(PlayerPusher), "OnTriggerEnter")]
        private static class Pusher
        {
            private static bool Prefix(PlayerPusher __instance, Collider other)
            {
                SamusController c = SamusController.Current;
                if (!SamusController.IsActive || c == null) { return true; }
                NewMovement nm = MonoSingleton<NewMovement>.Instance;
                if (nm == null || other.gameObject != nm.gameObject || (__instance.oneTime && __instance.activated)) { return false; }
                __instance.activated = true;
                c.OnPush(__instance.force);
                return false;
            }
        }

        // no blood healing / hard damage for Samus (enemies drop MPH health pickups instead)
        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHealth))]
        private static class GetHealth { private static bool Prefix() => !SamusController.IsActive; }

        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.ForceAntiHP))]
        private static class AntiHp { private static bool Prefix() => !SamusController.IsActive; }

        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.FullHeal))]
        private static class FullHeal
        {
            private static void Postfix()
            {
                SamusController c = SamusController.Current;
                if (c != null) { c.Sim.Health = c.Sim.HealthMax; }
            }
        }

        [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.Respawn))]
        private static class Respawn
        {
            private static void Postfix() => SamusController.Current?.OnRespawn();
        }

        // enemy deaths drop MPH health and ammo
        [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.Death), typeof(bool))]
        private static class EnemyDeath
        {
            private static void Prefix(EnemyIdentifier __instance, out bool __state) => __state = __instance.dead;

            private static void Postfix(EnemyIdentifier __instance, bool __state)
            {
                if (!__state && __instance.dead && SamusController.IsActive) { MphDrops.OnKilled(__instance); }
            }
        }
    }
}
