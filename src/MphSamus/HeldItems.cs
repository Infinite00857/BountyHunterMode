// HeldItems - E grabs / places / throws ULTRAKILL's carried items with Samus's left hand
// (skulls, torches, soap...: ItemIdentifier, placed on ItemPlaceZone pedestals). V1's arm is off in Samus mode, so
// this mirrors ULTRAKILL's Punch.ForceHold / PlaceHeldObject / ForceThrow / ForceDrop with Samus's left hand as the
// holder; FistControl.heldObject is kept set for the systems that read it (checkpoints, breakables, spin zones).
// Items without physics (skulls) can't be thrown, as in ULTRAKILL; they're only placed.
using MphSamus.Core;
using UnityEngine;

namespace MphSamus
{
    public sealed class HeldItems
    {
        // ---- TUNABLE CONSTANTS (carrying items; Unity units) ----
        private const float Reach = 6;             // E grabs / places within this distance of the view
        private const float ReachRadius = 0.5f;
        private const float ThrowForce = 5000;     // Punch.ForceThrow
        private const float HolderScale = 0.6f;    // held items sit smaller than in V1's bigger hand
        // ---- TUNABLE CONSTANTS (grab / throw / place sounds: ROM samples) ----
        private const int GrabSample = 25, ThrowSample = 24, PlaceSample = 28;
        private const float GrabVolume = 92 / 127f, ThrowVolume = 96 / 127f, PlaceVolume = 72 / 127f;

        private readonly SamusController _c;
        private readonly Transform _holder;
        private readonly int _layer;
        public ItemIdentifier Held { get; private set; }
        public bool Holding => Held != null;

        public HeldItems(SamusController c, Transform cam, int hudLayer)
        {
            _c = c;
            _layer = hudLayer;
            _holder = new GameObject("MPH Held Item").transform;
            _holder.SetParent(cam, false);
        }

        // E: holding -> place on a pedestal in reach, else throw (if it can be); empty-handed -> grab an item in reach
        public void Use(LeftArm arm)
        {
            if (Held == null && Held is object) { Forget(); }   // destroyed while held (checkpoint reset)
            Transform cam = _c.Cam != null ? _c.Cam.transform : null;
            if (cam == null) { return; }
            if (Holding)
            {
                if (FindZone(cam.position, cam.forward, Reach, out ItemPlaceZone[] zones, out Transform target))
                {
                    Place(zones, target, arm);
                    return;
                }
                Plugin.Event("no place zone; layer-22 hits: " + string.Join(", ",
                    System.Array.ConvertAll(Cast(cam.position, cam.forward, Reach), h => h.collider.name + "@" + h.distance.ToString("0.0"))));
                Throw(cam, arm);
                return;
            }
            if (FindItem(cam.position, cam.forward, Reach, out ItemIdentifier item)) { Hold(item, arm); }
        }

        public bool FindItem(Vector3 from, Vector3 dir, float reach, out ItemIdentifier item)
        {
            item = null;
            foreach (RaycastHit h in Cast(from, dir, reach))
            {
                if (h.collider.TryGetComponent(out ItemIdentifier it) && !it.pickedUp && Clear(from, h))
                {
                    item = it;
                    return true;
                }
            }
            return false;
        }

        public bool FindZone(Vector3 from, Vector3 dir, float reach, out ItemPlaceZone[] zones, out Transform target)
        {
            zones = null;
            target = null;
            foreach (RaycastHit h in Cast(from, dir, reach))
            {
                ItemPlaceZone[] z = h.collider.GetComponents<ItemPlaceZone>();
                if (z.Length > 0 && Clear(from, h))
                {
                    zones = z;
                    target = h.collider.transform;
                    return true;
                }
            }
            return false;
        }

