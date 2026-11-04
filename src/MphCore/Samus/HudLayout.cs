// HudLayout - Samus's MPH helmet HUD as a list of textured rectangles, engine-agnostic (the plugin draws them with
// IMGUI, MphSim rasterizes them to PNG). Port of the testbed's hunters/mph_hud.cpp (playtested), which follows
// MphRead (MIT) PlayerHud.cs (DrawHudObjects, DrawHealthbars, DrawMeter, DrawBoostBombs, UpdateHealthbars,
// UpdateDamageIndicators) and HUD/HudInfo.cs (Samus HudObjects). Positions are DS top-screen pixels (256x192): x is
// spread across the screen width as MphRead does, sizes use a uniform DS-pixel scale so the art keeps its shape.
// Output rectangles are in screen fractions (0..1, y down).
//
// Not MPH (owner decisions): the reticle stays at the screen center (CENTER CROSSHAIR); a hit lights every damage
// indicator direction (ULTRAKILL damage has no direction; owner playtest 2026-10-31).
using System;
using System.Collections.Generic;

namespace MphSamus.Core
{
    public struct HudDraw
    {
        public int Image;                     // index into HudImages.All
        public float SrcX, SrcY, SrcW, SrcH;  // source rectangle, image pixels (y down)
        public float X, Y, W, H;              // destination, screen fractions (y down)
        public float R, G, B, A;
        public bool FlipH, FlipV;
    }

    public sealed class HudImages
    {
        public readonly List<RgbaImage> All = new List<RgbaImage>();
        public readonly List<int> FrameWidths = new List<int>();
        public int White = -1, Helmet = -1, HelmetDrop = -1, Visor = -1, WeaponIcon = -1, AmmoBar = -1, Reticle = -1, Boost = -1, Bombs = -1;
        public int Font = -1, Damage = -1, DamageT = -1;
        public readonly int[] EnergyBar = { -1, -1, -1 }, Etank = { -1, -1, -1 };
        public HudArt.HudFont? FontData;
        public List<HudArt.DamageQuad>[]? DamageQuads;

        private int Add(RgbaImage img, int frameWidth)
        {
            All.Add(img);
            FrameWidths.Add(frameWidth > 0 ? frameWidth : img.Width);
            return All.Count - 1;
        }

        private int AddStrip(HudStrip s) => Add(s.Image, s.FrameWidth);

        // everything from the ROM cache; pieces that fail stay -1 (not drawn). errors: what failed.
        public static HudImages Load(List<string> errors)
        {
            var h = new HudImages();
            var white = new RgbaImage(1, 1);
            for (int i = 0; i < 4; i++) { white.Pixels[i] = 255; }
            h.White = h.Add(white, 0);   // untextured rectangles (weapon wheel)
            void Try(string what, Action a)
            {
                try { a(); }
                catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
            }
            Try("helmet", () => h.Helmet = h.Add(HudArt.LoadLayer(HudArt.Files.Helmet), 0));
            Try("helmet drop", () => h.HelmetDrop = h.Add(HudArt.LoadLayer(HudArt.Files.HelmetDrop), 0));
            Try("visor", () => h.Visor = h.Add(HudArt.LoadLayer(HudArt.Files.Visor, 32), 0));
            for (int p = 0; p < 3; p++)
            {
                int pal = p;
                Try($"energy bar {p}", () => h.EnergyBar[pal] = h.AddStrip(HudArt.LoadObject(HudArt.Files.EnergyBar, pal)));
                Try($"etank {p}", () => h.Etank[pal] = h.AddStrip(HudArt.LoadObject(HudArt.Files.EnergyTanks, pal)));
            }
            Try("weapon icon", () => h.WeaponIcon = h.AddStrip(HudArt.LoadObject(HudArt.Files.WeaponIcon)));
            Try("ammo bar", () => h.AmmoBar = h.AddStrip(HudArt.LoadObject(HudArt.Files.AmmoBar)));
            Try("reticle", () => h.Reticle = h.AddStrip(HudArt.LoadObject(HudArt.Files.TargetCircle)));
            Try("boost", () => h.Boost = h.AddStrip(HudArt.LoadObject(HudArt.Files.Boost)));
            Try("bombs", () => h.Bombs = h.AddStrip(HudArt.LoadObject(HudArt.Files.Bombs)));
            Try("font", () =>
            {
                h.FontData = HudArt.LoadFont();
                h.Font = h.Add(h.FontData.Atlas, 8);
            });
            Try("damage indicator", () =>
            {
                h.DamageQuads = HudArt.LoadDamageIndicator(out RgbaImage? tex);
                if (tex != null)
                {
                    h.Damage = h.Add(tex, 0);
                    var t = new RgbaImage(tex.Height, tex.Width);   // transposed copy for the rotated pieces
                    for (int y = 0; y < tex.Height; y++)
                    {
                        for (int x = 0; x < tex.Width; x++)
                        {
                            Buffer.BlockCopy(tex.Pixels, (y * tex.Width + x) * 4, t.Pixels, (x * tex.Height + y) * 4, 4);
                        }
                    }
                    h.DamageT = h.Add(t, 0);
                }
            });
            return h;
        }
    }

