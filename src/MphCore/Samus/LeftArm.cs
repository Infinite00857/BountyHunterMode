// LeftArm - Samus's first-person left arm (carrying ULTRAKILL's items, reaching for terminals). The model is
// Samus_lod0's triangles skinned to L_shoulder / L_elbow / L_wrist, taken from the ROM model through
// PosedModel.KeepNode. Posed in camera space (MPH axes: +X left, +Y up, +Z forward, MPH units): rest bones, an elbow
// bend, the shoulder tucked under the view; it trails the view's turns.
using System;
using MphRead;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    public enum ArmState { Hidden, Reach, Hold, Throw, Point }

    public sealed class LeftArm
    {
        // ---- TUNABLE CONSTANTS (first-person left arm, camera-space MPH units) ----
        private const float ArmSide = 0.34f;          // shoulder: this far left of the eye
        private const float ArmDown = 0.30f;          //   ... below it
        private const float ArmBack = 0.05f;          //   ... and behind it
        private const float ArmHideDrop = 0.45f;      // hidden arm: shoulder this much lower (slides in from below)
        private const float ArmYawIn = 8;             // degrees: the arm points in toward the view center
        private const float ArmDropMax = 28;          // degrees: upper arm points down this much when not extended
        private const float ArmBendMax = 75;          // degrees: elbow bend when not extended (forearm raised)
        private const float ArmShowRate = 0.12f;      // per 60 Hz step
        private const float ArmReachRate = 0.16f;
        private const float ArmHoldReach = 0.35f;     // extension while holding an object
        private const float ArmPointReach = 0.8f;     // extension while using a terminal
        private const int ArmGrabSteps = 12, ArmThrowSteps = 10;
        private const float ArmLag = 1;               // the arm trails the view's turn (aim lead, inverted)
        private const float PalmOffset = 0.11f;       // hand point: past the wrist along the forearm
        private const float HoldSide = 0.22f, HoldDown = 0.32f;   // holding: closer to the center, lower
        private const float HoldUpperPitch = -35;     // degrees the upper arm points up from forward
        private const float HoldBend = 75;
        private const float HoldRoll = 90;            // forearm roll so the palm turns toward the camera
        private const float HoldObjBack = 0.10f, HoldObjUp = 0.05f;   // held object: in front of the palm
        private const float ArmHoldRate = 0.12f;

        public ModelInstance Instance { get; }
        public PosedModel Posed { get; }
        public bool Loaded => _shoulder >= 0 && _elbow >= 0 && _wrist >= 0;
        public ArmState State = ArmState.Hidden;
        public float Show, Reach, HoldT;
        public Vector3 Hand, Held;                    // camera space (MPH units): the palm, a held object
        private readonly int _shoulder = -1, _elbow = -1, _wrist = -1;
        private int _timer;

        public LeftArm()
        {
            Instance = Read.GetModelInstance("Samus_lod0");
            for (int i = 0; i < Instance.Model.Nodes.Count; i++)
            {
                string n = Instance.Model.Nodes[i].Name;
                if (n == "L_shoulder") { _shoulder = i; }
                else if (n == "L_elbow") { _elbow = i; }
                else if (n == "L_wrist") { _wrist = i; }
            }
            Posed = new PosedModel(Instance) { KeepNode = n => n == _shoulder || n == _elbow || n == _wrist };
        }

        public void Grab() { State = ArmState.Reach; _timer = 0; }
        public void Throw() { State = ArmState.Throw; _timer = 0; }

        // arm state -> show / reach / hold (60 Hz); pointing: a terminal is in use (the hand reaches for its screen)
        public void Step(bool pointing, bool holding)
        {
            float showT = 0, reachT = 0;
            switch (State)
            {
                case ArmState.Reach:
                    showT = 1;
                    reachT = 1;
                    if (++_timer >= ArmGrabSteps) { State = holding ? ArmState.Hold : ArmState.Hidden; }
                    break;
                case ArmState.Hold:
                    showT = 1;
                    reachT = ArmHoldReach;
                    if (!holding) { State = ArmState.Hidden; }
                    break;
                case ArmState.Throw:
                    showT = 1;
                    reachT = 1;
                    if (++_timer >= ArmThrowSteps) { State = holding ? ArmState.Hold : ArmState.Hidden; }
                    break;
                case ArmState.Point:
                    showT = 1;
                    reachT = ArmPointReach;
                    if (!pointing) { State = holding ? ArmState.Hold : ArmState.Hidden; }
                    break;
            }
            if (pointing && (State == ArmState.Hidden || State == ArmState.Hold)) { State = ArmState.Point; }
            if (holding && State == ArmState.Hidden) { State = ArmState.Hold; }
            Show = Approach(Show, showT, ArmShowRate);
            Reach = Approach(Reach, reachT, ArmReachRate);
            HoldT = Approach(HoldT, State == ArmState.Hold ? 1 : 0, ArmHoldRate);
        }

        // aim: the aim direction in camera space (pointer aim lead); the arm trails it the other way
        public void Pose(Vector3 aim, MphLighting light)
        {
            Model m = Instance.Model;
            var camFwd = Vector3.UnitZ;
            Vector3 fwd = (camFwd - (aim.Normalized() - camFwd) * ArmLag).Normalized();
            Vector3 right = Vector3.Cross(fwd, Vector3.UnitY).Normalized();
            Vector3 up = Vector3.Cross(right, fwd).Normalized();
            float h = HoldT;
            Vector3 armDir = RotateAxis(fwd, up, -ArmYawIn * MathF.PI / 180);
            float pitch = (1 - h) * -(1 - Reach) * ArmDropMax + h * HoldUpperPitch;
            armDir = RotateAxis(armDir, right, pitch * MathF.PI / 180);
            float side = ArmSide + (HoldSide - ArmSide) * h, down = ArmDown + (HoldDown - ArmDown) * h;
            Vector3 shoulder = right * -side + up * -(down + (1 - Show) * ArmHideDrop) + fwd * -ArmBack;
            // rest pose (no animation group: node transforms), model units
            m.ComputeNodeMatrices(index: 0);
            m.AnimateNodes(index: 0, true, Matrix4.Identity, m.Scale, new AnimationInfo());
            Vector3 sModel = m.Nodes[_shoulder].Animation.Row3.Xyz, eModel = m.Nodes[_elbow].Animation.Row3.Xyz;
            Vector3 wModel = m.Nodes[_wrist].Animation.Row3.Xyz;
            // model: the arm runs along -X from the shoulder; +Y up
            Vector3 x = -armDir;
            Vector3 y = (up - armDir * Vector3.Dot(up, armDir)).Normalized();
            Vector3 z = Vector3.Cross(x, y);
            Vector3 t = shoulder - (x * sModel.X + y * sModel.Y + z * sModel.Z);
            var world = new Matrix4(new Vector4(x, 0), new Vector4(y, 0), new Vector4(z, 0), new Vector4(t, 1));
            // forearm roll about its own axis, then the elbow bend (negative raises it)
            float bendDeg = (1 - h) * (1 - Reach) * ArmBendMax + h * HoldBend;
            Matrix4 bend = Matrix4.CreateTranslation(-eModel) * Matrix4.CreateRotationX(h * HoldRoll * MathF.PI / 180)
                * Matrix4.CreateRotationZ(-bendDeg * MathF.PI / 180) * Matrix4.CreateTranslation(eModel);
            m.Nodes[_elbow].Animation *= bend;
            m.Nodes[_wrist].Animation *= bend;
            for (int i = 0; i < m.Nodes.Count; i++) { m.Nodes[i].Animation *= world; }
            Posed.PoseAnimated(light);
            Hand = Vector3.TransformPosition(wModel + new Vector3(-PalmOffset, 0, 0), bend * world);
            float toCam = Math.Max(0.01f, Hand.Length);
            Held = Hand - Hand * (HoldObjBack / toCam) + up * HoldObjUp;
        }

        private static Vector3 RotateAxis(Vector3 v, Vector3 axis, float angle) =>
            Vector3.Transform(v, Quaternion.FromAxisAngle(axis.Normalized(), angle));

        private static float Approach(float v, float target, float rate) =>
            v < target ? Math.Min(target, v + rate) : Math.Max(target, v - rate);
    }
}
