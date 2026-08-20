<#
.SYNOPSIS
    Watches KSP.log and captures the game window when a configured event happens.

.DESCRIPTION
    Screenshots of a running game are the one documentation asset that cannot be generated
    from source, and getting them by hand means playing to a specific moment with one finger
    over PrintScreen - which is why mod READMEs are full of ASCII mock-ups instead.

    This turns "I need a picture of the keypad refusing a recovery" into a line of config.
    The mod already logs that moment; the watcher tails the log, and when the marker appears
    it grabs the window a beat later and writes a PNG.

    **Each shot is taken once.** A manifest next to the images records what has been
    captured, so the watcher can be left running through every future deploy and test session
    without re-taking - and without overwriting a good screenshot with a worse one from a
    half-built craft. Delete a PNG, or pass -Recapture <id>, to re-arm one.

.PARAMETER Config
    Shot definitions. Defaults to Screenshots.json beside this script.

.PARAMETER Recapture
    Ids to take again even though the manifest already has them.

.PARAMETER All
    Re-arm every shot. Use after a UI change that dates all the images at once.

.PARAMETER ListOnly
    Print what is outstanding and what the player has to do to trigger it, then exit.

.EXAMPLE
    .\Scripts\Watch-Screenshots.ps1 -ListOnly
    # what is still missing, and how to make each one happen

.EXAMPLE
    .\Scripts\Watch-Screenshots.ps1
    # start watching, then play. Ctrl-C when done.

.EXAMPLE
    .\Scripts\Watch-Screenshots.ps1 -Recapture recovery-keypad
#>
param(
    [string]$Config = (Join-Path $PSScriptRoot "Screenshots.json"),
    [string[]]$Recapture = @(),
    [switch]$All,
    [switch]$ListOnly
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# GetWindowRect is the only native call needed: everything else is System.Drawing.
# CopyFromScreen reads the desktop, so the game window has to be visible - which it is,
# because somebody is playing it. A minimised or fully covered window captures whatever is
# on top instead, and that is a limitation rather than a bug worth engineering around.
if (-not ("IkosAegis.Win32" -as [type])) {
    Add-Type -Namespace IkosAegis -Name Win32 -MemberDefinition @'
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
'@
}

# ---------------------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------------------

if (-not (Test-Path $Config)) { throw "No shot config at $Config" }
$cfg = Get-Content $Config -Raw | ConvertFrom-Json

$repoRoot = Split-Path $PSScriptRoot -Parent
$outDir   = if ([System.IO.Path]::IsPathRooted($cfg.outputDir)) { $cfg.outputDir }
            else { Join-Path $repoRoot $cfg.outputDir }
$logPath  = $cfg.logPath

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$manifestPath = Join-Path $outDir "manifest.json"

$manifest = @{}
if (Test-Path $manifestPath) {
    (Get-Content $manifestPath -Raw | ConvertFrom-Json).PSObject.Properties |
        ForEach-Object { $manifest[$_.Name] = $_.Value }
}

function Save-Manifest {
    ($manifest | ConvertTo-Json -Depth 6) | Set-Content $manifestPath -Encoding UTF8
}

# ---------------------------------------------------------------------------------------
# Which shots are outstanding
# ---------------------------------------------------------------------------------------
# A manifest entry whose PNG has been deleted counts as outstanding: deleting the image is
# the obvious way to ask for a retake, so it should be the way that works.

$outstanding = @()
foreach ($shot in $cfg.shots) {
    $file = Join-Path $outDir "$($shot.id).png"

    $done = $manifest.ContainsKey($shot.id) -and (Test-Path $file)
    if ($All) { $done = $false }
    if ($Recapture -contains $shot.id) { $done = $false }

    if (-not $done) { $outstanding += $shot }
}

Write-Host ""
Write-Host "Screenshot watcher - $($cfg.shots.Count) shot(s) defined, $($outstanding.Count) outstanding." -ForegroundColor Cyan
Write-Host "Images: $outDir"
Write-Host ""

if ($outstanding.Count -eq 0) {
    Write-Host "Nothing to capture. Delete a PNG or pass -Recapture <id> to retake one." -ForegroundColor Green
    return
}

# Everything the player has to actually do. This is the half of the job a script cannot do,
# so it is stated up front rather than left to be inferred from shot ids.
Write-Host "To trigger the outstanding shots:" -ForegroundColor Yellow
$n = 0
foreach ($shot in $outstanding) {
    $n++
    Write-Host ("  {0}. [{1}] {2}" -f $n, $shot.id, $shot.caption)
    if ($shot.requires) {
        Write-Host ("      -> {0}" -f $shot.requires) -ForegroundColor Gray
    } else {
        Write-Host  "      -> no action needed; fires on its own" -ForegroundColor Gray
    }
}
Write-Host ""

if ($ListOnly) { return }

# ---------------------------------------------------------------------------------------
# Capture
# ---------------------------------------------------------------------------------------

function Get-KspWindow {
    foreach ($name in $cfg.processNames) {
        $p = Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p -and $p.MainWindowHandle -ne 0) { return $p.MainWindowHandle }
    }
    return [IntPtr]::Zero
}

