// PosedModel - CPU version of MphRead's model drawing: decodes each mesh's display list into triangles once
// (Renderer.DoDlist), then every frame poses the model (EntityBase.UpdateTransforms: AnimateMaterials/Textures,
// ComputeNodeMatrices, AnimateNodes, UpdateMatrixStack), skins the vertices (matrix stack or node transform, as the
// MphRead vertex shader does) and lights them with MPH's two directional lights (Shaders.cs light_calc).
// Output is engine-agnostic: one Batch per drawn mesh, positions in MPH coordinates (right-handed, Y up).
// The engine adapter converts handedness (the ULTRAKILL plugin mirrors X) and uploads the arrays.
// Lighting/texgen details follow the testbed's ModelDrawer (testbed/code/core/hunters/mph_model.cpp).
using System;
using System.Collections.Generic;
using MphRead;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    public struct MphLighting
    {
        // MPH room light vectors (Combat Hall defaults, as in the testbed). Directions in the posing space.
        public Vector3 Light1Dir, Light1Color, Light2Dir, Light2Color;
        public Vector3 ViewDir, ViewRight, ViewUp;   // camera axes in the posing space (specular, env map, billboards)

        public static MphLighting Default => new MphLighting
        {
            Light1Dir = new Vector3(0.1f, -1.0f, 0.0f).Normalized(),
            Light1Color = new Vector3(1.0f, 24 / 31.0f, 18 / 31.0f),
            Light2Dir = new Vector3(0.0f, 1.0f, -0.1f).Normalized(),
            Light2Color = new Vector3(13 / 31.0f, 12 / 31.0f, 7 / 31.0f),
            ViewDir = new Vector3(0, 0, -1),
            ViewRight = new Vector3(1, 0, 0),
            ViewUp = new Vector3(0, 1, 0)
        };
    }

    public sealed class RgbaImage
    {
        public int Width, Height;
        public byte[] Pixels;   // RGBA8, rows top-down
        public bool PartialAlpha;   // some texels between transparent and opaque (A3I5 / A5I3 glows)
        public RgbaImage(int w, int h) { Width = w; Height = h; Pixels = new byte[w * h * 4]; }
    }

    public sealed class PosedModel
    {
        private struct DVertex
        {
            public Vector3 Pos, Normal, Color;
            public bool HasColor, DifAmb;
            public Vector2 Uv;
            public int Matrix;
        }

        // One drawn mesh this frame.
        public sealed class Batch
        {
            public int MaterialId;
            public int TextureKey = -1;         // index into Textures (-1 = untextured)
            public bool Translucent;
            public CullingMode Culling;
            public RepeatMode RepeatS, RepeatT;
            public int Count;                   // vertices used (multiple of 3)
            public Vector3[] Positions = new Vector3[0];
            public Vector2[] Uvs = new Vector2[0];
            public byte[] Colors = new byte[0]; // RGBA8 per vertex
            internal void Ensure(int n)
            {
                if (Positions.Length < n)
                {
                    Positions = new Vector3[n];
                    Uvs = new Vector2[n];
                    Colors = new byte[n * 4];
                }
            }
        }

        public ModelInstance Instance { get; }
        public Model Model => Instance.Model;
        public List<RgbaImage> Textures { get; private set; } = new List<RgbaImage>();
        public List<Batch> Batches { get; } = new List<Batch>();   // valid after Pose()
        public int BatchCount { get; private set; }
        public bool Hidden { get; set; }
        // keep only triangles whose vertices all belong to nodes passing this (e.g. the left arm's bones); null = all
        public Func<int, bool>? KeepNode { get; set; }
        private bool[] _keepFlags = new bool[0];

        private readonly List<DVertex>[] _meshVerts;
        private Dictionary<(int, int), int> _textureKeys = new Dictionary<(int, int), int>();
        private readonly int _recolor;
        // shared decodes (hunters: every Kanden draws the same Kanden): the vertex lists and textures are read-only
        private sealed class Decoded
        {
            public List<DVertex>[] Verts;
            public List<RgbaImage> Textures;
            public Dictionary<(int, int), int> Keys;
        }
        private static readonly Dictionary<(string, int), Decoded> _shared = new Dictionary<(string, int), Decoded>();

        // a posed model that shares its decoded vertices / textures with every other shared one of the same model
        public static PosedModel Shared(ModelInstance inst, int recolor = 0)
        {
            var key = (inst.Model.Name, recolor);
            if (_shared.TryGetValue(key, out Decoded d)) { return new PosedModel(inst, recolor, d); }
            var pm = new PosedModel(inst, recolor);
            _shared[key] = new Decoded { Verts = pm._meshVerts, Textures = pm.Textures, Keys = pm._textureKeys };
            return pm;
        }

        private PosedModel(ModelInstance inst, int recolor, Decoded d)
        {
            Instance = inst;
            _recolor = recolor;
            _meshVerts = d.Verts;
            Textures = d.Textures;
            _textureKeys = d.Keys;
            _stack = new Matrix4[Math.Max(1, inst.Model.NodeMatrixIds.Count)];
        }
        private readonly Matrix4[] _stack;

        public PosedModel(ModelInstance inst, int recolor = 0)
        {
            Instance = inst;
            _recolor = recolor;
            Model m = inst.Model;
            _meshVerts = new List<DVertex>[m.Meshes.Count];
            for (int i = 0; i < m.Meshes.Count; i++)
            {
                Mesh mesh = m.Meshes[i];
                Material mat = m.Materials[mesh.MaterialId];
                int tw = 0, th = 0;
                if (mat.TextureId >= 0)
                {
                    Texture t = m.Recolors[recolor].Textures[mat.TextureId];
                    tw = t.Width;
                    th = t.Height;
                }
                _meshVerts[i] = DecodeDlist(m, mesh, tw, th, mat.TexgenMode == TexgenMode.Normal);
            }
            _stack = new Matrix4[Math.Max(1, m.NodeMatrixIds.Count)];
            // textures for every (texture, palette) a material can show (incl. texture animations)
            foreach (Material mat in m.Materials)
            {
                if (mat.TextureId >= 0)
                {
                    GetTextureKey(mat.TextureId, mat.PaletteId, recolor);
                }
            }
            foreach (TextureAnimationGroup group in m.AnimationGroups.Texture)
            {
                for (int i = 0; i < group.TextureIds.Count; i++)
                {
                    GetTextureKey(group.TextureIds[i], group.PaletteIds[i], recolor);
                }
            }
        }

        private int GetTextureKey(int textureId, int paletteId, int recolor = 0)
        {
            if (_textureKeys.TryGetValue((textureId, paletteId), out int key))
            {
                return key;
            }
            RgbaImage? img = DecodeTexture(Model, textureId, paletteId, recolor);
            if (img == null)
            {
                return -1;
            }
            key = Textures.Count;
            Textures.Add(img);
            _textureKeys[(textureId, paletteId)] = key;
            return key;
        }

        // One texture + palette as RGBA (null = no such texture).
        public static RgbaImage? DecodeTexture(Model model, int textureId, int paletteId, int recolor = 0)
        {
            Recolor rc = model.Recolors[recolor];
            if (textureId < 0 || textureId >= rc.Textures.Count)
            {
                return null;
            }
            Texture t = rc.Textures[textureId];
            var img = new RgbaImage(t.Width, t.Height);
            try
            {
                IReadOnlyList<ColorRgba> px = rc.GetPixels(textureId, paletteId);
                for (int i = 0; i < px.Count && i < t.Width * t.Height; i++)
                {
                    img.Pixels[i * 4] = px[i].Red;
                    img.Pixels[i * 4 + 1] = px[i].Green;
                    img.Pixels[i * 4 + 2] = px[i].Blue;
                    img.Pixels[i * 4 + 3] = px[i].Alpha;
                    if (px[i].Alpha > 0 && px[i].Alpha < 255) { img.PartialAlpha = true; }
                }
            }
            catch (ArgumentException)
            {
            }
            return img;
        }

        // Unposed triangles of one mesh (particle models drawn as meshes). Uv in texture units (0..1 over the image).
        public struct MeshVertex
        {
            public Vector3 Pos;
            public Vector2 Uv;
            public Vector3 Color;
            public bool HasColor;
        }

        public static List<MeshVertex> DecodeMesh(Model model, int meshIndex)
        {
            Mesh mesh = model.Meshes[meshIndex];
            Material mat = model.Materials[mesh.MaterialId];
            int tw = 0, th = 0;
            if (mat.TextureId >= 0 && mat.TextureId < model.Recolors[0].Textures.Count)
            {
                Texture t = model.Recolors[0].Textures[mat.TextureId];
                tw = t.Width;
                th = t.Height;
            }
            var list = new List<MeshVertex>();
            foreach (DVertex v in DecodeDlist(model, mesh, tw, th, false))
            {
                list.Add(new MeshVertex { Pos = v.Pos, Uv = v.Uv, Color = v.Color, HasColor = v.HasColor && !v.DifAmb });
            }
            return list;
        }

        // Pose + skin + light. transform = model placement in the posing space (MphRead entity transform, row-vector).
        public void Pose(Matrix4 transform, MphLighting light, float alpha = 1, Vector3? tint = null)
        {
            Model m = Model;
            AnimationInfo info = Instance.AnimInfo;
            m.AnimateMaterials(info);
            m.AnimateTextures(info);
            m.ComputeNodeMatrices(index: 0);
            m.AnimateNodes(index: 0, true, transform, m.Scale, info);
            PoseAnimated(light, alpha, tint);
        }

        // Skin + light with the node matrices already animated (e.g. PlayerDraw's legs/torso split for the biped).
        public void PoseAnimated(MphLighting light, float alpha = 1, Vector3? tint = null)
        {
            Model m = Model;
            bool skinned = m.NodeMatrixIds.Count > 0;
            if (skinned)
            {
                for (int i = 0; i < m.NodeMatrixIds.Count; i++)
                {
                    _stack[i] = m.Nodes[m.NodeMatrixIds[i]].Animation;
                }
            }
            Vector3 tintV = tint ?? Vector3.One;
            BatchCount = 0;
            if (Hidden || m.Nodes.Count == 0)
            {
                return;
            }
            AddNode(0, light, alpha, tintV, skinned);
        }

        private void AddNode(int nodeIndex, MphLighting light, float alpha, Vector3 tint, bool skinned)
        {
            Model m = Model;
            for (int ni = nodeIndex; ni != -1;)
            {
                Node node = m.Nodes[ni];
                if (node.Enabled)
                {
                    int start = node.MeshId / 2;
                    for (int k = 0; k < node.MeshCount; k++)
                    {
                        int meshIndex = start + k;
                        if (meshIndex < m.Meshes.Count && m.Meshes[meshIndex].Visible)
                        {
                            AddMesh(ni, meshIndex, light, alpha, tint, skinned);
                        }
                    }
                    if (node.ChildIndex != -1)
                    {
                        AddNode(node.ChildIndex, light, alpha, tint, skinned);
                    }
                }
                ni = node.NextIndex;
            }
        }

        private void AddMesh(int nodeIndex, int meshIndex, MphLighting light, float alpha, Vector3 tint, bool skinned)
        {
            Model m = Model;
            Mesh mesh = m.Meshes[meshIndex];
            Material mat = m.Materials[mesh.MaterialId];
            float matAlpha = mat.CurrentAlpha * alpha;
            if (matAlpha <= 0)
            {
                return;
            }
            List<DVertex> verts = _meshVerts[meshIndex];
            if (BatchCount == Batches.Count)
            {
                Batches.Add(new Batch());
            }
            Batch b = Batches[BatchCount++];
            b.MaterialId = mesh.MaterialId;
            b.TextureKey = mat.CurrentTextureId >= 0 ? GetTextureKey(mat.CurrentTextureId, mat.CurrentPaletteId, _recolor) : -1;
            // MphRead Renderer.UpdateMaterial: a Normal material whose texture isn't opaque draws translucent (the
            // pickups' A3I5 glow billboards); kept to textures with real partial alpha so palette cutouts stay cutouts
            bool texBlend = b.TextureKey >= 0 && Textures[b.TextureKey].PartialAlpha && mat.RenderMode == RenderMode.Normal;
            b.Translucent = mat.RenderMode == RenderMode.Translucent || matAlpha < 0.999f || texBlend;
            b.Culling = mat.Culling;
            b.RepeatS = mat.XRepeat;
            b.RepeatT = mat.YRepeat;
            b.Count = verts.Count;
            b.Ensure(verts.Count);
            Matrix4 uvMtx = GetTexcoordMatrix(mat);
            Vector3 dif = mat.CurrentDiffuse, amb = mat.CurrentAmbient, spe = mat.CurrentSpecular;
            Node node = m.Nodes[nodeIndex];
            byte a = ToByte(matAlpha);
            if (KeepNode != null && _keepFlags.Length < verts.Count) { _keepFlags = new bool[verts.Count * 2]; }
            for (int i = 0; i < verts.Count; i++)
            {
                DVertex v = verts[i];
                int slot = skinned && v.Matrix < _stack.Length ? v.Matrix : 0;
                Matrix4 mtx = skinned ? _stack[slot] : node.Animation;
                int owner = skinned ? m.NodeMatrixIds[slot] : nodeIndex;
                if (KeepNode != null) { _keepFlags[i] = KeepNode(owner); }
                Vector3 p;
                if (m.Nodes[owner].BillboardMode == BillboardMode.Sphere || m.Nodes[owner].BillboardMode == BillboardMode.Cylinder)
                {
                    // UpdateMatrixStack + view_inv_mtx: no node rotation, face the camera (testbed keeps node scale)
                    float bs = new Vector3(mtx.M11, mtx.M12, mtx.M13).Length;
                    Vector3 lp = v.Pos * bs;
                    p = light.ViewRight * lp.X + light.ViewUp * lp.Y - light.ViewDir * lp.Z + mtx.Row3.Xyz;
                }
                else
                {
                    p = Vector3.TransformPosition(v.Pos, mtx);
                }
                b.Positions[i] = p;
                Vector2 uv = new Vector2(v.Uv.X * uvMtx.M11 + v.Uv.Y * uvMtx.M21 + uvMtx.M41,
                    v.Uv.X * uvMtx.M12 + v.Uv.Y * uvMtx.M22 + uvMtx.M42);
                Vector3 col = v.HasColor ? v.Color : dif;
                if (mat.Lighting > 0 || mat.TexgenMode == TexgenMode.Normal)
                {
                    Vector3 n = Vector3.TransformNormal(v.Normal, mtx);
                    n = n.LengthSquared > 0 ? n.Normalized() : Vector3.UnitY;
                    if (mat.Lighting > 0)
                    {
                        Vector3 d = dif, am = amb;
                        if (v.HasColor && v.DifAmb)
                        {
                            d = v.Color;
                            am = Vector3.Zero;
                        }
                        Vector3 c = LightCalc(light.Light1Dir, light.Light1Color, n, d, am, spe, light.ViewDir)
                            + LightCalc(light.Light2Dir, light.Light2Color, n, d, am, spe, light.ViewDir);
                        col = new Vector3(Math.Min(c.X, 1), Math.Min(c.Y, 1), Math.Min(c.Z, 1));
                    }
                    if (mat.TexgenMode == TexgenMode.Normal)
                    {
                        // environment map: texcoords from the view-space normal (testbed simplification)
                        uv = new Vector2(0.5f + 0.5f * Vector3.Dot(n, light.ViewRight), 0.5f - 0.5f * Vector3.Dot(n, light.ViewUp));
                    }
                }
                b.Uvs[i] = uv;
                b.Colors[i * 4] = ToByte(col.X * tint.X);
                b.Colors[i * 4 + 1] = ToByte(col.Y * tint.Y);
                b.Colors[i * 4 + 2] = ToByte(col.Z * tint.Z);
                b.Colors[i * 4 + 3] = a;
            }
            if (KeepNode != null)
            {
                int w = 0;
                for (int t = 0; t + 2 < verts.Count; t += 3)
                {
                    if (!_keepFlags[t] || !_keepFlags[t + 1] || !_keepFlags[t + 2]) { continue; }
                    for (int k = 0; k < 3; k++)
                    {
                        b.Positions[w + k] = b.Positions[t + k];
                        b.Uvs[w + k] = b.Uvs[t + k];
                        Buffer.BlockCopy(b.Colors, (t + k) * 4, b.Colors, (w + k) * 4, 4);
                    }
                    w += 3;
                }
                b.Count = w;
            }
        }

        private Matrix4 GetTexcoordMatrix(Material material)
        {
            // EntityBase.GetTexcoordMatrix (texcoord animation, or the material matrix for texcoord texgen)
            AnimationInfo info = Instance.AnimInfo;
            TexcoordAnimationGroup? group = info.Texcoord.Group;
            if (group != null && group.Animations.TryGetValue(material.Name, out TexcoordAnimation anim))
            {
                return Model.AnimateTexcoords(group, anim, info.TexcoordFrame);
            }
            if (material.TexgenMode == TexgenMode.Texcoord)
            {
                Matrix4 mm = Matrix4.CreateTranslation(material.ScaleS * material.TranslateS, material.ScaleT * material.TranslateT, 0.0f);
                mm = Matrix4.CreateScale(material.ScaleS, material.ScaleT, 1.0f) * mm;
                return Matrix4.CreateRotationZ(material.RotateZ) * mm;
            }
            return Matrix4.Identity;
        }

        private static Vector3 LightCalc(Vector3 lightVec, Vector3 lightCol, Vector3 n, Vector3 dif, Vector3 amb, Vector3 spe, Vector3 sight)
        {
            float difFactor = Math.Max(0, -Vector3.Dot(lightVec, n));
            Vector3 half = (lightVec + sight) / 2;
            float speFactor = Math.Max(0, Vector3.Dot(-half, n));
            speFactor *= speFactor;
            return spe * lightCol * speFactor + dif * lightCol * difFactor + amb * lightCol;
        }

        private static byte ToByte(float v)
        {
            if (v <= 0) { return 0; }
            if (v >= 1) { return 255; }
            return (byte)(v * 255 + 0.5f);
        }

        // Renderer.DoDlist, emitting triangles instead of GL calls.
        private static List<DVertex> DecodeDlist(Model model, Mesh mesh, int tw, int th, bool texgen)
        {
            var tris = new List<DVertex>();
            IReadOnlyList<RenderInstruction> list = model.RenderInstructionLists[mesh.DlistId];
            var prim = new List<DVertex>();
            int primType = 0;
            Vector3 vtx = Vector3.Zero, nrm = Vector3.UnitY, color = Vector3.One;
            bool hasColor = false, difAmb = false;
            Vector2 tex = texgen ? new Vector2(0.5f, 0.5f) : Vector2.Zero;
            int matrixId = 0;

            void Emit()
            {
                prim.Add(new DVertex { Pos = vtx, Normal = nrm, Color = color, HasColor = hasColor, DifAmb = difAmb, Uv = tex, Matrix = matrixId });
            }
            static int Sext(uint v, int bits)
            {
                int s = (int)(v & ((1u << bits) - 1));
                if ((s & (1 << (bits - 1))) != 0) { s -= 1 << bits; }
                return s;
            }
            void Flush()
            {
                int n = prim.Count;
                switch (primType)
                {
                case 0:
                    for (int i = 0; i + 2 < n; i += 3) { tris.Add(prim[i]); tris.Add(prim[i + 1]); tris.Add(prim[i + 2]); }
                    break;
                case 1:
                    for (int i = 0; i + 3 < n; i += 4)
                    {
                        tris.Add(prim[i]); tris.Add(prim[i + 1]); tris.Add(prim[i + 2]);
                        tris.Add(prim[i]); tris.Add(prim[i + 2]); tris.Add(prim[i + 3]);
                    }
                    break;
                case 2:
                    for (int i = 0; i + 2 < n; i++)
                    {
                        if ((i & 1) == 0) { tris.Add(prim[i]); tris.Add(prim[i + 1]); tris.Add(prim[i + 2]); }
                        else { tris.Add(prim[i + 1]); tris.Add(prim[i]); tris.Add(prim[i + 2]); }
                    }
                    break;
                case 3:
                    for (int i = 0; i + 3 < n; i += 2)
                    {
                        // GL quad strip: quad (2i, 2i+1, 2i+3, 2i+2)
                        DVertex a = prim[i], b = prim[i + 1], c = prim[i + 3], d = prim[i + 2];
                        tris.Add(a); tris.Add(b); tris.Add(c);
                        tris.Add(a); tris.Add(c); tris.Add(d);
                    }
                    break;
                }
                prim.Clear();
            }

            for (int i = 0; i < list.Count; i++)
            {
                RenderInstruction ins = list[i];
                uint a0 = ins.Arguments.Count > 0 ? ins.Arguments[0] : 0;
                switch (ins.Code)
                {
                case InstructionCode.BEGIN_VTXS:
                    prim.Clear();
                    primType = (int)a0;
                    break;
                case InstructionCode.END_VTXS:
                    Flush();
                    break;
                case InstructionCode.COLOR:
                    color = new Vector3((a0 & 0x1F) / 31f, ((a0 >> 5) & 0x1F) / 31f, ((a0 >> 10) & 0x1F) / 31f);
                    hasColor = true;
                    difAmb = false;
                    break;
                case InstructionCode.DIF_AMB:
                    color = new Vector3((a0 & 0x1F) / 31f, ((a0 >> 5) & 0x1F) / 31f, ((a0 >> 10) & 0x1F) / 31f);
                    hasColor = true;
                    difAmb = true;   // "smuggled" diffuse (alpha 0 in MphRead)
                    break;
                case InstructionCode.NORMAL:
                    nrm = new Vector3(Sext(a0, 10) / 512f, Sext(a0 >> 10, 10) / 512f, Sext(a0 >> 20, 10) / 512f);
                    break;
                case InstructionCode.TEXCOORD:
                    if (tw > 0 && th > 0)
                    {
                        tex = new Vector2(Sext(a0, 16) / 16f / tw, Sext(a0 >> 16, 16) / 16f / th);
                    }
                    break;
                case InstructionCode.VTX_16:
                    vtx = new Vector3(Sext(a0, 16) / 4096f, Sext(a0 >> 16, 16) / 4096f, Sext(ins.Arguments[1], 16) / 4096f);
                    Emit();
                    break;
                case InstructionCode.VTX_10:
                    vtx = new Vector3(Sext(a0, 10) / 64f, Sext(a0 >> 10, 10) / 64f, Sext(a0 >> 20, 10) / 64f);
                    Emit();
                    break;
                case InstructionCode.VTX_XY:
                    vtx = new Vector3(Sext(a0, 16) / 4096f, Sext(a0 >> 16, 16) / 4096f, vtx.Z);
                    Emit();
                    break;
                case InstructionCode.VTX_XZ:
                    vtx = new Vector3(Sext(a0, 16) / 4096f, vtx.Y, Sext(a0 >> 16, 16) / 4096f);
                    Emit();
                    break;
                case InstructionCode.VTX_YZ:
                    vtx = new Vector3(vtx.X, Sext(a0, 16) / 4096f, Sext(a0 >> 16, 16) / 4096f);
                    Emit();
                    break;
                case InstructionCode.VTX_DIFF:
                    vtx += new Vector3(Sext(a0, 10) / 4096f, Sext(a0 >> 10, 10) / 4096f, Sext(a0 >> 20, 10) / 4096f);
                    Emit();
                    break;
                case InstructionCode.MTX_RESTORE:
                    matrixId = (int)a0;
                    break;
                }
            }
            return tris;
        }
    }
}
