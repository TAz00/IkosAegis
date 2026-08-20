# Builds a CKAN/CurseForge-ready release zip.
#
# The zip's top-level folder is GameData, and the folder CKAN looks for is
# GameData\IkosAegis - which matches the `find` directive in IkosAegis.netkan and is also
# what CKAN would default to with no `install` section at all.
#
# Nothing here is clever. The value is in the checks: four separate files carry the version
# number and a release that disagrees with itself is the kind of thing nobody notices until
# CKAN indexes it.
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root    = $PSScriptRoot
$modName = "IkosAegis"
$staging = Join-Path $root "obj\package"
$dist    = Join-Path $root "dist"

# ---------------------------------------------------------------------------------------
# 1. Version consistency
# ---------------------------------------------------------------------------------------
# Four places carry the version, and CLAUDE.md says all four must agree. Saying it is not
# enforcing it, so this is where it is enforced. The git tag is the fourth and is checked
# only advisorily - it does not exist yet at packaging time.

$assemblyInfo = Get-Content (Join-Path $root "Properties\AssemblyInfo.cs") -Raw
if ($assemblyInfo -notmatch 'AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)') {
    throw "Could not read AssemblyVersion out of Properties\AssemblyInfo.cs"
}
$asmMajor, $asmMinor, $asmPatch = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]
$version = "$asmMajor.$asmMinor.$asmPatch"

$versionFile = Join-Path $root "GameData\$modName\$modName.version"
$avc = Get-Content $versionFile -Raw | ConvertFrom-Json
$avcVersion = "$($avc.VERSION.MAJOR).$($avc.VERSION.MINOR).$($avc.VERSION.PATCH)"

if ($avcVersion -ne $version) {
    throw "Version mismatch: AssemblyInfo.cs says $version, $modName.version says $avcVersion."
}

# The ModuleManager beacon other mods branch on. Encoded MAJOR*10000 + MINOR*100 + PATCH
# because :HAS[] comparisons are numeric and understand only < and >, so a dotted version
# could not be compared at all.
$expectedBeacon = $asmMajor * 10000 + $asmMinor * 100 + $asmPatch
$patch = Get-Content (Join-Path $root "GameData\$modName\Patches\AegisLock.cfg") -Raw
if ($patch -notmatch '%aegisVersion\s*=\s*(\d+)') {
    throw "Could not find %aegisVersion in Patches\AegisLock.cfg"
}
if ([int]$Matches[1] -ne $expectedBeacon) {
    throw "Version mismatch: AegisLock.cfg beacon is $($Matches[1]), expected $expectedBeacon for $version."
}

Write-Host "Version $version agrees across AssemblyInfo, $modName.version and the MM beacon."

# ---------------------------------------------------------------------------------------
# 2. Metadata sanity
# ---------------------------------------------------------------------------------------
# A malformed .netkan is rejected by the indexer long after the release is published, so it
# is worth a parse here. The identifier check is the CKAN convention that the identifier,
# the GameData folder and the :FOR[] token are one string.

$netkanPath = Join-Path $root "$modName.netkan"
$netkan = Get-Content $netkanPath -Raw | ConvertFrom-Json    # throws on malformed JSON

if ($netkan.identifier -ne $modName) {
    throw "netkan identifier '$($netkan.identifier)' does not match the GameData folder name '$modName'."
}
if ($patch -notmatch ":FOR\[$modName\]") {
    throw "AegisLock.cfg has no :FOR[$modName] pass - the CKAN identifier and the MM token must agree."
}

$dependNames = @($netkan.depends | ForEach-Object { $_.name })
foreach ($required in @("ModuleManager", "Harmony2")) {
    if ($dependNames -notcontains $required) {
        throw "netkan is missing a hard dependency on $required."
    }
}
Write-Host "netkan parses; identifier, :FOR[] token and dependencies ($($dependNames -join ', ')) all check out."

# ---------------------------------------------------------------------------------------
# 3. Build
# ---------------------------------------------------------------------------------------

if (-not $SkipBuild) {
    & (Join-Path $root "Build.ps1") -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Release build failed." }
}

$dll = Join-Path $root "bin\Release\$modName.dll"
if (-not (Test-Path $dll)) { throw "No Release build at $dll. Run without -SkipBuild." }

# ---------------------------------------------------------------------------------------
# 4. Stage
# ---------------------------------------------------------------------------------------
# Rebuilt from nothing every time, so a file deleted from the repo cannot survive in a
# release because it was still sitting in the staging directory.

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$modRoot = Join-Path $staging "GameData\$modName"
New-Item -ItemType Directory -Path (Join-Path $modRoot "Plugins") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $modRoot "Patches") -Force | Out-Null

Copy-Item $dll (Join-Path $modRoot "Plugins") -Force
Copy-Item $versionFile $modRoot -Force
Copy-Item (Join-Path $root "LICENSE") $modRoot -Force

# .cfg only. Patches\ also holds opt-in patches shipped as .txt in some versions, and those
# are documentation - copying them as-is is correct, but a stray .cfg.bak must never travel.
Get-ChildItem (Join-Path $root "GameData\$modName\Patches") -File |
    Where-Object { $_.Extension -in ".cfg", ".txt" } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $modRoot "Patches") -Force }

# Not the README: it lives on GitHub, where its screenshots resolve. A copy installed into
# GameData would be a page of broken image links.
@"
IkosAegis $version
==================

A keypad PIN lock for Kerbal Space Program 1.12.

Full documentation, screenshots and configuration notes:
    https://github.com/taz00/IkosAegis

Requires ModuleManager, and Harmony (GameData/000_Harmony) for recovery blocking.

CREDITS
    Idea and concept   Ice King of Space  https://www.twitch.tv/icekingofspace
    Developed by       drebsdorf          https://www.twitch.tv/drebsdorf

    AI was used in this project.

Licensed MIT - see LICENSE.
"@ | Set-Content (Join-Path $modRoot "ReadMe.txt") -Encoding UTF8

# 0Harmony.dll must never appear here. Two copies of one assembly in a single Mono process
# fight over the same patch database, which is why the dependency exists instead.
$stray = Get-ChildItem $staging -Recurse -Include "0Harmony.dll", "ModuleManager*.dll", "*.pdb"
if ($stray) {
    throw "Refusing to package a redistributed dependency or debug symbols: $($stray.Name -join ', ')"
}

# ---------------------------------------------------------------------------------------
# 5. Zip
# ---------------------------------------------------------------------------------------

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist -Force | Out-Null }
$zip = Join-Path $dist "$modName-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip)

Write-Host ""
Write-Host "Packaged: $zip"
[System.IO.Compression.ZipFile]::OpenRead($zip).Entries |
    ForEach-Object { "  $($_.FullName)" }
Write-Host ""
Write-Host "Next:"
Write-Host "  1. git tag $version && git push --tags"
Write-Host "  2. Create a GitHub release on that tag and attach the zip as an ASSET"
Write-Host "     (the `$kref reads release assets, not the source archive)."
Write-Host "  3. First release only: PR $modName.netkan into KSP-CKAN/NetKAN under NetKAN/."
Write-Host "     After that the bot picks up each new release on its own."
