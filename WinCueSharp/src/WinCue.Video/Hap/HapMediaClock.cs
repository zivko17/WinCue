// =============================================================================
// HapMediaClock.cs — Reloj de media del player HAP: audio como RELOJ MAESTRO
// con extrapolación monotónica, o Stopwatch propio si no hay audio
// =============================================================================
// SEMÁNTICA ELEGIDA (documentada también en tools/hap-lab/INFORME.md, paso 3):
//   · Mientras el reloj externo (elapsed del stream de audio del cue) entregue
//     valores COMPATIBLES con el local actual, ES el maestro: cada valor nuevo
//     re-ancla el reloj y entre valores (el audio publica por búfer, a saltos)
//     se extrapola con el monotónico. Si el salto contra la estimación supera
//     SnapSeconds se cuenta como RESYNC (para la traza).
//   · PROTECCIÓN CONTRA EL AUDIO ATRASADO (depuración julio 2026): el espejo
//     de audio decodifica el .mov entero a RAM antes de sonar — puede arrancar
//     SEGUNDOS (o minutos) tarde y entregar elapsed≈0 con el vídeo ya avanzado.
//     Re-anclar hacia atrás rebobinaba el vídeo, retrasaba el fin natural el
//     mismo tiempo (Dur negativa en la fila) y descuadraba las cadenas
//     AUTO_CONTINUE. Regla nueva: un externo más de MaxBackwardSnapSeconds POR
//     DETRÁS de la estimación se IGNORA (el reloj sigue en free-run monotónico
//     y el fin llega a su hora) hasta que su valor CONVERJA con el local
//     ([-SnapSeconds, ForwardAdoptSeconds]); entonces recupera el mando.
//     Retrocesos pequeños (≤ MaxBackwardSnapSeconds) siguen re-anclando: son
//     correcciones legítimas de sincronía (el audio es el maestro).
//   · Si el externo devuelve null (vídeo sin pista de audio, audio terminado
//     antes que el vídeo, espejo de audio caído): se CONGELA el último anclaje
//     y se sigue avanzando con el monotónico desde ahí — el vídeo nunca se
//     detiene por perder el audio.
//   · LOOP: el elapsed de audio se resetea en cada pasada. Solo es PASADA
//     NUEVA un salto atrás > LoopJumpSeconds con la pasada avanzada más de la
//     mitad Y que aterrice cerca del principio (e < LoopResetHeadSeconds y
//     < ¼ del segmento — un reinicio real cae a un búfer del 0); y solo si
//     QUEDAN pasadas (jamás en la última: un reset espurio del audio no debe
//     re-reproducir el final). Red de seguridad: si el audio está
//     congelado/ausente/ignorado, la pasada envuelve por tiempo al superar la
//     duración del segmento; un salto atrás del audio inmediatamente después
//     de un envolvido por tiempo se trata como resync (no como pasada doble).
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
    private const double LoopResetHeadSeconds = 1.0; // un reset real cae cerca de 0
    private const double WrapMarginSeconds = 0.25; // envolvido por tiempo (sin audio)
    private const double WrapGuardSeconds = 0.75; // ignora el salto de audio tras envolver
    private const double MaxBackwardSnapSeconds = 1.0; // retroceso máximo re-anclable
    private const double ForwardAdoptSeconds = 1.0;    // adopción hacia delante máxima

    private readonly double _segmentSeconds;
    private readonly int _totalPasses;            // int.MaxValue = loop infinito
    private readonly Func<double?>? _external;
    private readonly Func<double> _monotonic;

    private bool _started;
    private double _anchorLocal;                  // local en el instante del anclaje
    private double _anchorMono;                   // monotónico del anclaje
    private double? _lastExternal;
    private bool _ignoringExternal;               // audio atrasado: free-run hasta converger
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
        double? e0 = ReadExternal();
        _anchorLocal = e0 ?? 0.0;
        _anchorMono = _monotonic();
        _lastExternal = e0;
        // Sin audio en el arranque: cuando aparezca (decode tardío) llegará
        // POR DETRÁS del local — entrar ya en modo ignorar-hasta-converger
        _ignoringExternal = e0 is null;
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
            ConsumeExternal(e, mono);
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

    /// <summary>Aplica un valor del reloj externo: re-anclar, pasada nueva,
    /// o ignorarlo si viene demasiado atrasado (ver cabecera).</summary>
    private void ConsumeExternal(double e, double mono)
    {
        double est = _anchorLocal + (mono - _anchorMono);
        double diff = e - est;

        if (_ignoringExternal)
        {
            // Convergencia: readoptar el audio solo cuando su valor vuelva a
            // ser compatible con el local actual (nunca saltos atrás grandes)
            if (diff >= -SnapSeconds && diff <= ForwardAdoptSeconds)
            {
                _ignoringExternal = false;
                Anchor(e, mono);
            }
            return;
        }

        // ¿Reinicio del loop del audio = pasada nueva? Solo con la pasada
        // avanzada, aterrizando cerca de 0, con pasadas RESTANTES y sin haber
        // envuelto por tiempo justo antes (mismo límite visto dos veces).
        if (_lastExternal is double last && e < last - LoopJumpSeconds
            && last > _segmentSeconds * 0.5
            && e < Math.Min(LoopResetHeadSeconds, _segmentSeconds * 0.25)
            && _pass + 1 < _totalPasses
            && mono - _lastWrapMono > WrapGuardSeconds)
        {
            _pass++;
            Anchor(e, mono);
            return;
        }

        if (diff < -MaxBackwardSnapSeconds)
        {
            // Audio demasiado atrás (arrancó tarde o se reinició sin ser
            // loop): JAMÁS re-anclar tan atrás — free-run hasta converger
            _ignoringExternal = true;
            Resyncs++;
            return;
        }

        if (Math.Abs(diff) > SnapSeconds)
            Resyncs++;

        // El audio manda: re-anclar SIEMPRE que entregue valor nuevo (entre
        // valores iguales, la extrapolación monotónica rellena)
        if (_lastExternal != e)
            Anchor(e, mono);
        else
            _lastExternal = e;
    }

    private void Anchor(double e, double mono)
    {
        _anchorLocal = e;
        _anchorMono = mono;
        _lastExternal = e;
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