    // HUD state that animates at 60 Hz (PlayerHud.UpdateHealthbars / UpdateBoostBombs / UpdateDamageIndicators).
    public sealed class HudState
    {
        public float BoostBombsY = 208;       // bomb/boost row: slides up to 160 in the ball
        public float HealthYOffset = 32;      // HealthOffsetY (+ HealthOffsetYAlt in the ball)
        public int Palette;                   // 0 normal, 1 just healed, 2 low energy / just damaged
        private bool _changedColor;
        public float ReticleExpand;           // 0..1, grows while charging
        public readonly int[] DamageTimers = new int[8];   // north, ne, east, se, south, sw, west, nw
        public int Frame;
        public float ShiftX, ShiftY;          // HUD sway (DS pixels, -8..8)
        private readonly float[] _pastAimX = new float[8], _pastAimY = new float[8];

        // ---- TUNABLE CONSTANTS (HUD timing; MphRead values at 60 Hz) ----
        private const int LowHealth = 25;
        private const int HealedPaletteSteps = 10 * 2, DamagedPaletteSteps = 6 * 2;
        private const float ReticleGrowPerStep = 1 / 8f;   // testbed CHARGE_RETICLE_GROW
        public const int DamageIndicatorSteps = 63 * 2;   // MPH: blinks for this long
        public const int DamageFlashSteps = 15;           // owner: one flash per hit instead (0.25 s)

        public void Step(SamusSim sim)
        {
            Frame++;
            float bbTarget = sim.InBall ? 160 : 208;
            if (BoostBombsY > bbTarget) { BoostBombsY -= 1; } else if (BoostBombsY < bbTarget) { BoostBombsY += 1; }
            float hTarget = 32 + (sim.InBall ? -10 : 0);
            if (HealthYOffset > hTarget) { HealthYOffset -= 0.5f; } else if (HealthYOffset < hTarget) { HealthYOffset += 0.5f; }
            if (sim.Health < LowHealth)
            {
                if (!_changedColor) { Palette = 2; _changedColor = true; }
            }
            else if (sim.TimeSinceHeal < HealedPaletteSteps)
            {
                if (!_changedColor) { Palette = 1; _changedColor = true; }
            }
            else if (sim.TimeSinceDamage < DamagedPaletteSteps)
            {
                if (!_changedColor) { Palette = 2; _changedColor = true; }
            }
            else if (_changedColor)
            {
                Palette = 0;
                _changedColor = false;
            }
            ReticleExpand = Math.Clamp(ReticleExpand + (sim.ChargeFraction > 0 ? ReticleGrowPerStep : -ReticleGrowPerStep), 0, 1);
            for (int i = 0; i < 8; i++) { if (DamageTimers[i] > 0) { DamageTimers[i]--; } }
        }

        // owner: every direction flashes once on a hit (MPH blinks the hit side; ULTRAKILL damage has no direction)
        public void Damaged()
        {
            for (int i = 0; i < 8; i++) { DamageTimers[i] = DamageFlashSteps; }
        }

