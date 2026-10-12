param(
    [Parameter(Mandatory=$true)][string]$OfficialNetRoot,
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$netstandard = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\netstandard.dll'
$cecil = Join-Path $PSScriptRoot 'vendor\Mono.Cecil.dll'
$client = Join-Path $OfficialNetRoot 'AlecaFrameClientLib.dll'
$public = Join-Path $OfficialNetRoot 'AlecaFramePublicLib.dll'
$json = Join-Path $OfficialNetRoot 'Newtonsoft.Json.dll'
$packageRoot = Split-Path -Parent $PSScriptRoot
$itemNamesRoot = Join-Path $packageRoot 'item-names'
$itemNames = Join-Path $itemNamesRoot 'item-names.json'
$itemSource = Join-Path $itemNamesRoot 'ItemNames.cs'
$relicRoot = Join-Path $packageRoot 'relic-helper'
$relicSource = Join-Path $relicRoot 'RelicHelper.cs'
$recommendationSource = Join-Path $relicRoot 'RelicRecommendation.cs'
$ocrScript = Join-Path $relicRoot 'ZhRelicOcr.ps1'
foreach ($required in @($compiler, $netstandard, $cecil, $client, $public, $json, $itemNames, $itemSource, $relicSource, $recommendationSource, $ocrScript)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "交易汉化构建缺少文件：$required" }
}
$expectedClientHash = '077E0DBF89265AE08BF5C907ABBF1232DA168B2376FD636563EFDB8027522972'
if ((Get-FileHash -LiteralPath $client -Algorithm SHA256).Hash -ne $expectedClientHash) {
    throw '官方原生插件与已验证的 2.6.94/2.6.95 版本不一致，已停止交易补丁构建。'
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Copy-Item -LiteralPath $itemNames -Destination (Join-Path $OutputRoot 'item-names.json') -Force
Copy-Item -LiteralPath $ocrScript -Destination (Join-Path $OutputRoot 'ZhRelicOcr.ps1') -Force
foreach ($dependency in @($cecil, $public, $json)) {
    Copy-Item -LiteralPath $dependency -Destination (Join-Path $OutputRoot (Split-Path -Leaf $dependency)) -Force
}
$helper = Join-Path $OutputRoot 'AlecaFrameZh.TradeHelper.dll'
$patcher = Join-Path $OutputRoot 'TradePatcher.exe'
& $compiler /nologo /target:library /optimize+ "/out:$helper" /reference:System.Drawing.dll "/reference:$netstandard" "/reference:$public" "/reference:$json" (Join-Path $PSScriptRoot 'TradeHelper.cs') $itemSource $relicSource $recommendationSource
if ($LASTEXITCODE -ne 0) { throw '中文交易解析器编译失败。' }
& $compiler /nologo /target:exe /optimize+ "/out:$patcher" /reference:System.Drawing.dll "/reference:$cecil" (Join-Path $PSScriptRoot 'TradePatcher.cs')
if ($LASTEXITCODE -ne 0) { throw '中文交易补丁构建器编译失败。' }
$patched = Join-Path $OutputRoot 'AlecaFrameClientLib.dll'
& $patcher $client $helper $patched
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $patched)) { throw '中文交易 DLL 补丁构建失败。' }
