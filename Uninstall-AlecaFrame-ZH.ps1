$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Settings.ps1")
$config = Get-AlecaFrameZhSettings -PackageRoot $PSScriptRoot
$appId = [string]$config.appId
$appVersion = [string]$config.alecaFrameVersion
$extensionsRoot = if ($config.extensionsRoot) { [string]$config.extensionsRoot } else {
    Join-Path $env:LOCALAPPDATA "Overwolf\Extensions\$appId"
}
$packagesRoot = if ($config.packagesRoot) { [string]$config.packagesRoot } else {
    Join-Path $env:LOCALAPPDATA "Overwolf\PackagesCache\$appId"
}
$stateRoot = Join-Path $env:LOCALAPPDATA "AlecaFrame-ZH-Patch"
$relicService = Join-Path $PSScriptRoot "build\AlecaFrameZhRelicServer.exe"
$targetPages = @(
    "main.html",
    "AFBuilds.html",
    "InGameNotification.html",
    "relicOverlay.html",
    "relicRecommendation.html",
    "rivenOverlay.html",
    "SquadMakingMain.html",
    "SquadMakingSquad.html",
    "tradeFinishedNotification.html"
)

Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -like "Overwolf*" } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "AlecaFrameZhRelicServer" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $relicService } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4

$versionPath = Join-Path $extensionsRoot $appVersion
$versionDirectory = if (Test-Path -LiteralPath (Join-Path $versionPath "manifest.json")) {
    Get-Item -LiteralPath $versionPath
} else { $null }
if ($versionDirectory) {
    $expectedRoot = [IO.Path]::GetFullPath($extensionsRoot).TrimEnd("\") + "\"
    $resolved = [IO.Path]::GetFullPath($versionDirectory.FullName)
    if (-not $resolved.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "安全检查失败：目标目录不属于 AlecaFrame。"
    }

    $opk = Join-Path $packagesRoot "$($versionDirectory.Name)\app.opk"
    $backupRoot = Join-Path $stateRoot "OfficialBackup\$($versionDirectory.Name)"
    $entries = @("_metadata/verified_contents.json", "manifest.json", "web/assets/js/relicOverlay/main.js")
    $entries += $targetPages | ForEach-Object { "web/$_" }
    $backupComplete = $true
    foreach ($entryName in $entries) {
        $backup = Join-Path $backupRoot ($entryName.Replace("/", "\"))
        if (-not (Test-Path -LiteralPath $backup)) {
            $backupComplete = $false
            break
        }
    }

    if ($backupComplete) {
        foreach ($entryName in $entries) {
            $source = Join-Path $backupRoot ($entryName.Replace("/", "\"))
            $target = Join-Path $versionDirectory.FullName ($entryName.Replace("/", "\"))
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) `
                -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
    }
    elseif (Test-Path -LiteralPath $opk) {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($opk)
        try {
            foreach ($entryName in $entries) {
                $entry = $archive.GetEntry($entryName)
                if (-not $entry) { continue }
                $target = Join-Path $versionDirectory.FullName ($entryName.Replace("/", "\"))
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) `
                    -Force | Out-Null
                $input = $entry.Open()
                $output = [IO.File]::Create($target)
                try { $input.CopyTo($output) }
                finally {
                    $output.Dispose()
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    else {
        throw "没有官方 OPK 或本地备份，无法安全还原。请在 Overwolf 中重新安装 AlecaFrame。"
    }

    $extra = Join-Path $versionDirectory.FullName "web\assets\js\alecaframe-zh-cn.js"
    if (Test-Path -LiteralPath $extra) {
        Remove-Item -LiteralPath $extra -Force
    }
    $extraRelic = Join-Path $versionDirectory.FullName "web\assets\js\alecaframe-zh-relic.js"
    if (Test-Path -LiteralPath $extraRelic) {
        Remove-Item -LiteralPath $extraRelic -Force
    }
    foreach ($extraNative in @("AlecaFrameClientLib.zh.dll", "AlecaFrameZh.RelicHelper.dll", "ZhRelicOcr.ps1")) {
        $path = Join-Path $versionDirectory.FullName "NET\$extraNative"
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
}

Write-Host ""
Write-Host "AlecaFrame 官方文件已恢复。" -ForegroundColor Green
