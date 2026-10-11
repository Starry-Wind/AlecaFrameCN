param([switch]$NoErrorDialog)
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Settings.ps1")
$config = Get-AlecaFrameZhSettings -PackageRoot $PSScriptRoot
$appId = [string]$config.appId
$appVersion = [string]$config.alecaFrameVersion
$requiredOverwolfVersion = [string]$config.overwolfVersion
$overwolfExe = $null
$launcherExe = $null
$extensionsRoot = if ($config.extensionsRoot) { [string]$config.extensionsRoot } else {
    Join-Path $env:LOCALAPPDATA "Overwolf\Extensions\$appId"
}
$packagesRoot = if ($config.packagesRoot) { [string]$config.packagesRoot } else {
    Join-Path $env:LOCALAPPDATA "Overwolf\PackagesCache\$appId"
}
$stateRoot = Join-Path $env:LOCALAPPDATA "AlecaFrame-ZH-Patch"
$localizerSource = Join-Path $PSScriptRoot "alecaframe-zh-cn.js"
$itemNamesRoot = Join-Path $PSScriptRoot "item-names"
$itemNamesSource = Join-Path $itemNamesRoot "item-names.json"
$relicPatchRoot = Join-Path $PSScriptRoot "relic-helper"
$relicServiceSource = Join-Path $PSScriptRoot "build\AlecaFrameZhRelicServer.exe"
$relicScriptSource = Join-Path $relicPatchRoot "alecaframe-zh-relic.js"
$tradePatchRoot = Join-Path $PSScriptRoot "trade-helper"
$tradeBuildRoot = Join-Path $PSScriptRoot "build\trade"
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
$scriptTag = '<script src="assets/js/alecaframe-zh-cn.js"></script>'
$relicScriptTag = '<script src="assets/js/alecaframe-zh-relic.js"></script>'
$didModifySession = $false

function Write-LauncherLog {
    param([string]$Message)
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    $line = "$(Get-Date -Format o) $Message"
    Add-Content -LiteralPath (Join-Path $stateRoot "launcher.log") -Value $line -Encoding UTF8
}

function Update-ItemNamesForGameVersion {
    $source = Join-Path $itemNamesRoot "ItemNamesUpdater.cs"
    $output = Join-Path $PSScriptRoot "build\item-names-updater"
    $program = Join-Path $output "AlecaFrameZh.ItemNamesUpdater.exe"
    $runtimeLibrary = Join-Path $output "Newtonsoft.Json.dll"
    $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $libraryChanged = -not (Test-Path -LiteralPath $runtimeLibrary) -or
        (Get-FileHash -LiteralPath $officialJsonLibrary).Hash -ne (Get-FileHash -LiteralPath $runtimeLibrary).Hash
    if ($libraryChanged) { Copy-Item -LiteralPath $officialJsonLibrary -Destination $runtimeLibrary -Force }
    if ($libraryChanged -or -not (Test-Path -LiteralPath $program) -or
        (Get-Item -LiteralPath $program).LastWriteTimeUtc -lt (Get-Item -LiteralPath $source).LastWriteTimeUtc) {
        & $compiler /nologo /target:exe /optimize+ "/out:$program" "/reference:$runtimeLibrary" $source
        if ($LASTEXITCODE -ne 0) { throw "物品词库更新程序构建失败。" }
    }
    & $program $PSScriptRoot $gameDataRoot (Join-Path $env:LOCALAPPDATA "Warframe") | ForEach-Object {
        Write-Host $_
        Write-LauncherLog "Item-name update: $_"
    }
    if ($LASTEXITCODE -ne 0) { throw "物品词库更新程序无法读取现有词库。" }
}