        private static RaycastHit[] Cast(Vector3 from, Vector3 dir, float reach)
        {
            RaycastHit[] hits = Physics.SphereCastAll(from, ReachRadius, dir, reach, 1 << 22, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            return hits;
        }

        // line of sight to the hit: walls block, the target's own hierarchy (a pedestal's mesh around its zone) doesn't
        private static bool Clear(Vector3 from, RaycastHit h)
        {
            Vector3 to = h.distance > 0 ? h.point : h.collider.ClosestPoint(from);
            Vector3 d = to - from;
            Transform root = h.collider.transform.root;
            foreach (RaycastHit b in Physics.RaycastAll(from, d, d.magnitude, LayerMaskDefaults.Get(LMD.Environment), QueryTriggerInteraction.Ignore))
            {
                if (b.collider != h.collider && b.collider.transform.root != root) { return false; }
            }
            return true;
        }

        // Punch.ForceHold
        public void Hold(ItemIdentifier itid, LeftArm arm)
        {
            if (itid == null || Holding) { return; }
            if (itid.infiniteSource) { itid = itid.CreateCopy(); }
            ItemPlaceZone[] fromZones = itid.GetComponentsInParent<ItemPlaceZone>();
            itid.ipz = null;
            Held = itid;
            itid.transform.SetParent(_holder);
            if (MonoSingleton<FistControl>.TryGetInstance(out FistControl fc) && fc != null) { fc.heldObject = itid; }
            itid.pickedUp = true;
            itid.beenPickedUp = true;
            itid.SendMessage("OffCorrectUse", SendMessageOptions.DontRequireReceiver);
            ResetHeldItemPosition();
            foreach (Transform t in itid.GetComponentsInChildren<Transform>()) { t.gameObject.layer = _layer; }
            foreach (Rigidbody rb in itid.GetComponentsInChildren<Rigidbody>()) { rb.isKinematic = true; }
            foreach (Collider col in itid.GetComponentsInChildren<Collider>()) { col.enabled = false; }
            if (itid.pickUpSound != null) { Object.Instantiate(itid.pickUpSound); }
            itid.SendMessage("PickUp", SendMessageOptions.DontRequireReceiver);
            foreach (ItemPlaceZone z in fromZones) { z.CheckItem(); }
            arm?.Grab();
            Sound(GrabSample, GrabVolume);
            Plugin.Event($"grabbed {itid.name} ({itid.itemType})");
        }

        // Punch.ResetHeldItemPosition (reverseTransformSettings: the put-down transform is the held one)
        private void ResetHeldItemPosition()
        {
            Transform t = Held.transform;
            if (Held.reverseTransformSettings)
            {
                t.localPosition = Held.putDownPosition;
                t.localScale = Held.putDownScale;
                t.localRotation = Quaternion.Euler(Held.putDownRotation);
            }
            else
            {
                t.localPosition = Vector3.zero;
                t.localScale = Vector3.one;
                t.localRotation = Quaternion.identity;
            }
        }

        // Punch.PlaceHeldObject
        public void Place(ItemPlaceZone[] zones, Transform target, LeftArm arm)
        {
            if (!Holding) { return; }
            ItemIdentifier it = Held;
            it.transform.SetParent(target);
            it.pickedUp = false;
            if (it.reverseTransformSettings)
            {
                it.transform.localPosition = Vector3.zero;
                it.transform.localScale = Vector3.one;
                it.transform.localRotation = Quaternion.identity;
            }
            else
            {
                it.transform.localPosition = it.putDownPosition;
                it.transform.localScale = it.putDownScale;
                it.transform.localRotation = Quaternion.Euler(it.putDownRotation);
            }
            foreach (Transform t in it.GetComponentsInChildren<Transform>()) { t.gameObject.layer = 22; }
            foreach (Collider col in it.GetComponentsInChildren<Collider>()) { col.enabled = true; }
            it.SendMessage("PutDown", SendMessageOptions.DontRequireReceiver);
            if (it.pickUpSound != null) { Object.Instantiate(it.pickUpSound); }
            Forget();
            foreach (ItemPlaceZone z in zones) { z.CheckItem(); }
            arm?.Throw();
            Sound(PlaceSample, PlaceVolume);
            Plugin.Event($"placed {it.name} on {target.name}");
        }

        // Punch.ForceThrow: only items with physics fly; the rest (skulls) stay in hand
        private void Throw(Transform cam, LeftArm arm)
        {
            Rigidbody[] rbs = Held.GetComponentsInChildren<Rigidbody>();
            if (rbs.Length == 0)
            {
                _c.Audio.Play("BEAM_SWITCH_FAIL", 72 / 127f);
                return;
            }
            ItemIdentifier it = Held;
            Drop(cam.position + cam.forward);
            foreach (Rigidbody rb in rbs) { rb.AddForce((cam.forward + Vector3.up * 0.1f) * ThrowForce); }
            arm?.Throw();
            Sound(ThrowSample, ThrowVolume);
            Plugin.Event($"threw {it.name}");
        }

        // Punch.ForceDrop: back into the world with physics (at a point)
        public void Drop(Vector3 at)
        {
            if (!Holding) { return; }
            ItemIdentifier it = Held;
            if (it.GetComponentsInChildren<Rigidbody>().Length == 0) { return; }   // (skulls can't be dropped either)
            it.transform.SetParent(null, true);
            it.pickedUp = false;
            it.transform.localScale = it.reverseTransformSettings ? Vector3.one : it.putDownScale;
            foreach (Transform t in it.GetComponentsInChildren<Transform>()) { t.gameObject.layer = 22; }
            foreach (Rigidbody rb in it.GetComponentsInChildren<Rigidbody>()) { rb.isKinematic = false; }
            foreach (Collider col in it.GetComponentsInChildren<Collider>()) { col.enabled = true; }
            if (!it.hooked) { it.transform.position = at; }
            it.SendMessage("PutDown", SendMessageOptions.DontRequireReceiver);
            Forget();
        }

        private void Forget()
        {
            if (MonoSingleton<FistControl>.TryGetInstance(out FistControl fc) && fc != null && fc.heldObject == Held) { fc.heldObject = null; }
            Held = null;
        }

        // the held item rides in front of the left palm (first person) or the hand (third person / ball)
        public void LateUpdate(LeftArm arm, bool firstPerson, Vector3 handWorld)
        {
            if (Held == null && Held is object) { Forget(); }
            if (!Holding) { return; }
            Transform cam = _c.Cam != null ? _c.Cam.transform : null;
            if (cam == null) { return; }
            _holder.localRotation = Quaternion.identity;
            _holder.localScale = Vector3.one * HolderScale;
            _holder.localPosition = firstPerson && arm != null && arm.Loaded
                ? SamusController.ToUnity(arm.Held) * _c.K
                : cam.InverseTransformPoint(handWorld);
            Held.gameObject.SetActive(true);
        }

        private void Sound(int sample, float vol)
        {
            string n = SamusController.SoundName(sample);
            if (n != null) { _c.Audio.Play(n, vol); }
        }

        public void Destroy()
        {
            if (_holder != null) { Object.Destroy(_holder.gameObject); }
        }
    }
}
