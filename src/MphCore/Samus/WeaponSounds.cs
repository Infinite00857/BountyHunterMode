// WeaponSounds - per-weapon sound samples (SNDTBLS index) and volumes (/127): the testbed's WEAPON_SOUNDS (BeamSfx shot,
// charged shot, charge loop) and BEAM_SOUNDS (PlayHitSfx) tables, hunters/mph_player.cpp + mph_weapons.cpp.
// Engines resolve names with SoundBank.NameAt.
namespace MphSamus.Core
{
    public readonly struct WeaponSounds
    {
        public readonly int Shot, ChargeShot, Charge, Hit, ChargeHit;   // -1 = none
        public readonly int ShotVol, ChargeShotVol, ChargeVol, HitVol, ChargeHitVol;

        public WeaponSounds(int shot, int chargeShot, int charge, int shotVol, int chargeShotVol, int chargeVol,
            int hit, int chargeHit, int hitVol, int chargeHitVol)
        {
            Shot = shot; ChargeShot = chargeShot; Charge = charge;
            ShotVol = shotVol; ChargeShotVol = chargeShotVol; ChargeVol = chargeVol;
            Hit = hit; ChargeHit = chargeHit; HitVol = hitVol; ChargeHitVol = chargeHitVol;
        }

        // by BeamType (Power Beam .. Omega Cannon)
        public static readonly WeaponSounds[] ByBeam =
        {
            new WeaponSounds(135, 138, 137, 110, 96, 110, 136, 136, 50, 50),     // Power Beam
            new WeaponSounds(146, 152, 151, 127, 127, 106, 147, 148, 127, 127),  // Volt Driver
            new WeaponSounds(140, 155, 154, 96, 116, 116, 141, 141, 127, 127),   // Missile
            new WeaponSounds(159, 159, 180, 127, 127, 110, 160, 160, 96, 96),    // Battlehammer
            new WeaponSounds(162, 162, 151, 110, 110, 106, 164, 164, 96, 96),    // Imperialist
            new WeaponSounds(184, 182, 180, 96, 110, 110, 185, 185, 48, 48),     // Judicator
            new WeaponSounds(168, 170, 169, 127, 127, 96, 173, 172, 127, 127),   // Magmaul
            new WeaponSounds(-1, -1, 169, 0, 0, 96, -1, -1, 0, 0),               // Shock Coil (DGN 47 loops: later)
            new WeaponSounds(-1, -1, -1, 0, 0, 0, -1, -1, 0, 0),                 // Omega Cannon
        };
    }
}
