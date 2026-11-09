#requires -Version 7.0
param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/brand'),
    [switch]$UpdateAssets
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$brandRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$brandOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $brandOutput.StartsWith($brandRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Brand outputs must stay inside this project.'
}
[IO.Directory]::CreateDirectory($brandOutput) | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$markPath = 'M 80 44 L 80 28 C 80 21.37 74.63 16 68 16 L 28 16 C 21.37 16 16 21.37 16 28 L 16 72 C 16 78.63 21.37 84 28 84 L 44 84'
function Write-Utf8([string]$name, [string]$value) {
    [IO.File]::WriteAllText((Join-Path $brandOutput $name), ([regex]::Replace($value, '(?m)[ \t]+$', '')), $utf8)
}

function Brush([string]$value) {
    $result = New-Object Windows.Media.SolidColorBrush([Windows.Media.ColorConverter]::ConvertFromString($value))
    $result.Freeze()
    return $result
}

function Rect([double]$x, [double]$y, [double]$width, [double]$height) {
    return New-Object Windows.Rect($x, $y, $width, $height)
}

function Text($drawing, [string]$value, [double]$x, [double]$y, [double]$size, [string]$color, [string]$family = 'Microsoft YaHei UI', [bool]$bold = $false) {
    $weight = [Windows.FontWeights]::Normal
    if ($bold) { $weight = [Windows.FontWeights]::SemiBold }
    $typeface = New-Object Windows.Media.Typeface((New-Object Windows.Media.FontFamily($family)), [Windows.FontStyles]::Normal, $weight, [Windows.FontStretches]::Normal)
    $formatted = New-Object Windows.Media.FormattedText($value, [Globalization.CultureInfo]::GetCultureInfo('zh-CN'), [Windows.FlowDirection]::LeftToRight, $typeface, $size, (Brush $color), 1.0)
    $drawing.DrawText($formatted, (New-Object Windows.Point($x, $y)))
}

function Png([string]$name, [int]$width, [int]$height, [scriptblock]$paint) {
    $visual = New-Object Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    try { & $paint $drawing } finally { $drawing.Close() }
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($width, $height, 96.0, 96.0, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $brandOutput $name))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

function Ico([string]$name, [string]$prefix, [int[]]$sizes) {
    $images = @($sizes | ForEach-Object { ,([IO.File]::ReadAllBytes((Join-Path $brandOutput ($prefix + '-' + $_ + '.png')))) })
    $stream = [IO.File]::Create((Join-Path $brandOutput $name))
    $writer = New-Object IO.BinaryWriter($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = $sizes[$index]; if ($dimension -eq 256) { $dimension = 0 }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
            $offset += $images[$index].Length
        }
        foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
    } finally { $writer.Dispose(); $stream.Dispose() }
}

