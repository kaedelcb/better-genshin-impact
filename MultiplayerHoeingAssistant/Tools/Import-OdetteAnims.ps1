# 奥黛塔动画素材审计+导入：15 个表情文件夹（各 2 帧）→ Assets/Pet/Anims/{key}/f1.png,f2.png + anims.json
# 审计项：尺寸、透明像素占比（alpha<250 视为透明，全不透明=实底需抠图，标记）、超大帧降采样（>800px → 800，控内嵌体积）
param(
    [string]$SourceDir = "E:\360MoveData\Users\Administrator\Desktop\奥黛塔",
    [string]$DestDir = (Join-Path $PSScriptRoot "..\Assets\Pet"),
    [int]$MaxSide = 800
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public static class OdetteImport
{
    /// <summary>审计单帧：尺寸 + 透明像素占比；按需降采样后另存 PNG（保留 alpha）。返回报告行。</summary>
    public static string AuditAndSave(string src, string dst, int maxSide, out bool opaque)
    {
        using (var bmp = new Bitmap(src))
        {
            int w = bmp.Width, h = bmp.Height;
            long transparent = 0, total = (long)w * h;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var buf = new byte[data.Stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            bmp.UnlockBits(data);
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * data.Stride;
                for (int x = 0; x < w; x++)
                    if (buf[rowOff + x * 4 + 3] < 250) transparent++;
            }
            opaque = transparent == 0;

            // 降采样（双三次，保留 alpha；SmoothingMode 保证边缘质量）
            int nw = w, nh = h;
            float scale = Math.Min(1f, maxSide / (float)Math.Max(w, h));
            if (scale < 1f) { nw = (int)Math.Round(w * scale); nh = (int)Math.Round(h * scale); }
            using (var outBmp = new Bitmap(nw, nh, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(outBmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(bmp, new Rectangle(0, 0, nw, nh), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
                }
                outBmp.Save(dst, ImageFormat.Png);
            }
            string flag = opaque ? " [实底!]" : (scale < 1f ? string.Format(" [降采样{0}x{1}->{2}x{3}]", w, h, nw, nh) : "");
            string report = string.Format("{0}: {1}x{2} 透明{3:F1}%{4}{5}",
                System.IO.Path.GetFileName(src), w, h, transparent * 100.0 / total,
                scale < 1f ? string.Format(" -> {0}x{1}", nw, nh) : "", flag);
            return report;
        }
    }
}
'@

# 中文文件夹 → 动画 key（与状态机映射一致）
$map = [ordered]@{
    '喜' = 'joy';       '怒' = 'anger';     '哀' = 'sorrow';    '惊' = 'shock'
    '羞' = 'shy';       '得意' = 'smug';    '嫌弃' = 'disdain'; '无奈' = 'helpless'
    '疑惑' = 'confused'; '喝茶' = 'tea';    '睡觉' = 'sleep';   '互动' = 'interact'
    '执行锄地' = 'act_hoeing'; '执行狗粮' = 'act_artifact'; '执行采集' = 'act_gather'
}

$animsDir = Join-Path $DestDir 'Anims'
if (Test-Path $animsDir) { Remove-Item $animsDir -Recurse -Force }
New-Item -ItemType Directory -Path $animsDir | Out-Null

$sets = [ordered]@{}
$flags = @()
foreach ($folder in $map.Keys) {
    $key = $map[$folder]
    $dir = Join-Path $SourceDir $folder
    if (-not (Test-Path $dir)) { $flags += "缺文件夹: $folder"; continue }
    $frames = Get-ChildItem $dir -File | Where-Object { $_.Extension -match '\.(png|jpg|webp)$' } | Sort-Object Name
    if ($frames.Count -lt 1) { $flags += "$folder 无图片"; continue }
    $outDir = Join-Path $animsDir $key
    New-Item -ItemType Directory -Path $outDir | Out-Null

    $names = @()
    for ($i = 0; $i -lt [Math]::Min(2, $frames.Count); $i++) {
        $isOpaque = $false
        $dst = Join-Path $outDir ("f" + ($i + 1) + ".png")
        $line = [OdetteImport]::AuditAndSave($frames[$i].FullName, $dst, $MaxSide, [ref]$isOpaque)
        Write-Output $line
        if ($isOpaque) { $flags += "$folder 第$($i+1)帧全不透明（实底），需抠图处理" }
        $names += "f" + ($i + 1) + ".png"
    }
    if ($frames.Count -eq 1) { $flags += "$folder 只有 1 帧，将用静态+呼吸动效兜底" }
    $sets[$key] = @{ frames = $names; source = $folder; frameCount = $frames.Count }
}

# 动画清单（播放时序：hold=单帧停留，fade=交叉淡化时长；Director 可覆盖）
$manifest = [ordered]@{
    version = 1
    timing  = @{ holdMs = 500; fadeMs = 260; stateFadeMs = 200 }
    sets    = $sets
}
$manifestPath = Join-Path $DestDir 'anims.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content $manifestPath -Encoding UTF8

Write-Output "`n=== 汇总 ==="
Write-Output ("导入 {0} 套动画，清单: {1}" -f $sets.Count, $manifestPath)
$total = (Get-ChildItem $animsDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Output ("导入后总大小: {0:F1} MB" -f $total)
if ($flags.Count -gt 0) { Write-Output "`n=== 异常标记 ==="; $flags | ForEach-Object { Write-Output "- $_" } }
