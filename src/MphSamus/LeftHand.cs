// LeftHand - Samus's first-person left arm in ULTRAKILL (Core.LeftArm): it holds carried items (HeldItems) and reaches
// for terminals (ScreenZone shops / screens) while the arm cannon is lowered. Drawn on the weapon layer like the cannon.
using MphSamus.Core;
using UnityEngine;
using OV3 = OpenTK.Mathematics.Vector3;

namespace MphSamus
{
    public sealed class LeftHand
    {
        private readonly SamusController _c;
        private readonly LeftArm _arm;
        private readonly ModelView _view;

        public LeftArm Arm => _arm;

        public LeftHand(SamusController c, Transform cam, int hudLayer)
        {
            _c = c;
            try
            {
                _arm = new LeftArm();
                if (_arm.Loaded) { _view = new ModelView(_arm.Posed, "MPH Left Arm", cam, hudLayer) { Scale = c.K }; }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"left arm model: {ex.Message}");
            }
        }

        private SamusSim Sim => _c.Sim;

        // 60 Hz: pointing = a terminal is in use, holding = an item in the hand
        public void Step(bool pointing, bool holding) => _arm?.Step(pointing, holding);

        // the palm in world space (first person), else roughly where the biped's left hand is
        public Vector3 HandWorld
        {
            get
            {
                if (Sim.FirstPerson && _arm != null && _arm.Loaded && _c.Cam != null)
                {
                    return _c.Cam.transform.TransformPoint(SamusController.ToUnity(_arm.Hand) * _c.K);
                }
                Vector3 center = _c.BodyCollider != null ? _c.BodyCollider.bounds.center : _c.transform.position;
                Vector3 up = _c.transform.up;
                Vector3 f = Vector3.ProjectOnPlane(_c.Cam != null ? _c.Cam.transform.forward : _c.transform.forward, up);
                f = f.sqrMagnitude > 1e-6f ? f.normalized : _c.transform.forward;
                Vector3 left = Vector3.Cross(f, up);
                return center + up * 0.35f * _c.K + left * 0.3f * _c.K + f * 0.45f * _c.K;
            }
        }

        // LateUpdate (first person only)
        public void Draw(MphLighting gunLight, OV3 aimLocal)
        {
            bool on = _arm != null && _view != null && Sim.FirstPerson && !_c.IsDead && _arm.Show > 0.01f;
            _view?.SetVisible(on);
            if (_arm == null || !_arm.Loaded || !Sim.FirstPerson) { return; }
            _arm.Pose(aimLocal, gunLight);
            if (on)
            {
                _view.Scale = _c.K;
                _view.Upload(Vector3.zero, 0);
            }
        }

        public void Destroy() => _view?.Destroy();
    }
}
