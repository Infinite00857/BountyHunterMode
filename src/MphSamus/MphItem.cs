// MphItem - an MPH item in the world (MphRead ItemInstanceEntity): health, UA / missile ammo, weapons, energy tanks,
// missile / UA expansions. Model Metadata.Items[type]; spins 0.35 turns/s and bobs by (sin(spin) + 1) / 8
// (SpinningEntityBase); drops vanish after 450 DS frames (ItemSpawnEntity.SpawnItemDrop), placed items stay.
// Hum: ItemInstanceEntity._sfxIds (33 small items, 34 tanks) on SND3DLIST range 7 (linear fade, silent past 6.1
// MPH units; testbed). Picked up when Samus touches it (PlayerProcess.PickUpItems range test); what it gives is
// SamusController.PickUp.
using MphRead;
using MphSamus.Core;
using UnityEngine;
using MphItemType = MphRead.ItemType;

namespace MphSamus
{
    public sealed class MphItem : MonoBehaviour
    {
        // ---- TUNABLE CONSTANTS (MPH items) ----
        public const float DropLifetime = 450 / 30f;          // SpawnItemDrop despawnTime: 450 DS frames = 15 s
        private const float SpinTurnsPerSec = 0.35f;
        private const float ItemRadius = 0.35f;               // ItemInstanceEntity volume (MPH units)
        private const float PickupReach = 0.45f;              // PickUpItems: item radius + this, laterally
        private const float HumRange = 6.1f;                  // SND3DLIST range 7 (MPH units)
        // ---- TUNABLE CONSTANTS (owner: charging pulls items in, like the Prime games; MPH units) ----
        private const float PullRange = 15f;                  // items this close ...
        private const float PullCone = 0.8f;                  // ... and in front (cos of the angle off the aim)
        private const float PullSpeedMin = 6f, PullSpeedMax = 18f;   // per second, growing with the charge
        private static readonly int[] HumSamples = { 33, 33, 33, -1, 34, -1, 34, -1, -1, -1, -1, -1, -1, 33, 33, 33, 33, -1, -1, -1, -1, -1 };

        public MphItemType Type { get; private set; }
        private ModelView _view;
        private float _age, _lifetime, _spin;
        private Vector3 _home;
        private AudioSource _hum;

        // lifetime: seconds until it vanishes (0 = never)
        public static MphItem Spawn(MphItemType type, Vector3 pos, float lifetime)
        {
            SamusController c = SamusController.Current;
            if (c == null || type == MphItemType.None || (int)type >= Metadata.Items.Count) { return null; }
            var go = new GameObject($"MPH {type}");
            go.transform.position = pos;
            var item = go.AddComponent<MphItem>();
            item.Type = type;
            item._home = pos;
            item._lifetime = lifetime;
            item._spin = Random.Range(0f, 360f);
            string model = Metadata.Items[(int)type];
            try
            {
                ModelInstance inst = Read.GetModelInstance(model);
                inst.SetAnimation(0, AnimFlags.None);
                item._view = new ModelView(new PosedModel(inst), model, go.transform, 0) { Scale = c.K };
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"{model}: {ex.Message}");
            }
            int hum = HumSamples[(int)type];
            string humName = hum >= 0 ? SamusController.SoundName(hum) : null;
            float vol = 1;
            AudioClip clip = humName != null ? MphAssets.Clip(humName, out vol, out _) : null;
            if (clip != null)
            {
                item._hum = go.AddComponent<AudioSource>();
                item._hum.clip = clip;
                item._hum.loop = true;
                item._hum.spatialBlend = 1;
                item._hum.rolloffMode = AudioRolloffMode.Linear;
                item._hum.minDistance = 0.01f;
                item._hum.maxDistance = HumRange * c.K;
                item._hum.volume = vol * Plugin.SfxVolume.Value;
                item._hum.outputAudioMixerGroup = c.Audio.Group;
                item._hum.Play();
            }
            return item;
        }

        private void Update()
        {
            SamusController c = SamusController.Current;
            if (c == null) { Destroy(gameObject); return; }
            float dt = Time.deltaTime;
            if (dt <= 0) { return; }
            _age += dt;
            if (_lifetime > 0 && _age > _lifetime) { Destroy(gameObject); return; }
            Pull(c, dt);
            _spin = (_spin + SpinTurnsPerSec * 360 * dt) % 360f;
            float bob = (Mathf.Sin(_spin * Mathf.Deg2Rad) + 1) / 8f * c.K;
            transform.position = _home + Vector3.up * bob;
            transform.rotation = Quaternion.Euler(0, _spin, 0);
            Draw(c);
            if (Touching(c) && c.PickUp(Type)) { Destroy(gameObject); }
        }

        // while a beam charges, items in front of Samus drift to her
        private void Pull(SamusController c, float dt)
        {
            Camera cam = c.Cam;
            if (cam == null || !c.Sim.Charging || c.Sim.InBall || c.Sim.Dead) { return; }
            Vector3 target = c.MphPosition + Vector3.up * 0.3f * c.K;
            Vector3 to = target - _home;
            float d = to.magnitude;
            if (d < 0.01f || d > PullRange * c.K) { return; }
            Vector3 fromEye = (_home - cam.transform.position).normalized;
            if (Vector3.Dot(cam.transform.forward, fromEye) < PullCone) { return; }
            float speed = Mathf.Lerp(PullSpeedMin, PullSpeedMax, c.Sim.ChargeFraction) * c.K;
            _home += to / d * Mathf.Min(d, speed * dt);
        }

        // PickUpItems: the ball's sphere within reach, or the biped within reach laterally and between its pickup heights
        private bool Touching(SamusController c)
        {
            if (c.Sim.Dead) { return false; }
            float k = c.K, reach = (ItemRadius + PickupReach) * k;
            if (c.Sim.InBall)
            {
                return (transform.position - c.BallCenter).sqrMagnitude < reach * reach;
            }
            Vector3 between = transform.position - c.MphPosition;
            var lateral = new Vector3(between.x, 0, between.z);
            return lateral.sqrMagnitude < reach * reach
                && between.y >= Fixed.ToFloat(SamusSim.V.MinPickupHeight) * k && between.y <= Fixed.ToFloat(SamusSim.V.MaxPickupHeight) * k;
        }

        private void Draw(SamusController c)
        {
            if (_view == null) { return; }
            Camera cam = c.Cam;
            var light = MphLighting.Default;
            if (cam != null)
            {
                light.ViewDir = SamusController.ToMph(transform.InverseTransformDirection(cam.transform.forward));
                light.ViewRight = SamusController.ToMph(transform.InverseTransformDirection(cam.transform.right));
                light.ViewUp = SamusController.ToMph(transform.InverseTransformDirection(cam.transform.up));
            }
            if ((int)(_age * 30) % 2 == 0) { _view.Model.Instance.UpdateAnimFrames(); }
            _view.Model.Pose(OpenTK.Mathematics.Matrix4.Identity, light);
            Vector3 camLocal = cam != null ? transform.InverseTransformPoint(cam.transform.position) : Vector3.zero;
            _view.Upload(camLocal, 10);
        }

        private void OnDestroy()
        {
            _view?.Destroy();
        }
    }
}
