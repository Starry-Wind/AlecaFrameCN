function Get-AlecaFrameZhSettings {
    param([Parameter(Mandatory=$true)][string]$PackageRoot)

    $values = [ordered]@{
        appId = "afmcagbpgggkpdkokjhjkllpegnadmkignlonpjm"
        alecaFrameVersion = "2.6.95"
        overwolfVersion = "0.311.0.6"
        overwolfInstallDir = ""
        extensionsRoot = ""
        packagesRoot = ""
    }
    $configPath = Join-Path $PackageRoot "config.json"
    $versionExplicit = $false
    if (Test-Path -LiteralPath $configPath) {
        $overrides = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $versionExplicit = [bool]$overrides.PSObject.Properties['alecaFrameVersion']
        foreach ($name in @($values.Keys)) {
            if ($overrides.PSObject.Properties[$name]) {
                $values[$name] = [string]$overrides.$name
            }
        }
    }
    if (-not $versionExplicit) {
        $installRoot = if ($values.extensionsRoot) { $values.extensionsRoot } else {
            Join-Path $env:LOCALAPPDATA "Overwolf\Extensions\$($values.appId)"
        }
        foreach ($candidate in @('2.6.95', '2.6.94')) {
            $versionRoot = Join-Path $installRoot $candidate
            if ((Test-Path -LiteralPath (Join-Path $versionRoot 'manifest.json')) -and
                (Test-Path -LiteralPath (Join-Path $versionRoot 'NET\AlecaFrameClientLib.dll'))) {
                $values.alecaFrameVersion = $candidate
                break
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
