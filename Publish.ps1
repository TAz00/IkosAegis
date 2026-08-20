# Build output + ModuleManager patches -> KSP dev install, then launch and patch the
# debugger endpoint.
#
# Adapted from KSPRedeem\Publish.ps1. The one structural difference: this mod's behaviour
# lives half in the DLL and half in .cfg patches, so the patch files are *source* and are
# deployed on every publish. KSPRedeem's PluginData is runtime state and is deliberately
# left alone; there is no equivalent here.
param(
    [string]$Configuration = "Debug",
    [switch]$NoLaunch,

    # Permission to stop a running KSP. Deploying requires it - a running game holds
    # IkosAegis.dll open - but stopping someone's session is not a thing to do silently.
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$fromPath   = Join-Path $PSScriptRoot "bin\$Configuration"
$basePath   = "C:\Projects\KSP\Kerbal Space Program Dev"
$modName    = "IkosAegis"
$modRoot    = Join-Path $basePath "GameData\$modName"
$pluginsDir = Join-Path $modRoot "Plugins"
$patchesDir = Join-Path $modRoot "Patches"
$launchJson = Join-Path $PSScriptRoot ".vscode\launch.json"

foreach ($d in @($modRoot, $pluginsDir, $patchesDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# Keep the outgoing log before anything destroys it.
#
# The kill below is a *hard* kill, and the relaunch truncates KSP.log in place, so any error
# still unexplained in the current log is gone the moment this script runs. Copying it here
# mechanically is cheaper than remembering to look first - and a ModuleManager patch error
# is exactly the kind of thing that is easy to miss and expensive to lose.
$kspLog = Join-Path $basePath "KSP.log"
if ((Get-Process -Name "ksp_x64_dbg","KSP_x64" -ErrorAction SilentlyContinue) -and (Test-Path $kspLog)) {
    $debugDir = Join-Path $modRoot "Debug"
    if (-not (Test-Path $debugDir)) { New-Item -ItemType Directory -Path $debugDir -Force | Out-Null }

    $stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
    $target = Join-Path $debugDir "KSP-predeploy-$stamp.log"

    # FileShare.ReadWrite: the game still has it open.
    try {
        $in  = [System.IO.File]::Open($kspLog, 'Open', 'Read', 'ReadWrite')
        $out = [System.IO.File]::Create($target)
        $in.CopyTo($out)
        $out.Dispose(); $in.Dispose()
        Write-Host "Kept the outgoing log: $([System.IO.Path]::GetFileName($target))"
    } catch {
        Write-Warning "Could not keep the outgoing log: $_"
    }

    # MMPatch.log is where a bad patch actually reports itself; KSP.log only gets a replay
    # at the end of loading, which a crash during loading never reaches.
    $mmLog = Join-Path $basePath "Logs\ModuleManager\MMPatch.log"
    if (Test-Path $mmLog) {
        Copy-Item $mmLog (Join-Path $debugDir "MMPatch-predeploy-$stamp.log") -Force -ErrorAction SilentlyContinue
    }
}

# A running KSP holds the plugin file open - stop it before copying.
#
# **Refuse by default rather than killing silently.** -NoLaunch stops this script *starting*
# the game and says nothing about stopping one, which is a distinction that cost a live test
# session: the operator asked for a deploy without a launch while running their own tests,
# and got their game closed underneath them. Deploying genuinely does require the file lock
# to go, so the fix is not to skip the kill - it is to make it a decision.
$running = @(Get-Process -Name "ksp_x64_dbg", "KSP_x64" -ErrorAction SilentlyContinue)
if ($running -and -not $Force) {
    $names = ($running | ForEach-Object { "$($_.ProcessName) (pid $($_.Id), started $($_.StartTime.ToString('HH:mm:ss')))" }) -join ", "
    throw "KSP is running: $names`n" +
          "Deploying would close it, because a running game holds IkosAegis.dll open.`n" +
          "Close KSP yourself, or re-run with -Force to let this script stop it."
}

$running | Stop-Process -Force -ErrorAction SilentlyContinue
if ($running) { Start-Sleep -Milliseconds 500 }

Remove-Item -Path (Join-Path $pluginsDir "$modName.dll") -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $pluginsDir "$modName.pdb") -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

Copy-Item -Path (Join-Path $fromPath "$modName.dll") -Destination $pluginsDir -Force
Copy-Item -Path (Join-Path $fromPath "$modName.pdb") -Destination $pluginsDir -Force -ErrorAction SilentlyContinue

# Patches are source. Mirror them rather than merging, so a patch deleted here is also gone
# from the install - a stale .cfg left behind still patches the database and is invisible in
# the repo, which is a genuinely confusing failure.
Remove-Item -Path (Join-Path $patchesDir "*") -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $PSScriptRoot "GameData\$modName\Patches\*") -Destination $patchesDir -Recurse -Force

# Version manifest (KSP-AVC).
$versionFile = Join-Path $PSScriptRoot "GameData\$modName\$modName.version"
if (Test-Path $versionFile) {
    Copy-Item -Path $versionFile -Destination $modRoot -Force
}

Write-Host "Deployed $modName ($Configuration) to $modRoot"

# ModuleManager keys its cache on every config file's URL *and* contents, so the copy above
# has already invalidated it and the next launch re-patches. Nothing to delete by hand.
# Warn if MM is missing entirely, because without it no part ever receives the module and
# the mod does nothing at all, silently.
if (-not (Get-ChildItem (Join-Path $basePath "GameData") -Filter "ModuleManager*.dll" -ErrorAction SilentlyContinue)) {
    Write-Warning "No ModuleManager*.dll in GameData - the patches will not be applied and the mod will do nothing."
}

if ($NoLaunch) { exit 0 }

Start-Process -FilePath (Join-Path $basePath "KSP_x64_dbg.exe") `
    -ArgumentList "-force-d3d11" `
    -WorkingDirectory $basePath

if ($Configuration -ne "Debug") { exit 0 }

# The Unity player-connection port is dynamic (55000-57000) and -playerConnectionPort is
# ignored by this 2019.4.18f1 build, so discover it and rewrite launch.json each launch.
Write-Host "Waiting for KSP player connection port..."
$port = $null
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    $proc = Get-Process -Name "ksp_x64_dbg" -ErrorAction SilentlyContinue
    if ($proc) {
        $listeners = Get-NetTCPConnection -OwningProcess $proc.Id -ErrorAction SilentlyContinue |
            Where-Object { $_.State -eq 'Listen' -and $_.LocalPort -ge 55000 -and $_.LocalPort -le 57000 } |
            Sort-Object LocalPort -Descending
        if ($listeners) { $port = $listeners[0].LocalPort; break }
    }
    Start-Sleep -Milliseconds 500
}

if ($port) {
    $json = Get-Content $launchJson -Raw | ConvertFrom-Json
    $json.configurations[0].endPoint = "127.0.0.1:$port"
    $json | ConvertTo-Json -Depth 10 | Set-Content $launchJson -Encoding UTF8
    Write-Host "launch.json updated: endPoint = 127.0.0.1:$port"
} else {
    Write-Host "WARNING: Could not detect KSP player connection port within 60s. launch.json not updated."
}
