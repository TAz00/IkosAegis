<#
.SYNOPSIS
    Finds KSP/Unity type and member names by reflecting over the game's managed assemblies.

.DESCRIPTION
    We build against a closed game with no source, so "what is this class actually called,
    and what is the exact signature" is the single most common question. Guessing and
    waiting for the compiler is slow and often wrong twice in a row; reflecting over the
    shipped assemblies answers it outright.

    Every assembly here throws ReflectionTypeLoadException when loaded outside the game -
    its Unity dependencies do not resolve - but the exception still carries a Types array of
    everything that *did* load, which is nearly all of it. That is the whole trick.

.EXAMPLE
    .\Find-KspType.ps1 -Type Strut
    # -> CompoundParts.CModuleStrut, CompoundParts.CModuleFuelLine, CompoundPart, ...

.EXAMPLE
    .\Find-KspType.ps1 -Type KerbalPortraitGallery -Member Portrait
    # lists every member matching /Portrait/ with its full signature

.EXAMPLE
    .\Find-KspType.ps1 -Type ^Vessel$ -Member . -Static
    # every static member of Vessel

.EXAMPLE
    .\Find-KspType.ps1 -Type ProtoVessel -Member CreateVesselNode -Assembly Assembly-CSharp
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Type,

    # Regex matched against member names. Omit to list matching types only.
    [string]$Member,

    # Restrict to assemblies whose file name matches this regex (e.g. 'UnityEngine\.UI').
    [string]$Assembly = '.',

    # Show only static members.
    [switch]$Static,

    # Include non-public types and members.
    [switch]$NonPublic,

    [string]$ManagedPath = 'C:\Projects\KSP\Kerbal Space Program Dev\KSP_x64_Data\Managed'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ManagedPath)) {
    throw "Managed folder not found: $ManagedPath"
}

# Assembly-CSharp holds all of KSP itself; KSPAssets covers the asset/bundle layer; the
# UnityEngine.*Module split means a type can live in any one of ~40 files.
$files = Get-ChildItem $ManagedPath -Filter '*.dll' |
    Where-Object { $_.Name -match $Assembly }

$allTypes = @()
foreach ($file in $files) {
    try {
        $asm = [System.Reflection.Assembly]::LoadFrom($file.FullName)
        $types = $asm.GetTypes()
    }
    catch [System.Reflection.ReflectionTypeLoadException] {
        # Expected: partial load. Keep whatever resolved.
        $types = $_.Exception.Types | Where-Object { $_ -ne $null }
    }
    catch {
        continue    # not a managed assembly, or unloadable - skip quietly
    }

    foreach ($t in $types) {
        if ($null -eq $t) { continue }
        if (-not $NonPublic -and -not ($t.IsPublic -or $t.IsNestedPublic)) { continue }
        # Match the short name as well as the namespaced one, so an anchored search like
        # '^KerbalPortraitGallery$' works without knowing it lives in KSP.UI.Screens.Flight.
        if ($t.FullName -match $Type -or $t.Name -match $Type) {
            $allTypes += [pscustomobject]@{ Type = $t; Assembly = $file.Name }
        }
    }
}

if ($allTypes.Count -eq 0) {
    Write-Host "No type matching /$Type/ found in $($files.Count) assemblies." -ForegroundColor Yellow
    return
}

$flags = [System.Reflection.BindingFlags]::Public -bor
         [System.Reflection.BindingFlags]::Instance -bor
         [System.Reflection.BindingFlags]::Static -bor
         [System.Reflection.BindingFlags]::FlattenHierarchy
if ($NonPublic) { $flags = $flags -bor [System.Reflection.BindingFlags]::NonPublic }

foreach ($entry in ($allTypes | Sort-Object { $_.Type.FullName })) {
    $t = $entry.Type

    if (-not $Member) {
        "{0,-60} [{1}]" -f $t.FullName, $entry.Assembly
        continue
    }

    Write-Host ("=== {0}   [{1}]" -f $t.FullName, $entry.Assembly) -ForegroundColor Cyan

    if ($t.IsEnum) {
        "    values: " + ($t.GetEnumNames() -join ', ')
        continue
    }

    $members = $t.GetMembers($flags) |
        Where-Object { $_.Name -match $Member } |
        Where-Object { -not $Static -or $_.IsStatic -or ($_ -is [System.Reflection.MethodBase] -and $_.IsStatic) }

    foreach ($m in ($members | Sort-Object MemberType, Name)) {
        "    {0,-10} {1}" -f $m.MemberType, $m.ToString()
    }

    if (-not $members) { "    (no member matching /$Member/)" }
}
