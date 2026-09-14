using System.Windows.Media;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 奥黛塔桌宠音效：合成 WAV（Assets/Pet/Sounds/，见 Tools/Make-PetSounds.py）经 MediaPlayer 播放。
/// 每次播放新建实例（避免复用冲突/重叠卡顿），MediaEnded 后释放。须在 UI 线程调用（并发契约）。
/// </summary>
public static class PetSoundPlayer
{
    private const string BaseUri = "pack://application:,,,/MultiplayerHoeingAssistant;component/Assets/Pet/Sounds/";

    /// <summary>可用音效 key：online(上线)/start(开锄)/done(完成)/alert(告警)/click(互动)。</summary>
    public static void Play(string key, double volume, bool muted)
    {
        if (muted) return;
        try
        {
            var player = new MediaPlayer { Volume = Math.Clamp(volume, 0, 1) };
            player.Open(new Uri(BaseUri + key + ".wav"));
            player.MediaEnded += (_, _) =>
            {
                player.Close();
            };
            player.Play();
        }
        catch
        {
            // 音效失败不影响主流程
        }
    }
}
