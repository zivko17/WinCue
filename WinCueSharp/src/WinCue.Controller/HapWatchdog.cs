// =============================================================================
// HapWatchdog.cs — Vigía de salud del motor HAP durante el soak (auditoría 04)
// =============================================================================
// Se activa con WINCUE_STRESS_WATCH=1. En cada tick del StressRunner revisa la
// MISMA instantánea que recibe la vista (SnapshotActiveCues → ActiveCueInfo) y
// REGISTRA A GRITOS en logs/stress-watch-*.log (o WINCUE_STRESS_WATCH_LOG)
// cualquier anomalía que JAMÁS debería pasar en una cadena HAP sana:
//
//   (a) REMAINING NEGATIVO en un cue activo (el bug de la «Dur negativa»: el
//       reloj re-anclado atrás por el audio espejo atrasado). El panel clampa
//       a 0, así que el vigía lo detecta comparando el elapsed del MOTOR con la
//       duración DECLARADA del cue en el show.
//   (b) CUE COLGADO: un cue lleva sonando más que su duración + margen (no
//       termina — fin natural que no llega, cadena AUTO_CONTINUE descuadrada).
//   (c) CADENA ESTANCADA: N ticks sin que cambie el conjunto de cues activos NI
//       progrese ningún elapsed (cuelgue del AUTO_CONTINUE).
//   (d) FUGA: crecimiento MONÓTONO del working set / handles vuelta a vuelta
//       entre vueltas idénticas (el audio espejo HAP decodifica el .mov a RAM:
//       debe liberarse al terminar el cue).
//
// El soak es AUTODIAGNÓSTICO: si algo va mal queda en el log sin que nadie mire
// la pantalla. Cuenta las anomalías y, al cerrar, imprime un resumen en stdout.
// =============================================================================

using System.Diagnostics;
using System.Globalization;
using System.IO;
using WinCue.Core;

namespace WinCue.Controller;

public sealed class HapWatchdog
{
    // Umbrales de las anomalías (holgados para no gritar por jitter normal).
    private const double NegativeRemainingSeconds = 0.30; // (a) remaining < -0,30 s
    private const double HangMarginSeconds = 1.50;        // (b) elapsed > dur + 1,5 s
    private const double HangMarginFraction = 0.25;       // (b) o dur·1,25
    private const int StallTicks = 30;                    // (c) 30 × 500 ms = 15 s
    private const int LeakWindowLoops = 6;                // (d) ventana de vueltas
    private const double LeakWorkingSetMb = 60.0;         // (d) crecimiento total
    private const int LeakHandles = 400;                  // (d) crecimiento de handles
    // El working set del motor HAP RAMPA de forma monótona durante el arranque
    // (creación perezosa de los pipelines del pool por spec, decode del .mov a
    // RAM del audio espejo, cachés JIT) y no llega a régimen hasta ~10 vueltas;
    // un warmup corto metía la ventana de fuga DENTRO de esa rampa y gritaba
    // fuga falsa. El soak muestra que a partir de la vuelta ~10 el working set
    // oscila (sierra por el decode 4K) sin tendencia — se ignoran esas vueltas.
    private const int LeakWarmupLoops = 12;               // ignora arranque (rampa de cachés)

    private readonly Func<int, double> _declaredDuration;
    private readonly string _logPath;
    private readonly Stopwatch _wall = Stopwatch.StartNew();

    // Estado por cue activo (para no gritar en cada tick lo mismo)
    private sealed class CueWatch
    {
        public double MaxElapsed;
        public bool HangReported;
        public bool NegReported;
    }
    private readonly Dictionary<int, CueWatch> _watch = new();

    // Detección de estancamiento (c)
    private string _lastSetSig = "";
    private double _lastProgress = -1.0;
    private int _stallTicks;
    private bool _stallReported;

    // Detección de fuga (d): working set / handles al cierre de cada vuelta
    private readonly List<(int Loop, double Ws, int Handles)> _loopMem = new();

    // Contadores del resumen final
    private int _negCount, _hangCount, _stallCount, _leakCount;

