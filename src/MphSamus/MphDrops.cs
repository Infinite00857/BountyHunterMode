// MphDrops - what killed ULTRAKILL enemies leave for Samus. MPH has no drop table: each story EnemySpawn has its own
// MphItemType + ItemChance (dumped from the ROM by MphSim MPH_DROP_DUMP=1). The tiers below follow those numbers -
// small enemies (Zoomer, Geemer, War Wasp, Shriekbat) mostly drop nothing or a small item at 10-30%, mid ones
// (Ithraks, Voldrums, Quadtroids, Carnivorous Plants) 20-50% with medium health / big ammo, big ones a HealthBig at
// 100% - and the owner's rule that heavier enemies drop more and bosses a ton (rolls per kill). Items appear at the
// body's center + 0.5 up (EnemyInstanceEntity death), floating where it died (owner), and vanish after 15 s;
// high ones come down with the charge pull (MphItem). Sand (Stalker) enemies drop nothing
// and neither do puppets (copies).
using System.Collections.Generic;
using MphRead;
using UnityEngine;
using MphItemType = MphRead.ItemType;

namespace MphSamus
{
    public static class MphDrops
    {
        private enum Tier { Small, Medium, Heavy, Boss }

        // ---- TUNABLE CONSTANTS (drops per kill; mod values from MPH's spawner data + owner) ----
        private static readonly int[] Rolls = { 1, 2, 4, 12 };
        private static readonly float[] Chance = { 0.20f, 0.35f, 0.50f, 1f };
        private const float Scatter = 0.6f;      // MPH units between items of one kill (spiral)

        private static readonly HashSet<EnemyType> SmallTypes = new HashSet<EnemyType>
        {
            EnemyType.Filth, EnemyType.Stray, EnemyType.Schism, EnemyType.Soldier, EnemyType.Drone, EnemyType.Stalker,
            EnemyType.CancerousRodent,
        };
        private static readonly HashSet<EnemyType> HeavyTypes = new HashSet<EnemyType>
        {
            EnemyType.Swordsmachine, EnemyType.Cerberus, EnemyType.Mindflayer, EnemyType.Ferryman, EnemyType.Guttertank,
            EnemyType.Power, EnemyType.Mandalore, EnemyType.BigJohnator, EnemyType.VeryCancerousRodent,
        };
        private static readonly HashSet<EnemyType> BossTypes = new HashSet<EnemyType>
        {
            EnemyType.HideousMass, EnemyType.V2, EnemyType.V2Second, EnemyType.Gabriel, EnemyType.GabrielSecond,
            EnemyType.Minos, EnemyType.MinosPrime, EnemyType.Sisyphus, EnemyType.SisyphusPrime, EnemyType.FleshPrison,
            EnemyType.FleshPanopticon, EnemyType.Leviathan, EnemyType.Minotaur, EnemyType.Centaur, EnemyType.Geryon,
            EnemyType.MirrorReaper,
        };

        // item mixes per tier (weights), from the spawner data
        private static readonly (MphItemType, int)[][] Mix =
        {
            new[] { (MphItemType.HealthMedium, 45), (MphItemType.HealthSmall, 15), (MphItemType.MissileSmall, 20), (MphItemType.UASmall, 20) },
            new[] { (MphItemType.HealthMedium, 40), (MphItemType.HealthSmall, 20), (MphItemType.MissileBig, 15), (MphItemType.UABig, 15), (MphItemType.HealthBig, 10) },
            new[] { (MphItemType.HealthMedium, 30), (MphItemType.HealthSmall, 25), (MphItemType.MissileBig, 15), (MphItemType.UABig, 15), (MphItemType.HealthBig, 15) },
            new[] { (MphItemType.HealthBig, 40), (MphItemType.HealthSmall, 20), (MphItemType.MissileBig, 20), (MphItemType.UABig, 20) },
        };

        public static void OnKilled(EnemyIdentifier eid)
        {
            SamusController c = SamusController.Current;
            if (c == null || eid == null || eid.sandified || eid.puppet) { return; }
            Tier tier = eid.isBoss || BossTypes.Contains(eid.enemyType) ? Tier.Boss
                : SmallTypes.Contains(eid.enemyType) ? Tier.Small
                : HeavyTypes.Contains(eid.enemyType) ? Tier.Heavy : Tier.Medium;
            Collider col = eid.GetComponentInChildren<Collider>();
            Vector3 center = (col != null ? col.bounds.center : eid.transform.position) + Vector3.up * 0.5f * c.K;
            var items = new List<MphItemType>();
            for (int i = 0; i < Rolls[(int)tier]; i++)
            {
                if (Random.value < Chance[(int)tier]) { items.Add(Pick(Mix[(int)tier])); }
            }
            Spill(items, center, c.K);
            if (items.Count > 0) { Plugin.Event($"{eid.enemyType} ({tier}) dropped {string.Join(", ", items)}"); }
        }

        // owner: a charge-beam parry knocks a little health and ammo out of the enemy, dead or not
        public static void OnParried(EnemyIdentifier eid, Vector3 point)
        {
            SamusController c = SamusController.Current;
            if (c == null || eid == null || eid.sandified || eid.puppet) { return; }
            var items = new List<MphItemType>
            {
                Random.value < 0.75f ? MphItemType.HealthMedium : MphItemType.HealthSmall,
                Random.value < 0.5f ? MphItemType.MissileSmall : MphItemType.UASmall,
            };
            Collider col = eid.GetComponentInChildren<Collider>();
            Spill(items, (col != null ? col.bounds.center : point) + Vector3.up * 0.5f * c.K, c.K);
            Plugin.Event($"parry drops: {string.Join(", ", items)}");
        }

        // several items spread on a small spiral around the point (one stays at the center)
        public static void Spill(IList<MphItemType> items, Vector3 center, float k)
        {
            for (int i = 0; i < items.Count; i++)
            {
                float a = i * 2.4f, r = Scatter * k * Mathf.Sqrt(i);
                MphItem.Spawn(items[i], center + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r), MphItem.DropLifetime);
            }
        }

        private static MphItemType Pick((MphItemType, int)[] mix)
        {
            int total = 0;
            foreach ((MphItemType, int) m in mix) { total += m.Item2; }
            int roll = Random.Range(0, total);
            foreach ((MphItemType t, int w) in mix)
            {
                if (roll < w) { return t; }
                roll -= w;
            }
            return mix[0].Item1;
        }
    }
}
