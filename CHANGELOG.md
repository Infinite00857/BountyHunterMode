# Changelog

## 1.0.2 (2026-11-05)
- Not on Thunderstore: the mod is released on GitHub only. The release zip now extracts straight into the ULTRAKILL
  folder (`BepInEx\plugins\MphSamus\`); the Thunderstore manifest and icon are gone. No gameplay changes.

## 1.0.1 (2026-11-03)
- Clear AI disclaimer: all of the mod's code was written by AI (Anthropic's Claude).
- New icon: a render of Samus facing the camera.
- No ROM found: the mod opens a file picker (the choice is saved as `Samus/RomPath` in the config).
- ULTRAKILL launches (blasts, lava, launch triggers) follow V1's arc (V1's gravity until landing); the leftover
  bunny-hop speed carry from missile jumping is gone. Samus no longer gets thrown around by small launches.
- A fully charged Power Beam shot breaks Gutterman shields; missiles no longer do.
- Morph Ball: portals carry its camera and trail through (no camera swing across the room), and it's tracked through
  portals by the ball's center.

## 1.0.0 (2026-11-03)
First public release.
- Samus Mode cheat (ULTRAKILL's cheat menu, category METROID PRIME HUNTERS): Samus with every weapon and expansion;
  ULTRAKILL's FOV and other settings are kept.
- Power Beam uses Samus's single-player weapon data (4 autofire shots before the charge, as in the DS game).
- Missiles push enemies like ULTRAKILL's explosions.
- Electric weapons electrocute water; the charged Volt Driver powers electric switches (e.g. in 1-2).
- Fraud: shots go through portals, third-person / Morph Ball portal travel, gravity changes, the 8-4 free fall
  (no Morph Ball there).
- Terminals lower the arm cannon (no firing) and show Samus's left hand.
- Elevators hide the HUD and arm cannon and use V1's gravity.
- Pointer aim toggle moved to F4.