function Find-OverwolfInstall {
    $candidateDirectories = New-Object System.Collections.Generic.List[string]

    if ($config.overwolfInstallDir) {
        $candidateDirectories.Add([string]$config.overwolfInstallDir)
    }

    $running = Get-Process -Name "Overwolf" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path } |
        Select-Object -First 1
    if ($running) {
        $candidateDirectories.Add((Split-Path -Parent $running.Path))
        # Overwolf.exe often runs from a numbered version directory.
        $candidateDirectories.Add((Split-Path -Parent (Split-Path -Parent $running.Path)))
    }

    $uninstallRoots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
    )
    foreach ($root in $uninstallRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue |
            ForEach-Object {
                Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
            } |
            Where-Object {
                $_.DisplayName -eq "Overwolf" -and $_.InstallLocation
            } |
            ForEach-Object {
                $candidateDirectories.Add($_.InstallLocation.TrimEnd("\"))
            }
    }

    $candidateDirectories.Add((Join-Path $env:LOCALAPPDATA "Overwolf"))
    $candidateDirectories.Add((Split-Path -Parent $PSScriptRoot))
    if (${env:ProgramFiles(x86)}) {
        $candidateDirectories.Add((Join-Path ${env:ProgramFiles(x86)} "Overwolf"))
    }
    if ($env:ProgramFiles) {
        $candidateDirectories.Add((Join-Path $env:ProgramFiles "Overwolf"))
    }

    foreach ($directory in $candidateDirectories | Select-Object -Unique) {
        $overwolf = Join-Path $directory "Overwolf.exe"
        $launcher = Join-Path $directory "OverwolfLauncher.exe"
        if ((Test-Path -LiteralPath $overwolf) -and
            (Test-Path -LiteralPath $launcher)) {
            return [pscustomobject]@{
                Overwolf = $overwolf
                Launcher = $launcher
            }
        }
    }
    throw "找不到 Overwolf 安装目录，请先安装或修复 Overwolf。"
}

function Get-LatestVersionDirectory {
    $path = Join-Path $extensionsRoot $appVersion
    if (-not (Test-Path -LiteralPath (Join-Path $path "manifest.json")) -or
        -not (Test-Path -LiteralPath (Join-Path $path "NET\AlecaFrameClientLib.dll"))) {
        $installed = Get-ChildItem -LiteralPath $extensionsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "manifest.json") } |
            Select-Object -ExpandProperty Name
        $found = if ($installed) { $installed -join ", " } else { "未安装" }
        throw "当前包支持 AlecaFrame $appVersion；本机检测到：$found。请等待适配版本。"
    }
    return Get-Item -LiteralPath $path
}

function Assert-AppPath {
    param([string]$Path)
    $resolvedRoot = [IO.Path]::GetFullPath($extensionsRoot).TrimEnd("\") + "\"
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "安全检查失败：路径不属于 AlecaFrame 扩展目录。"
    }
}

