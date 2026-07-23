// =============================================================================
// HapMediaClock.cs — Reloj de media del player HAP: audio como RELOJ MAESTRO
// con extrapolación monotónica, o Stopwatch propio si no hay audio
// =============================================================================
// SEMÁNTICA ELEGIDA (documentada también en tools/hap-lab/INFORME.md, paso 3):
//   · Mientras el reloj externo (elapsed del stream de audio del cue) entregue
//     valores, ES el maestro: cada valor nuevo re-ancla el reloj y entre
//     valores (el audio publica por búfer, a saltos) se extrapola con el
//     monotónico. Si el salto contra la estimación supera SnapSeconds se
//     cuenta como RESYNC (para la traza).
//   · Si el externo devuelve null (vídeo sin pista de audio, audio terminado
//     antes que el vídeo, espejo de audio caído): se CONGELA el último anclaje
//     y se sigue avanzando con el monotónico desde ahí — el vídeo nunca se
//     detiene por perder el audio.
//   · LOOP: el elapsed de audio se resetea en cada pasada. Un salto hacia
//     atrás > LoopJumpSeconds con el reloj ya avanzado más de media pasada se
//     interpreta como nueva PASADA (el límite de pasada lo marca el AUDIO).
//     Red de seguridad: si el audio está congelado/ausente, la pasada envuelve
//     por tiempo al superar la duración del segmento; un salto atrás del audio
//     inmediatamente después de un envolvido por tiempo se trata como resync
//     (no como pasada doble).
// Clase pura (sin hilos ni D3D): el monotónico es inyectable para testearla
// en headless. Los métodos se llaman SOLO desde el hilo de presentación.
// =============================================================================

using System.Diagnostics;

namespace WinCue.Video.Hap;

/// <summary>Lectura del reloj: pasada actual (0-based) y segundos dentro de la
/// pasada (pueden superar la duración del segmento en la última pasada).</summary>
public readonly record struct HapClockSample(int Pass, double LocalSeconds);

public sealed class HapMediaClock
{
    private const double SnapSeconds = 0.25;      // resync duro contra el audio
    private const double LoopJumpSeconds = 0.5;   // salto atrás = nueva pasada
    private const double WrapMarginSeconds = 0.25; // envolvido por tiempo (sin audio)
    private const double WrapGuardSeconds = 0.75; // ignora el salto de audio tras envolver

    private readonly double _segmentSeconds;
    private readonly int _totalPasses;            // int.MaxValue = loop infinito
    private readonly Func<double?>? _external;
    private readonly Func<double> _monotonic;

    private bool _started;
    private double _anchorLocal;                  // local en el instante del anclaje
    private double _anchorMono;                   // monotónico del anclaje
    private double? _lastExternal;
    private int _pass;
    private double _lastWrapMono = double.NegativeInfinity;

    private bool _paused;
    private HapClockSample _frozen;

    /// <summary>Resyncs duros contra el audio (para la traza de diagnóstico).</summary>
    public int Resyncs { get; private set; }

    /// <param name="segmentSeconds">Duración de UNA pasada del segmento recortado.</param>
    /// <param name="totalPasses">Pasadas totales (int.MaxValue = loop infinito).</param>
    /// <param name="external">Reloj externo (elapsed del audio del cue, o null).</param>
    /// <param name="monotonic">Segundos monotónicos (tests; default Stopwatch).</param>
    public HapMediaClock(double segmentSeconds, int totalPasses,
        Func<double?>? external = null, Func<double>? monotonic = null)
    {
        _segmentSeconds = Math.Max(0.001, segmentSeconds);
        _totalPasses = Math.Max(1, totalPasses);
        _external = external;
        long t0 = Stopwatch.GetTimestamp();
        _monotonic = monotonic ??
            (() => (double)(Stopwatch.GetTimestamp() - t0) / Stopwatch.Frequency);
    }

    /// <summary>Arranca el reloj (llamar cuando el primer frame ya se pintó).</summary>
    public void Start()
    {
        _anchorLocal = ReadExternal() ?? 0.0;
        _anchorMono = _monotonic();
        _started = true;
    }

    private double? ReadExternal()
    {
        try { return _external?.Invoke(); }
        catch (Exception) { return null; }   // el reloj jamás tira el hilo de vídeo
    }

    /// <summary>Instante de media actual. Monotónico por pasada salvo resyncs.</summary>
    public HapClockSample Now()
    {
        if (!_started)
            return new HapClockSample(0, 0.0);
        if (_paused)
            return _frozen;

        double mono = _monotonic();
        double? ext = ReadExternal();

        if (ext is double e)
        {
            double est = _anchorLocal + (mono - _anchorMono);
            if (_lastExternal is double last && e < last - LoopJumpSeconds
                && last > _segmentSeconds * 0.5)
            {
                // Salto atrás grande con la pasada avanzada: el audio ha vuelto
                // a empezar. Si ACABAMOS de envolver por tiempo, es el mismo
                // límite de pasada visto dos veces → solo re-anclar.
                if (mono - _lastWrapMono > WrapGuardSeconds)
                    _pass++;
            }
            else if (Math.Abs(est - e) > SnapSeconds)
            {
                Resyncs++;
            }

            // El audio manda: re-anclar SIEMPRE que entregue valor (entre
            // valores iguales, la extrapolación monotónica rellena)
            if (_lastExternal != e)
            {
                _anchorLocal = e;
                _anchorMono = mono;
            }
            _lastExternal = e;
        }
        // ext null → anclaje congelado: el monotónico sigue avanzando solo

        double local = Math.Max(0.0, _anchorLocal + (mono - _anchorMono));

        // Red de seguridad sin señal de audio: envolver la pasada por tiempo
        while (local >= _segmentSeconds + WrapMarginSeconds && _pass + 1 < _totalPasses)
        {
            _pass++;
            _anchorLocal -= _segmentSeconds;
            local -= _segmentSeconds;
            _lastWrapMono = mono;
            // El elapsed del audio ya no es comparable hasta su propio reset
            _lastExternal = null;
        }

        if (_pass >= _totalPasses)
            _pass = _totalPasses - 1;
        return new HapClockSample(_pass, local);
    }

    /// <summary>Congela el reloj (pausa del cue). El audio se pausa aparte.</summary>
    public void Pause()
    {
        if (_paused || !_started)
            return;
        _frozen = Now();
        _paused = true;
    }

    /// <summary>Reanuda desde el instante congelado, re-anclando el monotónico.</summary>
    public void Resume()
    {
        if (!_paused)
            return;
        _paused = false;
        _anchorLocal = _frozen.LocalSeconds;
        _anchorMono = _monotonic();
        _pass = _frozen.Pass;
        _lastExternal = null;   // el próximo valor del audio re-ancla
    }
}
