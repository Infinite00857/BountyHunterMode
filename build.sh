#!/bin/zsh
# Build the MPH Samus mod and copy it into ULTRAKILL's BepInEx/plugins/MphSamus.
# Usage: ./build.sh [--sim] [--package]
#   --sim      also runs the headless checks in src/MphSim
#   --package  also makes the release zip: release/MPH_Samus-<version>.zip
set -e
cd "$(dirname "$0")"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
UK="${UK:-$HOME/Library/Application Support/Steam/steamapps/common/ULTRAKILL}"
ROM="${ROM:-$PWD/game/Metroid Prime Hunters.nds}"
OUT="$UK/BepInEx/plugins/MphSamus"
"$DOTNET" build src/MphSamus/MphSamus.csproj -c Release -nologo -v q
BIN=src/MphSamus/bin/Release/netstandard2.1
mkdir -p "$OUT"
cp "$BIN/MphSamus.dll" "$BIN/MphCore.dll" lib/OpenTK.Mathematics.dll lib/CommunityToolkit.HighPerformance.dll "$OUT/"
cp src/MphCore/MphRead/LICENSE-MphRead.txt "$OUT/"
# the mod reads the player's own ROM from next to its DLL (never shipped with the mod)
if [ -z "$(find "$OUT" -maxdepth 1 -name "*.nds" -print -quit)" ] && [ -f "$ROM" ]; then
  cp "$ROM" "$OUT/"
  echo "copied your ROM next to the DLL: $OUT/$(basename "$ROM")"
fi
echo "deployed to $OUT"
if [[ " $* " == *" --package "* ]]; then
  # players extract the zip into the ULTRAKILL folder: it holds BepInEx/plugins/MphSamus/ (never the ROM or game files)
  VERSION=$(sed -n 's/.*const string Version = "\([^"]*\)".*/\1/p' src/MphSamus/Plugin.cs)
  STAGE="$PWD/release/MPH_Samus-$VERSION"
  rm -rf "$STAGE" "$STAGE.zip"
  mkdir -p "$STAGE/BepInEx/plugins/MphSamus"
  cp "$BIN/MphSamus.dll" "$BIN/MphCore.dll" lib/OpenTK.Mathematics.dll lib/CommunityToolkit.HighPerformance.dll \
     src/MphCore/MphRead/LICENSE-MphRead.txt LICENSE THIRD-PARTY-NOTICES.md "$STAGE/BepInEx/plugins/MphSamus/"
  (cd "$STAGE" && zip -q -r -X "../MPH_Samus-$VERSION.zip" BepInEx)
  echo "packaged $STAGE.zip"
fi
if [[ " $* " == *" --sim "* ]]; then
  "$DOTNET" run --project src/MphSim -c Release -- "$ROM" "$PWD/work/uk_cache" "$PWD/work/uk_sim_out"
fi
