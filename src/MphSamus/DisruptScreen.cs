// DisruptScreen - the Volt Driver's disruption on Samus's view: MphRead's shift shader (Renderer: _shiftShaderProgramId,
// Shaders.ShiftFragmentShader) redone without a custom shader - the frame is copied as 192 horizontal bands, each
// shifted sideways by MPH's shift table (a triangle wave over 64 entries, odd bands half a wave apart, scrolling every
// 1/30 s), scaled by SamusSim.HudDisruptionFactor; uncovered edges are black.
using UnityEngine;

namespace MphSamus
{
    public sealed class DisruptScreen : MonoBehaviour
    {
        // ---- MPH values (Renderer shift table / shader) ----
        private const int Bands = 192;
        private static readonly float[] ShiftTable = BuildTable();

        public SamusController Controller;
        private float _elapsed;
        private bool _logged;

        private static float[] BuildTable()
        {
            var t = new float[64];
            for (int i = 0; i < 64; i++)
            {
                int val = (i & 32) != 0 ? 31 - (i & 31) : i & 31;
                t[i] = -(val - 16) / 256f;
            }
            return t;
        }

        // (GPU region copies, not a drawn mesh: the first version's GL quads came out black - culled by winding / depth)
        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            float factor = Controller != null && Controller.Sim != null ? Controller.Sim.HudDisruptionFactor : 0;
            if (factor <= 0 || SystemInfo.copyTextureSupport == UnityEngine.Rendering.CopyTextureSupport.None)
            {
                Graphics.Blit(src, dst);
                return;
            }
            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"disruption drawn on {name} ({src.width}x{src.height} {src.format})");
            }
            _elapsed += Time.unscaledDeltaTime;
            float div = _elapsed / (1 / 30f);
            int shiftIdx = (int)div;
            float lerp = div % 1;
            int w = src.width, h = src.height;
            RenderTexture tmp = RenderTexture.GetTemporary(src.descriptor);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = tmp;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = prev;
            for (int band = 0; band < Bands; band++)
            {
                int index = (band + shiftIdx + band % 2 * 32) % 64;
                float shift = Mathf.Lerp(ShiftTable[index], ShiftTable[(index + 1) % 64], lerp) * factor;
                int dx = Mathf.RoundToInt(-shift * w);
                // band 0 is the top row (texture y counts up from the bottom)
                int y0 = h - (band + 1) * h / Bands, y1 = h - band * h / Bands;
                int bw = w - Mathf.Abs(dx), bh = y1 - y0;
                if (bw <= 0 || bh <= 0) { continue; }
                Graphics.CopyTexture(src, 0, 0, Mathf.Max(0, -dx), y0, bw, bh, tmp, 0, 0, Mathf.Max(0, dx), y0);
            }
            Graphics.Blit(tmp, dst);
            RenderTexture.ReleaseTemporary(tmp);
        }
    }
}
