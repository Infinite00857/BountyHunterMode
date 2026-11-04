// HudArt - decodes MPH 2D art from the extracted ROM: HUD OAM objects (strips of frames) and model textures
// (the title logo). Port of the testbed's tools/scripts/mph_hud.py export_object (source: MphRead HUD/HudInfo.cs).
using System;
using System.Collections.Generic;
using System.IO;
using MphRead;

namespace MphSamus.Core
{
    public sealed class HudStrip
    {
        public RgbaImage Image = new RgbaImage(1, 1);   // frames side by side
        public int FrameWidth, FrameCount;
    }

    public static class HudArt
    {
        private static readonly (int, int)[,] ObjDims =
        {
            { (1, 1), (2, 2), (4, 4), (8, 8) },
            { (2, 1), (4, 1), (4, 2), (8, 4) },
            { (1, 2), (1, 4), (2, 4), (4, 8) }
        };

        public static class Files
        {
            public const string MessageBox = @"_archives\spSamus\hud_msgBox.bin";
            public const string MessageSpacer = @"_archives\spSamus\message_spacer.bin";
            public const string AmmoBar = @"_archives\localSamus\hud_ammobar.bin";
            public const string EnergyBar = @"_archives\localSamus\hud_energybar.bin";
            public const string TitleLogo = @"main menu\toplogo_Model.bin";
            // Samus's helmet HUD (HudInfo.HunterObjects[Samus])
            public const string Helmet = @"_archives\localSamus\bg_top.bin";
            public const string HelmetDrop = @"_archives\localSamus\bg_top_drop.bin";
            public const string Visor = @"_archives\localSamus\bg_top_ovl.bin";
            public const string EnergyTanks = @"_archives\spSamus\hud_etank.bin";
            public const string WeaponIcon = @"_archives\localSamus\hud_weaponicon.bin";
            public const string TargetCircle = @"_archives\localSamus\hud_targetcircle.bin";
            public const string Boost = @"_archives\common\hud_boost.bin";   // 0 = ready, 1 = recharging
            public const string Bombs = @"_archives\common\hud_bombs.bin";   // 0 = have, 1 = used
        }

