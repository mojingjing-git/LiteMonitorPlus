#requires -Version 7.0
<#
.SYNOPSIS
    生成 LiteMonitor++ 的应用图标（resources/assets/app.ico）。

.DESCRIPTION
    本脚本以纯代码方式绘制图标，不依赖任何外部素材，也不复用上游 LiteMonitor 的
    app.ico（上游图标为「黄色闪电 + 薄荷绿圆底」）。MIT 协议不授予商标许可，
    沿用上游图标 + 改名分发容易让用户误认为官方版本，因此本项目使用自制图标。

    图形构成：
      * 圆角方形深色底板（slate-800 → 近黑蓝 垂直渐变）
      * 青色闪电（性能监控语义），线性渐变填充
      * 右下角琥珀色「++」（品牌后缀）

    之所以用代码绘制而不是 AI 生成图：图标需要在小尺寸（16/24 px）下依然清晰，
    且必须可复现（任何人都能在本机重新生成完全一致的字节）。

    输出为多帧 ICO（每帧为 32 位 DIB，未压缩），兼容 Windows Vista 及以上系统
    与 System.Drawing.Icon / WinForms。

.PARAMETER OutPath
    输出 .ico 路径，默认 resources/assets/app.ico。

.PARAMETER Sizes
    要写入的帧尺寸（像素），默认 16,24,32,48,64,128,256。

.EXAMPLE
    pwsh -File scripts/make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutPath,
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutPath) {
    $OutPath = Join-Path $repoRoot 'resources\assets\app.ico'
}

# 设计坐标系为 256x256，绘制时按需缩放
$DesignSize = 256.0
$MasterCanvas = 1024   # 先在高分辨率画，再缩放到各目标尺寸，保证小尺寸也清晰

# ----------------------------------------------------------------------
# 绘图
# ----------------------------------------------------------------------

function New-RoundedRectPath {
    param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$Radius)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-MasterBitmap {
    param([int]$Canvas, [bool]$WithPlus)

    $bmp = [System.Drawing.Bitmap]::new($Canvas, $Canvas, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        $k = $Canvas / $DesignSize

        # --- 圆角底板 ---
        $radius = 58 * $k
        $tile = New-RoundedRectPath -X 0 -Y 0 -W $Canvas -H $Canvas -Radius $radius
        try {
            $tileBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.RectangleF]::new(0, 0, $Canvas, $Canvas),
                [System.Drawing.Color]::FromArgb(255, 30, 41, 59),
                [System.Drawing.Color]::FromArgb(255, 8, 13, 26),
                [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
            try { $g.FillPath($tileBrush, $tile) } finally { $tileBrush.Dispose() }

            $tilePen = [System.Drawing.Pen]::new(
                [System.Drawing.Color]::FromArgb(80, 148, 163, 184),
                [Math]::Max(1.0, 2.0 * $k))
            try { $g.DrawPath($tilePen, $tile) } finally { $tilePen.Dispose() }
        }
        finally { $tile.Dispose() }

        # --- 闪电 ---
        $boltPoints = @(
            @(138, 24), @(62, 140), @(118, 140), @(108, 214), @(200, 110), @(144, 110)
        )
        $pts = [System.Drawing.PointF[]]@(
            $boltPoints | ForEach-Object {
                [System.Drawing.PointF]::new([float]($_[0] * $k), [float]($_[1] * $k))
            }
        )
        $bolt = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $bolt.AddPolygon($pts)
            $boltBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.RectangleF]::new([float](58 * $k), [float](20 * $k), [float](146 * $k), [float](214 * $k)),
                [System.Drawing.Color]::FromArgb(255, 125, 211, 252),
                [System.Drawing.Color]::FromArgb(255, 14, 165, 233),
                [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
            try { $g.FillPath($boltBrush, $bolt) } finally { $boltBrush.Dispose() }
        }
        finally { $bolt.Dispose() }

        # --- 品牌后缀「++」---
        if ($WithPlus) {
            $plusSize = 30.0
            $plusThick = 10.0
            $plusGap = 8.0
            $groupW = 2 * $plusSize + $plusGap
            $groupH = $plusSize

            # ------------------------------------------------------------------
            # 「++」的定位：到闪电的垂线距离 == 到圆角边界的垂线距离
            #
            #   设底板圆角半径 R，圆心 C=(256-R, 256-R)；
            #   「++」整体宽 groupW、高 groupH，其右下角落在对角线 C+(t,t) 上，
            #   则左下角为 (C-groupW+t, C-groupH+t)。
            #
            #   到边界：dEdge = R - t*sqrt(2)
            #   到闪电：闪电右下边线过 (108,214)、方向 (92,-104)，
            #           外法线 n = (104, 92)/|d|，
            #           dBolt = (X0-108)*n.x + (Y0-214)*n.y
            #   令 dBolt = dEdge 解得 t = (R - b) / (k + sqrt(2))
            #   其中 k = n.x + n.y，b = (C-groupW-108)*n.x + (C-groupH-214)*n.y
            # ------------------------------------------------------------------
            $cornerR = 58.0
            $cornerC = $DesignSize - $cornerR
            $boltEdgeX = 108.0
            $boltEdgeY = 214.0
            $edgeLen = [Math]::Sqrt(92.0 * 92.0 + 104.0 * 104.0)
            $nx = 104.0 / $edgeLen
            $ny = 92.0 / $edgeLen
            $kSlope = $nx + $ny
            $bOffset = ($cornerC - $groupW - $boltEdgeX) * $nx + ($cornerC - $groupH - $boltEdgeY) * $ny
            $t = ($cornerR - $bOffset) / ($kSlope + [Math]::Sqrt(2))

            $originX = ($cornerC - $groupW + $t) * $k
            $originY = ($cornerC - $groupH + $t) * $k
            $plusSize = $plusSize * $k
            $plusThick = $plusThick * $k
            $plusGap = $plusGap * $k

            foreach ($i in 0, 1) {
                $ox = $originX + $i * ($plusSize + $plusGap)
                $oy = $originY

                # 轻微投影，压暗底板、提升层次
                $shadow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(120, 2, 6, 16))
                try {
                    $g.FillRectangle($shadow, [float]($ox + 2 * $k), [float]($oy + $plusSize / 2 - $plusThick / 2 + 2 * $k), [float]$plusSize, [float]$plusThick)
                    $g.FillRectangle($shadow, [float]($ox + $plusSize / 2 - $plusThick / 2 + 2 * $k), [float]($oy + 2 * $k), [float]$plusThick, [float]$plusSize)
                }
                finally { $shadow.Dispose() }

                $fill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 251, 191, 36))
                try {
                    $g.FillRectangle($fill, [float]$ox, [float]($oy + $plusSize / 2 - $plusThick / 2), [float]$plusSize, [float]$plusThick)
                    $g.FillRectangle($fill, [float]($ox + $plusSize / 2 - $plusThick / 2), [float]$oy, [float]$plusThick, [float]$plusSize)
                }
                finally { $fill.Dispose() }
            }
        }
    }
    finally {
        $g.Dispose()
    }
    return $bmp
}

