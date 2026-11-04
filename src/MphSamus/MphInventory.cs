// MphInventory - Samus Mode's inventory: every MPH weapon and every expansion the story has (counted from the ROM's
// story rooms: 7 Energy Tanks, 9 Missile Expansions, 12 UA Expansions). Amounts from PlayerProcess.PickUpItems
// (energy tank +100 max energy, missile expansion +100 raw = 10 missiles, UA expansion +300 raw).
namespace MphSamus
{
    public static class MphInventory
    {
        // ---- TUNABLE CONSTANTS (MPH PickUpItems; story totals) ----
        public const int EnergyTankAmount = 100;
        public const int MissileExpansionAmount = 100;
        public const int UaExpansionAmount = 300;
        public const int EnergyTanks = 7, MissileExpansions = 9, UaExpansions = 12;
    }
}
