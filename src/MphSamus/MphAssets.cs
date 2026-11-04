// MphAssets - Unity objects built from the ROM cache (main thread only): textures/sprites for the MPH message box and
// target circle, audio clips, and the shader used for MPH models.
using System;
using System.Collections.Generic;
using MphSamus.Core;
using UnityEngine;

namespace MphSamus
{
    public static class MphAssets
    {
        public static bool Loaded { get; private set; }
        private static bool _failed;

        public static Sprite MessageBox;   // 9-slice MPH message box (hud_msgBox brackets + fill)
        public static Texture2D TargetCircle;
        public static Shader ModelShader;
        public static bool ShaderWritesDepth;
        // depth pre-pass for shaders without depth writes (Sprites/Default): opaque MPH meshes are drawn once with
        // Unlit/Texture (writes depth, opaque queue), then lit with the vertex-color shader at equal depth, so only
        // the nearest surface shows (playtest 1: the arm cannon's inner/back faces showed through)
        public static Shader DepthShader;

        private static readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private static readonly Dictionary<string, float> _clipVolumes = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _loopStarts = new Dictionary<string, float>();

        public static void EnsureLoaded()
        {
            if (Loaded || _failed)
            {
                return;
            }
            try
            {
                Texture2D boxTex = ToTexture(HudArt.BuildSliceableMessageBox(), "MphMsgBox");
                int bd = HudArt.SliceBorder;
                MessageBox = Sprite.Create(boxTex, new Rect(0, 0, boxTex.width, boxTex.height), new Vector2(0.5f, 0.5f), 100, 0,
                    SpriteMeshType.FullRect, new Vector4(bd, bd, bd, bd));
                try
                {
                    HudStrip circle = HudArt.LoadObject(@"_archives\localSamus\hud_targetcircle.bin");
                    TargetCircle = ToTexture(HudArt.Frame(circle, 0), "MphTargetCircle");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"target circle: {ex.Message}");
                }
                PickShader();
                Loaded = true;
                Plugin.Log.LogInfo("MPH assets ready");
            }
            catch (Exception ex)
            {
                _failed = true;
                Plugin.Log.LogError($"MPH assets failed: {ex}");
            }
        }

        // MPH images are top-down RGBA; Unity textures are bottom-up. Point filtering for the DS look.
        public static Texture2D ToTexture(RgbaImage img, string name, TextureWrapMode wrapU = TextureWrapMode.Clamp,
            TextureWrapMode wrapV = TextureWrapMode.Clamp)
        {
            var t = new Texture2D(img.Width, img.Height, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Point };
            t.wrapModeU = wrapU;
            t.wrapModeV = wrapV;
            var flipped = new byte[img.Pixels.Length];
            int row = img.Width * 4;
            for (int y = 0; y < img.Height; y++)
            {
                Buffer.BlockCopy(img.Pixels, y * row, flipped, (img.Height - 1 - y) * row, row);
            }
            t.LoadRawTextureData(flipped);
            t.Apply(false, false);
            UnityEngine.Object.DontDestroyOnLoad(t);
            return t;
        }

        // Shaders the game ships that do texture x vertex color. Models are drawn with CPU-sorted triangles, so a
        // shader without depth writes still looks right.
        private static readonly string[] ShaderCandidates =
        {
            "Particles/Standard Unlit",
            "Legacy Shaders/Particles/Alpha Blended Premultiply",
            "Sprites/Default",
            "UI/Default",
            "Legacy Shaders/Particles/Alpha Blended",
            "Unlit/Transparent",
            "Unlit/Texture",
        };

        // shaders load with levels (Addressables): look again when Samus spawns if the menu had none of the good ones
        public static void RefreshShader()
        {
            if (ModelShader == null || ModelShader.name != ShaderCandidates[0]) { PickShader(); }
        }

        private static void PickShader()
        {
            var found = new List<string>();
            foreach (string n in ShaderCandidates)
            {
                if (Shader.Find(n) != null) { found.Add(n); }
            }
            Plugin.Log.LogInfo($"Shaders available for MPH models: {(found.Count == 0 ? "none" : string.Join(", ", found))}");
            string forced = Plugin.GunShader.Value;
            if (!string.IsNullOrEmpty(forced) && Shader.Find(forced) != null)
            {
                ModelShader = Shader.Find(forced);
            }
            else if (found.Count > 0)
            {
                ModelShader = Shader.Find(found[0]);
            }
            else
            {
                Shader master = MonoSingleton<DefaultReferenceManager>.Instance?.masterShader;
                ModelShader = master;
                Plugin.Log.LogWarning($"No unlit vertex-color shader found; using {(master ? master.name : "nothing")}");
            }
            ShaderWritesDepth = ModelShader != null && ModelShader.name == "Particles/Standard Unlit";
            DepthShader = ShaderWritesDepth ? null : Shader.Find("Unlit/Texture");
            Plugin.Log.LogInfo($"MPH depth pre-pass shader: {(DepthShader != null ? DepthShader.name : "none (triangle sorting only)")}");
            if (ModelShader != null)
            {
                Plugin.Log.LogInfo($"MPH model shader: {ModelShader.name}");
            }
        }

        public static Material NewModelMaterial(Texture tex, bool translucent)
        {
            var m = new Material(ModelShader) { mainTexture = tex };
            if (ModelShader != null && ModelShader.name == "Particles/Standard Unlit")
            {
                // _Mode 1 = cutout (alpha test) with depth writes; translucent materials blend without depth writes
                m.SetFloat("_Mode", translucent ? 2 : 1);
                m.SetFloat("_Cull", 0);
                m.SetFloat("_ZWrite", translucent ? 0 : 1);
                m.SetFloat("_Cutoff", 0.5f);
                m.SetFloat("_SrcBlend", translucent ? (float)UnityEngine.Rendering.BlendMode.SrcAlpha : (float)UnityEngine.Rendering.BlendMode.One);
                m.SetFloat("_DstBlend", translucent ? (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : (float)UnityEngine.Rendering.BlendMode.Zero);
                if (translucent) { m.EnableKeyword("_ALPHABLEND_ON"); } else { m.EnableKeyword("_ALPHATEST_ON"); }
                m.renderQueue = translucent ? 3000 : 2450;
            }
            return m;
        }

        public static Material NewDepthMaterial(Texture tex)
        {
            return DepthShader != null ? new Material(DepthShader) { mainTexture = tex, renderQueue = 2000 } : null;
        }

        // ---- audio ----
        public static AudioClip Clip(string name, out float volume, out float loopStartSeconds)
        {
            volume = 1;
            loopStartSeconds = -1;
            if (_clips.TryGetValue(name, out AudioClip c))
            {
                volume = _clipVolumes[name];
                loopStartSeconds = _loopStarts[name];
                return c;
            }
            PcmSound s = Plugin.Rom == Plugin.RomStatus.Ready ? SoundBank.Get(name) : null;
            if (s == null)
            {
                _clips[name] = null;
                _clipVolumes[name] = 1;
                _loopStarts[name] = -1;
                return null;
            }
            c = AudioClip.Create("mph_" + name, s.Samples.Length, 1, s.SampleRate, false);
            c.SetData(s.Samples, 0);
            UnityEngine.Object.DontDestroyOnLoad(c);
            _clips[name] = c;
            _clipVolumes[name] = s.Volume;
            _loopStarts[name] = s.Loop ? s.LoopStart / (float)s.SampleRate : -1;
            volume = s.Volume;
            loopStartSeconds = _loopStarts[name];
            return c;
        }
    }
}
