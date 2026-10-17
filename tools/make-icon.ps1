# Draws BMAP's application icon (a framed 8x8 pixel-art map) and writes src/BMAP.App/Assets/bmap.ico.
# Original artwork, no game assets. Run: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\BMAP.App\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

# 8x8 "map art": a small sunset landscape.
$art = @(
  'SSSSSSSS',
  'SSSOSSSS',
  'SSOYOSSS',
  'HHHHHHHH',
  'GGGHHGGG',
  'GGGGGGGG',
  'DGGGDGGD',
  'DDDDDDDD'
)
$colors = @{ S = '#5B9BD5'; O = '#F2A541'; Y = '#FFE066'; H = '#7A5C99'; G = '#6BAF3A'; D = '#8B5A2B' }

function Draw-Icon([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'None'
  $g.InterpolationMode = 'NearestNeighbor'
  $g.Clear([System.Drawing.Color]::Transparent)
  $pad = [Math]::Max(1, [int]($size * 0.06))
  $frame = [Math]::Max(1, [int]($size * 0.07))
  $paper = [System.Drawing.ColorTranslator]::FromHtml('#E8D7A8')
  $edge = [System.Drawing.ColorTranslator]::FromHtml('#6B4A2B')
  $g.FillRectangle((New-Object System.Drawing.SolidBrush $edge), $pad, $pad, $size - 2 * $pad, $size - 2 * $pad)
  $g.FillRectangle((New-Object System.Drawing.SolidBrush $paper), $pad + [int]($frame / 2), $pad + [int]($frame / 2), $size - 2 * $pad - $frame, $size - 2 * $pad - $frame)
  $inner = $pad + $frame
  $cell = ($size - 2 * $inner) / 8.0
  for ($y = 0; $y -lt 8; $y++) {
    for ($x = 0; $x -lt 8; $x++) {
      $c = [System.Drawing.ColorTranslator]::FromHtml($colors[[string]$art[$y][$x]])
      $x0 = [int][Math]::Round($inner + $x * $cell); $y0 = [int][Math]::Round($inner + $y * $cell)
      $x1 = [int][Math]::Round($inner + ($x + 1) * $cell); $y1 = [int][Math]::Round($inner + ($y + 1) * $cell)
      $g.FillRectangle((New-Object System.Drawing.SolidBrush $c), $x0, $y0, $x1 - $x0, $y1 - $y0)
    }
  }
  $g.Dispose()
  return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
  $bmp = Draw-Icon $s
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  if ($s -eq 256) { $bmp.Save((Join-Path $out 'bmap.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
  $bmp.Dispose()
  , $ms.ToArray()
}

# ICO container with PNG-compressed entries.
$fs = [System.IO.File]::Create((Join-Path $out 'bmap.ico'))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]
  $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
  $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose()
"icon written to $out"
