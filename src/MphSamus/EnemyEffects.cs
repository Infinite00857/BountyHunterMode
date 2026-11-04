// EnemyEffects - what Samus's weapons do to ULTRAKILL enemies beyond damage (owner's weapon integration, 2026-11-01):
// - Judicator: chill - the enemy turns icy blue and slows down (JudicatorChill; ULTRAKILL has no freeze, the slow goes
//   through EnemyIdentifier.UpdateModifiers -> totalSpeedModifier, which enemy scripts read);
// - Magmaul: ignite - ULTRAKILL's own burning (EnemyIdentifier.StartBurning / Flammable.Burn, like an igniting
//   Explosion) and gasoline on the floor;
// - Volt Driver / Shock Coil: electrocution like the red nailgun's Zapper (hitter "zapper" + Electricity, a jolt up,
//   zap sparks on every limb) and ULTRAKILL's Aftershock chain.
using UnityEngine;

namespace MphSamus
{
    public static class EnemyEffects
    {
        // ---- TUNABLE CONSTANTS (ULTRAKILL values) ----
        private const float IgniteHeat = 4;            // Flammable.Burn heat (environment fire / Explosion damage / 10)
        private const float ZapForce = 1000;           // Zapper.Zap: Vector3.up * 1000
        private const float AftershockDelay = 0.5f;    // EnemyIdentifier.DelayedAfterShock

        public static void Ignite(EnemyIdentifier eid)
        {
            if (eid == null || eid.dead) { return; }
            if (eid.flammables != null && eid.flammables.Count > 0) { eid.StartBurning(IgniteHeat); }
            else
            {
                Flammable f = eid.GetComponentInChildren<Flammable>();
                if (f != null) { f.Burn(IgniteHeat); }
            }
        }

        // fire on gasoline stains around a point (Grenade.Explode: StainVoxelManager.TryIgniteAt)
        public static void IgniteGround(Vector3 pos)
        {
            if (MonoSingleton<StainVoxelManager>.TryGetInstance(out StainVoxelManager svm) && svm != null) { svm.TryIgniteAt(pos, 3); }
        }

        // Zapper.Zap: electrocute (big hit + sparks on every limb)
        public static void Electrocute(EnemyIdentifier eid, Collider limb, Vector3 point, float damage)
        {
            if (eid == null || eid.dead) { return; }
            eid.hitter = "zapper";
            eid.hitterAttributes.Add(HitterAttribute.Electricity);
            eid.DeliverDamage(limb != null ? limb.gameObject : eid.gameObject, Vector3.up * ZapForce, point, damage, true, 0, null);
            Sparks(eid, 0.5f);
        }

        public static void Sparks(EnemyIdentifier eid, float scale)
        {
            GameObject spark = MonoSingleton<DefaultReferenceManager>.Instance != null ? MonoSingleton<DefaultReferenceManager>.Instance.zapImpactParticle : null;
            if (spark == null || eid == null) { return; }
            foreach (EnemyIdentifierIdentifier part in eid.GetComponentsInChildren<EnemyIdentifierIdentifier>())
            {
                Object.Instantiate(spark, part.transform.position, Quaternion.identity).transform.localScale *= scale;
            }
        }

        public static void Spark(Vector3 pos)
        {
            GameObject spark = MonoSingleton<DefaultReferenceManager>.Instance != null ? MonoSingleton<DefaultReferenceManager>.Instance.zapImpactParticle : null;
            if (spark != null) { Object.Instantiate(spark, pos, Quaternion.identity); }
        }

        // EnemyIdentifier.DelayedAfterShock without the nails/water requirement: after half a second the hit enemy
        // zaps the enemies around it (EnemyIdentifier.Zap: grounded ones within 30 units in sight, electric lines)
        public static void Aftershock(EnemyIdentifier eid)
        {
            if (eid == null) { return; }
            Transform c = eid.GetCenter();
            Aftershock a = new GameObject("MPH Volt Driver aftershock").AddComponent<Aftershock>();
            a.delay = AftershockDelay;
            a.transform.position = c != null ? c.position : eid.transform.position;
            a.waterOnly = false;
            a.ZapFrom(eid);
        }
    }

    // Judicator chill: icy blue + slowed while it lasts (refreshed by more hits)
    public sealed class JudicatorChill : MonoBehaviour
    {
        // ---- TUNABLE CONSTANTS ----
        private static readonly Color Ice = new Color(0.55f, 0.78f, 1.25f, 1f);
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private EnemyIdentifier _eid;
        private float _left;
        private bool _active;
        private Renderer[] _renderers;
        private Color[] _original;
        private MaterialPropertyBlock _block;

        public static void Apply(EnemyIdentifier eid, float seconds)
        {
            if (eid == null || eid.dead || seconds <= 0) { return; }
            if (!eid.TryGetComponent(out JudicatorChill c))
            {
                c = eid.gameObject.AddComponent<JudicatorChill>();
                c.Begin(eid);
            }
            c._left = Mathf.Max(c._left, seconds);
        }

        // EnemyIdentifier.UpdateModifiers postfix (Patches): chilled enemies move at this fraction
        public static float SpeedFactor(EnemyIdentifier eid) =>
            eid.TryGetComponent(out JudicatorChill c) && c._active ? Mathf.Clamp(Plugin.JudicatorSlow.Value, 0.05f, 1) : 1;

        private void Begin(EnemyIdentifier eid)
        {
            _eid = eid;
            _active = true;
            _block = new MaterialPropertyBlock();
            var list = new System.Collections.Generic.List<Renderer>();
            foreach (Renderer r in eid.GetComponentsInChildren<Renderer>())
            {
                if ((r is SkinnedMeshRenderer || r is MeshRenderer) && r.sharedMaterial != null && r.sharedMaterial.HasProperty(ColorId)) { list.Add(r); }
            }
            _renderers = list.ToArray();
            _original = new Color[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++) { _original[i] = _renderers[i].sharedMaterial.GetColor(ColorId); }
            Tint(true);
            eid.UpdateBuffs(false, false);   // re-runs UpdateModifiers (and tells the enemy scripts: UpdateBuff)
            Plugin.Event($"{eid.enemyType} chilled");
        }

        private void Tint(bool on)
        {
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer r = _renderers[i];
                if (r == null) { continue; }
                r.GetPropertyBlock(_block);
                _block.SetColor(ColorId, on ? _original[i] * Ice : _original[i]);
                r.SetPropertyBlock(_block);
            }
        }

        private void LateUpdate()
        {
            _left -= Time.deltaTime;
            if (_active && (_left > 0 && !_eid.dead)) { Tint(true); return; }   // (other systems reset property blocks)
            End();
        }

        private void End()
        {
            if (!_active) { return; }
            _active = false;
            Tint(false);
            if (_eid != null) { _eid.UpdateBuffs(false, false); }
            Destroy(this);
        }

        private void OnDestroy()
        {
            if (_active)
            {
                _active = false;
                Tint(false);
            }
        }
    }
}
