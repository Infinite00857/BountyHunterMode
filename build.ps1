# Build the MPH Samus mod and copy it into ULTRAKILL's BepInEx\plugins\MphSamus (Windows / PowerShell).
# Usage:  .\build.ps1                (build + deploy)
#         .\build.ps1 -Sim           (also run the headless checks in src\MphSim)
#         .\build.ps1 -Package       (also make the release zip: release\MPH_Samus-<version>.zip, Thunderstore layout)
#         .\build.ps1 -UkDir "D:\SteamLibrary\steamapps\common\ULTRAKILL" -Rom "C:\roms\Metroid Prime Hunters.nds"
# Needs the .NET 10 SDK on PATH: https://dotnet.microsoft.com/download
param(
    [string]$UkDir = $(if ($env:ULTRAKILL_DIR) { $env:ULTRAKILL_DIR } else { "C:\Program Files (x86)\Steam\steamapps\common\ULTRAKILL" }),
    [string]$Rom = (Join-Path $PSScriptRoot "game\Metroid Prime Hunters.nds"),
    [switch]$Sim,
    [switch]$Package
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if (-not (Test-Path (Join-Path $UkDir "ULTRAKILL_Data\Managed\Assembly-CSharp.dll"))) {
    throw "ULTRAKILL not found at '$UkDir'. Pass -UkDir or set ULTRAKILL_DIR."
}
if (-not (Test-Path (Join-Path $UkDir "BepInEx\core\BepInEx.dll"))) {
    throw "BepInEx 5 is not installed in '$UkDir' (BepInEx\core\BepInEx.dll missing)."
}
dotnet build src\MphSamus\MphSamus.csproj -c Release -nologo -v q "-p:UkDir=$UkDir"
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$bin = "src\MphSamus\bin\Release\netstandard2.1"
$out = Join-Path $UkDir "BepInEx\plugins\MphSamus"
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item "$bin\MphSamus.dll", "$bin\MphCore.dll", "lib\OpenTK.Mathematics.dll", "lib\CommunityToolkit.HighPerformance.dll", "src\MphCore\MphRead\LICENSE-MphRead.txt" $out -Force
# the mod reads the player's own ROM from next to its DLL (never shipped with the mod)
if (-not (Get-ChildItem $out -Filter *.nds -ErrorAction SilentlyContinue) -and (Test-Path $Rom)) {
    Copy-Item $Rom $out
    Write-Host "copied your ROM next to the DLL: $out"
}
Write-Host "deployed to $out"
if ($Package) {
    # Thunderstore layout: manifest, icon, README and CHANGELOG at the root, next to the DLLs (no ROM, no game files)
    $version = (Get-Content thunderstore\manifest.json -Raw | ConvertFrom-Json).version_number
    $stage = Join-Path $PSScriptRoot "release\MPH_Samus-$version"
    $zip = "$stage.zip"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    if (Test-Path $zip) { Remove-Item $zip -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item "$bin\MphSamus.dll", "$bin\MphCore.dll", "lib\OpenTK.Mathematics.dll", "lib\CommunityToolkit.HighPerformance.dll", "src\MphCore\MphRead\LICENSE-MphRead.txt", "thunderstore\manifest.json", "thunderstore\icon.png", "README.md", "CHANGELOG.md", "LICENSE", "THIRD-PARTY-NOTICES.md" $stage
    Compress-Archive -Path "$stage\*" -DestinationPath $zip
    Write-Host "packaged $zip"
}
if ($Sim) {
    $cache = Join-Path $PSScriptRoot "work\uk_cache"
    $simOut = Join-Path $PSScriptRoot "work\uk_sim_out"
    dotnet run --project src\MphSim -c Release "-p:UkDir=$UkDir" -- $Rom $cache $simOut
}
