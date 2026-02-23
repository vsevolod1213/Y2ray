[CmdletBinding()]
param(
    [switch]$RunAsAdmin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $rootDir "v2rayN"
$csprojPath = Join-Path $projectDir "v2rayN.Desktop\v2rayN.Desktop.csproj"
$publishDirPreferred = Join-Path $projectDir "Release\win-x64-uifix"
$appDisplayName = "Yvpn"
$preferredExeName = "$appDisplayName.exe"
$iconSourcePng = Join-Path $projectDir "v2rayN.Desktop\Assets\web-app-manifest-512x512.png"
$iconTargetIco = Join-Path $projectDir "v2rayN.Desktop\Assets\favicon.ico"
$localDataDir = Join-Path $env:LOCALAPPDATA $appDisplayName
$localGuiConfigsDir = Join-Path $localDataDir "guiConfigs"
$publishDir = $publishDirPreferred
$exePath = ""
$legacyGuiConfigsBackup = $null

if (-not (Test-Path $csprojPath)) {
    throw "Project file not found: $csprojPath"
}

function Stop-ProcessIfOwned {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process
    )

    try {
        $path = $null
        try {
            $path = $Process.MainModule.FileName
        }
        catch {
        }

        if (-not $path) {
            return
        }

        $fullPath = [System.IO.Path]::GetFullPath($path)
        $ownedRoots = @(
            [System.IO.Path]::GetFullPath((Join-Path $projectDir "Release")),
            [System.IO.Path]::GetFullPath((Join-Path $projectDir "v2rayN.Desktop\\bin")),
            [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA $appDisplayName))
        )

        $isOwned = $false
        foreach ($root in $ownedRoots) {
            if ($fullPath.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
                $isOwned = $true
                break
            }
        }

        if (-not $isOwned) {
            return
        }

        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
    catch {
    }
}

function Stop-ClientProcesses {
    foreach ($name in @("Yvpn", "v2rayN")) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }

    # Stop core processes only when they are from this workspace/app data.
    foreach ($name in @("xray", "sing-box")) {
        foreach ($proc in (Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            Stop-ProcessIfOwned -Process $proc
        }
    }
}

