# 奥黛塔桌宠素材处理：圆形LOGO白底图 → 透明徽章 odette_base.png
# 流程：非白像素做最大连通域（保徽章、去水印/浮点）→ 圆形羽化边缘 → 裁剪 → 缩放输出
# 另输出暗底合成预览图（仅验收用，不入库）。
param(
    [string]$SourcePath = "C:\Users\Administrator\Downloads\奥黛塔Q版圆形LOGO设计.png",
    [string]$OutPng = (Join-Path $PSScriptRoot "..\Assets\Pet\odette_base.png"),
    [string]$PreviewPng = "$env:TEMP\odette_preview_dark.png",
    [int]$OutputSize = 1024,
    [int]$WhiteTolerance = 16
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public static class OdetteCut
{
    /// <summary>最大连通域提取 + 圆形羽化 + 裁剪。返回处理后的新图；bbox 通过 out 返回。</summary>
    public static Bitmap Extract(Bitmap src, int whiteTol, out Rectangle bbox, out int keptPixels)
    {
        int w = src.Width, h = src.Height;
        var bmp = new Bitmap(src); // Format32bppArgb 副本
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var buf = new byte[data.Stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(data);

        int stride = data.Stride;
        // 1) 掩码：与纯白的最大通道偏差 >= tol 视为前景
        var mask = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int o = row + x * 4; // BGAR
                int dev = Math.Max(255 - buf[o + 2], Math.Max(255 - buf[o + 1], 255 - buf[o]));
                mask[y * w + x] = dev >= whiteTol;
            }
        }

        // 2) 8 连通最大域，其余像素置透明
        var label = new int[w * h];
        int bestId = 0, bestCount = 0, nextId = 0;
        var queue = new Queue<int>();
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || label[i] != 0) continue;
            int id = ++nextId, count = 0;
            queue.Enqueue(i);
            label[i] = id;
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                count++;
                int cx = cur % w, cy = cur / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ni = ny * w + nx;
                        if (mask[ni] && label[ni] == 0) { label[ni] = id; queue.Enqueue(ni); }
                    }
            }
            if (count > bestCount) { bestCount = count; bestId = id; }
        }
        int minX = w, minY = h, maxX = -1, maxY = -1;
        // 逐行记录最大域左右端：圆盘是凸形，行内首末像素之间整体保留（撑回圆内被白色阈值误伤的浅色雾区），
        // 行外清零。雾区像素保留原色、alpha 置满，避免圆内透明碎洞。
        var rowFirst = new int[h];
        var rowLast = new int[h];
        for (int y = 0; y < h; y++) { rowFirst[y] = -1; rowLast[y] = -1; }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (label[y * w + x] != bestId) continue;
                if (rowFirst[y] < 0) rowFirst[y] = x;
                rowLast[y] = x;
            }
            if (rowFirst[y] < 0) continue;
            if (rowFirst[y] < minX) minX = rowFirst[y];
            if (rowLast[y] > maxX) maxX = rowLast[y];
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int o = row + x * 4;
                if (rowFirst[y] >= 0 && x >= rowFirst[y] && x <= rowLast[y])
                {
                    buf[o + 3] = 255;
                }
                else
                {
                    buf[o] = 0; buf[o + 1] = 0; buf[o + 2] = 0; buf[o + 3] = 0;
                }
            }
        }
        keptPixels = bestCount;
        bbox = Rectangle.Round(new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1));
        if (bbox.IsEmpty) return new Bitmap(4, 4);

        // 3) 椭圆抗锯齿边缘（v2）：以 bbox 半轴为椭圆基准做归一化距离，
        //    边缘带 2.5px 平滑线性过渡——替代旧窄羽化，消除正圆半径与微椭圆不匹配造成的毛刺/菱角。
        float fcx = (minX + maxX) / 2f, fcy = (minY + maxY) / 2f;
        float ea = (maxX - minX) / 2f + 0.5f, eb = (maxY - minY) / 2f + 0.5f;
        const float featherPx = 2.5f;
        float band = featherPx / Math.Min(ea, eb); // 归一化距离上的过渡带宽
        for (int y = minY; y <= maxY; y++)
        {
            int row = y * stride;
            for (int x = minX; x <= maxX; x++)
            {
                int o = row + x * 4 + 3;
                if (buf[o] == 0) continue;
                float nx = (x - fcx) / ea, ny = (y - fcy) / eb;
                float dn = (float)Math.Sqrt(nx * nx + ny * ny);
                float a = (1f + band * 0.5f - dn) / band;
                if (a <= 0) buf[o] = 0;
                else if (a < 1) buf[o] = (byte)(buf[o] * a);
            }
        }

        // 4) 裁剪回托管位图
        var cut = new Bitmap(bbox.Width, bbox.Height, PixelFormat.Format32bppArgb);
        var cutData = cut.LockBits(new Rectangle(0, 0, cut.Width, cut.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < bbox.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(buf, (minY + y) * stride + minX * 4,
                new IntPtr(cutData.Scan0.ToInt64() + (long)y * cutData.Stride), bbox.Width * 4);
        }
        cut.UnlockBits(cutData);
        return cut;
    }

    /// <summary>暗底合成预览（验收透明度用）。</summary>
    public static Bitmap ComposeOnDark(Bitmap img, int backSide)
    {
        var back = new Bitmap(backSide, backSide, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(back))
        {
            g.Clear(Color.FromArgb(34, 34, 34));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int side = (int)(backSide * 0.86);
            var dst = new Rectangle((backSide - side) / 2, (backSide - side) / 2, side, side);
            g.DrawImage(img, dst, new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
        }
        return back;
    }
}
'@

if (-not (Test-Path $SourcePath)) { throw "源图不存在: $SourcePath" }
$src = [System.Drawing.Bitmap]::new($SourcePath)
try {
    [System.Drawing.Rectangle]$bbox = [System.Drawing.Rectangle]::Empty
    [int]$kept = 0
    $cut = [OdetteCut]::Extract($src, $WhiteTolerance, [ref]$bbox, [ref]$kept)
    try {
        Write-Output ("源图: {0}x{1}  徽章bbox: {2}  保留像素: {3} ({4:P1})" -f `
            $src.Width, $src.Height, $bbox, $kept, ($kept / ($src.Width * $src.Height)))

        $outDir = Split-Path -Parent $OutPng
        if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

        # 缩放输出（高质量双三次）
        $final = [System.Drawing.Bitmap]::new($OutputSize, $OutputSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($final)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.DrawImage($cut, 0, 0, $OutputSize, $OutputSize)
            $g.Dispose()
            $final.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Output ("已输出: {0} ({1}x{1})" -f (Resolve-Path $OutPng), $OutputSize)
        }
        finally { $final.Dispose() }

        # 暗底预览
        $preview = [OdetteCut]::ComposeOnDark($cut, 800)
        try {
            $preview.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Output ("暗底预览: " + $PreviewPng)
        }
        finally { $preview.Dispose() }
    }
    finally { $cut.Dispose() }
}
finally { $src.Dispose() }
