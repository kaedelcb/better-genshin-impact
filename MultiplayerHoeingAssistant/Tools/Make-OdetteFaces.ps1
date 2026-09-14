# 奥黛塔表情脸变体生成：基于 odette_base.png（1024 徽章）做眼部位图改绘
#   - odette_sleeping.png：肤色补底 + 下弯闭眼弧 ︶︶（睡觉）
#   - odette_happy.png：   肤色补底 + 上弯开心弧 ＾＾（庆祝）
# MarkerMode 只画十字/椭圆轮廓用于校准眼位。
# 表情包 drop-in 约定：放同名 PNG 进 Assets/Pet/ 即可覆盖脚本产物（手绘/豆包生成均可）。
param(
    [string]$BasePng = (Join-Path $PSScriptRoot "..\Assets\Pet\odette_base.png"),
    [string]$OutDir = (Join-Path $PSScriptRoot "..\Assets\Pet"),
    [string]$TempDir = "$env:TEMP",
    [int]$EyeLX = 396, [int]$EyeLY = 609,
    [int]$EyeRX = 628, [int]$EyeRY = 605,
    [int]$PatchRX = 62, [int]$PatchRY = 46,
    [string]$SkinHex = "",
    [switch]$MarkerMode
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public static class OdetteFace
{
    /// <summary>肤色取样：取眼上方与眼外侧两点的中值，尽量贴合脸部渐变。</summary>
    public static Color SampleSkin(Bitmap bmp, Point eye, int patchRX, int patchRY, int side)
    {
        var c1 = bmp.GetPixel(eye.X, Math.Min(bmp.Height - 1, eye.Y + patchRY + 26));
        var c2 = bmp.GetPixel(Math.Max(0, Math.Min(bmp.Width - 1, eye.X + side * (patchRX - 20))), Math.Max(0, eye.Y + patchRY / 2));
        return Color.FromArgb(255, (c1.R + c2.R) / 2, (c1.G + c2.G) / 2, (c1.B + c2.B) / 2);
    }

    /// <summary>改绘一只眼：肤色椭圆补底 + 表情弧线。curveUp=false 画下弯 ︶（闭眼），true 画上弯 ＾（开心）。</summary>
    public static void RedrawEye(Graphics g, Point eye, int patchRX, int patchRY, Color skin, Color line, bool curveUp, float lineWidth)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 1) 肤色补底（略大于眼，遮住原睁眼）
        using (var skinBrush = new SolidBrush(skin))
            g.FillEllipse(skinBrush, eye.X - patchRX, eye.Y - patchRY, patchRX * 2, patchRY * 2);

        // 2) 表情弧线：bounding box 比补底略小
        int ax = eye.X - patchRX + 14, aw = (patchRX - 14) * 2;
        using (var pen = new Pen(line, lineWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            if (curveUp)
            {
                // ＾：椭圆中心在眼下，画上半弧（180°→360°）
                var rect = new Rectangle(ax, eye.Y - 12, aw, 76);
                g.DrawArc(pen, rect, 200, 140);
            }
            else
            {
                // ︶：椭圆中心在眼上，画下半弧（0°→180°）
                var rect = new Rectangle(ax, eye.Y - 18, aw, 76);
                g.DrawArc(pen, rect, 20, 140);
            }
        }
    }
}
'@

if (-not (Test-Path $BasePng)) { throw "底图不存在: $BasePng" }
$base = [System.Drawing.Bitmap]::new($BasePng)
try {
    $eyes = @(
        @{ P = [System.Drawing.Point]::new($EyeLX, $EyeLY) },
        @{ P = [System.Drawing.Point]::new($EyeRX, $EyeRY) }
    )

    if ($MarkerMode) {
        $dbg = [System.Drawing.Bitmap]::new($base)
        $g = [System.Drawing.Graphics]::FromImage($dbg)
        foreach ($e in $eyes) {
            $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::Red, 3)
            $g.DrawLine($pen, $e.P.X - 40, $e.P.Y, $e.P.X + 40, $e.P.Y)
            $g.DrawLine($pen, $e.P.X, $e.P.Y - 40, $e.P.X, $e.P.Y + 40)
            $g.DrawEllipse($pen, $e.P.X - $PatchRX, $e.P.Y - $PatchRY, $PatchRX * 2, $PatchRY * 2)
            $pen.Dispose()
        }
        $g.Dispose()
        $crop = [System.Drawing.Rectangle]::new([Math]::Min($EyeLX, $EyeRX) - 160, $EyeLY - 130, ([Math]::Abs($EyeRX - $EyeLX)) + 320, 260)
        $face = $dbg.Clone($crop, $dbg.PixelFormat)
        $dbg.Dispose()
        $out = Join-Path $TempDir 'odette_marker.png'
        $face.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
        $face.Dispose()
        Write-Output "标记图: $out (裁剪区 $crop)"
        return
    }

    foreach ($spec in @(
        @{ Name = 'sleeping'; Up = $false },
        @{ Name = 'happy';    Up = $true }
    )) {
        $img = [System.Drawing.Bitmap]::new($base)
        $g = [System.Drawing.Graphics]::FromImage($img)
        $line = [System.Drawing.Color]::FromArgb(255, 43, 35, 51)   # 贴线稿深棕黑
        # 肤色：优先用 -SkinHex 手动指定（自动采样会被鼻侧阴影/垂下发丝污染，右眼曾发灰）
        if ($SkinHex) {
            $v = [Convert]::ToInt32($SkinHex, 16)
            $skin = [System.Drawing.Color]::FromArgb(255, ($v -shr 16) -band 0xFF, ($v -shr 8) -band 0xFF, $v -band 0xFF)
        }
        foreach ($e in $eyes) {
            if (-not $SkinHex) {
                $side = if ($e.P.X -lt ($EyeLX + $EyeRX) / 2) { 1 } else { -1 }
                $skin = [OdetteFace]::SampleSkin($img, $e.P, $PatchRX, $PatchRY, $side)
            }
            [OdetteFace]::RedrawEye($g, $e.P, $PatchRX, $PatchRY, $skin, $line, $spec.Up, [float]11)
        }
        $g.Dispose()
        $out = Join-Path $OutDir ("odette_" + $spec.Name + ".png")
        $img.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
        $img.Dispose()
        Write-Output "已生成: $out"

        # 脸部特写预览
        $chk = [System.Drawing.Bitmap]::new($base -as [System.Drawing.Bitmap])
        $chk.Dispose()
        $preview = [System.Drawing.Bitmap]::new($out)
        $crop = [System.Drawing.Rectangle]::new([Math]::Min($EyeLX, $EyeRX) - 160, $EyeLY - 130, ([Math]::Abs($EyeRX - $EyeLX)) + 320, 260)
        $face = $preview.Clone($crop, $preview.PixelFormat)
        $preview.Dispose()
        $pout = Join-Path $TempDir ("odette_" + $spec.Name + "_face.png")
        $face.Save($pout, [System.Drawing.Imaging.ImageFormat]::Png)
        $face.Dispose()
        Write-Output "特写: $pout"
    }
}
finally { $base.Dispose() }