        // PlayerInput.UpdateHudShift: the HUD lags the aim - average of this and the last 7 frames' aim change
        // (aimX = degrees turned left, aimY = degrees turned up, per 60 Hz step), rounded, clamped to 8 DS pixels
        public void AddAim(float aimX, float aimY)
        {
            float sumX = 0, sumY = 0;
            for (int i = 6; i >= 0; i--)
            {
                sumX += _pastAimX[i];
                _pastAimX[i + 1] = _pastAimX[i];
                sumY += _pastAimY[i];
                _pastAimY[i + 1] = _pastAimY[i];
            }
            _pastAimX[0] = aimX;
            _pastAimY[0] = aimY;
            ShiftX = Math.Clamp(MathF.Round((sumX + aimX) / 8), -8, 8);
            ShiftY = Math.Clamp(-MathF.Round((sumY + aimY) / 8), -8, 8);
        }
    }

    public static class HudLayout
    {
        // ---- TUNABLE CONSTANTS (MPH Samus HUD; DS pixels unless noted, testbed mph_hud.cpp / HudInfo.cs) ----
        private const float DsW = 256, DsH = 192;
        private const float HelmetOpacity = 1, VisorOpacity = 0.5f, HudOpacity = 1;   // MphRead Features defaults
        private const float HealthPosX = 93, HealthPosY = -5;
        private const int HealthMeterLength = 72;
        private const float HealthTextX = 0, HealthTextY = -8, HealthLabelX = 30, HealthLabelY = -8;
        private const float WeaponIconX = 214, WeaponIconY = 150;
        // hud_weaponicon frames (AnimParams expanded by duration): 0-8 = icon per beam, 9-27 = the switch spin
        private static readonly int[] WeaponIconFrames = { 0, 1, 2, 10, 11, 12, 13, 14, 15,
            3, 3, 3, 7, 7, 7, 3, 3, 3, 9, 9, 9, 4, 5, 6, 7, 7, 8, 8 };
        private const int WeaponIconAnimStart = 9, WeaponIconAnimEnd = 27;
        public static int WeaponIconFrame(int beam) => WeaponIconFrames[Math.Clamp(beam, 0, 8)];
        private const float AmmoBarX = 236, AmmoBarY = 137;       // HunterObjects[Samus].AmmoBarPos
        private const int AmmoBarLength = 72;                    // AmmoBars[Samus]: vertical
        private const float AmmoTextX = -3, AmmoTextY = 0;        // BarOffset (number, right-aligned)
        private const float WheelR = 56, WheelSlotBox = 30, WheelCursor = 4;   // testbed weapon wheel
        private const float BombX = 244, BombSpacing = 14, BombTextX = 230, BoostX = 29, BbTextDy = 18, BoostDy = -16;
        private static readonly int[] BoostFlashFrames = { 0, 1, 0, 0, 0, 1, 1, 1, 0, 0, 0 };
        private const int EnergyTank = 100;
        private const float TankOffsetX = 1, TankOffsetY = 8, TankSpacing = 6;
        // text colors: health/ammo bar palettes 0/1/2, entry 3 (78F040 green, B8F8A0 pale green, F04078 red)
        private static readonly float[][] PalText =
        {
            new[] { 0x78 / 255f, 0xF8 / 255f, 0x40 / 255f }, new[] { 0xB8 / 255f, 0xF8 / 255f, 0xA0 / 255f },
            new[] { 0xF0 / 255f, 0x40 / 255f, 0x78 / 255f }
        };
        private static readonly float[] HudGreen = { 0.38f, 1.0f, 0.38f };