function Restore-OfficialFiles {
    param(
        [string]$Version,
        [string]$VersionPath
    )
    Assert-AppPath $VersionPath
    $opk = Join-Path $packagesRoot "$Version\app.opk"
    $backupRoot = Join-Path $stateRoot "OfficialBackup\$Version"
    $entries = @("_metadata/verified_contents.json", "manifest.json", "web/assets/js/relicOverlay/main.js", "web/assets/js/public/stats.js")
    $entries += $targetPages | ForEach-Object { "web/$_" }

    # Older releases did not edit stats.js. Migrate that one backup entry only
    # when its installed contents still have the untouched official structure.
    $statsBackup = Join-Path $backupRoot "web\assets\js\public\stats.js"
    if (-not (Test-Path -LiteralPath $opk) -and
        (Test-Path -LiteralPath (Join-Path $backupRoot "manifest.json")) -and
        -not (Test-Path -LiteralPath $statsBackup)) {
        $statsSource = Join-Path $VersionPath "web\assets\js\public\stats.js"
        $statsContent = [IO.File]::ReadAllText($statsSource, [Text.Encoding]::UTF8)
        if ($statsContent.Contains("GetTradeDisplayNamesZH") -or
            -not $statsContent.Contains("tradeItem.displayName = item.name;")) {
            throw "旧备份缺少官方统计页面，且当前页面已修改。请在 Overwolf 中重新安装 AlecaFrame。"
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $statsBackup) -Force | Out-Null
        Copy-Item -LiteralPath $statsSource -Destination $statsBackup -Force
    }

    $backupComplete = $true
    foreach ($entryName in $entries) {
        $backupFile = Join-Path $backupRoot ($entryName.Replace("/", "\"))
        if (-not (Test-Path -LiteralPath $backupFile)) {
            $backupComplete = $false
            break
        }
    }

    if (Test-Path -LiteralPath $opk) {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($opk)
        try {
            foreach ($entryName in $entries) {
                $entry = $archive.GetEntry($entryName)
                if (-not $entry) {
                    throw "官方 OPK 中缺少文件：$entryName"
                }
                $target = Join-Path $VersionPath ($entryName.Replace("/", "\"))
                $targetParent = Split-Path -Parent $target
                New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
                $input = $entry.Open()
                $output = [IO.File]::Create($target)
                try {
                    $input.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }

        foreach ($entryName in $entries) {
            $source = Join-Path $VersionPath ($entryName.Replace("/", "\"))
            $backup = Join-Path $backupRoot ($entryName.Replace("/", "\"))
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) `
                -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $backup -Force
        }
        Write-LauncherLog "Official files restored from OPK and backed up"
    }
    elseif ($backupComplete) {
        foreach ($entryName in $entries) {
            $source = Join-Path $backupRoot ($entryName.Replace("/", "\"))
            $target = Join-Path $VersionPath ($entryName.Replace("/", "\"))
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) `
                -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
        Write-LauncherLog "Official OPK unavailable; restored local official backup"
    }
    else {
        $extraLocalizer = Join-Path $VersionPath "web\assets\js\alecaframe-zh-cn.js"
        if (Test-Path -LiteralPath $extraLocalizer) {
            throw "官方 OPK 缓存和本地备份都不存在。请在 Overwolf 中重新安装 AlecaFrame 后再启动中文版。"
        }
        foreach ($entryName in $entries) {
            $source = Join-Path $VersionPath ($entryName.Replace("/", "\"))
            if (-not (Test-Path -LiteralPath $source)) {
                throw "AlecaFrame 官方文件不完整：$entryName。请重新安装 AlecaFrame。"
            }
            if ($entryName -like "web/*") {
                $content = [IO.File]::ReadAllText($source, [Text.Encoding]::UTF8)
                if ($content.Contains($scriptTag) -or $content.Contains($relicScriptTag) -or
                    $content.Contains("GetRelicWindowDataZH(sizeTestingMode,")) {
                    throw "检测到旧汉化文件，但没有可用的官方备份。请重新安装 AlecaFrame。"
                }
            }
            $backup = Join-Path $backupRoot ($entryName.Replace("/", "\"))
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) `
                -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $backup -Force
        }
        Write-LauncherLog "Official OPK unavailable; created local official backup"
    }

    $extraLocalizer = Join-Path $VersionPath "web\assets\js\alecaframe-zh-cn.js"
    if (Test-Path -LiteralPath $extraLocalizer) {
        Remove-Item -LiteralPath $extraLocalizer -Force
    }
    $extraRelicScript = Join-Path $VersionPath "web\assets\js\alecaframe-zh-relic.js"
    if (Test-Path -LiteralPath $extraRelicScript) {
        Remove-Item -LiteralPath $extraRelicScript -Force
    }
    $extraItemNames = Join-Path $VersionPath "web\assets\js\item-names.json"
    if (Test-Path -LiteralPath $extraItemNames) { Remove-Item -LiteralPath $extraItemNames -Force }
    foreach ($extraNative in @("AlecaFrameClientLib.zh.dll", "AlecaFrameZh.TradeHelper.dll", "AlecaFrameZh.RelicHelper.dll", "ZhRelicOcr.ps1", "item-names.json")) {
        $path = Join-Path $VersionPath "NET\$extraNative"
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
}

function Stop-OverwolfProcesses {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "Overwolf*" } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 4
    $remaining = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "Overwolf*" }
    if ($remaining) {
        Write-LauncherLog "Residual Overwolf processes: $(($remaining | ForEach-Object Id) -join ',')"
    }
}

function Add-ChineseFiles {
    param([string]$VersionPath, [switch]$SkipNativeFiles)
    Assert-AppPath $VersionPath
    $webRoot = Join-Path $VersionPath "web"
    foreach ($relativePage in $targetPages) {
        $target = Join-Path $webRoot $relativePage
        $content = [IO.File]::ReadAllText($target, [Text.Encoding]::UTF8)
        if (-not $content.Contains($scriptTag)) {
            if ($content -notmatch "(?i)</body>") {
                throw "页面缺少 </body>，无法注入：$relativePage"
            }
            $injection = if ($relativePage -eq "relicOverlay.html") {
                "    $relicScriptTag`r`n    $scriptTag"
            } else { "    $scriptTag" }
            $patched = [regex]::Replace(
                $content,
                "(?i)</body>",
                "$injection`r`n</body>",
                1
            )
            [IO.File]::WriteAllText($target, $patched, [Text.UTF8Encoding]::new($false))
        }
    }
    Copy-Item -LiteralPath $localizerSource `
        -Destination (Join-Path $webRoot "assets\js\alecaframe-zh-cn.js") `
        -Force
    Copy-Item -LiteralPath $itemNamesSource -Destination (Join-Path $webRoot "assets\js\item-names.json") -Force
    Copy-Item -LiteralPath $itemNamesSource -Destination (Join-Path $VersionPath "NET\item-names.json") -Force
    Copy-Item -LiteralPath $relicScriptSource `
        -Destination (Join-Path $webRoot "assets\js\alecaframe-zh-relic.js") `
        -Force
    $relicMainJs = Join-Path $webRoot "assets\js\relicOverlay\main.js"
    $mainContent = [IO.File]::ReadAllText($relicMainJs, [Text.Encoding]::UTF8)
    $oldCall = "plugin.get().GetRelicWindowData(sizeTestingMode,"
    if (-not $mainContent.Contains($oldCall) -and -not $mainContent.Contains("GetRelicWindowDataZH(sizeTestingMode,")) {
        throw "官方遗物页面格式已变化，无法接入中文识别。"
    }
    $mainContent = $mainContent.Replace($oldCall, "GetRelicWindowDataZH(sizeTestingMode,")
    [IO.File]::WriteAllText($relicMainJs, $mainContent, [Text.UTF8Encoding]::new($false))

    & (Join-Path $tradePatchRoot "Patch-TradePage.ps1") -WebRoot $webRoot
    if (-not $SkipNativeFiles) {
        Copy-Item -LiteralPath (Join-Path $tradeBuildRoot "AlecaFrameClientLib.dll") `
            -Destination (Join-Path $VersionPath "NET\AlecaFrameClientLib.zh.dll") -Force
        Copy-Item -LiteralPath (Join-Path $tradeBuildRoot "AlecaFrameZh.TradeHelper.dll") `
            -Destination (Join-Path $VersionPath "NET\AlecaFrameZh.TradeHelper.dll") -Force
    }
    $manifestPath = Join-Path $VersionPath "manifest.json"
    $manifestText = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8)
    $manifest = $manifestText | ConvertFrom-Json
    if ($manifest.data.'extra-objects'.AlecaFrameWrapper.file -ne "NET/AlecaFrameClientLib.dll" -and
        $manifest.data.'extra-objects'.AlecaFrameWrapper.file -ne "NET/AlecaFrameClientLib.zh.dll") {
        throw "官方原生插件入口已变化，无法接入中文交易。"
    }
    # Keep the official DLL untouched and load the patched copy on renderer reload.
    $manifestText = $manifestText.Replace('"NET/AlecaFrameClientLib.dll"', '"NET/AlecaFrameClientLib.zh.dll"')
    [IO.File]::WriteAllText($manifestPath, $manifestText, [Text.UTF8Encoding]::new($false))
}

function Start-RelicService {
    Get-Process -Name "AlecaFrameZhRelicServer" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    & (Join-Path $PSScriptRoot "Build.ps1")
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $relicServiceSource)) {
        throw "中文遗物识别服务构建失败。"
    }
    Start-Process -FilePath $relicServiceSource -ArgumentList "--serve" -WindowStyle Hidden | Out-Null
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        Start-Sleep -Milliseconds 250
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:37619/health" -TimeoutSec 2
            if ($health.ok) { return }
        } catch { }
    }
    throw "中文遗物识别服务未能启动。"
}

function Get-AlecaFrameRenderer {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -eq "OverwolfBrowser.exe" -and
            $_.CommandLine -match [regex]::Escape("--uid=$appId")
        } |
        Select-Object -First 1
}

function Wait-OverwolfExtensionReady {
    param([datetime]$StartedAt)

    $traceRoot = Join-Path $env:LOCALAPPDATA "Overwolf\Log"
    for ($attempt = 0; $attempt -lt 600; $attempt++) {
        Start-Sleep -Milliseconds 500
        $trace = Get-ChildItem -LiteralPath $traceRoot -Filter "Trace_*.log" -File `
            -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $StartedAt.AddSeconds(-2) } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($trace) {
            $tail = Get-Content -LiteralPath $trace.FullName -Tail 500 `
                -ErrorAction SilentlyContinue
            if ($tail -match "Loading extension '$appId") {
                Start-Sleep -Seconds 2
                return
            }
        }
    }
    throw "Overwolf 启动超时，未能加载 AlecaFrame 扩展。"
}

function Wait-OverwolfPatchedSessionReady {
    param([datetime]$StartedAt)

    $traceRoot = Join-Path $env:LOCALAPPDATA "Overwolf\Log"
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        Start-Sleep -Milliseconds 500
        $traces = Get-ChildItem -LiteralPath $traceRoot -Filter "Trace_*.log" -File `
            -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $StartedAt.AddSeconds(-2) } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 5
        foreach ($trace in $traces) {
            $head = Get-Content -LiteralPath $trace.FullName -TotalCount 100 `
                -ErrorAction SilentlyContinue
            if (($head -match "Override feature 'extension-validation' to 'False'") -and
                ($head -match "Override feature 'force-validation-report-only' to 'True'") -and
                ($head -match "Override feature 'read-opk-from-memory' to 'False'") -and
                ($head -match "Loading per-load extensions")) {
                Write-LauncherLog "Overwolf feature overrides confirmed"
                return
            }
        }
    }
    throw "Overwolf 未确认汉化启动参数，已停止修改。"
}

function Start-AlecaFrameAndWait {
    param([int]$TimeoutSeconds = 60)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $nextLaunch = Get-Date
    do {
        if ((Get-Date) -ge $nextLaunch) {
            Start-Process -FilePath $launcherExe `
                -ArgumentList "-launchapp", $appId, "-from-desktop"
            $nextLaunch = (Get-Date).AddSeconds(30)
        }
        Start-Sleep -Milliseconds 500
        $renderer = Get-AlecaFrameRenderer
        if ($renderer) {
            return $renderer
        }
    } while ((Get-Date) -lt $deadline)

    throw "AlecaFrame 启动超时。"
}

try {
    $overwolfInstall = Find-OverwolfInstall
    $overwolfExe = $overwolfInstall.Overwolf
    $launcherExe = $overwolfInstall.Launcher
    if (-not (Test-Path -LiteralPath $overwolfExe)) {
        throw "找不到 Overwolf：$overwolfExe"
    }
    if (-not (Test-Path -LiteralPath $launcherExe)) {
        throw "找不到 OverwolfLauncher：$launcherExe"
    }
    if (-not (Test-Path -LiteralPath $localizerSource)) {
        throw "缺少汉化词库：$localizerSource"
    }
    foreach ($required in @($relicScriptSource, $itemNamesSource, (Join-Path $itemNamesRoot "ItemNames.cs"), (Join-Path $itemNamesRoot "ItemNamesUpdater.cs"), (Join-Path $PSScriptRoot "Build.ps1"),
        (Join-Path $PSScriptRoot "Settings.ps1"),
        (Join-Path $tradePatchRoot "Build-TradePatch.ps1"),
        (Join-Path $tradePatchRoot "Patch-TradePage.ps1"),
        (Join-Path $tradePatchRoot "TradeHelper.cs"),
        (Join-Path $tradePatchRoot "TradePatcher.cs"),
        (Join-Path $tradePatchRoot "vendor\Mono.Cecil.dll"))) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "缺少中文遗物识别文件：$required"
        }
    }

    $versionDirectory = Get-LatestVersionDirectory
    $overwolfVersion = (Get-Item -LiteralPath $overwolfExe).VersionInfo.FileVersion
    if ($overwolfVersion -ne $requiredOverwolfVersion -or $versionDirectory.Name -ne $appVersion) {
        throw "当前包仅验证 Overwolf $requiredOverwolfVersion 和 AlecaFrame $appVersion；本机为 Overwolf $overwolfVersion、AlecaFrame $($versionDirectory.Name)。"
    }
    [void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
    [void][Windows.Globalization.Language, Windows.Foundation, ContentType=WindowsRuntime]
    $ocrLanguage = [Windows.Globalization.Language]::new("zh-Hans-CN")
    if (-not [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($ocrLanguage)) {
        throw "Windows 简体中文文字识别组件不可用。请先在 Windows 语言设置中安装简体中文文字识别。"
    }
    $gameDataRoot = Join-Path $env:LOCALAPPDATA "AlecaFrame\cachedData\json"
    foreach ($dataFile in @("lang.json", "Components.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $gameDataRoot $dataFile))) {
            throw "AlecaFrame 的游戏物品数据尚未准备好。请先打开官方 AlecaFrame，等待游戏数据加载完成后再启动中文版。"
        }
    }
    $officialJsonLibrary = Join-Path $versionDirectory.FullName "NET\Newtonsoft.Json.dll"
    if (-not (Test-Path -LiteralPath $officialJsonLibrary)) {
        throw "AlecaFrame 官方文件不完整：NET\Newtonsoft.Json.dll。请在 Overwolf 中重新安装 AlecaFrame。"
    }
    Write-LauncherLog "Starting AlecaFrame ZH for $($versionDirectory.Name)"

    try {
        Update-ItemNamesForGameVersion
    } catch {
        Write-LauncherLog "Item-name update unavailable; keeping bundled dictionary: $($_.Exception.Message)"
    }

    $didModifySession = $true
    Stop-OverwolfProcesses
    Restore-OfficialFiles -Version $versionDirectory.Name -VersionPath $versionDirectory.FullName
    & (Join-Path $tradePatchRoot "Build-TradePatch.ps1") `
        -OfficialNetRoot (Join-Path $versionDirectory.FullName "NET") -OutputRoot $tradeBuildRoot
    Start-RelicService

    $overwolfStartedAt = Get-Date
    Start-Process -FilePath $overwolfExe `
        -ArgumentList "--ow-enable-features=force-validation-report-only", "--ow-disable-features=extension-validation,read-opk-from-memory"
    Wait-OverwolfPatchedSessionReady -StartedAt $overwolfStartedAt

    # First launch uses untouched official files so Overwolf can complete its normal
    # integrity check and initialize AlecaFrame.
    $originalRenderer = Start-AlecaFrameAndWait -TimeoutSeconds 240
    Wait-OverwolfExtensionReady -StartedAt $overwolfStartedAt
    Start-Sleep -Seconds 4

    if (-not (Test-Path -LiteralPath (Join-Path $versionDirectory.FullName "NET\AlecaFrameClientLib.dll"))) {
        throw "AlecaFrame 在启动时自动更新了版本，请重新运行中文版启动器以检测新版。"
    }
    Add-ChineseFiles -VersionPath $versionDirectory.FullName

    # Overwolf caches the extra-object DLL path in its main process. Restart it
    # after changing the manifest so the patched native plugin is really loaded.
    Stop-OverwolfProcesses
    $patchedStartedAt = Get-Date
    Start-Process -FilePath $overwolfExe `
        -ArgumentList "--ow-enable-features=force-validation-report-only", "--ow-disable-features=extension-validation,read-opk-from-memory"
    Wait-OverwolfPatchedSessionReady -StartedAt $patchedStartedAt
    $null = Start-AlecaFrameAndWait -TimeoutSeconds 120
    Wait-OverwolfExtensionReady -StartedAt $patchedStartedAt
    Start-Sleep -Seconds 4

    # Recovery can restore official web assets after caching the new native
    # entry. Reapply the page patch after recovery and keep the loaded DLLs.
    Add-ChineseFiles -VersionPath $versionDirectory.FullName -SkipNativeFiles
    $patchTime = Get-Date
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq "OverwolfBrowser.exe" -and $_.CommandLine -match [regex]::Escape("--uid=$appId") } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 3
    $null = Start-AlecaFrameAndWait -TimeoutSeconds 120

    $loaded = $false
    $mainLog = Join-Path $env:LOCALAPPDATA "Overwolf\Log\Apps\AlecaFrame\MainWindow.html.log"
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        Start-Sleep -Milliseconds 500
        if (Test-Path -LiteralPath $mainLog) {
            $logItem = Get-Item -LiteralPath $mainLog
            if ($logItem.LastWriteTime -ge $patchTime.AddSeconds(-2)) {
                $tail = Get-Content -LiteralPath $mainLog -Tail 40 -ErrorAction SilentlyContinue
                if (($tail -match "\[AlecaFrame 中文补丁\] 已加载") -and
                    ($tail -match "\[AlecaFrame 中文交易\] 原生解析器已加载") -and
                    ($tail -match "\[AlecaFrame 中文物品\] 已加载")) {
                    $loaded = $true
                    break
                }
            }
        }
    }
    if (-not $loaded) {
        throw "中文界面或交易解析器未能加载，请查看 $stateRoot\launcher.log 和 MainWindow.html.log"
    }

    # A renderer launch can finish a delayed OPK recovery after the earlier
    # reapplication. The running native entry is now confirmed, so keep that
    # instance and repair recovered files without replacing its loaded DLLs.
    $stableChecks = 0
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        $currentManifest = Get-Content -LiteralPath (Join-Path $versionDirectory.FullName "manifest.json") -Raw -Encoding UTF8 | ConvertFrom-Json
        $statsPatched = [IO.File]::ReadAllText((Join-Path $versionDirectory.FullName "web\assets\js\public\stats.js")).Contains("GetTradeDisplayNamesZH")
        if ($currentManifest.data.'extra-objects'.AlecaFrameWrapper.file -ne "NET/AlecaFrameClientLib.zh.dll" -or -not $statsPatched) {
            Add-ChineseFiles -VersionPath $versionDirectory.FullName -SkipNativeFiles
            Write-LauncherLog "Reapplied Chinese files after delayed Overwolf recovery"
            $stableChecks = 0
        } else {
            $stableChecks++
            if ($stableChecks -ge 3) { break }
        }
        Start-Sleep -Seconds 1
    }
    if ($stableChecks -lt 3) {
        throw "Overwolf 持续还原中文插件文件，中文补丁未能稳定加载。"
    }

    $installedRelicJs = Join-Path $versionDirectory.FullName "web\assets\js\relicOverlay\main.js"
    if (-not ([IO.File]::ReadAllText($installedRelicJs)).Contains("GetRelicWindowDataZH(sizeTestingMode,")) {
        throw "中文遗物页面补丁未能保留。"
    }

    $installedManifest = Get-Content -LiteralPath (Join-Path $versionDirectory.FullName "manifest.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($installedManifest.data.'extra-objects'.AlecaFrameWrapper.file -ne "NET/AlecaFrameClientLib.zh.dll") {
        throw "中文交易插件入口未能保留。"
    }

    Write-LauncherLog "Chinese UI loaded; relic fallback and Chinese trade parser ready for in-game validation"
}
catch {
    Write-LauncherLog "ERROR: $($_.Exception.Message)"
    if ($didModifySession) {
        try {
            Stop-OverwolfProcesses
            if ($versionDirectory -and (Test-Path -LiteralPath (Join-Path $versionDirectory.FullName "NET\AlecaFrameClientLib.dll"))) {
                Restore-OfficialFiles -Version $versionDirectory.Name -VersionPath $versionDirectory.FullName
            }
        }
        catch {
            Write-LauncherLog "RESTORE ERROR: $($_.Exception.Message)"
        }
    }
    if ($NoErrorDialog) {
        Write-Host $_.Exception.Message -ForegroundColor Red
    } else {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show(
            $_.Exception.Message,
            "AlecaFrame 中文启动失败",
            [Windows.Forms.MessageBoxButtons]::OK,
            [Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
    }
    exit 1
}