function Update-ProjectIconFromSource {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePng,
        [Parameter(Mandatory = $true)]
        [string]$TargetIco
    )

    if (-not (Test-Path $SourcePng)) {
        Write-Warning "Icon source not found: $SourcePng"
        return
    }

    Add-Type -AssemblyName System.Drawing

    $src = [System.Drawing.Bitmap]::new($SourcePng)
    $processed = $null
    $crop = $null
    $canvas = $null
    try {
        $corners = @(
            $src.GetPixel(0, 0),
            $src.GetPixel($src.Width - 1, 0),
            $src.GetPixel(0, $src.Height - 1),
            $src.GetPixel($src.Width - 1, $src.Height - 1)
        )
        $bgR = [int][Math]::Round((($corners | ForEach-Object { $_.R } | Measure-Object -Average).Average))
        $bgG = [int][Math]::Round((($corners | ForEach-Object { $_.G } | Measure-Object -Average).Average))
        $bgB = [int][Math]::Round((($corners | ForEach-Object { $_.B } | Measure-Object -Average).Average))

        $thresholdHard = 18.0
        $thresholdSoft = 62.0

        $processed = [System.Drawing.Bitmap]::new($src.Width, $src.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

        $minX = $src.Width
        $minY = $src.Height
        $maxX = -1
        $maxY = -1

        for ($y = 0; $y -lt $src.Height; $y++) {
            for ($x = 0; $x -lt $src.Width; $x++) {
                $c = $src.GetPixel($x, $y)
                $dr = [double]($c.R - $bgR)
                $dg = [double]($c.G - $bgG)
                $db = [double]($c.B - $bgB)
                $dist = [Math]::Sqrt($dr * $dr + $dg * $dg + $db * $db)

                $alpha = [double]$c.A
                if ($dist -le $thresholdHard) {
                    $alpha = 0
                }
                elseif ($dist -lt $thresholdSoft) {
                    $ratio = ($dist - $thresholdHard) / ($thresholdSoft - $thresholdHard)
                    $alpha = $alpha * $ratio
                }

                $a = [Math]::Min(255, [Math]::Max(0, [int][Math]::Round($alpha)))
                $processed.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B))

                if ($a -gt 8) {
                    if ($x -lt $minX) { $minX = $x }
                    if ($y -lt $minY) { $minY = $y }
                    if ($x -gt $maxX) { $maxX = $x }
                    if ($y -gt $maxY) { $maxY = $y }
                }
            }
        }

        if ($maxX -lt $minX -or $maxY -lt $minY) {
            throw "Failed to detect non-transparent icon bounds."
        }

        $cropW = $maxX - $minX + 1
        $cropH = $maxY - $minY + 1
        $crop = [System.Drawing.Bitmap]::new($cropW, $cropH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gCrop = [System.Drawing.Graphics]::FromImage($crop)
        try {
            $gCrop.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $gCrop.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $gCrop.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $srcRect = [System.Drawing.Rectangle]::new($minX, $minY, $cropW, $cropH)
            $dstRect = [System.Drawing.Rectangle]::new(0, 0, $cropW, $cropH)
            $gCrop.DrawImage($processed, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $gCrop.Dispose()
        }

        $side = [Math]::Max($cropW, $cropH)
        $pad = [Math]::Max(2, [int][Math]::Ceiling($side * 0.06))
        $canvasSide = $side + $pad * 2
        $canvas = [System.Drawing.Bitmap]::new($canvasSide, $canvasSide, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gCanvas = [System.Drawing.Graphics]::FromImage($canvas)
        try {
            $gCanvas.Clear([System.Drawing.Color]::Transparent)
            $gCanvas.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $gCanvas.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $gCanvas.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $dx = [int](($canvasSide - $cropW) / 2)
            $dy = [int](($canvasSide - $cropH) / 2)
            $gCanvas.DrawImage($crop, $dx, $dy, $cropW, $cropH)
        }
        finally {
            $gCanvas.Dispose()
        }

        $iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
        $entries = @()
        foreach ($size in $iconSizes) {
            $bmpSized = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [System.Drawing.Graphics]::FromImage($bmpSized)
            try {
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.DrawImage($canvas, 0, 0, $size, $size)

                $ms = [System.IO.MemoryStream]::new()
                $bmpSized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
                $entries += [pscustomobject]@{
                    Size  = $size
                    Bytes = $ms.ToArray()
                }
                $ms.Dispose()
            }
            finally {
                $g.Dispose()
                $bmpSized.Dispose()
            }
        }

        $targetDir = Split-Path -Parent $TargetIco
        New-Item -ItemType Directory -Force $targetDir | Out-Null

        $fs = [System.IO.File]::Open($TargetIco, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        $bw = [System.IO.BinaryWriter]::new($fs)
        try {
            $bw.Write([UInt16]0) # reserved
            $bw.Write([UInt16]1) # type = icon
            $bw.Write([UInt16]$entries.Count)

            $offset = 6 + (16 * $entries.Count)
            foreach ($entry in $entries) {
                $sizeByte = if ($entry.Size -ge 256) { 0 } else { [byte]$entry.Size }
                $bw.Write([byte]$sizeByte)
                $bw.Write([byte]$sizeByte)
                $bw.Write([byte]0) # color count
                $bw.Write([byte]0) # reserved
                $bw.Write([UInt16]1) # planes
                $bw.Write([UInt16]32) # bpp
                $bw.Write([UInt32]$entry.Bytes.Length)
                $bw.Write([UInt32]$offset)
                $offset += $entry.Bytes.Length
            }

            foreach ($entry in $entries) {
                $bw.Write($entry.Bytes)
            }
        }
        finally {
            $bw.Dispose()
            $fs.Dispose()
        }
    }
    finally {
        if ($canvas) { $canvas.Dispose() }
        if ($crop) { $crop.Dispose() }
        if ($processed) { $processed.Dispose() }
        if ($src) { $src.Dispose() }
    }
}

function Backup-LegacyGuiConfigs {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishPath
    )

    $source = Join-Path $PublishPath "guiConfigs"
    if (-not (Test-Path $source)) {
        return $null
    }

    $backup = Join-Path $env:TEMP ("yvpn-guiConfigs-" + (Get-Date -Format "yyyyMMdd-HHmmss-fff"))
    New-Item -ItemType Directory -Force $backup | Out-Null
    Copy-Item (Join-Path $source "*") $backup -Recurse -Force -ErrorAction SilentlyContinue
    return $backup
}

function Migrate-GuiConfigsToLocalAppData {
    param(
        [string]$BackupPath
    )

    New-Item -ItemType Directory -Force $localGuiConfigsDir | Out-Null
    if (-not $BackupPath -or -not (Test-Path $BackupPath)) {
        return
    }

    $backupConfig = Join-Path $BackupPath "yvpnConfig.json"
    $localConfig = Join-Path $localGuiConfigsDir "yvpnConfig.json"

    $shouldRestore = $false
    if (-not (Test-Path $localConfig)) {
        $shouldRestore = $true
    }
    elseif (Test-Path $backupConfig) {
        $shouldRestore = (Get-Item $backupConfig).LastWriteTimeUtc -gt (Get-Item $localConfig).LastWriteTimeUtc
    }

    if ($shouldRestore) {
        Copy-Item (Join-Path $BackupPath "*") $localGuiConfigsDir -Recurse -Force
    }
}

function Enable-LocalConfigStorage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishPath
    )

    $flagPath = Join-Path $PublishPath "NotStoreConfigHere.txt"
    Set-Content -Path $flagPath -Encoding UTF8 -Value "When this file exists, app will not store configs under this folder"
}