        // A tile-map layer (bg_top*.bin: header {?, charSize, palSize}, 4bpp chars, BGR555 palette, screen map) as one
        // image. rows = tile rows to read (0 = all; MphRead reads 32 for the visor). Port of mph_hud.py export_layer.
        public static RgbaImage LoadLayer(string file, int rows = 0, int palette = -1, int startX = 0, int cols = 0)
        {
            byte[] d = File.ReadAllBytes(RomCache.Combine(file));
            int charSize = I32(d, 4), palSize = I32(d, 8);
            int p = 12;
            int chars = p;
            p += charSize;
            var pal = new ushort[palSize / 2];
            for (int i = 0; i < pal.Length; i++) { pal[i] = U16(d, p + i * 2); }
            p += palSize;
            int cx = U16(d, p), cy = U16(d, p + 2), scrSize = I32(d, p + 4);
            p += 8;
            int tilesX = cols > 0 ? cols : cx, tilesY = rows > 0 ? rows : cy;
            int w = tilesX * 8, h = tilesY * 8;
            var img = new RgbaImage(w, h);
            int stride = cx > 32 ? cx / 2 : cx;
            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int icx = tx + startX;
                    int idx = ty * stride + (icx / 32 == 1 ? 0x400 + (icx - 32) : icx);
                    if (idx * 2 >= scrSize) { continue; }
                    int e = U16(d, p + idx * 2);
                    int ch = e & 0x3FF, pid = e >> 12;
                    bool fh = (e & 0x400) != 0, fv = (e & 0x800) != 0;
                    for (int py = 0; py < 8; py++)
                    {
                        for (int px = 0; px < 8; px++)
                        {
                            int sy = fv ? 7 - py : py, sx = fh ? 7 - px : px;
                            int bi = chars + ch * 32 + sy * 4 + sx / 2;
                            if (bi >= chars + charSize) { continue; }
                            int ci = (sx & 1) == 0 ? d[bi] & 0xF : d[bi] >> 4;
                            if (ci == 0 || (palette != -1 && ci == 6)) { continue; }   // CharMapToTexture
                            int pi = (palette != -1 ? palette : pid) * 16 + ci;
                            ushort c = pi < pal.Length ? pal[pi] : (ushort)0x7FFF;
                            int o = ((ty * 8 + py) * w + tx * 8 + px) * 4;
                            img.Pixels[o] = (byte)((c & 31) * 255 / 31);
                            img.Pixels[o + 1] = (byte)(((c >> 5) & 31) * 255 / 31);
                            img.Pixels[o + 2] = (byte)(((c >> 10) & 31) * 255 / 31);
                            img.Pixels[o + 3] = 255;
                        }
                    }
                }
            }
            return img;
        }

        // The HUD font (arm9 font tables, MphRead Extract.LoadRuntimeData; AMHE0 offsets): 8x8 glyphs from ' ',
        // drawn in one color (pixel value 3; the HUD tints it with the text palette).
        public sealed class HudFont
        {
            public RgbaImage Atlas = new RgbaImage(1, 1);   // glyphs side by side, white where set
            public int[] Widths = Array.Empty<int>();
            public int[] Offsets = Array.Empty<int>();     // y offset per glyph (DS pixels)
            public int Count;
            public const int MinChar = 32;

            public int Index(char c)
            {
                int i = c - MinChar;
                return i >= 0 && i < Count ? i : 0;
            }

            public int TextWidth(string s)
            {
                int w = 0;
                foreach (char c in s) { w += Widths[Index(c)]; }
                return w;
            }
        }

        private const int FontWidthsOffset = 0xBF9B0, FontOffsetsOffset = 0xBFB90, FontCharsOffset = 0xC0270;   // AMHE0
        private const int FontGlyphs = 480;

        public static HudFont LoadFont()
        {
            byte[] arm9 = File.ReadAllBytes(RomCache.Combine(@"_bin\arm9.bin"));
            var font = new HudFont { Count = FontGlyphs, Widths = new int[FontGlyphs], Offsets = new int[FontGlyphs] };
            font.Atlas = new RgbaImage(8 * FontGlyphs, 8);
            for (int i = 0; i < FontGlyphs; i++)
            {
                font.Widths[i] = arm9[FontWidthsOffset + i];
                font.Offsets[i] = (sbyte)arm9[FontOffsetsOffset + i];
                for (int py = 0; py < 8; py++)
                {
                    for (int px = 0; px < 8; px++)
                    {
                        int b = arm9[FontCharsOffset + i * 32 + py * 4 + px / 2];
                        int v = (px & 1) == 0 ? b & 0xF : b >> 4;
                        if (v == 0) { continue; }
                        int o = (py * font.Atlas.Width + i * 8 + px) * 4;
                        font.Atlas.Pixels[o] = font.Atlas.Pixels[o + 1] = font.Atlas.Pixels[o + 2] = font.Atlas.Pixels[o + 3] = 255;
                    }
                }
            }
            return font;
        }

        // Directional damage indicator (hud "damage" model, DS pixel space x right / y up): per direction node
        // (north, ne, east, se, south, sw, west, nw) its rectangles in DS pixels (y down) + texcoords. Pieces whose
        // texture runs vertically are flagged Rotated (testbed mph_hud.cpp draws them from a transposed copy).
        public struct DamageQuad
        {
            public float X0, Y0, X1, Y1, U0, V0, U1, V1;
            public bool Rotated;
        }

        public static List<DamageQuad>[] LoadDamageIndicator(out RgbaImage? texture)
        {
            var result = new List<DamageQuad>[8];
            for (int i = 0; i < 8; i++) { result[i] = new List<DamageQuad>(); }
            Model model = Read.GetModelInstance("damage", dir: MetaDir.Hud).Model;
            texture = null;
            if (model.Materials.Count > 0 && model.Materials[0].TextureId >= 0)
            {
                texture = PosedModel.DecodeTexture(model, model.Materials[0].TextureId, model.Materials[0].PaletteId);
            }
            string[] names = { "north", "ne", "east", "se", "south", "sw", "west", "nw" };
            float s = model.Scale.X;
            for (int dir = 0; dir < 8; dir++)
            {
                Node? node = null;
                foreach (Node n in model.Nodes) { if (n.Name == names[dir]) { node = n; break; } }
                if (node == null) { continue; }
                int start = node.MeshId / 2;
                for (int mi = start; mi < start + node.MeshCount && mi < model.Meshes.Count; mi++)
                {
                    List<PosedModel.MeshVertex> v = PosedModel.DecodeMesh(model, mi);
                    for (int q = 0; q + 6 <= v.Count && result[dir].Count < 2; q += 6)
                    {
                        float minX = 1e9f, maxX = -1e9f, minY = 1e9f, maxY = -1e9f;
                        for (int k = q; k < q + 6; k++)
                        {
                            minX = Math.Min(minX, v[k].Pos.X); maxX = Math.Max(maxX, v[k].Pos.X);
                            minY = Math.Min(minY, v[k].Pos.Y); maxY = Math.Max(maxY, v[k].Pos.Y);
                        }
                        float tlU = 0, tlV = 0, trU = 0, brU = 0, brV = 0;
                        for (int k = q; k < q + 6; k++)
                        {
                            if (v[k].Pos.X == minX && v[k].Pos.Y == maxY) { tlU = v[k].Uv.X; tlV = v[k].Uv.Y; }
                            if (v[k].Pos.X == maxX && v[k].Pos.Y == maxY) { trU = v[k].Uv.X; }
                            if (v[k].Pos.X == maxX && v[k].Pos.Y == minY) { brU = v[k].Uv.X; brV = v[k].Uv.Y; }
                        }
                        var dq = new DamageQuad { X0 = minX * s, Y0 = 192 - maxY * s, X1 = maxX * s, Y1 = 192 - minY * s };
                        dq.Rotated = tlU == trU && tlU != brU;
                        if (dq.Rotated) { dq.U0 = tlV; dq.V0 = tlU; dq.U1 = brV; dq.V1 = brU; }
                        else { dq.U0 = tlU; dq.V0 = tlV; dq.U1 = brU; dq.V1 = brV; }
                        result[dir].Add(dq);
                    }
                }
            }
            return result;
        }

        // hud_msgBox animation (AnimParams expanded): image per DS frame while the box opens (testbed mph_hud.cpp)
        public static readonly int[] MessageBoxFrames = { 0, 1, 2, 2, 3, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6 };

        private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        private static int I32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        private static ushort[] ReadPalette(byte[] d)
        {
            int paramSize = I32(d, 8), attrSize = I32(d, 12), charSize = I32(d, 16), palSize = I32(d, 20);
            int p = 24 + paramSize + attrSize + charSize;
            var pal = new ushort[palSize / 2];
            for (int i = 0; i < pal.Length; i++) { pal[i] = U16(d, p + i * 2); }
            return pal;
        }

        // palette: 16-color bank; palFrom: take the palette from another object (the dialog box uses the ammo bar's)
        public static HudStrip LoadObject(string file, int palette = 0, string? palFrom = null)
        {
            byte[] d = File.ReadAllBytes(RomCache.Combine(file));
            int paramSize = I32(d, 8), attrSize = I32(d, 12), charSize = I32(d, 16);
            int p = 24 + paramSize;
            ushort attr0 = U16(d, p), attr1 = U16(d, p + 2);
            p += attrSize;
            int chars = p;
            ushort[] pal = palFrom != null ? ReadPalette(File.ReadAllBytes(RomCache.Combine(palFrom))) : ReadPalette(d);
            (int tw, int th) = ObjDims[attr0 >> 14, attr1 >> 14];
            int w = tw * 8, h = th * 8;
            int count = Math.Max(1, charSize / (tw * th * 32));
            var strip = new HudStrip { FrameWidth = w, FrameCount = count, Image = new RgbaImage(w * count, h) };
            int outW = w * count;
            for (int img = 0; img < count; img++)
            {
                for (int ty = 0; ty < th; ty++)
                {
                    for (int tx = 0; tx < tw; tx++)
                    {
                        int ch = chars + (img * tw * th + ty * tw + tx) * 32;
                        for (int py = 0; py < 8; py++)
                        {
                            for (int px = 0; px < 8; px++)
                            {
                                int bi = ch + py * 4 + px / 2;
                                if (bi >= d.Length) { continue; }
                                int idx = (px & 1) == 0 ? d[bi] & 0xF : d[bi] >> 4;
                                if (idx == 0) { continue; }
                                int pi = palette * 16 + idx;
                                ushort c = pi < pal.Length ? pal[pi] : (ushort)0x7FFF;
                                int o = ((ty * 8 + py) * outW + img * w + tx * 8 + px) * 4;
                                strip.Image.Pixels[o] = (byte)((c & 31) * 255 / 31);
                                strip.Image.Pixels[o + 1] = (byte)(((c >> 5) & 31) * 255 / 31);
                                strip.Image.Pixels[o + 2] = (byte)(((c >> 10) & 31) * 255 / 31);
                                strip.Image.Pixels[o + 3] = 255;
                            }
                        }
                    }
                }
            }
            return strip;
        }

        // One frame of a strip as its own image.
        public static RgbaImage Frame(HudStrip s, int frame)
        {
            var img = new RgbaImage(s.FrameWidth, s.Image.Height);
            for (int y = 0; y < img.Height; y++)
            {
                Buffer.BlockCopy(s.Image.Pixels, (y * s.Image.Width + frame * s.FrameWidth) * 4, img.Pixels, y * img.Width * 4, img.Width * 4);
            }
            return img;
        }

        // PlayerDialog.DrawDialogs box, assembled into one image (DS pixels): four mirrored quarters of a msgBox frame
        // with the spacer between the left and right halves. Used as a 9-slice sprite for ULTRAKILL's popups.
        public static RgbaImage BuildMessageBox(int boxFrame, int palette = 0)
        {
            HudStrip box = LoadObject(Files.MessageBox, palette, Files.AmmoBar);
            HudStrip spacer = LoadObject(Files.MessageSpacer, palette, Files.AmmoBar);
            int qw = box.FrameWidth, qh = box.Image.Height;
            int sw = boxFrame >= 5 ? spacer.FrameWidth : 0;
            int w = qw * 2 + sw, h = qh * 2;
            var outImg = new RgbaImage(w, h);
            void Blit(HudStrip s, int frame, int dx, int dy, bool fh, bool fv)
            {
                for (int y = 0; y < s.Image.Height; y++)
                {
                    for (int x = 0; x < s.FrameWidth; x++)
                    {
                        int sx = frame * s.FrameWidth + (fh ? s.FrameWidth - 1 - x : x);
                        int sy = fv ? s.Image.Height - 1 - y : y;
                        int si = (sy * s.Image.Width + sx) * 4;
                        int ox = dx + x, oy = dy + y;
                        if (ox < 0 || oy < 0 || ox >= w || oy >= h || s.Image.Pixels[si + 3] == 0) { continue; }
                        int oi = (oy * w + ox) * 4;
                        Buffer.BlockCopy(s.Image.Pixels, si, outImg.Pixels, oi, 4);
                    }
                }
            }
            if (sw > 0)
            {
                int si = (boxFrame & 1) != 0 ? 0 : 1;
                if (si >= spacer.FrameCount) { si = 0; }
                Blit(spacer, si, qw, 0, false, false);
                Blit(spacer, si, qw, qh, false, true);
            }
            Blit(box, boxFrame, 0, 0, false, false);
            Blit(box, boxFrame, qw + sw, 0, true, false);
            Blit(box, boxFrame, 0, qh, false, true);
            Blit(box, boxFrame, qw + sw, qh, true, true);
            return outImg;
        }

        // A 9-slice-ready version of the message box (playtest 1: slicing the assembled box stretched the spacer piece
        // into an inner rectangle). Corner = the box's top-left 8x8 bracket, edges/fill = 1-px strips from its left
        // quarter, mirrored for the other sides. Result: (2 * Border + 1)^2 image, slice border = Border.
        public const int SliceBorder = 8;
        public static RgbaImage BuildSliceableMessageBox(int palette = 0)
        {
            RgbaImage box = BuildMessageBox(6, palette);
            int b = SliceBorder, n = b * 2 + 1;
            int midX = 40, midY = box.Height / 2;   // inside the left quarter, away from the spacer and brackets
            var img = new RgbaImage(n, n);
            void Copy(int sx, int sy, int dx, int dy)
            {
                Buffer.BlockCopy(box.Pixels, (sy * box.Width + sx) * 4, img.Pixels, (dy * n + dx) * 4, 4);
            }
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    // fold onto the top-left quadrant (mirror right/bottom)
                    int fx = x <= b ? x : n - 1 - x;
                    int fy = y <= b ? y : n - 1 - y;
                    int sx = fx < b ? fx : midX;
                    int sy = fy < b ? fy : midY;
                    Copy(sx, sy, x, y);
                }
            }
            return img;
        }

        // First texture of a model's first textured material (e.g. the MPH title logo, 256x128).
        public static RgbaImage? LoadModelTexture(string modelFile, string? materialName = null)
        {
            var recolors = new List<RecolorMetadata> { new RecolorMetadata("default", modelFile, modelFile) };
            Model model = Read.ReadModel(Path.GetFileNameWithoutExtension(modelFile), modelFile, null, null, recolors, false);
            foreach (Material mat in model.Materials)
            {
                if (mat.TextureId < 0 || (materialName != null && mat.Name != materialName))
                {
                    continue;
                }
                Texture t = model.Recolors[0].Textures[mat.TextureId];
                IReadOnlyList<ColorRgba> px = model.Recolors[0].GetPixels(mat.TextureId, mat.PaletteId);
                var img = new RgbaImage(t.Width, t.Height);
                for (int i = 0; i < px.Count && i < t.Width * t.Height; i++)
                {
                    img.Pixels[i * 4] = px[i].Red;
                    img.Pixels[i * 4 + 1] = px[i].Green;
                    img.Pixels[i * 4 + 2] = px[i].Blue;
                    img.Pixels[i * 4 + 3] = px[i].Alpha;
                }
                return img;
            }
            return null;
        }
    }
}
