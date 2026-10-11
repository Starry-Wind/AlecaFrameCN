param([Parameter(Mandatory=$true)][string]$WebRoot)
$ErrorActionPreference = 'Stop'
$path = Join-Path $WebRoot 'assets\js\public\stats.js'
$content = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
if ($content.Contains('let zhTradeNames = {};') -and $content.Contains('tradeItem.displayName = zhTradeNames[tradeItem.name] || item.name;')) { return }
$anchor = 'parsedData.trades.forEach(trade => {'
$display = 'tradeItem.displayName = item.name;'
if (-not $content.Contains($anchor) -or -not $content.Contains($display)) {
    throw '官方统计页面格式已变化，无法接入中文交易名称。'
}
$injection = @'
let zhTradeNames = {};
                    if (this.fromOverwolf) {
                        try {
                            const tradeIds = [...new Set(parsedData.trades.flatMap(t => [...t.tx, ...t.rx].map(i => i.name)))];
                            zhTradeNames = JSON.parse(plugin.get().GetTradeDisplayNamesZH(JSON.stringify(tradeIds)));
                        } catch (e) { console.warn("[AlecaFrame 中文交易] 名称汉化失败", e); }
                    }
                    parsedData.trades.forEach(trade => {
'@
$content = $content.Replace($anchor, $injection).Replace($display, 'tradeItem.displayName = zhTradeNames[tradeItem.name] || item.name;')
[IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