function Draw-Mark($drawing, [double]$x, [double]$y, [double]$size, [string]$frameColor, [string]$tileColor) {
    $drawing.PushTransform((New-Object Windows.Media.TranslateTransform($x, $y)))
    $drawing.PushTransform((New-Object Windows.Media.ScaleTransform(($size / 100), ($size / 100))))
    $pen = New-Object Windows.Media.Pen((Brush $frameColor), 12)
    $pen.StartLineCap = [Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
    $drawing.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse($markPath))
    $drawing.DrawRoundedRectangle((Brush $tileColor), $null, (Rect 54 58 28 28), 8, 8)
    $drawing.Pop(); $drawing.Pop()
}
function Draw-App($drawing, [double]$x, [double]$y, [double]$size, [bool]$dark = $false) {
    $drawing.PushTransform((New-Object Windows.Media.TranslateTransform($x, $y)))
    $drawing.PushTransform((New-Object Windows.Media.ScaleTransform(($size / 128), ($size / 128))))
    if ($dark) {
        $drawing.DrawRoundedRectangle((Brush '#0F1D38'), $null, (Rect 0 0 128 128), 28, 28)
        Draw-Mark $drawing 14 14 100 '#FFFFFF' '#38BDF8'
    } else {
        $drawing.DrawRoundedRectangle((Brush '#FFFFFF'), $null, (Rect 0 0 128 128), 28, 28)
        $drawing.DrawRoundedRectangle($null, (New-Object Windows.Media.Pen((Brush '#EBEDF0'), 1)), (Rect 0.5 0.5 127 127), 27.5, 27.5)
        Draw-Mark $drawing 14 14 100 '#0F1D38' '#2563EB'
    }
    $drawing.Pop(); $drawing.Pop()
}
function Draw-Tray($drawing, [int]$size, [string]$frameColor, [string]$tileColor) {
    $drawing.PushTransform((New-Object Windows.Media.ScaleTransform(($size / 16), ($size / 16))))
    $pen = New-Object Windows.Media.Pen((Brush $frameColor), 1.65)
    $pen.StartLineCap = [Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
    $drawing.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse('M 12.5 6.5 L 12.5 4.5 Q 12.5 2.5 10.5 2.5 L 4.5 2.5 Q 2.5 2.5 2.5 4.5 L 2.5 10.5 Q 2.5 12.5 4.5 12.5 L 6.5 12.5'))
    $drawing.DrawRoundedRectangle((Brush $tileColor), $null, (Rect 9 9 4.5 4.5), 1, 1)
    $drawing.Pop()
}
$references = Join-Path $brandRoot 'assets/brand'
foreach ($name in @('qige-app.svg','qige-app-dark.svg','qige-mark-dark.svg','qige-mark-light.svg')) {
    Write-Utf8 $name ([IO.File]::ReadAllText((Join-Path $references $name)))
}
Write-Utf8 'qige-tray.svg' @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none">
  <title>栖格 Qige 托盘图标</title>
  <path d="M12.5 6.5V4.5Q12.5 2.5 10.5 2.5H4.5Q2.5 2.5 2.5 4.5V10.5Q2.5 12.5 4.5 12.5H6.5" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round"/>
  <rect x="9" y="9" width="4.5" height="4.5" rx="1" fill="#2563EB"/>
</svg>
"@
Write-Utf8 'qige-wordmark.svg' @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 200" fill="none">
  <title>栖格 Qige 组合标志</title>
  <g transform="translate(20 20) scale(1.6)"><path d="$markPath" stroke="#0F1D38" stroke-width="12" stroke-linecap="round" stroke-linejoin="round"/><rect x="54" y="58" width="28" height="28" rx="8" fill="#2563EB"/></g>
  <text x="220" y="130" fill="#0F1D38" font-family="Microsoft YaHei UI,sans-serif" font-size="96" font-weight="600">栖格</text>
  <text x="470" y="130" fill="#64748B" font-family="Segoe UI,sans-serif" font-size="62" font-weight="600">Qige</text>
