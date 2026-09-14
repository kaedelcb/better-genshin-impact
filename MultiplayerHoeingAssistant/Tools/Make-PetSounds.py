# 奥黛塔桌宠音效合成：短促、轻柔、低音量的正弦+泛音提示音（无版权风险）
# 输出到 Assets/Pet/Sounds/*.wav；音量整体压得较低（峰值 0.32），由 MediaPlayer Volume 再控制
import math
import os
import struct
import wave

SR = 44100
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets", "Pet", "Sounds")


def tone(freq, ms, peak=0.32, attack=0.008, harmonics=((1, 1.0), (2, 0.25), (3, 0.08))):
    """单音：基频+柔和泛音，快起缓落包络。"""
    n = int(SR * ms / 1000)
    out = []
    for i in range(n):
        t = i / SR
        env = min(1.0, (i / SR) / attack) * math.exp(-2.2 * t / (ms / 1000.0))
        v = sum(a * math.sin(2 * math.pi * freq * h * t) for h, a in harmonics)
        out.append(env * v * peak / 1.33)
    return out


def silence(ms):
    return [0.0] * int(SR * ms / 1000)


def save(name, samples):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    with wave.open(path, "w") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, s)) * 32767)) for s in samples))
    print(name, f"{len(samples)/SR*1000:.0f}ms")


# 上线成功：E5→A5 双音上行（轻快确认）
save("online.wav", tone(659, 95) + silence(25) + tone(880, 140))
# 开锄：A4→C#5 短促起跑点
save("start.wav", tone(440, 80) + silence(20) + tone(554, 110))
# 任务完成：C5-E5-G5 三音琶音
save("done.wav", tone(523, 85) + tone(659, 85) + tone(784, 170))
# 告警：低音软闷点（G3，较慢衰减）
save("alert.wav", tone(196, 190, peak=0.30))
# 互动/双击：极短高音泡音
save("click.wav", tone(1175, 45, peak=0.22))