function Resolve-ExecutablePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishPath
    )

    $preferred = Join-Path $PublishPath $preferredExeName
    if (Test-Path $preferred) {
        return $preferred
    }

    $fallback = Join-Path $PublishPath "v2rayN.exe"
    if (Test-Path $fallback) {
        Copy-Item $fallback $preferred -Force
        return $preferred
    }

    $anyExe = Get-ChildItem -Path $PublishPath -Filter "*.exe" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($anyExe) {
        Copy-Item $anyExe.FullName $preferred -Force
        return $preferred
    }

    throw "Publish finished but executable not found in: $PublishPath"
}

function Select-PublishDir {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PreferredPath
    )

    if (-not (Test-Path $PreferredPath)) {
        return $PreferredPath
    }

    Stop-ClientProcesses
    Start-Sleep -Milliseconds 700

    try {
        Remove-Item -Recurse -Force $PreferredPath -ErrorAction Stop
        return $PreferredPath
    }
    catch {
        Write-Warning "Cannot clean '$PreferredPath' (file lock). Build will use a new output folder."
        Write-Warning "Tip: close app/tray and run terminal as Administrator to reuse the same folder."
    }

    $releaseDir = Split-Path -Parent $PreferredPath
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $fallbackPath = Join-Path $releaseDir ("win-x64-uifix-" + $timestamp)

    $suffix = 1
    while (Test-Path $fallbackPath) {
        $fallbackPath = Join-Path $releaseDir ("win-x64-uifix-" + $timestamp + "-" + $suffix)
        $suffix++
    }

    return $fallbackPath
}

function Resolve-CoreSourceBin {
    param(
        [string[]]$Candidates
    )

    $best = $null
    foreach ($candidate in $Candidates) {
        if (-not (Test-Path $candidate)) {
            continue
        }

        $hasXray = Test-Path (Join-Path $candidate "xray\xray.exe")
        $hasSing = Test-Path (Join-Path $candidate "sing_box\sing-box.exe")

        if ($hasXray -and $hasSing) {
            return $candidate
        }

        if (-not $best -and ($hasXray -or $hasSing)) {
            $best = $candidate
        }
    }

    return $best
}

function Copy-DirectoryContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDir,
        [Parameter(Mandatory = $true)]
        [string]$TargetDir
    )

    if (-not (Test-Path $SourceDir)) {
        return $false
    }

    New-Item -ItemType Directory -Force $TargetDir | Out-Null
    Copy-Item (Join-Path $SourceDir "*") $TargetDir -Recurse -Force
    return $true
}

