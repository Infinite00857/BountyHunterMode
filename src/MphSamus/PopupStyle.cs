// PopupStyle - ULTRAKILL's HUD text popups (HudMessageReceiver) drawn with Metroid Prime Hunters' message box:
// the hud_msgBox brackets (PlayerDialog.DrawDialogs) as a 9-slice frame, the box is half transparent
// (DIALOG_ALPHA 0.5) and the text uses the MPH dialog green. ULTRAKILL's typing text, opening scale effect and
// timing are kept.
using System.Collections;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MphSamus
{
    public static class PopupStyle
    {
        // ---- TUNABLE CONSTANTS (MPH dialog look; testbed mph_hud.cpp) ----
        private const float BoxAlpha = 0.5f;                                          // DIALOG_ALPHA
        private static readonly Color TextColor = new Color(0x78 / 255f, 0xF8 / 255f, 0x40 / 255f);   // dialog palette 0
        private const float DsFrame = 1 / 30f;
        private const float PixelScale = 1 / 3f;                                      // 1 DS pixel = 3 canvas units

        private class Saved : MonoBehaviour
        {
            public Sprite Sprite;
            public Image.Type Type;
            public float Ppu = 1;
            public Color ImageColor, TextColor;
            public bool Styled;
        }

        public static bool Active => (Plugin.SamusMode || Plugin.PopupStyleAlways.Value) && MphAssets.Loaded;

        private static void Apply(HudMessageReceiver r)
        {
            Image img = r.GetComponent<Image>();
            TMP_Text text = r.text != null ? r.text : r.GetComponentInChildren<TMP_Text>();
            if (img == null) { return; }
            Saved saved = r.GetComponent<Saved>() ?? r.gameObject.AddComponent<Saved>();
            if (!saved.Styled)
            {
                saved.Sprite = img.sprite;
                saved.Type = img.type;
                saved.Ppu = img.pixelsPerUnitMultiplier;
                saved.ImageColor = img.color;
                saved.TextColor = text != null ? text.color : Color.white;
            }
            if (Active)
            {
                img.sprite = MphAssets.MessageBox;
                img.type = Image.Type.Sliced;
                img.pixelsPerUnitMultiplier = PixelScale;
                img.color = new Color(1, 1, 1, BoxAlpha);
                if (text != null) { text.color = TextColor; }
                saved.Styled = true;
            }
            else if (saved.Styled)
            {
                img.sprite = saved.Sprite;
                img.type = saved.Type;
                img.pixelsPerUnitMultiplier = saved.Ppu;
                img.color = saved.ImageColor;
                if (text != null) { text.color = saved.TextColor; }
                saved.Styled = false;
            }
        }

        [HarmonyPatch(typeof(HudMessageReceiver), "Start")]
        private static class StartPatch
        {
            private static void Postfix(HudMessageReceiver __instance) => Apply(__instance);
        }

        [HarmonyPatch(typeof(HudMessageReceiver), "ShowHudMessage")]
        private static class ShowPatch
        {
            // ULTRAKILL's HudOpenEffect already scales the box open (playtest 1: the MPH frame swap fought it)
            private static void Postfix(HudMessageReceiver __instance) => Apply(__instance);
        }
    }
}
