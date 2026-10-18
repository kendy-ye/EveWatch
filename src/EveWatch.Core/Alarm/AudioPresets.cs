using System.Buffers.Binary;
using NAudio.Wave;

namespace EveWatch.Core.Alarm;

/// <summary>波形类型。</summary>
public enum ToneWave
{
    Sine,
    Square,
    Triangle,
    Sawtooth,
}

/// <summary>一个音阶：频率、起始延迟、持续时长（毫秒）。</summary>
public readonly record struct ToneStep(int Frequency, int DelayMs, int DurationMs);

/// <summary>一个内置报警音预设。</summary>
public sealed record AudioPreset(string Id, string Name, string Description, ToneWave Wave, ToneStep[] Steps);

/// <summary>
/// 内置报警音的合成。
/// <para>
/// 预设全部由程序合成波形，不依赖外部音频文件，保证开箱即有声音。
/// PCM 在首次使用时渲染一次并缓存，之后每次播放只是把缓存包成流。
/// </para>
/// </summary>
public static class AudioPresets
{
    /// <summary>默认预设。</summary>
    public const string DefaultId = "alarm";

    /// <summary>每个音阶尾部保留的淡出时长（毫秒）。</summary>
    private const int FadeOutMs = 40;

    private const int AttackMs = 12;

    private static readonly Dictionary<string, byte[]> PcmCache = [];
    private static readonly object CacheSync = new();

    /// <summary>输出格式：44.1kHz、16 位、单声道。</summary>
    public static WaveFormat Format { get; } = new(44100, 16, 1);

    /// <summary>五个内置预设。</summary>
    public static IReadOnlyList<AudioPreset> All { get; } =
    [
        new("alarm", "警报长鸣", "800 Hz 长音 ×2", ToneWave.Sawtooth,
            [new ToneStep(800, 0, 560), new ToneStep(800, 660, 560)]),
        new("beep", "短促嘀声", "1200 Hz 短音 ×3", ToneWave.Square,
            [new ToneStep(1200, 0, 85), new ToneStep(1200, 150, 85), new ToneStep(1200, 300, 85)]),
        new("rising", "三段上升", "520 / 700 / 900 Hz", ToneWave.Triangle,
            [new ToneStep(520, 0, 140), new ToneStep(700, 160, 140), new ToneStep(900, 320, 240)]),
        new("buzz", "低沉蜂鸣", "180 Hz 方波长音", ToneWave.Square,
            [new ToneStep(180, 0, 620)]),
        new("mech", "机械提示", "620 Hz 双击", ToneWave.Square,
            [new ToneStep(620, 0, 70), new ToneStep(620, 115, 70)]),
    ];

    public static AudioPreset? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static AudioPreset Default => Find(DefaultId)!;

    /// <summary>取预设的 PCM（16 位单声道），首次调用时渲染并缓存。</summary>
    public static byte[] PcmOf(AudioPreset preset)
    {
        lock (CacheSync)
        {
            if (PcmCache.TryGetValue(preset.Id, out var cached))
            {
                return cached;
            }

            var rendered = Render(preset);
            PcmCache[preset.Id] = rendered;
            return rendered;
        }
    }

    private static byte[] Render(AudioPreset preset)
    {
        var sampleRate = Format.SampleRate;
        var totalMs = 0;
        foreach (var step in preset.Steps)
        {
            totalMs = Math.Max(totalMs, step.DelayMs + step.DurationMs + FadeOutMs);
        }

        var totalSamples = (int)(sampleRate * (totalMs / 1000.0));
        var buffer = new byte[totalSamples * 2];
        var amplitude = 0.32;

        foreach (var step in preset.Steps)
        {
            var startSample = (int)(sampleRate * (step.DelayMs / 1000.0));
            var durationSamples = (int)(sampleRate * (step.DurationMs / 1000.0));
            var attackSamples = Math.Max(1, (int)(sampleRate * (AttackMs / 1000.0)));
            var fadeSamples = Math.Max(1, (int)(sampleRate * (FadeOutMs / 1000.0)));

            for (var i = 0; i < durationSamples; i++)
            {
                var index = startSample + i;
                if (index >= totalSamples)
                {
                    break;
                }

                var t = (double)i / sampleRate;
                var phase = 2.0 * Math.PI * step.Frequency * t;

                var raw = preset.Wave switch
                {
                    ToneWave.Square => Math.Sin(phase) >= 0 ? 1.0 : -1.0,
                    ToneWave.Triangle => 2.0 / Math.PI * Math.Asin(Math.Sin(phase)),
                    ToneWave.Sawtooth => 2.0 * ((step.Frequency * t) - Math.Floor((step.Frequency * t) + 0.5)),
                    _ => Math.Sin(phase),
                };

                double envelope;
                if (i < attackSamples)
                {
                    envelope = (double)i / attackSamples;
                }
                else if (i > durationSamples - fadeSamples)
                {
                    var remaining = durationSamples - i;
                    envelope = (double)remaining / fadeSamples;
                }
                else
                {
                    envelope = 1.0;
                }

                var value = (short)(raw * amplitude * Math.Clamp(envelope, 0.0, 1.0) * short.MaxValue);

                // 逐采样点直接写进缓冲区：这里每次渲染要跑几万个采样点，
                // 用 BitConverter.GetBytes 会每个点分配一个 2 字节数组
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(index * 2, 2), value);
            }
        }

        return buffer;
    }
}
