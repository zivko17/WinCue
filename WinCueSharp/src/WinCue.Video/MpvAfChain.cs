// =============================================================================
// MpvAfChain.cs — AudioEqParams → cadena de filtros de audio de mpv (lavfi)
// =============================================================================
//
// PARIDAD CON EL MOTOR DE AUDIO
//   Los cues de vídeo no pasan por NAudio (mpv → dispositivo directo), pero
//   mpv trae los filtros de ffmpeg: la MISMA AudioEqParams del modelo se
//   traduce a una cadena lavfi con semántica idéntica a EqChain:
//     · GainDb   → volume=XdB       (SIEMPRE que ≠ 0, pre-EQ, como EqChain)
//     · HpfOn    → highpass=f=X     (independiente de EqEnabled)
//     · LpfOn    → lowpass=f=X
//     · Bands    → equalizer=…      (solo con EqEnabled y |g| > 0.01)
//     · Comp     → acompressor=…    (threshold/makeup en LINEAL: ffmpeg no
//                                    acepta sufijo dB en esas opciones)
//   Todos son filtros LGPL de libavfilter: válidos en la build limpia.
//
// LO QUE NO DA
//   RTA, GR y VU por cue (mpv no expone muestras ni la GR del filtro). La
//   curva de EQ del editor sí se dibuja: es matemática de los parámetros.
// =============================================================================

using System.Globalization;
using WinCue.Core;

namespace WinCue.Video;

public static class MpvAfChain
{
    /// <summary>Valor para la propiedad "af" de mpv. "" = sin procesado
    /// (limpia la cadena). InvariantCulture: mpv parsea con punto decimal.</summary>
    public static string Build(AudioEqParams? eq)
    {
        if (eq is null)
            return "";

        var filters = new List<string>();
        var inv = CultureInfo.InvariantCulture;

        if (Math.Abs(eq.GainDb) > 0.01)
            filters.Add(string.Format(inv, "volume={0:0.##}dB", eq.GainDb));

        if (eq.HpfOn)
            filters.Add(string.Format(inv, "highpass=f={0:0.#}", eq.HpfFreq));
        if (eq.LpfOn)
            filters.Add(string.Format(inv, "lowpass=f={0:0.#}", eq.LpfFreq));

        if (eq.EqEnabled && eq.Bands is not null)
            foreach (var b in eq.Bands)
                if (Math.Abs(b.GainDb) > 0.01)
                    filters.Add(string.Format(inv,
                        "equalizer=f={0:0.#}:t=q:w={1:0.##}:g={2:0.##}",
                        b.Freq, Math.Max(0.05, b.Q), b.GainDb));

        if (eq.Comp is { Enabled: true } c)
        {
            // acompressor: threshold lineal [0.000977..1], makeup lineal [1..64]
            double th = Math.Clamp(Math.Pow(10.0, c.ThresholdDb / 20.0), 0.000977, 1.0);
            double mk = Math.Clamp(Math.Pow(10.0, c.MakeupDb / 20.0), 1.0, 64.0);
            filters.Add(string.Format(inv,
                "acompressor=threshold={0:0.######}:ratio={1:0.##}:attack={2:0.#}:release={3:0.#}:makeup={4:0.####}",
                th, Math.Clamp(c.Ratio, 1.0, 20.0),
                Math.Max(0.01, c.AttackMs), Math.Max(0.01, c.ReleaseMs), mk));
        }

        return filters.Count == 0 ? "" : $"lavfi=[{string.Join(",", filters)}]";
    }
}