function Save-WindowShot([string]$path) {
    $hwnd = Get-KspWindow
    if ($hwnd -eq [IntPtr]::Zero) {
        Write-Warning "KSP window not found - is the game running? Shot skipped."
        return $false
    }

    $rect = New-Object IkosAegis.Win32+RECT
    if (-not [IkosAegis.Win32]::GetWindowRect($hwnd, [ref]$rect)) {
        Write-Warning "GetWindowRect failed. Shot skipped."
        return $false
    }

    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) {
        Write-Warning "KSP window has no size (minimised?). Shot skipped."
        return $false
    }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
        } finally { $g.Dispose() }

        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bmp.Dispose() }

    return $true
}

# ---------------------------------------------------------------------------------------
# Tail the log
# ---------------------------------------------------------------------------------------
# Start at the END of the current file, not the beginning: the markers are almost certainly
# already in there from previous sessions, and replaying history would capture the menu
# screen for every shot at once.
#
# KSP truncates KSP.log *in place* on launch, so the file getting shorter means a new run
# started rather than that anything is wrong - reset to 0 and keep going. Without this the
# watcher silently stops seeing anything after the first relaunch.

Write-Host "Watching $logPath" -ForegroundColor Cyan
Write-Host "Play the game. Ctrl-C to stop." -ForegroundColor Cyan
Write-Host ""

$position = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }

while ($outstanding.Count -gt 0) {
    Start-Sleep -Milliseconds 400

    if (-not (Test-Path $logPath)) { continue }

    $len = (Get-Item $logPath).Length
    if ($len -lt $position) {
        Write-Host "  (log truncated - KSP restarted; following the new run)" -ForegroundColor DarkGray
        $position = 0
    }
    if ($len -eq $position) { continue }

    $stream = [System.IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
    try {
        $stream.Seek($position, 'Begin') | Out-Null
        $reader = New-Object System.IO.StreamReader($stream)
        $new = $reader.ReadToEnd()
        $position = $stream.Position
    } finally { $stream.Dispose() }

    foreach ($line in ($new -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        # ToArray(): the collection is rebuilt inside the loop when a shot is taken.
        foreach ($shot in @($outstanding)) {
            if ($line -notmatch $shot.pattern) { continue }

            # The log line fires when the *code* runs; the dialog or toast it describes needs
            # a frame or two to actually be on screen. Without this delay the keypad shots
            # come back showing the moment before the keypad appeared.
            $delay = if ($shot.delayMs) { [int]$shot.delayMs } else { 600 }
            Start-Sleep -Milliseconds $delay

            $file = Join-Path $outDir "$($shot.id).png"
            if (Save-WindowShot $file) {
                $manifest[$shot.id] = [ordered]@{
                    capturedUtc = (Get-Date).ToUniversalTime().ToString("o")
                    file        = "$($shot.id).png"
                    caption     = $shot.caption
                    matchedLine = $line.Trim()
                }
                Save-Manifest

                $outstanding = @($outstanding | Where-Object { $_.id -ne $shot.id })
                Write-Host ("  captured [{0}] -> {1}  ({2} left)" -f $shot.id, "$($shot.id).png", $outstanding.Count) -ForegroundColor Green
            }
        }
    }
}

Write-Host ""
Write-Host "All shots captured. Manifest: $manifestPath" -ForegroundColor Green
