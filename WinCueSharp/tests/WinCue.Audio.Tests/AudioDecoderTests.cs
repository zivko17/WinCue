// =============================================================================
// AudioDecoderTests.cs — Decodificación nativa (NAudio) y vía FFmpeg
// =============================================================================

using NAudio.Wave;
using WinCue.Audio;

namespace WinCue.Audio.Tests;

public class AudioDecoderTests : IDisposable
{
    private readonly string _dir;

    public AudioDecoderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wincue_audio_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Genera un WAV PCM 16-bit con un seno de 440 Hz.</summary>
    private string MakeWav(string name, int sampleRate = 44100, int channels = 2,
                           double seconds = 0.25, double amplitude = 0.8)
    {
        string path = Path.Combine(_dir, name);
        var format = new WaveFormat(sampleRate, 16, channels);
        using var writer = new WaveFileWriter(path, format);
        int frames = (int)(sampleRate * seconds);
        var frame = new float[channels];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            for (int c = 0; c < channels; c++) frame[c] = v;
            writer.WriteSamples(frame, 0, channels);
        }
        return path;
    }

    [Fact]
    public void DecodeWav_ReturnsNormalizedFloat32()
    {
        string path = MakeWav("test.wav", sampleRate: 44100, channels: 2, seconds: 0.25);
        var buf = AudioDecoder.Decode(path);

        Assert.Equal(44100, buf.SampleRate);
        Assert.Equal(2, buf.Channels);
        Assert.Equal((int)(44100 * 0.25), buf.FrameCount);
        Assert.Equal(0.25, buf.DurationSeconds, 3);

        // Amplitud pico ≈ 0.8, y todo dentro de −1..1
        float peak = buf.Samples.Max(MathF.Abs);
        Assert.InRange(peak, 0.75f, 0.85f);
    }

    [Fact]
    public void DecodeMonoWav_SingleChannel()
    {
        string path = MakeWav("mono.wav", channels: 1, seconds: 0.1);
        var buf = AudioDecoder.Decode(path);
        Assert.Equal(1, buf.Channels);
        Assert.Equal((int)(44100 * 0.1), buf.FrameCount);
    }

    [Fact]
    public void MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(
            () => AudioDecoder.Decode(Path.Combine(_dir, "no_existe.wav")));
    }

    [Fact]
    public void CorruptFile_ThrowsDecoderException()
    {
        string path = Path.Combine(_dir, "corrupto.wav");
        File.WriteAllText(path, "esto no es un wav");
        Assert.ThrowsAny<AudioDecoderException>(() => AudioDecoder.Decode(path));
    }

    [Fact]
    public void SupportedFormats_NativeAlwaysAvailable()
    {
        var formats = AudioDecoder.GetSupportedFormats();
        Assert.True(formats[".wav"]);
        Assert.True(formats[".mp3"]);
        Assert.True(formats[".flac"]);
        Assert.Equal(AudioDecoder.IsFFmpegAvailable, formats[".ogg"]);
    }

    [Fact]
    public void DecodeOgg_ViaFFmpeg_MatchesSourceShape()
    {
        if (!AudioDecoder.IsFFmpegAvailable)
            return;   // sin FFmpeg no hay ruta que probar

        string wav = MakeWav("src.wav", sampleRate: 48000, channels: 2, seconds: 0.25);
        string ogg = Path.Combine(_dir, "conv.ogg");
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(wav);
        psi.ArgumentList.Add(ogg);
        using (var proc = System.Diagnostics.Process.Start(psi)!)
        {
            proc.WaitForExit(30_000);
            Assert.Equal(0, proc.ExitCode);
        }

        var buf = AudioDecoder.Decode(ogg);
        Assert.Equal(48000, buf.SampleRate);
        Assert.Equal(2, buf.Channels);
        // Vorbis añade padding en los bordes: tolerancia del 10 %
        Assert.InRange(buf.FrameCount, (int)(48000 * 0.25 * 0.9), (int)(48000 * 0.25 * 1.1));
        float peak = buf.Samples.Max(MathF.Abs);
        Assert.InRange(peak, 0.6f, 1.0f);
    }
}