</svg>
"@
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024)) {
    Png ('qige-app-' + $size + '.png') $size $size { param($d) Draw-App $d 0 0 $size }
}
Png 'qige-app-light-512.png' 512 512 { param($d) Draw-App $d 0 0 512 }
Png 'qige-app-dark-512.png' 512 512 { param($d) Draw-App $d 0 0 512 $true }
Png 'qige-mark-light-512.png' 512 512 { param($d) Draw-Mark $d 0 0 512 '#FFFFFF' '#38BDF8' }
Png 'qige-mark-dark-512.png' 512 512 { param($d) Draw-Mark $d 0 0 512 '#0F1D38' '#2563EB' }
Png 'qige-wordmark.png' 800 200 {
    param($d)
    Draw-Mark $d 20 20 160 '#0F1D38' '#2563EB'
    Text $d '栖格' 220 25 96 '#0F1D38' 'Microsoft YaHei UI' $true
    Text $d 'Qige' 470 57 62 '#64748B' 'Segoe UI' $true
}
foreach ($size in @(16, 20, 24, 32, 40, 48, 64)) {
    Png ('qige-tray-light-' + $size + '.png') $size $size { param($d) Draw-Tray $d $size '#FFFFFF' '#38BDF8' }
    Png ('qige-tray-dark-' + $size + '.png') $size $size { param($d) Draw-Tray $d $size '#0F1D38' '#2563EB' }
}
Ico 'qige.ico' 'qige-app' @(16, 20, 24, 32, 40, 48, 64, 128, 256)
Ico 'qige-tray-light.ico' 'qige-tray-light' @(16, 20, 24, 32, 40, 48, 64)
Ico 'qige-tray-dark.ico' 'qige-tray-dark' @(16, 20, 24, 32, 40, 48, 64)
Png 'qige-brand-preview.png' 1600 900 {
    param($d)
    $d.DrawRectangle((Brush '#F8FAFC'), $null, (Rect 0 0 1600 900))
    Text $d 'QIGE / DESKTOP ORGANIZER' 72 45 17 '#64748B' 'Segoe UI' $true
    Text $d '栖格 Qige' 435 150 76 '#0F1D38' 'Microsoft YaHei UI' $true
    Text $d '给桌面留点空。' 442 265 32 '#64748B'
    Text $d '深藏青与钴蓝 · 对应管理面板的原型设计' 442 326 19 '#64748B'
    Draw-App $d 72 126 285
    $d.DrawLine((New-Object Windows.Media.Pen((Brush '#E2E8F0'), 1)), (New-Object Windows.Point(72, 463)), (New-Object Windows.Point(1528, 463)))
    Text $d '应用图标' 72 506 23 '#0F1D38' 'Microsoft YaHei UI' $true
    Draw-App $d 72 565 150
    Draw-App $d 252 565 150 $true
    Text $d '浅色 / 深色底板' 72 757 16 '#64748B'
    Text $d '托盘 · 浅色任务栏' 550 506 23 '#0F1D38' 'Microsoft YaHei UI' $true
    $d.DrawRoundedRectangle((Brush '#FFFFFF'), (New-Object Windows.Media.Pen((Brush '#E2E8F0'), 1)), (Rect 550 573 365 130), 16, 16)
    foreach ($tray in @(@(580, 620, 16), @(630, 616, 24), @(690, 612, 32), @(765, 604, 48))) {
        $d.PushTransform((New-Object Windows.Media.TranslateTransform($tray[0], $tray[1])))
        Draw-Tray $d $tray[2] '#0F1D38' '#2563EB'; $d.Pop()
    }
    Text $d '16 / 24 / 32 / 48 px' 550 757 16 '#64748B' 'Segoe UI'
    Text $d '托盘 · 深色任务栏' 1040 506 23 '#0F1D38' 'Microsoft YaHei UI' $true
    $d.DrawRoundedRectangle((Brush '#0F1D38'), $null, (Rect 1040 573 365 130), 16, 16)
    foreach ($tray in @(@(1070, 620, 16), @(1120, 616, 24), @(1180, 612, 32), @(1255, 604, 48))) {
        $d.PushTransform((New-Object Windows.Media.TranslateTransform($tray[0], $tray[1])))
        Draw-Tray $d $tray[2] '#FFFFFF' '#38BDF8'; $d.Pop()
    }
    Text $d '按 Windows 任务栏明暗自动选择' 1040 757 16 '#64748B'
}
Write-Output ('Generated Qige SVG, PNG, ICO and preview in ' + $brandOutput)
if ($UpdateAssets) {
    $canonical = Join-Path $brandRoot 'assets/brand'
    foreach ($name in @('qige-app.svg','qige-app-dark.svg','qige-mark-light.svg','qige-mark-dark.svg','qige-tray.svg','qige-wordmark.svg','qige.ico','qige-tray-light.ico','qige-tray-dark.ico')) {
        Copy-Item -LiteralPath (Join-Path $brandOutput $name) -Destination $canonical -Force
    }
}