    /// <param name="declaredDuration">Duración declarada del cue en el show (0 =
    /// loop/natural: sin chequeo de cuenta).</param>
    /// <param name="logPath">Ruta del log; null = WINCUE_STRESS_WATCH_LOG o el
    /// default logs/stress-watch-*.log (los tests inyectan una ruta temporal).</param>
    public HapWatchdog(Func<int, double> declaredDuration, string? logPath = null)
    {
        _declaredDuration = declaredDuration;
        string? env = logPath ?? Environment.GetEnvironmentVariable("WINCUE_STRESS_WATCH_LOG");
        _logPath = env is { Length: > 0 }
            ? env
            : Path.Combine(AppContext.BaseDirectory, "logs",
                $"stress-watch-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        Write($"=== WATCHDOG HAP arrancado {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    }

    /// <summary>Revisa la instantánea de cues activos (llamar cada tick del
    /// runner). rows = misma info que la vista; loop = vuelta en curso.</summary>
    public void Tick(IReadOnlyList<ActiveCueInfo> rows, int loop)
    {
        var present = new HashSet<int>();
        double progress = 0.0;

        foreach (var r in rows)
        {
            present.Add(r.Id);
            progress += r.Elapsed;

            // Solo interesan los cues de VÍDEO (el motor HAP) con acción ya en
            // marcha: en pre_wait el elapsed pertenece a otra fase.
            if (r.Type != CueType.Video || r.PreWaitRemaining is not null)
                continue;

            var w = _watch.TryGetValue(r.Id, out var e) ? e : (_watch[r.Id] = new CueWatch());
            if (r.Elapsed > w.MaxElapsed)
                w.MaxElapsed = r.Elapsed;

            double dur = _declaredDuration(r.Id);
            if (dur <= 0.0)
                continue;   // loop / duración natural desconocida: sin chequeo

            // (a) REMAINING NEGATIVO: el panel clampa remaining a 0, así que lo
            // deducimos del elapsed real del motor contra la duración declarada.
            double rawRemaining = dur - r.Elapsed;
            if (rawRemaining < -NegativeRemainingSeconds && !w.NegReported)
            {
                w.NegReported = true;
                _negCount++;
                Grito($"(a) REMAINING NEGATIVO cue {r.Id} «{r.Name}»: " +
                    $"elapsed={r.Elapsed:F2}s dur={dur:F2}s → remaining={rawRemaining:F2}s " +
                    $"(vuelta {loop})");
            }

            // (b) CUE COLGADO: sigue activo mucho después de su duración.
            double margin = Math.Max(HangMarginSeconds, dur * HangMarginFraction);
            if (r.Elapsed > dur + margin && !w.HangReported)
            {
                w.HangReported = true;
                _hangCount++;
                Grito($"(b) CUE COLGADO cue {r.Id} «{r.Name}»: elapsed={r.Elapsed:F2}s " +
                    $"> dur={dur:F2}s + margen={margin:F2}s (no termina — vuelta {loop})");
            }
        }

        // Purgar el estado de los cues que ya no están activos (evita falsos
        // «colgado» en la siguiente vuelta y libera memoria del propio vigía).
        foreach (int gone in _watch.Keys.Where(id => !present.Contains(id)).ToList())
            _watch.Remove(gone);

        // (c) CADENA ESTANCADA: mismo conjunto de cues y CERO progreso N ticks.
        string sig = string.Join(",", present.OrderBy(x => x));
        double quantProgress = Math.Round(progress, 1);
        if (sig == _lastSetSig && Math.Abs(quantProgress - _lastProgress) < 0.05 && sig.Length > 0)
        {
            if (++_stallTicks == StallTicks && !_stallReported)
            {
                _stallReported = true;
                _stallCount++;
                Grito($"(c) CADENA ESTANCADA: {_stallTicks} ticks sin cambio de " +
                    $"activos [{sig}] ni progreso (elapsed={quantProgress:F1}s, vuelta {loop})");
            }
        }
        else
        {
            _stallTicks = 0;
            _stallReported = false;
        }
        _lastSetSig = sig;
        _lastProgress = quantProgress;
    }

    /// <summary>Se llama al cerrar cada vuelta del bucle: registra la memoria
    /// para el análisis de fuga vuelta a vuelta (d).</summary>
    public void OnLoopClosed(int loop, double workingSetMb, int handles)
    {
        _loopMem.Add((loop, workingSetMb, handles));
        // Reiniciar el estado por cue: cada vuelta es idéntica; un «colgado»
        // detectado en la vuelta previa no debe silenciar la siguiente.
        _watch.Clear();

        if (_loopMem.Count < LeakWarmupLoops + LeakWindowLoops)
            return;

        // Ventana de las últimas LeakWindowLoops vueltas (tras el warmup):
        // ¿crecimiento estrictamente monótono y por encima del umbral?
        var win = _loopMem.Skip(_loopMem.Count - LeakWindowLoops).ToList();
        bool wsMono = true, hMono = true;
        for (int i = 1; i < win.Count; i++)
        {
            if (win[i].Ws <= win[i - 1].Ws) wsMono = false;
            if (win[i].Handles <= win[i - 1].Handles) hMono = false;
        }
        double wsGrow = win[^1].Ws - win[0].Ws;
        int hGrow = win[^1].Handles - win[0].Handles;

        if (wsMono && wsGrow > LeakWorkingSetMb)
        {
            _leakCount++;
            Grito($"(d) FUGA CANDIDATA (working set): +{wsGrow:F1} MB monótono en " +
                $"{LeakWindowLoops} vueltas idénticas [{win[0].Ws:F0}→{win[^1].Ws:F0} MB] " +
                $"(vueltas {win[0].Loop}-{win[^1].Loop})");
        }
        if (hMono && hGrow > LeakHandles)
        {
            _leakCount++;
            Grito($"(d) FUGA CANDIDATA (handles): +{hGrow} monótono en " +
                $"{LeakWindowLoops} vueltas [{win[0].Handles}→{win[^1].Handles}] " +
                $"(vueltas {win[0].Loop}-{win[^1].Loop})");
        }
    }

    /// <summary>Resumen final: al log y a stdout. Devuelve el total de anomalías
    /// (0 = soak limpio).</summary>
    public int Summarize()
    {
        int total = _negCount + _hangCount + _stallCount + _leakCount;
        string mem = _loopMem.Count > 0
            ? $"working set vuelta 1={_loopMem[0].Ws:F0} MB → última={_loopMem[^1].Ws:F0} MB; " +
              $"handles {_loopMem[0].Handles}→{_loopMem[^1].Handles}"
            : "sin muestras de memoria por vuelta";
        var lines = new[]
        {
            "",
            "======================= RESUMEN DEL WATCHDOG HAP =======================",
            $"  Duración vigilada : {_wall.Elapsed:hh\\:mm\\:ss}",
            $"  Vueltas medidas   : {_loopMem.Count}",
            $"  (a) remaining < 0 : {_negCount}",
            $"  (b) cues colgados : {_hangCount}",
            $"  (c) cadena estanc.: {_stallCount}",
            $"  (d) fugas candid. : {_leakCount}",
            $"  Memoria           : {mem}",
            $"  TOTAL ANOMALÍAS   : {total}   {(total == 0 ? "→ SOAK LIMPIO ✔" : "→ REVISAR EL LOG ✘")}",
            "========================================================================",
        };
        foreach (string l in lines)
        {
            WriteRaw(l);
            Console.WriteLine(l);
        }
        return total;
    }

    private void Grito(string msg) => Write("ANOMALÍA " + msg);

    private void Write(string msg) =>
        WriteRaw(string.Create(CultureInfo.InvariantCulture,
            $"[{_wall.Elapsed:hh\\:mm\\:ss}] {msg}"));

    private void WriteRaw(string line)
    {
        try { File.AppendAllText(_logPath, line + Environment.NewLine); }
        catch (IOException) { /* el vigía nunca debe tirar el soak */ }
    }
}