function Copy-CoreIfMissing {
    $dstBin = Join-Path $publishDir "bin"
    $dstXrayDir = Join-Path $dstBin "xray"
    $dstSingDir = Join-Path $dstBin "sing_box"
    $dstXray = Join-Path $dstXrayDir "xray.exe"
    $dstSing = Join-Path $dstSingDir "sing-box.exe"
    $dstXrayGeoip = Join-Path $dstXrayDir "geoip.dat"
    $dstXrayGeosite = Join-Path $dstXrayDir "geosite.dat"
    $dstSingWintun = Join-Path $dstSingDir "wintun.dll"
    $dstRootGeoip = Join-Path $dstBin "geoip.dat"
    $dstRootGeosite = Join-Path $dstBin "geosite.dat"

    $needXray = (-not (Test-Path $dstXray)) -or (-not (Test-Path $dstXrayGeoip)) -or (-not (Test-Path $dstXrayGeosite)) -or (-not (Test-Path $dstRootGeoip)) -or (-not (Test-Path $dstRootGeosite))
    $needSing = (-not (Test-Path $dstSing)) -or (-not (Test-Path $dstSingWintun))
    if (-not ($needXray -or $needSing)) {
        return
    }

    $sourceCandidates = @(
        (Join-Path $projectDir "v2rayN.Desktop\bin\Debug\net8.0\bin"),
        (Join-Path $projectDir "v2rayN.Desktop\bin\Release\net8.0\win-x64\bin"),
        (Join-Path $projectDir "Release\win-x64\bin")
    )

    $sourceBin = Resolve-CoreSourceBin -Candidates $sourceCandidates
    if (-not $sourceBin) {
        throw "Core binaries not found. Build once in Debug or place cores under Release\win-x64\bin."
    }

    New-Item -ItemType Directory -Force $dstXrayDir, $dstSingDir | Out-Null

    if ($needXray) {
        $srcXrayDir = Join-Path $sourceBin "xray"
        if (-not (Copy-DirectoryContent -SourceDir $srcXrayDir -TargetDir $dstXrayDir)) {
            $srcXray = (Get-ChildItem -Path $sourceBin -Recurse -Filter "xray.exe" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
            if (-not $srcXray) {
                throw "xray.exe not found in: $sourceBin"
            }
            Copy-Item $srcXray $dstXray -Force

            $srcGeoip = (Get-ChildItem -Path $sourceBin -Recurse -Filter "geoip.dat" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
            if ($srcGeoip) {
                Copy-Item $srcGeoip $dstXrayGeoip -Force
                Copy-Item $srcGeoip $dstRootGeoip -Force
            }
            $srcGeosite = (Get-ChildItem -Path $sourceBin -Recurse -Filter "geosite.dat" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
            if ($srcGeosite) {
                Copy-Item $srcGeosite $dstXrayGeosite -Force
                Copy-Item $srcGeosite $dstRootGeosite -Force
            }
        }
    }

    if ($needSing) {
        $srcSingDir = Join-Path $sourceBin "sing_box"
        if (-not (Copy-DirectoryContent -SourceDir $srcSingDir -TargetDir $dstSingDir)) {
            $srcSing = (Get-ChildItem -Path $sourceBin -Recurse -Filter "sing-box.exe" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
            if (-not $srcSing) {
                throw "sing-box.exe not found in: $sourceBin"
            }
            Copy-Item $srcSing $dstSing -Force
        }
    }

    foreach ($name in @("geoip.dat", "geosite.dat")) {
        $srcGeo = Join-Path $sourceBin $name
        if (Test-Path $srcGeo) {
            Copy-Item $srcGeo (Join-Path $dstXrayDir $name) -Force
            Copy-Item $srcGeo (Join-Path $dstBin $name) -Force
        }
    }

    if ((-not (Test-Path $dstRootGeoip)) -and (Test-Path $dstXrayGeoip)) {
        Copy-Item $dstXrayGeoip $dstRootGeoip -Force
    }
    if ((-not (Test-Path $dstRootGeosite)) -and (Test-Path $dstXrayGeosite)) {
        Copy-Item $dstXrayGeosite $dstRootGeosite -Force
    }

    if (-not (Test-Path $dstSingWintun)) {
        $srcWintun = Join-Path $sourceBin "xray\\wintun.dll"
        if (-not (Test-Path $srcWintun)) {
            $srcWintun = (Get-ChildItem -Path $sourceBin -Recurse -Filter "wintun.dll" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
        }
        if ($srcWintun) {
            Copy-Item $srcWintun $dstSingWintun -Force
        }
    }
}

$noRestoreRaw = [string]($env:YVPN_NO_RESTORE)
$noRestore = @("1", "true") -contains $noRestoreRaw.ToLowerInvariant()

Write-Host "Stopping running processes..."
Stop-ClientProcesses

Write-Host "Updating Windows icon..."
Update-ProjectIconFromSource -SourcePng $iconSourcePng -TargetIco $iconTargetIco

$legacyGuiConfigsBackup = Backup-LegacyGuiConfigs -PublishPath $publishDirPreferred
$publishDir = Select-PublishDir -PreferredPath $publishDirPreferred

if (-not $noRestore) {
    Write-Host "Restoring packages..."
    dotnet restore $csprojPath -r win-x64
}
else {
    Write-Host "Skipping restore because YVPN_NO_RESTORE=1"
}

Write-Host "Publishing win-x64..."
$publishArgs = @(
    $csprojPath,
    "-c", "Release",
    "-r", "win-x64",
    "-p:UseAppHost=true",
    "-p:SelfContained=true",
    "-o", $publishDir
)
if ($noRestore) {
    $publishArgs += "--no-restore"
}
dotnet publish @publishArgs

Write-Host "Checking core files..."
Copy-CoreIfMissing

Migrate-GuiConfigsToLocalAppData -BackupPath $legacyGuiConfigsBackup
Enable-LocalConfigStorage -PublishPath $publishDir
$exePath = Resolve-ExecutablePath -PublishPath $publishDir

Write-Host "Done."
Write-Host "Executable: $exePath"
Write-Host "User configs: $localGuiConfigsDir"
if ($publishDir -ne $publishDirPreferred) {
    Write-Host "Preferred output folder was locked; published to fallback: $publishDir"
}
Write-Host "Run: Start-Process `"$exePath`" -Verb RunAs"

if ($RunAsAdmin) {
    Start-Process $exePath -Verb RunAs
}

if ($legacyGuiConfigsBackup -and (Test-Path $legacyGuiConfigsBackup)) {
    Remove-Item -Path $legacyGuiConfigsBackup -Recurse -Force -ErrorAction SilentlyContinue
}
