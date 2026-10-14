param([Parameter(Mandatory=$true)][string]$ImageDirectory,[switch]$SortByX,[switch]$LineBounds)
$ErrorActionPreference='Stop'
[void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Storage.StorageFile, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Globalization.Language, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and
    $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function Await-WinRT($operation,[type]$resultType) {
    $task=$asTask.MakeGenericMethod($resultType).Invoke($null,@($operation))
    $task.Wait()
    return $task.Result
}
$engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage(
    [Windows.Globalization.Language]::new('zh-Hans-CN'))
if (-not $engine) { throw 'Windows 简体中文文字识别组件不可用' }
Get-ChildItem -LiteralPath $ImageDirectory -Filter '*.png' -File |
    Sort-Object Name | ForEach-Object {
        $file=Await-WinRT ([Windows.Storage.StorageFile]::GetFileFromPathAsync($_.FullName)) ([Windows.Storage.StorageFile])
        $stream=Await-WinRT ($file.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
        try {
            $decoder=Await-WinRT ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $bitmap=Await-WinRT ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
            try {
                $result=Await-WinRT ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
                $lines=$result.Lines
                if ($SortByX) {
                    $lines=$lines | Sort-Object { if ($_.Words.Count -gt 0) { $_.Words[0].BoundingRect.X } else { [double]::MaxValue } }
                }
                if ($LineBounds) {
                    $rows=@($lines | ForEach-Object {
                        $left=[double]::MaxValue; $top=[double]::MaxValue; $right=0; $bottom=0
                        foreach ($word in $_.Words) {
                            $rect=$word.BoundingRect
                            $left=[Math]::Min($left,$rect.X); $top=[Math]::Min($top,$rect.Y)
                            $right=[Math]::Max($right,$rect.X+$rect.Width); $bottom=[Math]::Max($bottom,$rect.Y+$rect.Height)
                        }
                        if ($_.Words.Count -gt 0) {
                            @{text=$_.Text;x=$left;y=$top;width=$right-$left;height=$bottom-$top}
                        }
                    })
                    $value=ConvertTo-Json -InputObject $rows -Compress
                } else {
                    $value=($lines | ForEach-Object {$_.Text}) -join [Environment]::NewLine
                }
                $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value))
                [Console]::Out.WriteLine($(if ($encoded.Length -eq 0) { '-' } else { $encoded }))
            } finally { $bitmap.Dispose() }
        } finally { $stream.Dispose() }
    }
