// SamusHud - draws Samus's MPH helmet HUD (Core.HudLayout: helmet + visor layers, energy meter + tanks, weapon
// icon, target circle, Morph Ball bombs/boost row, damage indicators, MPH font) with IMGUI, plus a light red
// damage flash (playtest 1). Art comes from the ROM via Core.HudImages, uploaded once.
using System.Collections.Generic;
using MphSamus.Core;
using UnityEngine;

namespace MphSamus
{
    public sealed class SamusHud : MonoBehaviour
    {
        public SamusController Controller;
        private const float IceAlpha = 9 / 16f;   // PlayerHud: Layer4 (bg_ice) alpha while frozen
        private Texture2D _ice;
        private bool _iceTried;

        // PlayerHud ice layer (frozen by the Judicator): bg_ice over the view, mirrored into the four quarters
        private void DrawIceLayer(float sw, float sh)
        {
            if (!_iceTried)
            {
                _iceTried = true;
                try { _ice = MphAssets.ToTexture(HudArt.LoadLayer(@"_archives\common\bg_ice.bin", rows: 32, startX: 16, cols: 32), "mph_ice"); }
                catch (System.Exception ex) { Plugin.Log.LogWarning($"ice layer: {ex.Message}"); }
            }
            if (_ice == null) { return; }
            Color old = GUI.color;
            GUI.color = new Color(1, 1, 1, IceAlpha);
            float hw = sw / 2, hh = sh / 2;
            GUI.DrawTextureWithTexCoords(new Rect(0, 0, hw, hh), _ice, new Rect(0, 0, 1, 1), true);
            GUI.DrawTextureWithTexCoords(new Rect(hw, 0, hw, hh), _ice, new Rect(1, 0, -1, 1), true);
            GUI.DrawTextureWithTexCoords(new Rect(0, hh, hw, hh), _ice, new Rect(0, 1, 1, -1), true);
            GUI.DrawTextureWithTexCoords(new Rect(hw, hh, hw, hh), _ice, new Rect(1, 1, -1, -1), true);
            GUI.color = old;
        }

        private static HudImages _images;
        private static Texture2D[] _textures;
        private static bool _tried;
        private readonly List<HudDraw> _draws = new List<HudDraw>();
        private readonly HudLayout.Input _input = new HudLayout.Input();

        private static void EnsureLoaded()
        {
            if (_tried) { return; }
            _tried = true;
            var errors = new List<string>();
            _images = HudImages.Load(errors);
            foreach (string e in errors) { Plugin.Log.LogWarning($"HUD art: {e}"); }
            _textures = new Texture2D[_images.All.Count];
            for (int i = 0; i < _textures.Length; i++)
            {
                _textures[i] = MphAssets.ToTexture(_images.All[i], "mph_hud_" + i);
            }
            Plugin.Log.LogInfo($"HUD art: {_images.All.Count} images");
        }

        private void OnGUI()
        {
            if (Controller == null || Controller.Sim == null || Event.current.type != EventType.Repaint)
            {
                return;
            }
            if (MonoSingleton<OptionsManager>.TryGetInstance(out OptionsManager om) && om != null && om.paused)
            {
                return;
            }
            if (Controller.HudHidden) { return; }   // riding an elevator
            EnsureLoaded();
            SamusSim sim = Controller.Sim;
            _input.Health = sim.Health;
            _input.HealthMax = sim.HealthMax;
            _input.InBall = sim.InBall;
            _input.FirstPerson = sim.FirstPerson;
            Vector2 rv = Controller != null ? Controller.ReticleViewport() : new Vector2(0.5f, 0.5f);
            _input.ReticleX = rv.x;
            _input.ReticleY = 1 - rv.y;
            _input.ThirdReticle = sim.ThirdView && sim.CamSwitchTimer >= sim.CamSwitchSteps;
            _input.Alive = sim.Health > 0 && !Controller.IsDead;
            _input.BombAmmo = sim.BombAmmo;
            _input.BoostReady = sim.AltAttackCooldown == 0;
            _input.BoostFlashSteps = sim.BoostHudSteps;
            _input.Weapon = (int)sim.CurrentWeapon;
            _input.WeaponIconSteps = sim.WeaponIconSteps;
            _input.AmmoShots = sim.AmmoShots;
            _input.AmmoRaw = sim.AmmoRaw;
            _input.AmmoMax = sim.AmmoMax;
            _input.WheelOpen = sim.WheelOpen;
            _input.WheelSelection = sim.WheelSelection;
            _input.WheelX = sim.WheelX;
            _input.WheelY = sim.WheelY;
            for (int i = 0; i < 8; i++)
            {
                _input.WheelWeapon[i] = (int)SamusSim.WeaponOrder[i];
                _input.WheelOwned[i] = sim.Has(SamusSim.WeaponOrder[i]);
            }
            float sw = Screen.width, sh = Screen.height;
            HudLayout.Build(_draws, _images, Controller.Hud, _input, sw / sh);
            Color old = GUI.color;
            if (sim.FrozenGfxTimer > 0 && !sim.InBall) { DrawIceLayer(sw, sh); }
            foreach (HudDraw d in _draws)
            {
                Texture2D t = _textures[d.Image];
                float tw = t.width, th = t.height;
                // images are uploaded bottom-up (MphAssets.ToTexture flips rows)
                var uv = new Rect(d.SrcX / tw, 1 - (d.SrcY + d.SrcH) / th, d.SrcW / tw, d.SrcH / th);
                GUI.color = new Color(d.R, d.G, d.B, d.A);
                GUI.DrawTextureWithTexCoords(new Rect(d.X * sw, d.Y * sh, d.W * sw, d.H * sh), t, uv, true);
            }
            GUI.color = old;
            if (sim.DamageFlash > 0)
            {
                GUI.color = new Color(1, 0, 0, 0.25f * sim.DamageFlash / 20f);
                GUI.DrawTexture(new Rect(0, 0, sw, sh), Texture2D.whiteTexture);
                GUI.color = old;
            }
        }
    }
}
