// ModelView - draws a Core.PosedModel in Unity. One child MeshRenderer per batch (MPH mesh), textures from the ROM
// with MPH's wrap modes, vertex colors = MPH lighting. MPH is right-handed: positions are mirrored on X (MPH +X is
// "left", Unity +X is right), which also flips the triangle winding, so triangles are written in reverse order.
// Shaders without depth writes are handled by sorting triangles back to front (and batches by depth).
// Face culling is done here on the CPU (the shaders draw both sides): MPH materials cull back or front faces like
// MphRead's GL.CullFace (counter-clockwise = front, in MPH space). Without it the arm cannon's stripe insides covered
// its red grooves (owner: "placeholder beam textures" - compared with the testbed 2026-10-31).
using System.Collections.Generic;
using RepeatMode = MphRead.RepeatMode;
using Material = UnityEngine.Material;
using Mesh = UnityEngine.Mesh;
using MphSamus.Core;
using UnityEngine;

namespace MphSamus
{
    public sealed class ModelView
    {
        public GameObject Root { get; }
        public PosedModel Model { get; }
        public float Scale = 1;              // MPH units -> Unity units (applied to positions)
        private readonly List<Part> _parts = new List<Part>();
        private readonly Dictionary<(int, bool, int, int), Material> _materials = new Dictionary<(int, bool, int, int), Material>();
        private readonly Dictionary<(int, int, int), Material> _depthMaterials = new Dictionary<(int, int, int), Material>();
        private readonly Material[] _two = new Material[2];
        private readonly Material[] _one = new Material[1];
        // one Unity texture per (image, wrap): materials sharing an image can wrap it differently (Sylux's ball shell
        // repeats the glow texture its billboard mirrors)
        private readonly Dictionary<(int, TextureWrapMode, TextureWrapMode), Texture2D> _textures =
            new Dictionary<(int, TextureWrapMode, TextureWrapMode), Texture2D>();
        private int _layer;

        private sealed class Part
        {
            public GameObject Go;
            public Mesh Mesh;
            public MeshRenderer Renderer;
            public Vector3[] Verts = new Vector3[0];
            public Vector2[] Uvs = new Vector2[0];
            public Color32[] Colors = new Color32[0];
            public int[] Tris = new int[0];
            public int[] DepthTris = new int[0];   // both windings: depth is right whatever the culling/winding
            public float[] Keys = new float[0];
            public int[] Order = new int[0];
        }

        public ModelView(PosedModel model, string name, Transform parent, int layer)
        {
            Model = model;
            Root = new GameObject(name);
            Root.transform.SetParent(parent, false);
            _layer = layer;
            Root.layer = layer;
        }

        public void SetLayer(int layer)
        {
            _layer = layer;
            Root.layer = layer;
            foreach (Part p in _parts) { p.Go.layer = layer; }
        }

        // Unity textures by decoded image (shared decodes: every hunter of a kind uses the same ones)
        private static readonly Dictionary<(RgbaImage, TextureWrapMode, TextureWrapMode), Texture2D> _sharedTex =
            new Dictionary<(RgbaImage, TextureWrapMode, TextureWrapMode), Texture2D>();

        private Texture2D Tex(int key, RepeatMode s, RepeatMode t)
        {
            if (key < 0) { return Texture2D.whiteTexture; }
            var local = (key, Wrap(s), Wrap(t));
            if (!_textures.TryGetValue(local, out Texture2D tex) || tex == null)
            {
                var k = (Model.Textures[key], Wrap(s), Wrap(t));
                if (!_sharedTex.TryGetValue(k, out tex) || tex == null)
                {
                    tex = MphAssets.ToTexture(Model.Textures[key], $"{Model.Model.Name}_{key}", Wrap(s), Wrap(t));
                    _sharedTex[k] = tex;
                }
                _textures[local] = tex;
            }
            return tex;
        }

        private static TextureWrapMode Wrap(RepeatMode m) => m == RepeatMode.Clamp ? TextureWrapMode.Clamp
            : m == RepeatMode.Mirror ? TextureWrapMode.Mirror : TextureWrapMode.Repeat;

        private Material Mat(PosedModel.Batch b)
        {
            var key = (b.TextureKey, b.Translucent, (int)b.RepeatS, (int)b.RepeatT);
            if (!_materials.TryGetValue(key, out Material m))
            {
                m = MphAssets.NewModelMaterial(Tex(b.TextureKey, b.RepeatS, b.RepeatT), b.Translucent);
                _materials[key] = m;
            }
            return m;
        }

        private Material DepthMat(PosedModel.Batch b)
        {
            var key = (b.TextureKey, (int)b.RepeatS, (int)b.RepeatT);
            if (!_depthMaterials.TryGetValue(key, out Material m))
            {
                m = MphAssets.NewDepthMaterial(Tex(b.TextureKey, b.RepeatS, b.RepeatT));
                _depthMaterials[key] = m;
            }
            return m;
        }

