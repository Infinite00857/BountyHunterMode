// EffectSystem - Metroid Prime Hunters particle effects (VFX), engine-agnostic and steppable headlessly (MphSim).
// Ported from the testbed's hunters/mph_effects.cpp (playtested 2026-10-27), which ports MphRead (MIT):
// Formats/Effects.cs (EffectFuncBase fx funcs, SetVecs/Draw funcs) and Renderer.cs (SpawnEffect, ProcessEffects,
// UnlinkEffectEntry, DetachEffectEntry). Effect definitions are read straight from the ROM cache by MphRead's
// Read.LoadEffect (effects/*_PS.bin); particle textures/meshes come from the particle models (particles, particles2,
// geo1, ...), the beam ribbon from the "trail" model.
//
// Usage: Spawn() returns a handle for effects the owner moves/ends itself (muzzle, charge); SpawnOneShot() for
// fire-and-forget effects (impacts). Step() once per 60 Hz frame after the owners moved their effects. AddSingle /
// AddTrail queue one-frame sprites for the next Draw(). Draw() fills an FxDrawList (triangles per texture, positions
// in the system's space) that the engine uploads.
// Space: whatever the owner uses consistently (MPH units, right-handed). The ULTRAKILL plugin runs one system in
// world space (Unity / k, X mirrored) and one in the arm cannon's camera space.
using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Effects;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    public struct FxHandle
    {
        internal int Slot;   // entry index + 1 (0 = none, so default(FxHandle) is "no effect")
        internal uint Gen;
        public bool Valid => Slot > 0;
    }

    // A texture the effects draw with (shared by all systems; the engine turns each into one material).
    public sealed class FxTexture
    {
        public RgbaImage Image = new RgbaImage(1, 1);
        public RepeatMode RepeatS, RepeatT;
    }

    // Triangles to draw this frame, grouped by texture (index into EffectSystem.Textures).
    public sealed class FxDrawList
    {
        public sealed class Group
        {
            public int Texture;
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();       // texture units (MPH t goes down the image)
            public readonly List<uint> Colors = new List<uint>();          // RGBA8 packed r | g<<8 | b<<16 | a<<24
        }
        public readonly List<Group> Groups = new List<Group>();
        public int GroupCount;
        public int TriangleCount;

        public void Clear()
        {
            for (int i = 0; i < GroupCount; i++)
            {
                Groups[i].Positions.Clear();
                Groups[i].Uvs.Clear();
                Groups[i].Colors.Clear();
            }
            GroupCount = 0;
            TriangleCount = 0;
        }

        internal Group For(int texture)
        {
            for (int i = 0; i < GroupCount; i++)
            {
                if (Groups[i].Texture == texture) { return Groups[i]; }
            }
            if (GroupCount == Groups.Count) { Groups.Add(new Group()); }
            Group g = Groups[GroupCount++];
            g.Texture = texture;
            return g;
        }
    }

    public sealed class EffectSystem
    {
        // ---- TUNABLE CONSTANTS (pool sizes; MPH has fixed effect heaps too) ----
        public const int MaxEntries = 64;       // effects with a handle
        public const int MaxElements = 256;     // running elements (an effect has 1-4)
        public const int MaxParticles = 2000;   // live particles over all elements
        private const float StepTime = 1 / 60f; // MphRead advances effects with a 60 Hz frame time
        private const float Fx = 1 / 4096f;     // 20.12 fixed point
        private const float Deg = MathF.PI / 180;

        // ---- shared assets (textures/particles are the same for every system) ----
        public static readonly List<FxTexture> Textures = new List<FxTexture>();
        private static readonly Dictionary<(string, int, int), int> _texKeys = new Dictionary<(string, int, int), int>();
        private static readonly Dictionary<Particle, PInfo> _pinfo = new Dictionary<Particle, PInfo>();
        private static readonly Dictionary<(string, string), Particle?> _named = new Dictionary<(string, string), Particle?>();
        private static readonly Dictionary<string, (int Tex, int W, int H, float Su, float Sv)> _trailTex = new Dictionary<string, (int, int, int, float, float)>();

        private sealed class PInfo
        {
            public int Tex = -1;
            public float ScaleU = 1, ScaleV = 1;      // mirror-wrapped textures: texcoords * material ScaleS/T
            public List<MeshPart>? Meshes;            // UseMesh particles
        }

        private sealed class MeshPart
        {
            public int Tex;
            public float Alpha;                       // material alpha (0-31) / 31
            public Matrix4 Uv = Matrix4.Identity;     // texcoord matrix (row-vector)
            public List<PosedModel.MeshVertex> Verts = new List<PosedModel.MeshVertex>();
        }

        private sealed class Particle_
        {
            public float Creation, Expiration, Lifespan;
            public Vector3 Pos, Speed;
            public float Scale, Rotation, Red = 1, Green = 1, Blue = 1, Alpha = 1;
            public int ParticleId;
            public float PortionTotal;
            public readonly float[] Ro = new float[4];
            public readonly float[] Rw = new float[4];

            public void Reset()
            {
                Creation = Expiration = Lifespan = 0;
                Pos = Speed = Vector3.Zero;
                Scale = Rotation = 0;
                Red = Green = Blue = Alpha = 1;
                ParticleId = 0;
                PortionTotal = 0;
                Array.Clear(Ro, 0, 4);
                Array.Clear(Rw, 0, 4);
            }
        }

        private sealed class Element
        {
            public bool Active;
            public Effect Effect = null!;
            public EffectElement Def = null!;
            public int Entry = -1;                    // owning entry, -1 = none
            public float Creation, Expiration, Lifespan, DrainTime, BufferTime;
            public EffElemFlags Flags;
            public Matrix4 Own, Transform;
            public float Amount;
            public bool Expired, Func39Called;
            public int Parity;
            public readonly float[] Ro = new float[4];
            public readonly List<Particle_> Particles = new List<Particle_>();
        }

        private sealed class Entry
        {
            public bool Active;
            public uint Gen = 1;
            public int EffectId;
            public readonly List<int> Elements = new List<int>();
        }

        private struct Times
        {
            public float Global, Elapsed, Lifespan;
            public Times(float g, float e, float l) { Global = g; Elapsed = e; Lifespan = l; }
        }

        private struct Single
        {
            public Particle Particle;
            public Vector3 Pos, Color;
            public float Alpha, HalfSize;
        }

        private struct Trail
        {
            public Vector3 V0, V1, V2, V3;
            public Vector2 T0, T1, T2, T3;
            public Vector3 Color;
            public float Alpha, Su, Sv;
            public int Tex;
        }

        private readonly Element[] _elements = new Element[MaxElements];
        private readonly Entry[] _entries = new Entry[MaxEntries];
        private readonly List<Single> _singles = new List<Single>();
        private readonly List<Trail> _trails = new List<Trail>();
        private readonly Stack<Particle_> _pool = new Stack<Particle_>();
        private int _particleCount;
        private float _elapsed;          // MphRead Scene._elapsedTime (seconds)
        private uint _frameCount;        // Scene._frameCount
        private uint _rng1 = 0x3DE9179B; // Rng.Rng1StartValue

        public EffectSystem()
        {
            for (int i = 0; i < MaxElements; i++) { _elements[i] = new Element(); }
            for (int i = 0; i < MaxEntries; i++) { _entries[i] = new Entry(); }
        }

        public int LiveParticles => _particleCount;
        public int LiveElements
        {
            get
            {
                int n = 0;
                foreach (Element e in _elements) { n += e.Active ? 1 : 0; }
                return n;
            }
        }

        // ---------------------------------------------------------------- assets

        // Loads an effect (and its child effects) from the ROM cache. null = missing/bad (the effect then does nothing).
        public static Effect? GetEffect(int id)
        {
            Effect? fx = Read.GetEffect(id);
            if (fx != null) { return fx; }
            try
            {
                return Read.LoadEffect(id, persistent: true);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int TexKey(Model model, int textureId, int paletteId, RepeatMode s, RepeatMode t)
        {
            var key = (model.Name + "/" + (int)s + (int)t, textureId, paletteId);
            if (_texKeys.TryGetValue(key, out int k)) { return k; }
            RgbaImage? img = PosedModel.DecodeTexture(model, textureId, paletteId);
            k = Textures.Count;
            Textures.Add(new FxTexture { Image = img ?? WhiteImage(), RepeatS = s, RepeatT = t });
            _texKeys[key] = k;
            return k;
        }

        private static RgbaImage WhiteImage()
        {
            var img = new RgbaImage(1, 1);
            img.Pixels[0] = img.Pixels[1] = img.Pixels[2] = img.Pixels[3] = 255;
            return img;
        }

        private static int MaterialTex(Model model, Material mat) => mat.TextureId >= 0
            ? TexKey(model, mat.TextureId, mat.PaletteId, mat.XRepeat, mat.YRepeat)
            : TexKey(model, -1, -1, RepeatMode.Clamp, RepeatMode.Clamp);

        private static PInfo Info(Particle p)
        {
            if (_pinfo.TryGetValue(p, out PInfo? info)) { return info; }
            info = new PInfo();
            Material mat = p.Model.Materials[p.MaterialId];
            info.Tex = MaterialTex(p.Model, mat);
            info.ScaleU = mat.XRepeat == RepeatMode.Mirror ? mat.ScaleS : 1;
            info.ScaleV = mat.YRepeat == RepeatMode.Mirror ? mat.ScaleT : 1;
            _pinfo[p] = info;
            return info;
        }

        private static List<MeshPart> Meshes(Particle p)
        {
            PInfo info = Info(p);
            if (info.Meshes != null) { return info.Meshes; }
            info.Meshes = new List<MeshPart>();
            Model m = p.Model;
            int start = p.Node.MeshId / 2;
            for (int i = start; i < start + p.Node.MeshCount && i < m.Meshes.Count; i++)
            {
                Material mat = m.Materials[m.Meshes[i].MaterialId];
                var part = new MeshPart { Tex = MaterialTex(m, mat), Alpha = mat.Alpha / 31f, Verts = PosedModel.DecodeMesh(m, i) };
                if (mat.TexgenMode == TexgenMode.Texcoord)
                {
                    part.Uv = Matrix4.CreateScale(mat.ScaleS, mat.ScaleT, 1)
                        * Matrix4.CreateTranslation(mat.ScaleS * mat.TranslateS, mat.ScaleT * mat.TranslateT, 0);
                }
                info.Meshes.Add(part);
            }
            return info.Meshes;
        }

        // A particle-model node by name (single sprites such as the beam's fuzzBall). null = not found.
        public static Particle? FindParticle(string model, string node)
        {
            if (_named.TryGetValue((model, node), out Particle? p)) { return p; }
            p = null;
            try
            {
                Model m = Read.GetModelInstance(model).Model;
                foreach (Node n in m.Nodes)
                {
                    if (n.Name == node && n.MeshCount > 0)
                    {
                        p = new Particle(node, m, n, m.Meshes[n.MeshId / 2].MaterialId);
                        break;
                    }
                }
            }
            catch (Exception)
            {
            }
            _named[(model, node)] = p;
            return p;
        }

        // Beam ribbon texture (BeamProjectileEntity: trail / electroTrail / arcWelder models, material 0).
        public static bool TrailTexture(string model, out int tex, out int width, out int height)
        {
            if (!_trailTex.TryGetValue(model, out (int Tex, int W, int H, float Su, float Sv) t))
            {
                t = (-1, 1, 1, 1, 1);
                try
                {
                    Model m = Read.GetModelInstance(model).Model;
                    Material mat = m.Materials[0];
                    t.Tex = MaterialTex(m, mat);
                    t.Su = mat.XRepeat == RepeatMode.Mirror ? mat.ScaleS : 1;
                    t.Sv = mat.YRepeat == RepeatMode.Mirror ? mat.ScaleT : 1;
                    if (mat.TextureId >= 0)
                    {
                        t.W = m.Recolors[0].Textures[mat.TextureId].Width;
                        t.H = m.Recolors[0].Textures[mat.TextureId].Height;
                    }
                }
                catch (Exception)
                {
                }
                _trailTex[model] = t;
            }
            tex = t.Tex;
            width = t.W;
            height = t.H;
            return tex >= 0;
        }

        // MphRead GetTransformMatrix(facing, up) + position (rows: up x facing, up, facing, position).
        public static Matrix4 Transform(Vector3 facing, Vector3 up, Vector3 position)
        {
            Matrix4 m = EntityBase.GetTransformMatrix(facing, up);
            m.Row3 = new Vector4(position, 1);
            return m;
        }

        // BeamProjectileEntity.GetCrossVector: a vector perpendicular to up.
        public static Vector3 CrossVector(Vector3 up)
        {
            if (up.Z <= -3686 * Fx || up.Z >= 3686 * Fx)
            {
                return Vector3.Cross(Vector3.UnitX, up).Normalized();
            }
            return Vector3.Cross(Vector3.UnitZ, up).Normalized();
        }

        // ---------------------------------------------------------------- entries (MphRead Renderer.cs)

        private Entry? EntryFor(FxHandle h)
        {
            if (h.Slot <= 0 || h.Slot > MaxEntries) { return null; }
            Entry e = _entries[h.Slot - 1];
            return e.Active && e.Gen == h.Gen ? e : null;
        }

        public bool Alive(FxHandle h) => EntryFor(h) != null;

        // SpawnEffectGetEntry
        public FxHandle Spawn(int effectId, Matrix4 transform)
        {
            Effect? fx = GetEffect(effectId);
            if (fx == null) { return default; }
            for (int i = 0; i < MaxEntries; i++)
            {
                Entry e = _entries[i];
                if (e.Active) { continue; }
                e.Active = true;
                e.Gen++;
                e.EffectId = effectId;
                e.Elements.Clear();
                SpawnInto(fx, transform, false, i);
                return new FxHandle { Slot = i + 1, Gen = e.Gen };
            }
            return default;
        }

        // SpawnEffect (fire and forget)
        public void SpawnOneShot(int effectId, Matrix4 transform, bool child = false)
        {
            Effect? fx = GetEffect(effectId);
            if (fx != null) { SpawnInto(fx, transform, child, -1); }
        }

        private void SpawnInto(Effect fx, Matrix4 transform, bool child, int entry)
        {
            foreach (EffectElement def in fx.Elements)
            {
                // InitEffectElement
                Element? el = null;
                int slot = -1;
                for (int i = 0; i < MaxElements; i++)
                {
                    if (!_elements[i].Active) { el = _elements[i]; slot = i; break; }
                }
                if (el == null) { return; }
                el.Active = true;
                el.Effect = fx;
                el.Def = def;
                el.Entry = entry;
                el.BufferTime = def.BufferTime;
                el.Creation = _elapsed + (child ? StepTime : 0);
                el.DrainTime = def.DrainTime;
                el.Lifespan = def.Lifespan;
                el.Expiration = el.Creation + el.Lifespan;
                el.Flags = def.Flags | EffElemFlags.DrawEnabled;
                el.Func39Called = false;
                el.Amount = 0;
                el.Expired = false;
                el.Parity = (int)(_frameCount % 2);
                Array.Clear(el.Ro, 0, 4);
                ReleaseParticles(el);
                if (entry >= 0) { _entries[entry].Elements.Add(slot); }
                // SpawnUnitVecs replaces the transform's rotation (and keeps it for later elements)
                if ((el.Flags & EffElemFlags.SpawnUnitVecs) != 0)
                {
                    transform = MphRead.Matrix.GetTransform4(Vector3.UnitX, Vector3.UnitY, transform.Row3.Xyz);
                }
                el.Own = el.Transform = transform;
            }
        }

        private void ReleaseParticles(Element el)
        {
            _particleCount -= el.Particles.Count;
            foreach (Particle_ p in el.Particles) { _pool.Push(p); }
            el.Particles.Clear();
        }

        private void UnlinkElement(int index)
        {
            Element el = _elements[index];
            ReleaseParticles(el);
            el.Active = false;
            el.Entry = -1;
        }

        // UnlinkEffectEntry: removes the effect and its particles now
        public void Unlink(ref FxHandle h)
        {
            Entry? e = EntryFor(h);
            h = default;
            if (e == null) { return; }
            foreach (int i in e.Elements) { UnlinkElement(i); }
            e.Elements.Clear();
            e.Active = false;
        }

        // DetachEffectEntry: let live particles finish
        public void Detach(ref FxHandle h, bool setExpired)
        {
            Entry? e = EntryFor(h);
            h = default;
            if (e == null) { return; }
            foreach (int i in e.Elements)
            {
                Element el = _elements[i];
                if ((el.Flags & EffElemFlags.DestroyOnDetach) != 0)
                {
                    UnlinkElement(i);
                }
                else
                {
                    el.Flags &= ~EffElemFlags.ElementExtension;
                    el.Flags |= EffElemFlags.KeepAlive;
                    el.Entry = -1;
                    if (setExpired) { el.Expired = true; }
                }
            }
            e.Elements.Clear();
            e.Active = false;
        }

        // EffectEntry.IsFinished
        public bool IsFinished(FxHandle h)
        {
            Entry? e = EntryFor(h);
            if (e == null) { return true; }
            foreach (int i in e.Elements)
            {
                Element el = _elements[i];
                if (!el.Expired || el.Particles.Count > 0) { return false; }
            }
            return true;
        }

        // EffectEntry.Transform
        public void SetTransform(FxHandle h, Matrix4 transform)
        {
            Entry? e = EntryFor(h);
            if (e == null) { return; }
            foreach (int i in e.Elements) { _elements[i].Own = transform; }
        }

        // Draw-time follow (not MPH): move the effect now, not at the next step, so effects that draw relative to their
        // element (UseTransform) stay on an object that moved since (the Morph Ball's boost glow lagged behind it)
        public void SetTransformNow(FxHandle h, Matrix4 transform)
        {
            Entry? e = EntryFor(h);
            if (e == null) { return; }
            foreach (int i in e.Elements) { _elements[i].Own = _elements[i].Transform = transform; }
        }

        public void SetElementExtension(FxHandle h, bool on)
        {
            Entry? e = EntryFor(h);
            if (e == null) { return; }
            foreach (int i in e.Elements)
            {
                Element el = _elements[i];
                el.Flags = on ? el.Flags | EffElemFlags.ElementExtension : el.Flags & ~EffElemFlags.ElementExtension;
            }
        }

        public void SetDrawEnabled(FxHandle h, bool on)
        {
            Entry? e = EntryFor(h);
            if (e == null) { return; }
            foreach (int i in e.Elements)
            {
                Element el = _elements[i];
                el.Flags = on ? el.Flags | EffElemFlags.DrawEnabled : el.Flags & ~EffElemFlags.DrawEnabled;
            }
        }

        public void Clear()
        {
            for (int i = 0; i < MaxElements; i++)
            {
                if (_elements[i].Active) { UnlinkElement(i); }
            }
            foreach (Entry e in _entries)
            {
                e.Active = false;
                e.Elements.Clear();
            }
            _singles.Clear();
            _trails.Clear();
        }

        // One-frame camera-facing sprite of a particle-model node (MphRead AddSingleParticle, e.g. the beam's
        // fuzzBall). halfSize = MphRead's Scale (the quad spans +-halfSize).
        public void AddSingle(Particle particle, Vector3 pos, Vector3 color, float alpha, float halfSize)
        {
            _singles.Add(new Single { Particle = particle, Pos = pos, Color = color, Alpha = alpha, HalfSize = halfSize });
        }

        // One-frame beam ribbon (BeamProjectileEntity.DrawTrail1): back -> front, +-height along up.
        public void AddTrail(string model, Vector3 back, Vector3 front, Vector3 up, float height, Vector3 color, float alpha)
        {
            if (!TrailTexture(model, out int tex, out int tw, out int th)) { return; }
            (_, _, _, float su, float sv) = _trailTex[model];
            // RenderTrailSingle: quad (front-h, front+h, back+h, back-h), texcoords (0,0) (0,T) (S,T) (S,0)
            float s = (tw - 1 / 16f) / tw, t = (th - 1 / 16f) / th;
            Vector3 h = up * height;
            _trails.Add(new Trail
            {
                V0 = front - h, V1 = front + h, V2 = back + h, V3 = back - h,
                T0 = new Vector2(0, 0), T1 = new Vector2(0, t), T2 = new Vector2(s, t), T3 = new Vector2(s, 0),
                Color = color, Alpha = alpha, Tex = tex, Su = su, Sv = sv
            });
        }

        // One-frame quad with a trail model's texture (Morph Ball trail segments). Corners in order, uv in texture units.
        public void AddTrailQuad(string model, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
            Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3, Vector3 color, float alpha)
        {
            if (!TrailTexture(model, out int tex, out _, out _)) { return; }
            (_, _, _, float su, float sv) = _trailTex[model];
            _trails.Add(new Trail
            {
                V0 = v0, V1 = v1, V2 = v2, V3 = v3, T0 = t0, T1 = t1, T2 = t2, T3 = t3,
                Color = color, Alpha = alpha, Tex = tex, Su = su, Sv = sv
            });
        }

        // ---------------------------------------------------------------- fx funcs (MphRead EffectFuncBase)

        private uint Rand1(uint max)
        {
            // Rng.GetRandomInt1
            _rng1 = _rng1 * 0x7FF8A3ED + 0x2AA01D31;
            return (uint)(((ulong)(_rng1 >> 16) * max) / 0x10000);
        }

        private float RandF() => Rand1(4096) * Fx;

        private static int P(IReadOnlyList<int> p, int i, int missing = int.MinValue) => i < p.Count ? p[i] : missing;

        private static FxFuncInfo? FuncAt(Effect fx, int offset)
        {
            return fx.Funcs.TryGetValue((uint)offset, out FxFuncInfo? f) ? f : null;
        }

        private float EvalFloatAt(Effect fx, int offset, Times t, Element el, Particle_? pt)
        {
            FxFuncInfo? f = FuncAt(fx, offset);
            return f == null ? 0 : EvalFloat(fx, f.FuncId, f.Parameters, t, el, pt);
        }

        private void EvalVecAt(Effect fx, int offset, Times t, Element el, Particle_? pt, ref Vector3 v)
        {
            FxFuncInfo? f = FuncAt(fx, offset);
            if (f != null) { EvalVec(fx, f, t, el, pt, ref v); }
        }

        private float EvalFloat(Effect fx, FxFuncInfo f, Times t, Element el, Particle_? pt)
            => EvalFloat(fx, f.FuncId, f.Parameters, t, el, pt);

        private float EvalFloat(Effect fx, uint id, IReadOnlyList<int> p, Times t, Element el, Particle_? pt)
        {
            switch (id)
            {
            case 21:
            case 28:
                return 0;
            case 22: return el.Lifespan;
            case 23: return t.Global - el.Creation;
            // 24-38 read the particle (the element versions throw in MphRead; return 0 here)
            case 24: return pt?.Alpha ?? 0;
            case 25: return pt?.Red ?? 0;
            case 26: return pt?.Green ?? 0;
            case 27: return pt?.Blue ?? 0;
            case 29: return pt?.Scale ?? 0;
            case 30: return pt?.Rotation ?? 0;
            case 31: case 32: case 33: case 34: return pt?.Ro[id - 31] ?? 0;
            case 35: case 36: case 37: case 38: return pt?.Rw[id - 35] ?? 0;
            case 39:
                if (el.Func39Called) { return 0; }
                el.Func39Called = true;
                return P(p, 0) * Fx;
            case 40:
                return t.Elapsed / t.Lifespan <= P(p, 0) * Fx ? P(p, 1) * Fx : P(p, 2) * Fx;
            case 41:
            {
                // keyframes (time%, value) ending with int.MinValue; linear between them
                float percent = t.Elapsed / t.Lifespan;
                if (percent < P(p, 0) * Fx) { return P(p, 1) * Fx; }
                bool none = true;
                int i = 0;
                do
                {
                    if (P(p, i) * Fx > percent) { break; }
                    none = false;
                    i += 2;
                }
                while (P(p, i) != int.MinValue && i < p.Count);
                if (none) { return 0; }
                i -= 2;
                if (P(p, i + 2) == int.MinValue) { return P(p, i + 1) * Fx; }
                float t0 = P(p, i) * Fx, v0 = P(p, i + 1) * Fx, t1 = P(p, i + 2) * Fx, v1 = P(p, i + 3) * Fx;
                return v0 + (v1 - v0) * ((percent - t0) / (t1 - t0));
            }
            case 42: return P(p, 0) * Fx;
            case 43: return RandF();
            case 44: return RandF() - 0.5f;
            case 45: return Rand1(0x168000) * Fx;   // random angle 0..360 (degrees)
            case 46: return EvalFloatAt(fx, P(p, 0), t, el, pt) + EvalFloatAt(fx, P(p, 1), t, el, pt);
            case 47: return EvalFloatAt(fx, P(p, 0), t, el, pt) - EvalFloatAt(fx, P(p, 1), t, el, pt);
            case 48: return EvalFloatAt(fx, P(p, 0), t, el, pt) * EvalFloatAt(fx, P(p, 1), t, el, pt);
            case 49:
                return EvalFloatAt(fx, P(p, 0), t, el, pt) >= EvalFloatAt(fx, P(p, 1), t, el, pt)
                    ? EvalFloatAt(fx, P(p, 2), t, el, pt) : EvalFloatAt(fx, P(p, 3), t, el, pt);
            default: return 0;
            }
        }

        private void EvalVec(Effect fx, FxFuncInfo f, Times t, Element el, Particle_? pt, ref Vector3 v)
        {
            IReadOnlyList<int> p = f.Parameters;
            switch (f.FuncId)
            {
            case 1:
            case 2:
                v = pt != null ? pt.Pos : el.Transform.Row3.Xyz;
                break;
            case 3:
                if (pt != null) { v = pt.Speed; }
                break;
            case 4: v = new Vector3(P(p, 0, 0) * Fx, P(p, 1, 0) * Fx, P(p, 2, 0) * Fx); break;
            case 5: v.X = RandF(); v.Y = RandF(); v.Z = RandF(); break;
            case 6: v.X = RandF(); v.Y = 0; v.Z = RandF(); break;
            case 7: v.X = RandF(); v.Y = 1; v.Z = RandF(); break;
            case 8: v.X = RandF() - 0.5f; v.Y = RandF() - 0.5f; v.Z = RandF() - 0.5f; break;
            case 9: v.X = RandF() - 0.5f; v.Y = 0; v.Z = RandF() - 0.5f; break;
            case 10: v.X = RandF() - 0.5f; v.Y = 1; v.Z = RandF() - 0.5f; break;
            case 11:
                if (pt != null)
                {
                    float a = 360 * pt.PortionTotal * Deg;
                    v = new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
                }
                break;
            case 13:
            {
                // MphRead FxFunc13: param[0] is a float func id run with the params of the func at/after offset param[1]
                int off = P(p, 1, 0);
                FxFuncInfo? g = FuncAt(fx, off);
                for (int guard = 0; g == null && guard < 1024; guard++) { g = FuncAt(fx, off += 4); }
                float value = g != null ? EvalFloat(fx, (uint)P(p, 0, 0), g.Parameters, t, el, pt) : 1;
                float percent = t.Elapsed / value;
                if (value < 0) { percent *= -1; }
                float a = 360 * percent * Deg;
                v = new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
                break;
            }
            case 14:
            {
                Vector3 tmp = Vector3.Zero;
                EvalVecAt(fx, P(p, 0, 0), t, el, pt, ref tmp);
                float value = EvalFloatAt(fx, P(p, 1, 0), t, el, pt);
                float div = t.Elapsed / value;
                if (value < 0) { div *= -1; }
                v = tmp * div;
                break;
            }
            case 15:
            {
                float v1 = EvalFloatAt(fx, P(p, 0, 0), t, el, pt);
                float v2 = EvalFloatAt(fx, P(p, 1, 0), t, el, pt);
                float a = (Rand1(0xFFFF) >> 4) * (360f / 4096f) * Deg;
                v = new Vector3(MathF.Sin(a) * v1, v2, MathF.Cos(a) * v1);
                break;
            }
            case 16:
            {
                float v1 = EvalFloatAt(fx, P(p, 0, 0), t, el, pt);
                float v2 = EvalFloatAt(fx, P(p, 1, 0), t, el, pt);
                v.X = (RandF() - 0.5f) * v1;
                v.Y = 0;
                v.Z = (RandF() - 0.5f) * v2;
                break;
            }
            case 17:
            case 18:
            case 19:
            {
                Vector3 a = Vector3.Zero, b = Vector3.Zero;
                EvalVecAt(fx, P(p, 0, 0), t, el, pt, ref a);
                EvalVecAt(fx, P(p, 1, 0), t, el, pt, ref b);
                v = f.FuncId == 17 ? a + b : f.FuncId == 18 ? a - b : a * b;
                break;
            }
            case 20:
            {
                Vector3 tmp = Vector3.Zero;
                float value = EvalFloatAt(fx, P(p, 0, 0), t, el, pt);
                EvalVecAt(fx, P(p, 1, 0), t, el, pt, ref tmp);
                v = tmp * value;
                break;
            }
            }
        }

        // ---------------------------------------------------------------- simulation (MphRead ProcessEffects)

        public void Step()
        {
            for (int i = 0; i < MaxElements; i++)
            {
                if (_elements[i].Active) { ProcessElement(i); }
            }
            _elapsed += StepTime;
            _frameCount++;
        }

        private static FxFuncInfo? Act(EffectElement def, FuncAction a) => def.Actions.TryGetValue(a, out FxFuncInfo? f) ? f : null;

        private static readonly FuncAction[] RoActs =
        {
            FuncAction.SetParticleRoField1, FuncAction.SetParticleRoField2, FuncAction.SetParticleRoField3, FuncAction.SetParticleRoField4
        };
        private static readonly FuncAction[] RwActs =
        {
            FuncAction.SetParticleRwField1, FuncAction.SetParticleRwField2, FuncAction.SetParticleRwField3, FuncAction.SetParticleRwField4
        };

        private void ProcessElement(int index)
        {
            Element el = _elements[index];
            Effect fx = el.Effect;
            EffectElement def = el.Def;

            if (!el.Expired && _elapsed > el.Expiration)
            {
                if (el.Entry < 0 && (el.Flags & EffElemFlags.KeepAlive) == 0)
                {
                    UnlinkElement(index);
                    return;
                }
                el.Expired = true;
            }
            if (el.Expired)
            {
                // with an entry the element lives until unlinked; without one (KeepAlive) until its particles are gone
                if (el.Entry < 0 && el.Particles.Count == 0)
                {
                    UnlinkElement(index);
                    return;
                }
                el.Transform = el.Own;
            }
            else
            {
                if ((el.Flags & EffElemFlags.ElementExtension) != 0 && _elapsed - el.Creation > el.BufferTime)
                {
                    el.Creation += el.BufferTime - el.DrainTime;
                    el.Expiration += el.BufferTime - el.DrainTime;
                }
                el.Transform = el.Own;
                var times = new Times(_elapsed, _elapsed - el.Creation, el.Lifespan);
                // DS runs at 30 Hz: the amount only grows on every other 60 Hz frame (element parity)
                FxFuncInfo? inc = Act(def, FuncAction.IncreaseParticleAmount);
                if ((int)(_frameCount % 2) == el.Parity && inc != null)
                {
                    el.Amount += EvalFloat(fx, inc, times, el, null);
                }
                int spawnCount = (int)MathF.Floor(el.Amount);
                el.Amount -= spawnCount;
                float portionTotal = 0;
                for (int j = 0; j < spawnCount; j++)
                {
                    if (_particleCount >= MaxParticles || def.Particles.Count == 0) { break; }
                    Particle_ p = _pool.Count > 0 ? _pool.Pop() : new Particle_();
                    p.Reset();
                    el.Particles.Add(p);
                    _particleCount++;
                    p.Creation = _elapsed;
                    p.PortionTotal = portionTotal;
                    Vector3 temp = Vector3.Zero;
                    FxFuncInfo? f;
                    if ((f = Act(def, FuncAction.SetNewParticlePosition)) != null) { EvalVec(fx, f, times, el, p, ref temp); p.Pos = temp; }
                    if ((f = Act(def, FuncAction.SetNewParticleSpeed)) != null) { EvalVec(fx, f, times, el, p, ref temp); p.Speed = temp; }
                    if ((el.Flags & EffElemFlags.UseTransform) == 0)
                    {
                        p.Pos = Vector3.TransformPosition(p.Pos, el.Transform);
                        p.Speed = Vector3.TransformVector(p.Speed, el.Transform);
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        f = Act(def, RoActs[k]);
                        p.Ro[k] = f != null ? EvalFloat(fx, f, times, el, p) : el.Ro[k];
                    }
                    if ((f = Act(def, FuncAction.SetNewParticleLifespan)) != null)
                    {
                        p.Lifespan = EvalFloat(fx, f, new Times(_elapsed, 1, el.Lifespan), el, p);
                        p.Expiration = p.Creation + p.Lifespan;
                    }
                    else
                    {
                        p.Lifespan = el.Lifespan;
                        p.Expiration = el.Expiration;
                    }
                    // one-time values: only constant funcs (vec 4 / float 42) are applied at spawn
                    if ((f = Act(def, FuncAction.UpdateParticleSpeed)) != null && f.FuncId == 4)
                    {
                        temp = p.Speed;
                        EvalVec(fx, f, times, el, p, ref temp);
                        p.Speed = temp;
                    }
                    p.Red = (f = Act(def, FuncAction.SetParticleRed)) != null && f.FuncId == 42 ? EvalFloat(fx, f, times, el, p) : 1;
                    p.Green = (f = Act(def, FuncAction.SetParticleGreen)) != null && f.FuncId == 42 ? EvalFloat(fx, f, times, el, p) : 1;
                    p.Blue = (f = Act(def, FuncAction.SetParticleBlue)) != null && f.FuncId == 42 ? EvalFloat(fx, f, times, el, p) : 1;
                    p.Alpha = (f = Act(def, FuncAction.SetParticleAlpha)) != null && f.FuncId == 42 ? MathF.Max(0, EvalFloat(fx, f, times, el, p)) : 1;
                    p.Scale = (f = Act(def, FuncAction.SetParticleScale)) != null && f.FuncId == 42 ? EvalFloat(fx, f, times, el, p) : 0;
                    if ((f = Act(def, FuncAction.SetParticleRotation)) != null && f.FuncId == 42) { p.Rotation = EvalFloat(fx, f, times, el, p); }
                    for (int k = 0; k < 4; k++)
                    {
                        if ((f = Act(def, RwActs[k])) != null) { p.Rw[k] = EvalFloat(fx, f, times, el, p); }
                    }
                    portionTotal += 1f / spawnCount;
                }
            }

            // particles
            for (int j = 0; j < el.Particles.Count;)
            {
                Particle_ p = el.Particles[j];
                if ((el.Flags & EffElemFlags.ElementExtension) != 0 && (el.Flags & EffElemFlags.ParticleExtension) != 0
                    && _elapsed - p.Creation > el.BufferTime)
                {
                    p.Creation += el.BufferTime - el.DrainTime;
                    p.Expiration += el.BufferTime - el.DrainTime;
                }
                if (_elapsed < p.Expiration)
                {
                    var times = new Times(_elapsed, _elapsed - p.Creation, p.Lifespan);
                    FxFuncInfo? f;
                    for (int k = 0; k < 4; k++)
                    {
                        if ((f = Act(def, RwActs[k])) != null) { p.Rw[k] = EvalFloat(fx, f, times, el, p); }
                    }
                    if ((f = Act(def, FuncAction.SetParticleId)) != null)
                    {
                        p.ParticleId = Math.Max(0, Math.Min((int)EvalFloat(fx, f, times, el, p), def.Particles.Count - 1));
                    }
                    if ((f = Act(def, FuncAction.UpdateParticleSpeed)) != null)
                    {
                        Vector3 s = p.Speed;
                        EvalVec(fx, f, times, el, p, ref s);
                        p.Speed = s;
                    }
                    if ((f = Act(def, FuncAction.SetParticleRed)) != null) { p.Red = EvalFloat(fx, f, times, el, p); }
                    if ((f = Act(def, FuncAction.SetParticleGreen)) != null) { p.Green = EvalFloat(fx, f, times, el, p); }
                    if ((f = Act(def, FuncAction.SetParticleBlue)) != null) { p.Blue = EvalFloat(fx, f, times, el, p); }
                    if ((f = Act(def, FuncAction.SetParticleAlpha)) != null) { p.Alpha = MathF.Max(0, EvalFloat(fx, f, times, el, p)); }
                    if ((f = Act(def, FuncAction.SetParticleScale)) != null) { p.Scale = EvalFloat(fx, f, times, el, p); }
                    if ((f = Act(def, FuncAction.SetParticleRotation)) != null) { p.Rotation = EvalFloat(fx, f, times, el, p); }
                    if ((el.Flags & EffElemFlags.UseAcceleration) != 0) { p.Speed += def.Acceleration * StepTime; }
                    p.Pos += p.Speed * StepTime;
                    // CheckCollision particles: the engine has no MPH collision here (none of the Power Beam's use it)
                    j++;
                }
                else
                {
                    if ((el.Flags & EffElemFlags.SpawnChildEffect) != 0 && def.ChildEffectId != 0)
                    {
                        Vector3 v1 = (-p.Speed).Normalized();
                        Vector3 v2 = v1.Z <= -3686 * Fx || v1.Z >= 3686 * Fx ? Vector3.UnitX : Vector3.UnitZ;
                        Effect? child = GetEffect((int)def.ChildEffectId);
                        if (child != null)
                        {
                            SpawnInto(child, MphRead.Matrix.GetTransform4(Vector3.Cross(v1, v2).Normalized(), v1, p.Pos), false, -1);
                        }
                    }
                    el.Particles.RemoveAt(j);
                    _pool.Push(p);
                    _particleCount--;
                }
            }
        }

        // ---------------------------------------------------------------- drawing (Effects.cs SetVecs*/Draw*)

        private static uint Pack(Vector3 c, float a)
        {
            static uint B(float f) => f <= 0 ? 0u : f >= 1 ? 255u : (uint)(f * 255 + 0.5f);
            return B(c.X) | B(c.Y) << 8 | B(c.Z) << 16 | B(a) << 24;
        }

        private static void Quad(FxDrawList list, int tex, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector2 ta, Vector2 tb, Vector2 tc, Vector2 td, float su, float sv, uint color)
        {
            FxDrawList.Group g = list.For(tex);
            Vector3[] v = { a, b, c, a, c, d };
            Vector2[] t = { ta, tb, tc, ta, tc, td };
            for (int i = 0; i < 6; i++)
            {
                g.Positions.Add(v[i]);
                g.Uvs.Add(new Vector2(t[i].X * su, t[i].Y * sv));
                g.Colors.Add(color);
            }
            list.TriangleCount += 2;
        }

        // camRight/Up/Back: camera axes in this system's space (view space x, y, z). Appends to list; clears the
        // one-frame singles/trails.
        public void Draw(FxDrawList list, Vector3 camRight, Vector3 camUp, Vector3 camBack)
        {
            Vector3 Bill(Vector3 v) => camRight * v.X + camUp * v.Y + camBack * v.Z;
            Vector2 uv0 = new Vector2(0, 1), uv1 = new Vector2(1, 1), uv2 = new Vector2(1, 0), uv3 = new Vector2(0, 0);

            foreach (Element el in _elements)
            {
                if (!el.Active || (el.Flags & EffElemFlags.DrawEnabled) == 0 || el.Particles.Count == 0) { continue; }
                EffectElement def = el.Def;
                if (def.Particles.Count == 0) { continue; }
                bool useXf = (el.Flags & EffElemFlags.UseTransform) != 0;
                bool useMesh = (el.Flags & EffElemFlags.UseMesh) != 0;
                // GetFuncIds
                int setVecs, drawId;
                if (useMesh)
                {
                    drawId = 7;
                    setVecs = def.DrawType == 3 ? 4 : 5;
                }
                else
                {
                    switch (def.DrawType)
                    {
                    case 1: setVecs = 1; drawId = useXf ? 1 : 2; break;
                    case 2: setVecs = 2; drawId = useXf ? 1 : 2; break;
                    case 3: setVecs = 3; drawId = 3; break;
                    case 4: setVecs = 1; drawId = useXf ? 4 : 5; break;
                    case 5: setVecs = 2; drawId = useXf ? 4 : 5; break;
                    case 6: setVecs = 3; drawId = 6; break;
                    default: continue;
                    }
                }
                if (setVecs == 4) { continue; }   // SetVecsD4: never used by MPH (MphRead asserts)
                Matrix4 T = el.Transform;
                Vector3 tpos = T.Row3.Xyz;
                // SetVecsC0: vec2 = the view matrix's z column, in element space when UseTransform
                Vector3 back = camBack;
                if (useXf)
                {
                    back = new Vector3(Vector3.Dot(T.Row0.Xyz, camBack), Vector3.Dot(T.Row1.Xyz, camBack), Vector3.Dot(T.Row2.Xyz, camBack));
                }

                foreach (Particle_ p in el.Particles)
                {
                    if (p.Alpha <= 0) { continue; }
                    Particle pdef = def.Particles[Math.Min(p.ParticleId, def.Particles.Count - 1)];
                    var color = new Vector3(p.Red, p.Green, p.Blue);

                    if (drawId == 7)
                    {
                        // DrawDC: the particle node's mesh, scaled, camera-facing (SetVecsD8 = sphere billboard)
                        Vector3 origin = useXf ? p.Pos + tpos : p.Pos;
                        foreach (MeshPart part in Meshes(pdef))
                        {
                            FxDrawList.Group g = list.For(part.Tex);
                            float a = Math.Clamp(part.Alpha * p.Alpha, 0, 1);
                            uint pc = Pack(color, a);
                            foreach (PosedModel.MeshVertex v in part.Verts)
                            {
                                g.Positions.Add(origin + Bill(v.Pos * p.Scale));
                                g.Uvs.Add(new Vector2(v.Uv.X * part.Uv.M11 + v.Uv.Y * part.Uv.M21 + part.Uv.M41,
                                    v.Uv.X * part.Uv.M12 + v.Uv.Y * part.Uv.M22 + part.Uv.M42));
                                g.Colors.Add(v.HasColor ? Pack(v.Color, a) : pc);
                            }
                            list.TriangleCount += part.Verts.Count / 3;
                        }
                        continue;
                    }

                    PInfo info = Info(pdef);
                    // SetVecs
                    Vector3 vec1, vec2;
                    bool billboard = false;
                    if (setVecs == 1) { vec1 = Vector3.UnitX; vec2 = -Vector3.UnitY; billboard = true; }
                    else if (setVecs == 2) { vec1 = Vector3.UnitX; vec2 = Vector3.UnitZ; }
                    else { vec1 = -Vector3.UnitY; vec2 = back; }

                    Vector3 o0, o1, o2, o3;   // corner offsets (texcoords (0,1) (1,1) (1,0) (0,0))
                    if (drawId == 1 || drawId == 2)
                    {
                        // DrawB8
                        Vector3 e1 = vec1 * p.Scale, e2 = vec2 * p.Scale;
                        o0 = e1 * -0.5f + e2 * 0.5f;
                        o1 = o0 + e1;
                        o2 = o1 - e2;
                        o3 = o2 - e1;
                    }
                    else if (drawId == 4 || drawId == 5)
                    {
                        // DrawCC: rotated quad (Rotation in degrees)
                        float a1 = p.Rotation * Deg, a2 = (p.Rotation + 90) * Deg;
                        Vector3 d20 = (vec1 * MathF.Sin(a2) + vec2 * MathF.Cos(a2)) * p.Scale;
                        Vector3 d26 = (vec1 * MathF.Sin(a1) + vec2 * MathF.Cos(a1)) * p.Scale;
                        o0 = d20 * -0.5f + d26 * 0.5f;
                        o1 = o0 + d20;
                        o2 = o1 - d26;
                        o3 = o2 - d20;
                    }
                    else
                    {
                        // DrawC4 (stretched along the speed) / DrawD0 (vertical): DrawShared, width = Rotation
                        if (drawId == 3)
                        {
                            if (p.Speed.LengthSquared <= 128 * Fx) { continue; }
                            vec1 = p.Speed.Normalized();
                        }
                        Vector3 cr = Vector3.Cross(vec1, vec2);
                        cr = (cr.LengthSquared > 0 ? cr.Normalized() : cr) * p.Rotation;
                        Vector3 e1 = vec1 * p.Scale;
                        o0 = e1 * -0.5f + cr * 0.5f;
                        o1 = o0 + e1;
                        o2 = o1 - cr;
                        o3 = o2 - e1;
                    }
                    Vector3 W(Vector3 off)
                    {
                        if (billboard)
                        {
                            Vector3 c = useXf ? Vector3.TransformVector(p.Pos, T) + tpos : p.Pos;
                            return c + Bill(off);
                        }
                        return useXf ? Vector3.TransformPosition(p.Pos + off, T) : p.Pos + off;
                    }
                    Quad(list, info.Tex, W(o0), W(o1), W(o2), W(o3), uv0, uv1, uv2, uv3, info.ScaleU, info.ScaleV, Pack(color, p.Alpha));
                }
            }

            // SingleParticle.Process: +-Scale camera-facing quad
            foreach (Single s in _singles)
            {
                PInfo info = Info(s.Particle);
                float h = s.HalfSize;
                Quad(list, info.Tex, s.Pos + Bill(new Vector3(-h, h, 0)), s.Pos + Bill(new Vector3(h, h, 0)),
                    s.Pos + Bill(new Vector3(h, -h, 0)), s.Pos + Bill(new Vector3(-h, -h, 0)),
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), info.ScaleU, info.ScaleV, Pack(s.Color, s.Alpha));
            }
            _singles.Clear();
            foreach (Trail t in _trails)
            {
                // mirror-wrapped trail textures: texcoords * material ScaleS/T (as for particles)
                Quad(list, t.Tex, t.V0, t.V1, t.V2, t.V3, t.T0, t.T1, t.T2, t.T3, t.Su, t.Sv, Pack(t.Color, t.Alpha));
            }
            _trails.Clear();
        }
    }
}
