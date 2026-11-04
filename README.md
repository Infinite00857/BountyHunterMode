# MPH Samus for ULTRAKILL

Play ULTRAKILL as **Samus from Metroid Prime Hunters (Nintendo DS)**: MPH movement and physics, the first-person arm
cannon with its animations, every MPH weapon, the Morph Ball, the helmet HUD, MPH effects and sounds, all inside
ULTRAKILL's own levels.

> Fan-made and unofficial. Not affiliated with or endorsed by Nintendo, Retro Studios, Nintendo Software Technology,
> Arsi "Hakita" Patala or New Blood Interactive. **No game data is included**: the mod reads your own copy of
> Metroid Prime Hunters.

## Features
- **Samus Mode cheat**: enable cheats, open the cheat menu and turn on *Samus Mode* (category METROID PRIME HUNTERS).
  Samus replaces V1 right away; turn it off to get V1 back. Your FOV, sensitivity and other settings stay as they are.
- **MPH movement**: walking, strafing, jumping, gravity and landing as in the DS game (fixed 60 Hz simulation).
- **Every weapon from the start**: Power Beam (tap, autofire, charge), Missiles (charged ones home in), Volt Driver,
  Battlehammer, Imperialist (right mouse zooms), Judicator, Magmaul, Shock Coil.
- **All expansions**: 7 Energy Tanks (799 energy), 9 Missile Expansions (95 missiles), 12 UA Expansions.
- **Morph Ball**: rolling, Boost Ball, bombs (they hit like ground slams and blow up breakables).
- **MPH helmet HUD**: energy, tanks, ammo, weapon icon, target circle, damage flashes and the low-energy alarm.
- **Plays by ULTRAKILL's rules**: missiles follow rocket rules (explosion knockback, Malicious Face / armor
  ricochets), the charged Power Beam parries melee attacks and destroys parryable projectiles, the Volt Driver and Shock
  Coil electrocute (and electrify water), the Magmaul ignites, the Judicator chills, the Imperialist pierces like the
  railcannon, and the charged Volt Driver powers electric switches.
- **Works with ULTRAKILL's level tech**: jump pads, launches, lava, water, the Fraud layer's portals and gravity
  changes, the 8-4 free fall, terminals (the arm cannon lowers and Samus reaches out with her left hand), elevators
  (HUD and arm cannon put away, V1's gravity while riding), carried items (skulls etc.) and Dual Wield.
- MPH health and ammo drops from killed enemies, and MPH pickups in the Sandbox spawner menu.
- F5 third-person view, MPH-style pointer aiming (F4 toggles it), MPH sound effects.

## Requirements
- ULTRAKILL (Steam, PC) with **BepInEx 5** (BepInExPack 5.4.21+; tested with BepInEx 5.4.23.6).
- Your own **Metroid Prime Hunters** ROM, **USA version** (game code AMHE, revision 0 or 1), as a `.nds` file.

## Install
1. Install BepInEx 5 for ULTRAKILL (or use a mod manager such as r2modman / Thunderstore Mod Manager).
2. Put the mod's files in `ULTRAKILL\BepInEx\plugins\MphSamus\` (a mod manager does this for you).
3. Copy your Metroid Prime Hunters `.nds` file into the **ULTRAKILL folder** (next to `ULTRAKILL.exe`). Any file name
   works. It can also sit next to `MphSamus.dll`.
4. Start the game. The first start extracts the ROM once (about 20 seconds) into `BepInEx\cache\MphSamus\`.
5. In a level: enable cheats (ULTRAKILL's cheat code), open the cheat menu (`~` or Home) and turn on **Samus Mode**.
   Turn on ULTRAKILL's *Keep cheats enabled* if you want Samus in every level.

Note: as with all ULTRAKILL cheats, levels played with cheats enabled don't get ranks.

## Controls
| Key | Action |
|---|---|
| ULTRAKILL move / jump keys | Walk, strafe, jump |
| Fire | Shoot (hold to charge); in the Morph Ball: bombs |
| Slide | Morph Ball / stand up |
| Dash (hold) | Boost Ball |
| 1-8, mouse wheel | Weapons (1 Power Beam, 2 Missiles, 3 Shock Coil, 4 Imperialist, 5 Battlehammer, 6 Volt Driver, 7 Judicator, 8 Magmaul) |
| F | Missiles |
| Hold Q | Weapon wheel |
| Right mouse | Imperialist zoom |
| E | Pick up / place / throw items (skulls...); looking down: pull out a Hideous Mass harpoon |
| F5 | Third-person view |
| F4 | Pointer aim on / off |

## Configuration
`BepInEx\config\infinite.mphsamus.cfg` (created after the first start): damage scales, knockback, missile capacity,
splash self-damage, third-person distance, sound volume, step height, slope limit and more.

## Uninstall
Delete `BepInEx\plugins\MphSamus\` (or remove it in your mod manager). `BepInEx\cache\MphSamus\` holds the extracted
ROM data and can be deleted too.

## Troubleshooting
- The mod writes `BepInEx\MphSamus.log`. Set `Debug/LogEvents = true` in the config for a detailed gameplay log.
- "Samus Mode needs your Metroid Prime Hunters (USA) .nds file...": the ROM wasn't found; see Install step 3.
- Extraction failed: make sure the ROM is the USA version and not a patched or trimmed copy.

## Building from source
Needs the .NET 10 SDK, and ULTRAKILL with BepInEx 5 installed (the game's DLLs are referenced from the game folder).
```powershell
.\build.ps1 -UkDir "C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL"
.\build.ps1 -UkDir "<ULTRAKILL folder>" -Rom "<your MPH .nds>" -Sim   # + headless checks (src\MphSim)
```
`build.ps1` builds `src\MphSamus` and copies the DLLs into the game's `BepInEx\plugins\MphSamus`.

- `src/MphCore`: MphRead's ROM readers (models, animations, textures, sounds, effects, weapon and player tables) and
  Samus's simulation (no Unity).
- `src/MphSamus`: the BepInEx plugin (Unity side, Harmony patches).
- `src/MphSim`: headless checks of the simulation, rendering a few things to PNG.

## Credits
- **MphRead** by NoneGiven (MIT, `src/MphCore/MphRead/LICENSE-MphRead.txt`): the reverse-engineered Metroid Prime
  Hunters readers and game logic this mod is built on.
- **BepInEx** and **Harmony** (mod loader and patching), **OpenTK.Mathematics** and
  **CommunityToolkit.HighPerformance** (MIT; see `THIRD-PARTY-NOTICES.md`).
- ULTRAKILL by Arsi "Hakita" Patala / New Blood Interactive. Metroid Prime Hunters by Nintendo / Nintendo Software
  Technology.
- Design and playtesting: infinite. Code written with Claude Code (Anthropic's Claude Opus).

## License
The mod's own code is MIT licensed (`LICENSE`). MphRead's code keeps its own MIT license. The mod contains no Nintendo
or ULTRAKILL assets; everything MPH is read from the player's own ROM at runtime.
