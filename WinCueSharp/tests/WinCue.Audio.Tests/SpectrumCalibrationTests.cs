// =============================================================================
// SpectrumCalibrationTests.cs — El RTA tiene que decir la VERDAD en dBFS
// =============================================================================
// El analizador no es decorativo: se usa para decidir dónde tocar la EQ. Si su
// escala miente, las decisiones que se toman mirándolo son erróneas — y encima
// el error no canta, porque la traza «se ve bonita» igual.
//
// Estos tests meten un tono de amplitud CONOCIDA y comprueban que sale donde
// tiene que salir. Es lo que se le pide a cualquier medidor: que esté calibrado.
// =============================================================================

using WinCue.Audio;

namespace WinCue.Audio.Tests;

public class SpectrumCalibrationTests
{
    private const int SR = 48_000;
    private const int FftSize = 2048;

    /// <summary>Tono senoidal mono de amplitud dada (1,0 = fondo de escala).</summary>
    private static AudioBuffer Tone(double freq, double amplitude, int frames)
    {
        var s = new float[frames];
        for (int i = 0; i < frames; i++)
            s[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * freq * i / SR));
        return new AudioBuffer { Samples = s, SampleRate = SR, Channels = 1 };
    }

    /// <summary>Reproduce lo suficiente para llenar varias ventanas de FFT y
    /// devuelve el espectro. La PRIMERA llamada a GetSpectrum solo enciende el
    /// análisis (los cues sin RTA a la vista no pagan la FFT), así que se pide
    /// antes de leer y se vuelve a pedir después.</summary>
    private static float[] SpectrumOf(double freq, double amplitude)
    {
        var p = new CueSampleProvider(1, Tone(freq, amplitude, FftSize * 8), volume: 1.0);
        p.GetSpectrum();   // enciende el analizador
        var block = new float[4096];
        for (int i = 0; i < 8; i++)
            p.Read(block, 0, block.Length);
        float[]? spec = p.GetSpectrum();
        Assert.NotNull(spec);
        return spec!;
    }

    [Fact]
    public void UnTonoAFondoDeEscala_SeLeeCercaDeCeroDbfs()
    {
        // ESTE es el test que importa. Con la normalización mal puesta —dividir
        // por N cuando la FFT de NAudio ya lo hace— un tono a fondo de escala
        // salía unos 66 dB por debajo: por debajo del suelo de la escala, o sea
        // una línea PLANA pegada al fondo pasara lo que pasara con el audio.
        float[] spec = SpectrumOf(1000.0, 1.0);
        float pico = spec.Max();
        Assert.True(pico > -6.0f,
            $"un tono a fondo de escala se está leyendo a {pico:0.0} dBFS");
        Assert.True(pico <= 0.5f,
            $"un tono a fondo de escala se está leyendo a {pico:0.0} dBFS (por encima de 0)");
    }

    [Fact]
    public void MediaEscala_SeLeeSeisDecibeliosPorDebajo()
    {
        // La mitad de amplitud son −6 dB. Comprueba que la escala no solo está
        // desplazada sino que además es PROPORCIONAL.
        float lleno = SpectrumOf(1000.0, 1.0).Max();
        float mitad = SpectrumOf(1000.0, 0.5).Max();
        Assert.Equal(-6.0, mitad - lleno, 1);
    }

    [Fact]
    public void ElTono_ApareceEnSuBanda_YNoEnLasDemas()
    {
        // Una traza plana también pasaría los tests de nivel si TODAS las bandas
        // subieran a la vez. Un tono tiene que destacar sobre sus vecinas.
        float[] spec = SpectrumOf(1000.0, 1.0);
        int pico = Array.IndexOf(spec, spec.Max());

        // Bandas log 20 Hz–20 kHz en 60 pasos: 1 kHz cae por el medio
        Assert.InRange(pico, 20, 45);

        // Lejos del tono el nivel tiene que ser MUCHO menor
        float lejos = spec[..(pico - 6)].Concat(spec[(pico + 7)..]).Max();
        Assert.True(spec[pico] - lejos > 20.0f,
            $"el tono solo destaca {spec[pico] - lejos:0.0} dB sobre el resto: "
            + "el espectro está plano");
    }

    [Fact]
    public void SilencioAbsoluto_SeVaAlSuelo()
    {
        float[] spec = SpectrumOf(1000.0, 0.0);
        Assert.True(spec.Max() < -80.0f,
            $"el silencio se lee a {spec.Max():0.0} dBFS");
    }
}