function New-ScaledBitmap {
    param([System.Drawing.Bitmap]$Source, [int]$Size)
    $bmp = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($Source, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
    }
    finally { $g.Dispose() }
    return $bmp
}

# ----------------------------------------------------------------------
# ICO 编码（每帧 32 位 DIB，未压缩）
# ----------------------------------------------------------------------

function Get-DibBytes {
    param([System.Drawing.Bitmap]$Bmp)

    $w = $Bmp.Width
    $h = $Bmp.Height
    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    try {
        # BITMAPINFOHEADER（biHeight = 2*h，含 XOR 位图 + AND 掩码）
        $bw.Write([uint32]40)
        $bw.Write([int32]$w)
        $bw.Write([int32]($h * 2))
        $bw.Write([uint16]1)   # biPlanes
        $bw.Write([uint16]32)  # biBitCount
        $bw.Write([uint32]0)   # biCompression = BI_RGB
        $bw.Write([uint32]0)   # biSizeImage
        $bw.Write([int32]0)
        $bw.Write([int32]0)
        $bw.Write([uint32]0)
        $bw.Write([uint32]0)

        $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
        $locked = $Bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stride = $locked.Stride
        $buf = [byte[]]::new($stride * $h)
        try {
            [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $buf, 0, $stride * $h)
        }
        finally { $Bmp.UnlockBits($locked) }

        # XOR 位图：自下而上，每行 BGRA
        for ($y = $h - 1; $y -ge 0; $y--) {
            $bw.Write($buf, $y * $stride, $w * 4)
        }

        # AND 掩码：32 位色时全 0（透明由 alpha 通道表达），每行按 4 字节对齐
        $maskStride = [int]([Math]::Floor(($w + 31) / 32) * 4)
        $bw.Write([byte[]]::new($maskStride * $h))

        $bw.Flush()
        # 逗号前缀：避免 byte[] 被 PowerShell 管道展开成逐字节输出
        return , $ms.ToArray()
    }
    finally {
        $bw.Dispose()
        $ms.Dispose()
    }
}

