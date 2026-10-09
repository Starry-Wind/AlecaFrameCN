function Get-AlecaFrameZhSettings {
    param([Parameter(Mandatory=$true)][string]$PackageRoot)

    $values = [ordered]@{
        appId = "afmcagbpgggkpdkokjhjkllpegnadmkignlonpjm"
        alecaFrameVersion = "2.6.94"
        overwolfVersion = "0.311.0.6"
        overwolfInstallDir = ""
        extensionsRoot = ""
        packagesRoot = ""
    }
    $configPath = Join-Path $PackageRoot "config.json"
    if (Test-Path -LiteralPath $configPath) {
        $overrides = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($name in @($values.Keys)) {
            if ($overrides.PSObject.Properties[$name]) {
                $values[$name] = [string]$overrides.$name
            }
        }
    }
    foreach ($name in @("appId", "alecaFrameVersion", "overwolfVersion")) {
        if ([string]::IsNullOrWhiteSpace($values[$name])) {
            throw "设置 $name 不能为空。"
        }
    }
    return [pscustomobject]$values
}