        public sealed class Input
        {
            public int Health, HealthMax;
            public bool InBall, FirstPerson, Alive = true;
            public bool ThirdReticle;         // F5 view: the target circle at the screen center too
            public float ReticleX = 0.5f, ReticleY = 0.5f;   // target circle (0..1 of the screen, y down): the aim
            public int BombAmmo;
            public bool BoostReady;
            public int BoostFlashSteps;
            public int Weapon;                // BeamType (Power Beam = 0, Missile = 2)
            public int WeaponIconSteps;       // switch spin left (SamusSim.WeaponIconAnimSteps = just switched)
            public int AmmoShots = -1, AmmoRaw, AmmoMax;   // -1 = no ammo bar (Power Beam)
            public bool WheelOpen;            // testbed weapon wheel
            public int WheelSelection = -1;
            public float WheelX, WheelY;
            public readonly int[] WheelWeapon = new int[8];
            public readonly bool[] WheelOwned = new bool[8];
        }

        // aspect = screen width / height
        public static void Build(List<HudDraw> outList, HudImages img, HudState st, Input d, float aspect)
        {
            outList.Clear();
            float px = 1 / DsH;                // one DS pixel in screen-height fractions
            float pxX = px / aspect;           // ... in screen-width fractions (uniform DS-pixel size)
            // HUD sway: layers move by the shift, objects (energy, weapon icon) by half of it (testbed mph_hud.cpp)
            float objShiftX = st.ShiftX / 2, objShiftY = -st.ShiftY / 2;
            void Obj(int image, int frame, float dsX, float dsY, bool center, float alpha, float r = 1, float g = 1, float b = 1)
            {
                if (image < 0) { return; }
                RgbaImage im = img.All[image];
                int fw = img.FrameWidths[image];
                int frames = Math.Max(1, im.Width / fw);
                frame = Math.Clamp(frame, 0, frames - 1);
                float w = fw * pxX, h = im.Height * px;
                float x = dsX / DsW, y = dsY * px;
                if (center) { x -= w / 2; y -= h / 2; }
                outList.Add(new HudDraw { Image = image, SrcX = frame * fw, SrcY = 0, SrcW = fw, SrcH = im.Height, X = x, Y = y, W = w, H = h, R = r, G = g, B = b, A = alpha });
            }
            // MphRead Renderer.DrawHudLayer: centered quad ScaleX screen-widths wide, ScaleY screen-heights tall
            void Layer(int image, float scaleX, float scaleY, float shiftX, float shiftY, float alpha)
            {
                if (image < 0) { return; }
                RgbaImage im = img.All[image];
                float x = (1 - scaleX) / 2 + shiftX / DsW * 0.5f, y = (1 - scaleY) / 2 - shiftY / DsH * 0.5f;
                outList.Add(new HudDraw { Image = image, SrcW = im.Width, SrcH = im.Height, X = x, Y = y, W = scaleX, H = scaleY, R = 1, G = 1, B = 1, A = alpha });
            }
            void Text(float dsX, float dsY, string text, float[] c, bool center = false, bool right = false)
            {
                HudArt.HudFont? f = img.FontData;
                if (f == null || img.Font < 0) { return; }
                float tw = f.TextWidth(text);
                float x = dsX / DsW - (center ? tw / 2 * pxX : right ? tw * pxX : 0);
                foreach (char ch in text)
                {
                    int i = f.Index(ch);
                    if (ch != ' ')
                    {
                        outList.Add(new HudDraw { Image = img.Font, SrcX = i * 8, SrcW = 8, SrcH = 8, X = x, Y = (dsY + f.Offsets[i]) * px, W = 8 * pxX, H = 8 * px, R = c[0], G = c[1], B = c[2], A = HudOpacity });
                    }
                    x += f.Widths[i] * pxX;
                }
            }
            // PlayerHud.DrawMeter: 8-pixel tiles, frame 0 = full ... 8 = empty, left to right
            void Meter(int image, float dsX, float dsY, int amount, int tankAmount, int length, bool vertical = false)
            {
                int tiles = (length + 7) / 8;
                int filled = 100000 * amount / (99000 * tankAmount / length);
                if (filled == 0 && amount > 0) { filled = 1; }
                float x = dsX;
                float step = 8 * pxX * DsW;    // 8 DS pixels, in stretched DS-x units
                float y = dsY;
                void Tile(int frame)
                {
                    Obj(image, frame, x, y, false, HudOpacity);
                    if (vertical) { y -= 8; } else { x += step; }
                }
                for (int i = 0; i < filled / 8 && tiles > 0; i++) { Tile(0); tiles--; }
                if (tiles > 0)
                {
                    Tile(8 - (filled & 7));
                    tiles--;
                    while (tiles-- > 0) { Tile(8); }
                }
            }

            if (d.FirstPerson && d.Alive)
            {
                // back to front: helmet underside, visor lines, helmet rim (DS_W / DS_H tall)
                Layer(img.HelmetDrop, 2, DsW / DsH, -st.ShiftX / 4, -st.ShiftY / 4, HelmetOpacity);
                Layer(img.Visor, 1, DsW / DsH, st.ShiftX, st.ShiftY, VisorOpacity);
                Layer(img.Helmet, 2, DsW / DsH, st.ShiftX, st.ShiftY, HelmetOpacity);
            }
            if (!d.Alive) { return; }

            // DrawBoostBombs: the row slides in from below the screen in the Morph Ball
            {
                float posY = st.BoostBombsY;
                float bx = BombX;
                int bombW = img.Bombs >= 0 ? img.FrameWidths[img.Bombs] : 8;
                for (int i = 3; i > 0; i--)
                {
                    Obj(img.Bombs, d.BombAmmo < i ? 1 : 0, bx - bombW / 2f, posY, false, HudOpacity);
                    bx -= BombSpacing;
                }
                Text(BombTextX, posY + BbTextDy, "BOMBS", HudGreen, center: true);
                int boostFrame = d.BoostReady ? 0 : 1;
                if (d.BoostFlashSteps > 0)
                {
                    float t = d.BoostFlashSteps / (float)SamusSim.BoostHudFlashSteps;
                    boostFrame = BoostFlashFrames[Math.Clamp((int)Math.Round(10 * (1 - t)), 0, 10)];
                }
                int boostW = img.Boost >= 0 ? img.FrameWidths[img.Boost] : 8;
                Obj(img.Boost, boostFrame, BoostX - boostW / 2f, posY + BoostDy, false, HudOpacity);
                Text(BoostX, posY + BbTextDy, "BOOST", HudGreen, center: true);
            }
            if (!d.InBall)
            {
                // weapon icon (HudOnWeaponSwitch plays the spin, frames 9-27, then shows the new weapon's icon)
                int iconFrame = WeaponIconFrames[Math.Clamp(d.Weapon, 0, 8)];
                if (d.WeaponIconSteps > 0)
                {
                    float t = d.WeaponIconSteps / (float)SamusSim.WeaponIconAnimSteps;
                    iconFrame = WeaponIconFrames[WeaponIconAnimStart + (int)Math.Round((WeaponIconAnimEnd - WeaponIconAnimStart) * (1 - t))];
                }
                Obj(img.WeaponIcon, iconFrame, WeaponIconX + objShiftX, WeaponIconY + objShiftY, false, HudOpacity);
                // DrawAmmoBar: only for weapons with an ammo cost; vertical, number = shots left
                if (d.AmmoShots >= 0)
                {
                    float ax = AmmoBarX + objShiftX, ay = AmmoBarY + objShiftY;
                    Meter(img.AmmoBar, ax, ay, d.AmmoRaw, d.AmmoMax + 1, AmmoBarLength, vertical: true);
                    Text(ax + AmmoTextX, ay + AmmoTextY, d.AmmoShots.ToString("00"), PalText[Math.Clamp(st.Palette, 0, 2)], right: true);
                }
                if (d.FirstPerson || d.ThirdReticle)
                {
                    // target circle on the aim (UpdateReticle; screen center without the pointer aim); frames 0-3 = expanded .. contracted
                    Obj(img.Reticle, (int)(st.ReticleExpand * 3 + 0.5f), d.ReticleX * DsW, d.ReticleY * DsH, true, 1);
                }
            }
            // DrawHealthbars (single player): bar = energy above the full tanks, tanks above the bar
            {
                float hx = HealthPosX + objShiftX, hy = HealthPosY + st.HealthYOffset + objShiftY;
                int pal = Math.Clamp(st.Palette, 0, 2);
                int tankCount = d.HealthMax / EnergyTank;
                int filledTanks = 0, remaining = d.Health;
                for (int i = 0; i < tankCount && remaining >= EnergyTank; i++) { filledTanks++; remaining -= EnergyTank; }
                int barAmount = d.Health - filledTanks * EnergyTank;
                Meter(img.EnergyBar[pal] >= 0 ? img.EnergyBar[pal] : img.EnergyBar[0], hx, hy, barAmount, EnergyTank, HealthMeterLength);
                Text(hx + HealthTextX, hy + HealthTextY, barAmount.ToString("00"), PalText[pal]);
                Text(hx + HealthLabelX, hy + HealthLabelY, "ENERGY", PalText[pal]);
                float tx = hx + TankOffsetX;
                for (int i = 0; i < tankCount; i++)
                {
                    Obj(img.Etank[pal] >= 0 ? img.Etank[pal] : img.Etank[0], i < filledTanks ? 0 : 1, tx, hy + TankOffsetY, false, HudOpacity);
                    tx += TankSpacing;
                }
            }
            // testbed weapon wheel (mph_hud.cpp): 8 slots around the center, Power Beam at the top, clockwise
            if (d.WheelOpen && img.White >= 0)
            {
                void Rect(float dsX, float dsY, float dsW, float dsH, float r, float g, float bl, float a)
                {
                    outList.Add(new HudDraw { Image = img.White, SrcW = 1, SrcH = 1, X = dsX / DsW, Y = dsY * px, W = dsW / DsW, H = dsH * px, R = r, G = g, B = bl, A = a });
                }
                Rect(0, 0, DsW, DsH, 0, 0, 0, 0.35f);
                for (int i = 0; i < 8; i++)
                {
                    float ang = i * 45f * MathF.PI / 180;
                    float cx = DsW / 2 + MathF.Sin(ang) * WheelR, cy = DsH / 2 - MathF.Cos(ang) * WheelR;
                    bool sel = i == d.WheelSelection, cur = d.WheelWeapon[i] == d.Weapon;
                    Rect(cx - WheelSlotBox / 2, cy - WheelSlotBox / 2, WheelSlotBox, WheelSlotBox,
                        sel ? HudGreen[0] : 0, sel ? HudGreen[1] : 0.12f, sel ? HudGreen[2] : 0.05f, sel ? 0.45f : cur ? 0.35f : 0.2f);
                    Obj(img.WeaponIcon, WeaponIconFrames[Math.Clamp(d.WheelWeapon[i], 0, 8)], cx, cy, true, d.WheelOwned[i] ? 1 : 0.25f);
                }
                float kx = DsW / 2 + d.WheelX * WheelR * 0.7f, ky = DsH / 2 - d.WheelY * WheelR * 0.7f;
                Rect(kx - WheelCursor / 2, ky - WheelCursor / 2, WheelCursor, WheelCursor, HudGreen[0], HudGreen[1], HudGreen[2], 0.9f);
            }
            // UpdateDamageIndicators (MPH blinks: (timer & 8) != 0); owner: shown for the whole single flash
            if (img.DamageQuads != null && img.Damage >= 0)
            {
                for (int dir = 0; dir < 8; dir++)
                {
                    if (st.DamageTimers[dir] <= 0) { continue; }
                    foreach (HudArt.DamageQuad q in img.DamageQuads[dir])
                    {
                        int image = q.Rotated && img.DamageT >= 0 ? img.DamageT : img.Damage;
                        RgbaImage im = img.All[image];
                        outList.Add(new HudDraw
                        {
                            Image = image,
                            SrcX = q.U0 * im.Width, SrcY = q.V0 * im.Height, SrcW = (q.U1 - q.U0) * im.Width, SrcH = (q.V1 - q.V0) * im.Height,
                            X = q.X0 / DsW, Y = q.Y0 / DsH, W = (q.X1 - q.X0) / DsW, H = (q.Y1 - q.Y0) / DsH,
                            R = 1, G = 1, B = 1, A = 1
                        });
                    }
                }
            }
        }
    }
}