function ConvertTo-IcoBytes {
    param([System.Drawing.Bitmap[]]$Frames)

    $images = @()
    foreach ($f in $Frames) { $images += , (Get-DibBytes $f) }

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    try {
        $bw.Write([uint16]0)                  # idReserved
        $bw.Write([uint16]1)                  # idType = 1 (icon)
        $bw.Write([uint16]$Frames.Count)

        $offset = 6 + 16 * $Frames.Count
        for ($i = 0; $i -lt $Frames.Count; $i++) {
            $w = $Frames[$i].Width
            $h = $Frames[$i].Height
            $bw.Write([byte]$(if ($w -ge 256) { 0 } else { $w }))
            $bw.Write([byte]$(if ($h -ge 256) { 0 } else { $h }))
            $bw.Write([byte]0)                 # bColorCount
            $bw.Write([byte]0)                 # bReserved
            $bw.Write([uint16]1)               # wPlanes
            $bw.Write([uint16]32)              # wBitCount
            $bw.Write([uint32]$images[$i].Length)
            $bw.Write([uint32]$offset)
            $offset += $images[$i].Length
        }

        foreach ($img in $images) { $bw.Write($img) }
        $bw.Flush()
        return , $ms.ToArray()
    }
    finally {
        $bw.Dispose()
        $ms.Dispose()
    }
}

# ----------------------------------------------------------------------
# 主流程
# ----------------------------------------------------------------------

$frames = [System.Collections.Generic.List[System.Drawing.Bitmap]]::new()
try {
    $masterFull = New-MasterBitmap -Canvas $MasterCanvas -WithPlus $true
    $masterSimple = New-MasterBitmap -Canvas $MasterCanvas -WithPlus $false
    try {
        foreach ($size in ($Sizes | Sort-Object -Unique)) {
            # 32px 以下「++」不可辨识，小尺寸改用无后缀的简化构图
            $src = if ($size -ge 32) { $masterFull } else { $masterSimple }
            $frames.Add((New-ScaledBitmap -Source $src -Size $size))
        }
    }
    finally {
        $masterFull.Dispose()
        $masterSimple.Dispose()
    }

    $icoBytes = ConvertTo-IcoBytes -Frames $frames.ToArray()
}
finally {
    foreach ($f in $frames) { $f.Dispose() }
}

$outDir = Split-Path -Parent $OutPath
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
[System.IO.File]::WriteAllBytes($OutPath, $icoBytes)

$sha = (Get-FileHash -LiteralPath $OutPath -Algorithm SHA256).Hash.ToLower()
$frameList = ($Sizes | Sort-Object -Unique)

Write-Host ("OK -> {0}" -f $OutPath)
Write-Host ("   frames : {0}" -f ($frameList -join ', '))
Write-Host ("   bytes  : {0}" -f $icoBytes.Length)
Write-Host ("   sha256 : {0}" -f $sha)

# ----------------------------------------------------------------------
# 回读校验：确认 .ico 结构可被正确解析
# ----------------------------------------------------------------------
$raw = [System.IO.File]::ReadAllBytes($OutPath)
$count = [BitConverter]::ToUInt16($raw, 4)
Write-Host ("   verify : ICONDIR type={0} frames={1}" -f [BitConverter]::ToUInt16($raw, 2), $count)
if ($count -ne $frameList.Count) {
    throw "ICO 帧数不符：期望 $($frameList.Count)，实际 $count"
}
for ($i = 0; $i -lt $count; $i++) {
    $e = 6 + 16 * $i
    $w = if ($raw[$e] -eq 0) { 256 } else { $raw[$e] }
    $h = if ($raw[$e + 1] -eq 0) { 256 } else { $raw[$e + 1] }
    $len = [BitConverter]::ToUInt32($raw, $e + 8)
    $off = [BitConverter]::ToUInt32($raw, $e + 12)
    $dibW = [BitConverter]::ToInt32($raw, $off + 4)
    $dibH = [BitConverter]::ToInt32($raw, $off + 8)
    $expected = 40 + ($w * 4 * $h) + ([int]([Math]::Floor(($w + 31) / 32) * 4) * $h)
    if ($len -ne $expected) { throw "第 $i 帧长度异常：期望 $expected，实际 $len" }
    if ($dibW -ne $w -or $dibH -ne ($h * 2)) { throw "第 $i 帧 DIB 头异常" }
    Write-Host ("            [{0,3}] {1}x{2}  bytes={3}  offset={4}" -f $i, $w, $h, $len, $off)
}
