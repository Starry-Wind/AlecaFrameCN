$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Settings.ps1")
$config = Get-AlecaFrameZhSettings -PackageRoot $PSScriptRoot
$appId = [string]$config.appId
$version = [string]$config.alecaFrameVersion
$extensionsRoot = if ($config.extensionsRoot) { [string]$config.extensionsRoot } else {
    Join-Path $env:LOCALAPPDATA "Overwolf\Extensions\$appId"
}
$newtonsoft = Join-Path (Join-Path $extensionsRoot $version) "NET\Newtonsoft.Json.dll"
if (-not (Test-Path -LiteralPath $newtonsoft)) {
    throw "找不到 AlecaFrame 官方安装中的 Newtonsoft.Json.dll：$newtonsoft"
}
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $compiler)) { throw "找不到 .NET Framework C# 编译器：$compiler" }

$output = Join-Path $PSScriptRoot "build"
$sources = Join-Path $PSScriptRoot "relic-helper"
$itemNamesRoot = Join-Path $PSScriptRoot "item-names"
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath $newtonsoft -Destination (Join-Path $output "Newtonsoft.Json.dll") -Force
Copy-Item -LiteralPath (Join-Path $sources "ZhRelicOcr.ps1") -Destination (Join-Path $output "ZhRelicOcr.ps1") -Force
Copy-Item -LiteralPath (Join-Path $itemNamesRoot "item-names.json") -Destination (Join-Path $output "item-names.json") -Force
$jsonReference = "/reference:$(Join-Path $output 'Newtonsoft.Json.dll')"
$helper = Join-Path $output "AlecaFrameZh.RelicHelper.dll"
$server = Join-Path $output "AlecaFrameZhRelicServer.exe"
& $compiler /nologo /target:library /optimize+ "/out:$helper" `
    /reference:System.Drawing.dll $jsonReference (Join-Path $sources "RelicHelper.cs") (Join-Path $itemNamesRoot "ItemNames.cs")
if ($LASTEXITCODE -ne 0) { throw "RelicHelper 编译失败。" }
& $compiler /nologo /target:exe /optimize+ "/out:$server" `
    /reference:System.Drawing.dll /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll $jsonReference "/reference:$helper" `
    (Join-Path $sources "RelicServer.cs")
if ($LASTEXITCODE -ne 0) { throw "RelicServer 编译失败。" }
Write-Host "中文遗物识别服务构建完成：$server" -ForegroundColor Green
