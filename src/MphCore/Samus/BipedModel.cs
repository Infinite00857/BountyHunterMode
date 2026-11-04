// BipedModel - Samus's third-person body (Samus_lod0 + SamusSharedAnim) and Morph Ball (SamusAlt_lod0). Drawn around
// form switches (Morph / Unmorph) and in the F5 view. Like MphRead's PlayerEntity it has two animation instances on
// one model: legs (Biped, up to Spine_1) and torso (Torso, below it), and pitches the spine with the aim
// (PlayerDraw.Draw). Animations advance at 30 fps (every other 60 Hz step), like GunModel.
using System;
using MphRead;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    public sealed class BipedModel
    {
        public ModelInstance Biped { get; }        // legs (MphRead _bipedModel1)
        public ModelInstance Torso { get; }        // torso (_bipedModel2), same model
        public PosedModel BipedPosed { get; }
        public ModelInstance Ball { get; }
        public PosedModel BallPosed { get; }
        public float AnimFraction { get; private set; }   // current biped animation progress 0..1
        public float TorsoFraction { get; private set; } = 1;   // the unmorph progress (torso)
        private int _step;

        public BipedModel()
        {
            Biped = Read.GetModelInstance("Samus_lod0");
            Torso = Read.GetModelInstance("Samus_lod0");
            BipedPosed = new PosedModel(Biped);
            foreach (Node n in Biped.Model.Nodes)
            {
                if (n.Name == "Spine_1") { _spine = n; break; }
            }
            Ball = Read.GetModelInstance("SamusAlt_lod0");
            BallPosed = new PosedModel(Ball);
            if (Ball.Model.AnimationGroups.Node.Count > 0 || Ball.Model.AnimationGroups.Material.Count > 0)
            {
                Ball.SetAnimation(0, AnimFlags.None);
            }
        }

        public int FrameCount(BipedAnimation anim)
        {
            int i = (int)anim;
            return i < Biped.Model.AnimationGroups.Node.Count ? Biped.Model.AnimationGroups.Node[i].FrameCount : 0;
        }

        public void Apply(SamusSim sim)
        {
            if (sim.BipedAnimChanged)
            {
                sim.BipedAnimChanged = false;
                Biped.SetAnimation((int)sim.BipedAnim, sim.BipedAnimFlags);
            }
            if (sim.TorsoAnimChanged)
            {
                sim.TorsoAnimChanged = false;
                Torso.SetAnimation((int)sim.TorsoAnim, sim.TorsoAnimFlags);
                if (sim.TorsoSyncFrame)
                {
                    // the torso joins the legs' animation where it is (PlayerInput: biped2.Frame = biped1.Frame)
                    Torso.AnimInfo.Frame[0] = System.Math.Min(Biped.AnimInfo.Frame[0], System.Math.Max(0, Torso.AnimInfo.FrameCount[0] - 1));
                }
            }
            if ((_step++ & 1) == 0)
            {
                Biped.UpdateAnimFrames();
                Torso.UpdateAnimFrames();
                Ball.UpdateAnimFrames();
            }
            sim.ReportTorso(Torso.AnimInfo.Flags[0].HasFlag(AnimFlags.Ended), Torso.AnimInfo.Frame[0]);
            int torsoCount = Torso.AnimInfo.FrameCount[0];
            TorsoFraction = torsoCount > 1 ? Torso.AnimInfo.Frame[0] / (float)(torsoCount - 1) : 1;
            int count = Biped.AnimInfo.FrameCount[0];
            AnimFraction = count > 1 ? Biped.AnimInfo.Frame[0] / (float)(count - 1) : 1;
            sim.ReportBipedAnimEnded(Biped.AnimInfo.Flags[0].HasFlag(AnimFlags.Ended));
        }

        // PlayerDraw.Draw (biped): rows (-left, up, -lateral), at the feet. MPH axes. left is derived from the
        // facing (_gunVec2 = (f74, 0, -f70)): a left vector from another frame (the facing before the ball turned) made
        // the rows non-orthogonal and squashed the model (owner playtest 2026-10-31).
        // aimY: the facing vector's Y (aim pitch); the spine turns by it, capped at 2896/4096 (PlayerDraw.Draw)
        public void PoseBiped(Vector3 lateralFacing, float aimY, MphLighting light, float alpha)
        {
            Vector3 lat = new Vector3(lateralFacing.X, 0, lateralFacing.Z);
            lat = lat.LengthSquared > 1e-8f ? lat.Normalized() : new Vector3(0, 0, -1);
            var left = new Vector3(lat.Z, 0, -lat.X);
            Vector3 up = Vector3.Cross(lat, left);
            var world = Matrix4.Identity;
            world.Row0 = new Vector4(-left, 0);
            world.Row1 = new Vector4(up, 0);
            world.Row2 = new Vector4(-lat, 0);
            Model model = Biped.Model;
            model.AnimateMaterials(Torso.AnimInfo);
            model.AnimateTextures(Torso.AnimInfo);
            if (_spine == null)
            {
                BipedPosed.Pose(Matrix4.CreateScale(model.Scale) * world, light, alpha);
                return;
            }
            // legs animation up to the spine (with the aim pitch after it), then the torso animation from its child
            float limit = SpineAimLimit;
            float cos = MathF.Sqrt(Math.Max(0, 1 - aimY * aimY)), sin = aimY;
            if (Math.Abs(aimY) > limit)
            {
                cos = limit;
                sin = aimY <= 0 ? -limit : limit;
            }
            _spine.AnimIgnoreChild = true;
            _spine.AfterTransform = Matrix4.CreateRotationZ(MathF.Atan2(sin, cos));
            model.AnimateNodes(index: 0, false, Matrix4.Identity, Vector3.One, Biped.AnimInfo);
            _spine.AnimIgnoreChild = false;
            if (_spine.ChildIndex != -1)
            {
                model.AnimateNodes(_spine.ChildIndex, false, Matrix4.Identity, Vector3.One, Torso.AnimInfo);
            }
            _spine.AfterTransform = null;
            Matrix4 transform = Matrix4.CreateScale(model.Scale) * world;
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                model.Nodes[i].Animation *= transform;
            }
            BipedPosed.PoseAnimated(light, alpha);
        }
        private const float SpineAimLimit = 2896 / 4096f;
        private readonly Node? _spine;

        // the arm cannon's muzzle on the posed biped (testbed V view: Metadata.MuzzleOffsets[Samus] on R_elbow), in the
        // posing space (feet at the origin). false if the model has no R_elbow.
        public bool Muzzle(out Vector3 muzzle)
        {
            muzzle = Vector3.Zero;
            if (_elbow == -1)
            {
                _elbow = -2;
                for (int i = 0; i < Biped.Model.Nodes.Count; i++)
                {
                    if (Biped.Model.Nodes[i].Name == "R_elbow") { _elbow = i; break; }
                }
            }
            if (_elbow < 0) { return false; }
            muzzle = Vector3.TransformPosition(new Vector3(0.63f, -0.02f, 0), Biped.Model.Nodes[_elbow].Animation);
            return true;
        }
        private int _elbow = -1;

        // ball: AltRot about its center, scaled so its radius is `radius` (MPH units of the drawn ball)
        public void PoseBall(Matrix4 altRot, float scale, MphLighting light)
        {
            BallPosed.Pose(Matrix4.CreateScale(Ball.Model.Scale * scale) * altRot, light);
        }
    }
}
