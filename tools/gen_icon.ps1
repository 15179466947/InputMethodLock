# Generate icon assets from the original artwork:
#   1. Scale to 512, remove the fake-transparency checkerboard (neutral light pixels -> real alpha 0)
#   2. Erase the "豆包AI生成" watermark region (bottom-right)
#   3. Crop to content bbox, paste centered on a 256px transparent canvas -> src\icon.png
#   4. Wrap that PNG into src\app.ico as a single 256px frame (small PNG frames in ICO are
#      invalid per spec and render as noise, so only 256 is embedded)
Add-Type -AssemblyName System.Drawing
$root = "D:\MyFile\MyProjects\Input_Method_Lock"
$sourcePath = "$root\src\Input_Method_Lock.jpeg.jpeg"
if (-not (Test-Path $sourcePath)) { $sourcePath = "$root\src\Input_Method_Lock.jpeg" }
$src = [System.Drawing.Image]::FromFile($sourcePath)

function New-ScaledBitmap([System.Drawing.Image]$image, [int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.DrawImage($image, 0, 0, $size, $size)
  $g.Dispose()
  return $bmp
}

$proc = 512
$bmp = New-ScaledBitmap $src $proc
$src.Dispose()

# --- pixel pass: neutral light pixels (checkerboard) -> transparent ---
$rect = New-Object System.Drawing.Rectangle 0, 0, $proc, $proc
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
[byte[]]$bytes = New-Object 'byte[]' ($data.Stride * $proc)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
for ($y = 0; $y -lt $proc; $y++) {
  $rowBase = $y * $data.Stride
  for ($x = 0; $x -lt $proc; $x++) {
    $i = $rowBase + $x * 4
    # 水印区域（右下角）直接透明
    if ($x -gt $proc * 0.70 -and $y -gt $proc * 0.90) { $bytes[$i+3] = 0; continue }
    $b = $bytes[$i]; $gr = $bytes[$i+1]; $r = $bytes[$i+2]
    $min = [Math]::Min($r, [Math]::Min($gr, $b))
    $max = [Math]::Max($r, [Math]::Max($gr, $b))
    $chroma = $max - $min
    if ($chroma -lt 26 -and $min -ge 168) {
      $bytes[$i+3] = 0            # 棋盘格/白底 -> 全透明
    } elseif ($chroma -lt 40 -and $min -ge 130) {
      $bytes[$i+3] = 130          # 半透明边缘羽化
    }
  }
}
[System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
$bmp.UnlockBits($data)

# --- crop to content bounding box ---
$minX = $proc; $minY = $proc; $maxX = 0; $maxY = 0
for ($y = 0; $y -lt $proc; $y++) {
  $rowBase = $y * $data.Stride
  for ($x = 0; $x -lt $proc; $x++) {
    if ($bytes[$rowBase + $x * 4 + 3] -gt 0) {
      if ($x -lt $minX) { $minX = $x }
      if ($x -gt $maxX) { $maxX = $x }
      if ($y -lt $minY) { $minY = $y }
      if ($y -gt $maxY) { $maxY = $y }
    }
  }
}
if ($maxX -le $minX -or $maxY -le $minY) { throw "content not found" }
$cropW = $maxX - $minX + 1
$cropH = $maxY - $minY + 1
$cropped = $bmp.Clone((New-Object System.Drawing.Rectangle $minX, $minY, $cropW, $cropH), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bmp.Dispose()

# --- center on 256px canvas with margin ---
$final = 256
$margin = 10
$scale = [Math]::Min(($final - 2 * $margin) / $cropW, ($final - 2 * $margin) / $cropH)
$drawW = [int]($cropW * $scale); $drawH = [int]($cropH * $scale)
$offX = [int](($final - $drawW) / 2); $offY = [int](($final - $drawH) / 2)
$canvas = New-Object System.Drawing.Bitmap $final, $final
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.DrawImage($cropped, $offX, $offY, $drawW, $drawH)
$g.Dispose()
$cropped.Dispose()

$canvas.Save("$root\src\icon.png", [System.Drawing.Imaging.ImageFormat]::Png)
$pm = New-Object System.IO.MemoryStream
$canvas.Save($pm, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()
$png = $pm.ToArray(); $pm.Dispose()

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
$bw.Write([byte]0); $bw.Write([byte]0)          # 256
$bw.Write([byte]0); $bw.Write([byte]0)
$bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]$png.Length)
$bw.Write([uint32]22)
$bw.Write($png)
$bw.Flush()
[System.IO.File]::WriteAllBytes("$root\src\app.ico", $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
Write-Host ("icon.png: " + (Get-Item "$root\src\icon.png").Length + " bytes; app.ico: " + (Get-Item "$root\src\app.ico").Length + " bytes; crop: ${cropW}x${cropH} at ($minX,$minY)")
