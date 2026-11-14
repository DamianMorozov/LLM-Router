# Создаёт SVG, PNG и многоразмерный ICO из одного векторного знака
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../src/OpenLLMCluster.Desktop/Assets'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function ConvertTo-SvgPath([System.Drawing.Drawing2D.GraphicsPath]$Path) {
    $points = $Path.PathPoints
    $types = $Path.PathTypes
    $parts = [Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $points.Length; $i++) {
        $point = $points[$i]
        $position = $point.X.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture) + ' ' +
            $point.Y.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
        switch ($types[$i] -band 7) {
            0 { $parts.Add("M $position") }
            1 { $parts.Add("L $position") }
            3 {
                $parts.Add("C $position")
                for ($j = 0; $j -lt 2; $j++) {
                    $i++
                    $point = $points[$i]
                    $parts.Add($point.X.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture) + ' ' +
                        $point.Y.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture))
                }
            }
        }
        if ($types[$i] -band 128) { $parts.Add('Z') }
    }
    return $parts -join ' '
}

try {
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $background = [Drawing.Drawing2D.GraphicsPath]::new()
    $background.AddArc(12, 12, 108, 108, 180, 90)
    $background.AddArc(136, 12, 108, 108, 270, 90)
    $background.AddArc(136, 136, 108, 108, 0, 90)
    $background.AddArc(12, 136, 108, 108, 90, 90)
    $background.CloseFigure()
    $letters = [Drawing.Drawing2D.GraphicsPath]::new()
    $font = [Drawing.FontFamily]::new('Segoe UI')
    $letters.AddString('OLC', $font, [int][Drawing.FontStyle]::Bold, 94, [Drawing.PointF]::new(0, 0), [Drawing.StringFormat]::GenericTypographic)
    $bounds = $letters.GetBounds()
    $center = [Drawing.Drawing2D.Matrix]::new()
    $center.Translate((256 - $bounds.Width) / 2 - $bounds.X, (256 - $bounds.Height) / 2 - $bounds.Y)
    $letters.Transform($center)
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="Open LLM Cluster">
  <title>Open LLM Cluster — OLC</title>
  <path d="$(ConvertTo-SvgPath $background)" fill="#5965F2" stroke="#818AFF" stroke-width="2" />
  <path d="$(ConvertTo-SvgPath $letters)" fill="#FFFFFF" />
</svg>
"@
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'open-llm-cluster.svg'), $svg + [Environment]::NewLine)
    $bitmap = [Drawing.Bitmap]::new(1024, 1024)
    $canvas = [Drawing.Graphics]::FromImage($bitmap)
    $canvas.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $canvas.ScaleTransform(4, 4)
    $fill = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#5965F2'))
    $border = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#818AFF'), 2)
    $canvas.FillPath($fill, $background)
    $canvas.DrawPath($border, $background)
    $canvas.FillPath([Drawing.Brushes]::White, $letters)
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = foreach ($size in $sizes) {
        $image = [Drawing.Bitmap]::new($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($image)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($bitmap, 0, 0, $size, $size)
        $stream = [IO.MemoryStream]::new()
        $image.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'open-llm-cluster.png'), $stream.ToArray()) }
        $result = [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
        $stream.Dispose(); $graphics.Dispose(); $image.Dispose()
        $result
    }
    $iconStream = [IO.File]::Create((Join-Path $OutputDirectory 'open-llm-cluster.ico'))
    $writer = [IO.BinaryWriter]::new($iconStream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
        $offset = 6 + 16 * $images.Count
        foreach ($image in $images) {
            $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$image.Bytes.Length); $writer.Write([uint32]$offset)
            $offset += $image.Bytes.Length
        }
        foreach ($image in $images) { $writer.Write([byte[]]$image.Bytes) }
    }
    finally { $writer.Dispose() }
    $canvas.Dispose(); $bitmap.Dispose(); $fill.Dispose(); $border.Dispose()
    $background.Dispose(); $letters.Dispose(); $center.Dispose(); $font.Dispose()
    Write-Output "Созданы SVG, PNG и ICO (16–256 px): $OutputDirectory"
}
catch { Write-Error "Не удалось создать иконку: $_"; throw }