        // Upload the posed batches. cameraLocal: camera position in Root's local Unity space (for sorting).
        public void Upload(Vector3 cameraLocal, int baseQueue)
        {
            bool prepass = MphAssets.DepthShader != null;
            while (_parts.Count < Model.BatchCount)
            {
                var go = new GameObject("batch" + _parts.Count) { layer = _layer };
                go.transform.SetParent(Root.transform, false);
                var mesh = new Mesh { name = "mph" };
                mesh.MarkDynamic();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                _parts.Add(new Part { Go = go, Mesh = mesh, Renderer = r });
            }
            var depths = new List<(float d, int i)>();
            for (int i = 0; i < _parts.Count; i++)
            {
                Part p = _parts[i];
                bool on = i < Model.BatchCount;
                if (p.Go.activeSelf != on) { p.Go.SetActive(on); }
                if (!on) { continue; }
                PosedModel.Batch b = Model.Batches[i];
                int n = b.Count;
                if (p.Verts.Length != n)
                {
                    p.Verts = new Vector3[n];
                    p.Uvs = new Vector2[n];
                    p.Colors = new Color32[n];
                    p.Tris = new int[n];
                    p.DepthTris = new int[n * 2];
                    p.Keys = new float[n / 3];
                    p.Order = new int[n / 3];
                    p.Mesh.Clear();
                }
                Vector3 sum = Vector3.zero;
                for (int v = 0; v < n; v++)
                {
                    OpenTK.Mathematics.Vector3 q = b.Positions[v];
                    var u = new Vector3(-q.X * Scale, q.Y * Scale, q.Z * Scale);
                    p.Verts[v] = u;
                    sum += u;
                    p.Uvs[v] = new Vector2(b.Uvs[v].X, 1 - b.Uvs[v].Y);   // MPH t goes down the image
                    p.Colors[v] = new Color32(b.Colors[v * 4], b.Colors[v * 4 + 1], b.Colors[v * 4 + 2], b.Colors[v * 4 + 3]);
                }
                int triCount = n / 3;
                bool depthPass = prepass && !b.Translucent;
                // camera in MPH model space (undo the X mirror and the scale)
                var camMph = new OpenTK.Mathematics.Vector3(-cameraLocal.x / Scale, cameraLocal.y / Scale, cameraLocal.z / Scale);
                MphRead.CullingMode cull = b.Culling;
                // translucent meshes draw in the model's order, unsorted, like the testbed / DS (sorting put a flat
                // stripe over the arm cannon's red gradient one); only opaque meshes without a depth pre-pass are sorted
                bool sort = !MphAssets.ShaderWritesDepth && !depthPass && !b.Translucent;
                for (int t = 0; t < triCount; t++)
                {
                    p.Order[t] = t;
                    if (sort)
                    {
                        Vector3 c = (p.Verts[t * 3] + p.Verts[t * 3 + 1] + p.Verts[t * 3 + 2]) / 3f;
                        p.Keys[t] = -(c - cameraLocal).sqrMagnitude;   // far first
                    }
                }
                if (sort) { System.Array.Sort(p.Keys, p.Order, 0, triCount); }
                for (int t = 0; t < triCount; t++)
                {
                    int s = p.Order[t] * 3;
                    if (cull != MphRead.CullingMode.Neither)
                    {
                        OpenTK.Mathematics.Vector3 a0 = b.Positions[s], a1 = b.Positions[s + 1], a2 = b.Positions[s + 2];
                        bool front = OpenTK.Mathematics.Vector3.Dot(OpenTK.Mathematics.Vector3.Cross(a1 - a0, a2 - a0), camMph - a0) > 0;
                        if (front == (cull == MphRead.CullingMode.Front))
                        {
                            p.Tris[t * 3] = p.Tris[t * 3 + 1] = p.Tris[t * 3 + 2] = s;   // culled: degenerate
                            continue;
                        }
                    }
                    // mirrored on X: reverse the winding
                    p.Tris[t * 3] = s;
                    p.Tris[t * 3 + 1] = s + 2;
                    p.Tris[t * 3 + 2] = s + 1;
                }
                p.Mesh.vertices = p.Verts;
                p.Mesh.uv = p.Uvs;
                p.Mesh.colors32 = p.Colors;
                if (depthPass)
                {
                    for (int t = 0; t < n; t++)
                    {
                        p.DepthTris[t] = p.Tris[t];
                        p.DepthTris[n + t] = p.Tris[t - t % 3 + (2 - t % 3)];   // reversed winding copy
                    }
                    p.Mesh.subMeshCount = 2;
                    p.Mesh.SetTriangles(p.DepthTris, 0, false);
                    p.Mesh.SetTriangles(p.Tris, 1, false);
                    _two[0] = DepthMat(b);
                    _two[1] = Mat(b);
                    p.Renderer.sharedMaterials = _two;
                }
                else
                {
                    p.Mesh.subMeshCount = 1;
                    p.Mesh.SetTriangles(p.Tris, 0, false);
                    _one[0] = Mat(b);
                    p.Renderer.sharedMaterials = _one;
                }
                p.Mesh.RecalculateBounds();
                depths.Add((b.Translucent ? -1 - i : n > 0 ? (sum / n - cameraLocal).sqrMagnitude : 0, i));
            }
            if (!MphAssets.ShaderWritesDepth)
            {
                // farther batches first (transparent-queue renderers sort by sortingOrder before distance)
                depths.Sort((a, c) => c.d.CompareTo(a.d));
                for (int k = 0; k < depths.Count; k++)
                {
                    _parts[depths[k].i].Renderer.sortingOrder = baseQueue + k;
                }
            }
        }

        public void SetVisible(bool v)
        {
            if (Root.activeSelf != v) { Root.SetActive(v); }
        }

        public void Destroy()
        {
            Object.Destroy(Root);
        }
    }
}
