// GunModel - Samus's first-person arm cannon (SamusGun): applies SamusSim's gun animation requests to the MphRead
// ModelInstance (PlayerEntity.SetGunAnimation: slot 0 node anim + slot 1 per-weapon material anim) and advances the
// animation at 30 fps (every other 60 Hz step, as MphRead's frame-advance does at 60 fps).
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphSamus.Core
{
    public sealed class GunModel
    {
        public ModelInstance Instance { get; }
        public PosedModel Posed { get; }
        private int _step;

        public GunModel()
        {
            Instance = Read.GetModelInstance("SamusGun");
            Posed = new PosedModel(Instance);
        }

        public void Apply(SamusSim sim)
        {
            if (sim.GunAnimChanged)
            {
                sim.GunAnimChanged = false;
                SetFlags setFlags = SetFlags.Texture | SetFlags.Texcoord | SetFlags.Material | SetFlags.Unused | SetFlags.Node;
                int id = SamusSim.GunAnimationId(sim.GunAnim, 0);
                Instance.SetAnimation(id, slot: 0, setFlags, sim.GunAnimFlags);
                // slot 1: the weapon's material/texcoord animation (column = weapon + 1; Switch: the previous weapon)
                int id1 = SamusSim.GunAnimationId(sim.GunAnim, (int)sim.GunAnimWeapon + 1);
                if (id1 >= 0)
                {
                    Instance.SetAnimation(id1, slot: 1, setFlags & ~SetFlags.Node, sim.GunAnimFlags);
                }
            }
            if (sim.ReverseRequested)
            {
                sim.ReverseRequested = false;
                Instance.AnimInfo.Flags[0] |= AnimFlags.Reverse;
                Instance.AnimInfo.Flags[0] &= ~AnimFlags.Ended;
            }
            if (sim.JumpToEndRequested)
            {
                sim.JumpToEndRequested = false;
                Instance.AnimInfo.Frame[0] = System.Math.Max(0, Instance.AnimInfo.FrameCount[0] - 1);
            }
            // Charging: frame follows the charge level (UpdateGunAnimation)
            if ((sim.GunAnim == GunAnimation.Charging || sim.GunAnim == GunAnimation.ChargingMissile) && sim.Charging
                && (_step & 1) == 0 && Instance.AnimInfo.FrameCount[0] > 0)
            {
                WeaponInfo w = sim.Weapon;
                int frame = (Instance.AnimInfo.FrameCount[0] - 1) * (sim.ChargeLevel / 2 - w.MinCharge) / (w.FullCharge - w.MinCharge);
                Instance.AnimInfo.Frame[0] = System.Math.Min(frame, Instance.AnimInfo.FrameCount[0] - 1);
            }
            if ((_step++ & 1) == 0)
            {
                Instance.UpdateAnimFrames();
            }
            sim.ReportGunAnimEnded(Instance.AnimInfo.Flags[0].HasFlag(AnimFlags.Ended));
        }

        // Pose in camera space (MPH axes: +X left, +Y up, +Z forward), gun at offset, pointing along aimDir.
        public void Pose(Vector3 offset, Vector3 aimDir, MphLighting light)
        {
            Matrix4 t = MphRead.EntityBase.GetTransformMatrix(aimDir.Normalized(), Vector3.UnitY, offset);
            Posed.Pose(Matrix4.CreateScale(Instance.Model.Scale) * t, light);
        }
    }
}
