// MphSpawnMenu - a METROID PRIME HUNTERS section in ULTRAKILL's spawner arm menu with MPH's health and ammo pickups.
// Each entry is a SpawnableObject whose template carries MphSpawnPoint: the arm instantiates it where you aim and
// MphSpawnPoint swaps itself for the MPH item. Icons are the MPH models rendered once into sprites.
using System.Collections.Generic;
using HarmonyLib;
using MphRead;
using MphSamus.Core;
using UnityEngine;
using MphItemType = MphRead.ItemType;

namespace MphSamus
{
    public static class MphSpawnMenu
    {
        // ---- TUNABLE CONSTANTS (spawner menu) ----
        private const string Section = "METROID PRIME HUNTERS";
        private const int IconSize = 128;
        private const int IconLayer = 31;
        // model yaw in the icon (MPH models face -Z; the icon camera looks from -Z)
        private const float ItemIconYaw = 200;
        private static readonly Color ItemColor = new Color(0.55f, 0.85f, 0.55f);

        private static readonly (MphItemType type, string name, string desc)[] Items =
        {
            (MphItemType.HealthSmall, "Energy (60)", "Restores 60 energy."),
            (MphItemType.HealthMedium, "Energy (30)", "Restores 30 energy."),
            (MphItemType.HealthBig, "Energy (100)", "Restores 100 energy."),
            (MphItemType.MissileSmall, "Missiles", "10 missiles."),
            (MphItemType.MissileBig, "Missiles (big)", "25 missiles."),
            (MphItemType.UASmall, "Universal Ammo", "10 units of Universal Ammo."),
            (MphItemType.UABig, "Universal Ammo (big)", "25 units of Universal Ammo."),
        };

        private static SpawnableObject[] _entries;
        private static int _iconSlot;

        private static SpawnableObject[] Entries()
        {
            if (_entries != null && System.Array.TrueForAll(_entries, e => e != null && e.gridIcon != null)) { return _entries; }
            var list = new List<SpawnableObject>();
            foreach ((MphItemType type, string name, string desc) in Items)
            {
                list.Add(Entry($"mph.item.{type}", name, "MPH ITEM", desc, Metadata.Items[(int)type], 0, 0, ItemColor,
                    p => p.Item = type));
            }
            _entries = list.ToArray();
            return _entries;
        }

        private static SpawnableObject Entry(string id, string name, string type, string desc, string model, int recolor,
            int anim, Color bg, System.Action<MphSpawnPoint> setup, float yaw = ItemIconYaw)
        {
            var template = new GameObject($"MPH spawn {name}");
            template.SetActive(false);
            Object.DontDestroyOnLoad(template);
            MphSpawnPoint point = template.AddComponent<MphSpawnPoint>();
            setup(point);
            var preview = new GameObject($"MPH preview {name}");
            preview.SetActive(false);
            Object.DontDestroyOnLoad(preview);
            SpawnableObject so = ScriptableObject.CreateInstance<SpawnableObject>();
            so.hideFlags = HideFlags.DontUnloadUnusedAsset;   // ULTRAKILL unloads unused assets on scene loads
            so.identifier = id;
            so.objectName = name;
            so.type = type;
            so.description = desc;
            so.spawnableObjectType = SpawnableObject.SpawnableObjectDataType.Object;
            so.gameObject = template;
            so.preview = preview;
            so.backgroundColor = bg;
            so.gridIcon = RenderIcon(model, recolor, anim, yaw);
            point.Source = so;
            return so;
        }

        // the model on its own layer far below the level, framed by a temporary camera
        private static Sprite RenderIcon(string model, int recolor, int anim, float yaw)
        {
            GameObject root = null;
            Camera cam = null;
            RenderTexture rt = null;
            try
            {
                ModelInstance inst = Read.GetModelInstance(model);
                inst.SetAnimation(anim < inst.Model.AnimationGroups.Node.Count ? anim : 0, AnimFlags.None);
                root = new GameObject("MPH icon");
                root.transform.position = new Vector3(_iconSlot++ * 100, -10000, 0);   // its own spot
                root.transform.rotation = Quaternion.Euler(0, yaw, 0);
                var view = new ModelView(new PosedModel(inst, recolor), model, root.transform, IconLayer);
                view.Model.Pose(OpenTK.Mathematics.Matrix4.Identity, MphLighting.Default);
                view.Upload(new Vector3(0, 0, -10), 10);
                var bounds = new Bounds(root.transform.position, Vector3.zero);
                bool any = false;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
                {
                    if (!any) { bounds = r.bounds; any = true; } else { bounds.Encapsulate(r.bounds); }
                }
                float size = Mathf.Max(bounds.extents.magnitude, 0.01f);
                cam = new GameObject("MPH icon camera").AddComponent<Camera>();
                cam.enabled = false;
                cam.cullingMask = 1 << IconLayer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.fieldOfView = 30;
                cam.nearClipPlane = size * 0.05f;
                cam.farClipPlane = size * 20;
                cam.transform.position = bounds.center + new Vector3(0, 0.25f, -1).normalized * size / Mathf.Tan(15 * Mathf.Deg2Rad) * 1.05f;
                cam.transform.LookAt(bounds.center);
                view.Upload(root.transform.InverseTransformPoint(cam.transform.position), 10);
                rt = RenderTexture.GetTemporary(IconSize, IconSize, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(IconSize, IconSize, UnityEngine.TextureFormat.ARGB32, false);
                tex.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
                tex.Apply();
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                RenderTexture.active = prev;
                view.Destroy();
                Sprite sprite = Sprite.Create(tex, new Rect(0, 0, IconSize, IconSize), new Vector2(0.5f, 0.5f));
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return sprite;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"spawn menu icon {model}: {ex.Message}");
                return null;
            }
            finally
            {
                if (cam != null) { cam.targetTexture = null; Object.Destroy(cam.gameObject); }
                if (rt != null) { RenderTexture.ReleaseTemporary(rt); }
                // (Destroy waits for the end of the frame, and every icon renders in the same frame)
                if (root != null) { root.SetActive(false); Object.Destroy(root); }
            }
        }

        [HarmonyPatch(typeof(SpawnMenu), nameof(SpawnMenu.RebuildMenu))]
        private static class Rebuild
        {
            private static void Postfix(SpawnMenu __instance)
            {
                if (Plugin.Rom != Plugin.RomStatus.Ready) { return; }
                try
                {
                    AccessTools.Method(typeof(SpawnMenu), "CreateButtons").Invoke(__instance, new object[] { Entries(), Section });
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"spawn menu: {ex}");
                }
            }
        }
    }

    // placed by the spawner arm: becomes the MPH item. The arm also makes stripped preview copies of the
    // template (their components are destroyed at the end of the frame), so this waits a frame before acting.
    public sealed class MphSpawnPoint : MonoBehaviour
    {
        public MphItemType Item = MphItemType.None;
        public SpawnableObject Source;
        private int _frames;
        private bool _waiting;

        private void Update()
        {
            if (++_frames < 2) { return; }
            SamusController c = SamusController.Current;
            // placed while playing as V1: MPH items need Samus (her scale) - they appear when Samus Mode is back on
            if (c == null)
            {
                if (!_waiting)
                {
                    _waiting = true;
                    Plugin.Event($"{Item} placed - appears when Samus Mode is on");
                }
                return;
            }
            if (Item != MphItemType.None)
            {
                MphItem.Spawn(Item, transform.position + Vector3.up * 0.6f * c.K, 0);
                Plugin.Event($"spawned {Item}");
            }
            Destroy(gameObject);
        }
    }
}
