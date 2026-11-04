// EffectView - draws a Core.EffectSystem's triangles (FxDrawList) in Unity: one dynamic mesh, one submesh per
// texture, Sprites/Default (alpha blend x vertex color, no culling, no depth writes) like MPH's translucent particle
// render items. Positions come in the system's MPH space and are mirrored on X and scaled by k into the view's
// local space (world: a root object at the origin; arm cannon: a child of the camera on the weapon layer).
using System.Collections.Generic;
using MphSamus.Core;
using UnityEngine;
using Material = UnityEngine.Material;
using Mesh = UnityEngine.Mesh;
using RepeatMode = MphRead.RepeatMode;

namespace MphSamus
{
    public sealed class EffectView
    {
        private static readonly List<Material> _materials = new List<Material>();   // by EffectSystem.Textures index
        private static Shader _shader;

        private readonly GameObject _go;
        private readonly Mesh _mesh;
        private readonly MeshRenderer _renderer;
        private readonly FxDrawList _list = new FxDrawList();
        private readonly List<Vector3> _verts = new List<Vector3>();
        private readonly List<Vector2> _uvs = new List<Vector2>();
        private readonly List<Color32> _colors = new List<Color32>();
        private readonly List<int> _tris = new List<int>();
        private Material[] _mats = new Material[0];

        public EffectView(string name, Transform parent, int layer, int sortingOrder)
        {
            _go = new GameObject(name) { layer = layer };
            if (parent != null) { _go.transform.SetParent(parent, false); }
            _mesh = new Mesh { name = "mph fx" };
            _mesh.MarkDynamic();
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.sortingOrder = sortingOrder;
        }

        private static Material Mat(int texture)
        {
            while (_materials.Count <= texture) { _materials.Add(null); }
            if (_materials[texture] == null)
            {
                if (_shader == null) { _shader = Shader.Find("Sprites/Default") ?? MphAssets.ModelShader; }
                FxTexture t = EffectSystem.Textures[texture];
                Texture2D tex = MphAssets.ToTexture(t.Image, "mph_fx_" + texture, Wrap(t.RepeatS), Wrap(t.RepeatT));
                var m = new Material(_shader) { mainTexture = tex, renderQueue = 3100 };
                Object.DontDestroyOnLoad(m);
                _materials[texture] = m;
            }
            return _materials[texture];
        }

        private static TextureWrapMode Wrap(RepeatMode m) => m == RepeatMode.Clamp ? TextureWrapMode.Clamp
            : m == RepeatMode.Mirror ? TextureWrapMode.Mirror : TextureWrapMode.Repeat;

        // camRight/Up/Back: the camera's axes in the system's MPH space. scale = k (MPH units -> Unity units).
        public void Draw(EffectSystem fx, OpenTK.Mathematics.Vector3 camRight, OpenTK.Mathematics.Vector3 camUp,
            OpenTK.Mathematics.Vector3 camBack, float scale, bool visible)
        {
            _list.Clear();
            fx.Draw(_list, camRight, camUp, camBack);   // always (clears the one-frame sprites)
            if (!visible || _list.TriangleCount == 0)
            {
                if (_go.activeSelf) { _go.SetActive(false); }
                return;
            }
            if (!_go.activeSelf) { _go.SetActive(true); }
            _verts.Clear();
            _uvs.Clear();
            _colors.Clear();
            _mesh.Clear();
            if (_mats.Length != _list.GroupCount) { _mats = new Material[_list.GroupCount]; }
            for (int gi = 0; gi < _list.GroupCount; gi++)
            {
                FxDrawList.Group g = _list.Groups[gi];
                for (int i = 0; i < g.Positions.Count; i++)
                {
                    OpenTK.Mathematics.Vector3 p = g.Positions[i];
                    _verts.Add(new Vector3(-p.X * scale, p.Y * scale, p.Z * scale));
                    _uvs.Add(new Vector2(g.Uvs[i].X, 1 - g.Uvs[i].Y));   // MPH t goes down the image
                    uint c = g.Colors[i];
                    _colors.Add(new Color32((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24)));
                }
                _mats[gi] = Mat(g.Texture);
            }
            _mesh.SetVertices(_verts);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetColors(_colors);
            _mesh.subMeshCount = _list.GroupCount;
            int start = 0;
            for (int gi = 0; gi < _list.GroupCount; gi++)
            {
                int n = _list.Groups[gi].Positions.Count;
                _tris.Clear();
                for (int i = 0; i < n; i++) { _tris.Add(start + i); }
                _mesh.SetTriangles(_tris, gi, false);
                start += n;
            }
            _mesh.RecalculateBounds();
            _renderer.sharedMaterials = _mats;
        }

        public void Destroy()
        {
            if (_go != null) { Object.Destroy(_go); }
        }
    }
}
