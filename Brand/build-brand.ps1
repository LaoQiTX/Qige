param([string]$OutputDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$brandRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$brandOutput = [IO.Path]::GetFullPath($OutputDirectory)
if ($brandOutput -ne $brandRoot -and -not $brandOutput.StartsWith($brandRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Brand outputs must stay inside the project Brand directory.'
}
[IO.Directory]::CreateDirectory($brandOutput) | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$markPath = 'M 360 230 L 360 176 C 360 153.9086 342.0914 136 320 136 L 176 136 C 153.9086 136 136 153.9086 136 176 L 136 320 C 136 342.0914 153.9086 360 176 360 L 230 360'

function Write-Utf8([string]$name, [string]$value) {
    [IO.File]::WriteAllText((Join-Path $brandOutput $name), $value, $utf8)
}
function Brush([string]$value) {
    $result = New-Object Windows.Media.SolidColorBrush([Windows.Media.ColorConverter]::ConvertFromString($value))
    $result.Freeze()
    return $result
}
function Rect([double]$x, [double]$y, [double]$width, [double]$height) {
    return New-Object Windows.Rect($x, $y, $width, $height)
}
function Draw-Mark($drawing, [double]$x, [double]$y, [double]$size, [string]$frameColor, [string]$tileColor) {
    $drawing.PushTransform((New-Object Windows.Media.TranslateTransform($x, $y)))
    $drawing.PushTransform((New-Object Windows.Media.ScaleTransform(($size / 512), ($size / 512))))
    $pen = New-Object Windows.Media.Pen((Brush $frameColor), 32)
    $pen.StartLineCap = [Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
    $drawing.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse($markPath))
    $drawing.DrawRoundedRectangle((Brush $tileColor), $null, (Rect 282 282 96 96), 21, 21)
    $drawing.Pop(); $drawing.Pop()
}
function Draw-App($drawing, [double]$x, [double]$y, [double]$size, [bool]$light = $false) {
    $drawing.PushTransform((New-Object Windows.Media.TranslateTransform($x, $y)))
    $drawing.PushTransform((New-Object Windows.Media.ScaleTransform(($size / 512), ($size / 512))))
    if ($light) {
        $drawing.DrawRoundedRectangle((Brush '#E7EEE7'), $null, (Rect 20 20 472 472), 108, 108)
        Draw-Mark $drawing 0 0 512 '#3F6656' '#284C3C'
    } else {
        $drawing.DrawRoundedRectangle((Brush '#1D2D26'), $null, (Rect 20 20 472 472), 108, 108)
        $drawing.DrawRoundedRectangle($null, (New-Object Windows.Media.Pen((Brush '#364D40'), 1.5)), (Rect 21 21 470 470), 107, 107)
        Draw-Mark $drawing 0 0 512 '#B2D1BC' '#E4EFE5'
    }
    $drawing.Pop(); $drawing.Pop()
}
function Draw-Tray($drawing, [int]$size, [string]$color) {
    # Align strokes to a 16 px grid, rather than shrink the display logo.
    $drawing.PushTransform((New-Object Windows.Media.ScaleTransform(($size / 16), ($size / 16))))
    $pen = New-Object Windows.Media.Pen((Brush $color), 1.65)
    $pen.StartLineCap = [Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
    $drawing.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse('M 12.5 6.5 L 12.5 4.5 Q 12.5 2.5 10.5 2.5 L 4.5 2.5 Q 2.5 2.5 2.5 4.5 L 2.5 10.5 Q 2.5 12.5 4.5 12.5 L 6.5 12.5'))
    $drawing.DrawRoundedRectangle((Brush $color), $null, (Rect 9 9 4.5 4.5), 1, 1)
    $drawing.Pop()
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

$appSvg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" fill="none">
  <title>栖格 Qige 应用图标</title>
  <rect x="20" y="20" width="472" height="472" rx="108" fill="#1D2D26"/>
  <rect x="21" y="21" width="470" height="470" rx="107" stroke="#364D40" stroke-width="1.5"/>
  <path d="$markPath" stroke="#B2D1BC" stroke-width="32" stroke-linecap="round" stroke-linejoin="round"/>
  <rect x="282" y="282" width="96" height="96" rx="21" fill="#E4EFE5"/>
</svg>
"@
Write-Utf8 'qige-app.svg' $appSvg
foreach ($variant in @(@('light', '#E4EFE5'), @('dark', '#284C3C'))) {
    Write-Utf8 ('qige-mark-' + $variant[0] + '.svg') @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="96 96 320 320" fill="none">
  <title>栖格 Qige 单色标志</title>
  <path d="$markPath" stroke="$($variant[1])" stroke-width="32" stroke-linecap="round" stroke-linejoin="round"/>
  <rect x="282" y="282" width="96" height="96" rx="21" fill="$($variant[1])"/>
</svg>
"@
}
Write-Utf8 'qige-tray.svg' @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" fill="none">
  <title>栖格 Qige 托盘图标</title>
  <path d="M12.5 6.5V4.5Q12.5 2.5 10.5 2.5H4.5Q2.5 2.5 2.5 4.5V10.5Q2.5 12.5 4.5 12.5H6.5" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round"/>
  <rect x="9" y="9" width="4.5" height="4.5" rx="1" fill="currentColor"/>
</svg>
'@
Write-Utf8 'qige-wordmark.svg' @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 240" fill="none">
  <title>栖格 Qige 组合标志</title>
  <g transform="translate(-60 -50) scale(0.703125)">
    <path d="$markPath" stroke="#284C3C" stroke-width="32" stroke-linecap="round" stroke-linejoin="round"/>
    <rect x="282" y="282" width="96" height="96" rx="21" fill="#284C3C"/>
  </g>
  <text x="240" y="158" fill="#284C3C" font-family="Microsoft YaHei UI,Microsoft YaHei,sans-serif" font-size="102" font-weight="600">栖格</text>
  <text x="512" y="159" fill="#607C68" font-family="Segoe UI,sans-serif" font-size="62" font-weight="600">Qige</text>
</svg>
"@

foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024)) {
    Png ('qige-app-' + $size + '.png') $size $size { param($d) Draw-App $d 0 0 $size }
}
Png 'qige-app-light-512.png' 512 512 { param($d) Draw-App $d 0 0 512 $true }
Png 'qige-mark-light-512.png' 512 512 { param($d) Draw-Mark $d -153.6 -153.6 819.2 '#E4EFE5' '#E4EFE5' }
Png 'qige-mark-dark-512.png' 512 512 { param($d) Draw-Mark $d -153.6 -153.6 819.2 '#284C3C' '#284C3C' }
Png 'qige-wordmark.png' 800 240 {
    param($d)
    Draw-Mark $d -60 -50 360 '#284C3C' '#284C3C'
    Text $d '栖格' 240 57 102 '#284C3C' 'Microsoft YaHei UI' $true
    Text $d 'Qige' 512 93 62 '#607C68' 'Segoe UI' $true
}
foreach ($size in @(16, 20, 24, 32, 40, 48, 64)) {
    Png ('qige-tray-light-' + $size + '.png') $size $size { param($d) Draw-Tray $d $size '#F2F6F0' }
    Png ('qige-tray-dark-' + $size + '.png') $size $size { param($d) Draw-Tray $d $size '#284C3C' }
}
Ico 'qige.ico' 'qige-app' @(16, 20, 24, 32, 40, 48, 64, 128, 256)
Ico 'qige-tray-light.ico' 'qige-tray-light' @(16, 20, 24, 32, 40, 48, 64)
Ico 'qige-tray-dark.ico' 'qige-tray-dark' @(16, 20, 24, 32, 40, 48, 64)

Png 'qige-brand-preview.png' 1600 1080 {
    param($d)
    $d.DrawRectangle((Brush '#F2F2EC'), $null, (Rect 0 0 1600 1080))
    $d.DrawRectangle((Brush '#16261F'), $null, (Rect 0 0 1600 720))
    Text $d 'QIGE / DESKTOP ORGANIZER' 80 53 16 '#ADC3B1' 'Segoe UI' $true
    Text $d '01  /  应用标识' 1308 53 16 '#ADC3B1'
    Draw-App $d 70 157 410
    Text $d '栖格' 575 205 110 '#EFF4EB' 'Microsoft YaHei UI' $true
    Text $d 'Qige' 850 244 54 '#B2D1BC' 'Segoe UI' $true
    Text $d '给桌面留点空。' 584 368 35 '#DFE9DA'
    Text $d '安放常用，收起杂乱。' 585 435 20 '#9DB39F'
    $d.DrawLine((New-Object Windows.Media.Pen((Brush '#3A4F40'), 1)), (New-Object Windows.Point(80, 637)), (New-Object Windows.Point(1520, 637)))
    Text $d '打开的收纳框  +  自由出入的小方块  =  Q' 86 660 17 '#ADC3B1'
    Text $d '轻巧 · 安静 · 各归其位' 1236 660 17 '#ADC3B1'

    Text $d '一个标志，适应不同尺度。' 80 766 25 '#20392C' 'Microsoft YaHei UI' $true
    Text $d '应用图标' 82 824 15 '#647569'
    Draw-App $d 76 864 100
    Draw-App $d 194 864 100 $true
    Text $d '深色 / 浅色' 87 990 14 '#647569'

    Text $d '单色标志' 440 824 15 '#647569'
    Draw-Mark $d 402 836 180 '#284C3C' '#284C3C'
    $d.DrawRoundedRectangle((Brush '#20362A'), $null, (Rect 618 860 118 118), 18, 18)
    Draw-Mark $d 585 827 180 '#E4EFE5' '#E4EFE5'
    Text $d '透明背景，保留轮廓与留白' 440 990 14 '#647569'

    Text $d '托盘与小尺寸' 868 824 15 '#647569'
    $d.DrawRoundedRectangle((Brush '#20362A'), $null, (Rect 868 874 260 66), 15, 15)
    foreach ($tray in @(@(890, 899, 16), @(934, 897, 20), @(982, 895, 24), @(1038, 891, 32))) {
        $d.PushTransform((New-Object Windows.Media.TranslateTransform($tray[0], $tray[1])))
        Draw-Tray $d $tray[2] '#F2F6F0'; $d.Pop()
    }
    Text $d '16 / 20 / 24 / 32 px，独立绘制' 868 990 14 '#647569'

    Text $d '品牌色' 1240 824 15 '#647569'
    foreach ($swatch in @(@(1240, '#1D2D26'), @(1314, '#B2D1BC'), @(1388, '#E4EFE5'))) {
        $d.DrawRoundedRectangle((Brush $swatch[1]), $null, (Rect $swatch[0] 880 60 60), 12, 12)
    }
    Text $d '墨绿 / 鼠尾草 / 雾白' 1240 990 14 '#647569'
}

Write-Output ('Generated Qige SVG, PNG, ICO and preview in ' + $brandOutput)
