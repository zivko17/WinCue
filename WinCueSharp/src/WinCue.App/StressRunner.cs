// =============================================================================
// StressRunner.cs — Modo de estrés desatendido (auditoría 04)
// =============================================================================
// Arranca con `--stress <show.json> [--stress-loops N]`: reproduce el show en
// bucle (GO al principio de cada vuelta; la vuelta debe estar encadenada con
// AUTO_CONTINUE) y registra en CSV cada 30 s las métricas de proceso que los
// perfiladores externos correlacionan: memoria gestionada, working set,
// memoria privada, handles, GCs por generación y cues disparados.
//
// El CSV va a logs/stress-<fecha>.csv (o WINCUE_STRESS_CSV). Con N vueltas
// cumplidas la app se cierra sola con exit 0 (scripts/stress.ps1 lo espera).
// =============================================================================

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinCue.Controller;
using WinCue.Core;

namespace WinCue.App;

public sealed class StressRunner
{
    private const int CsvPeriodSeconds = 30;
    private const int IdleTicksParaVuelta = 6;    // 6 × 500 ms = 3 s sin nada sonando

    private readonly MainController _controller;
    private readonly CueModel _model;
    private readonly int _maxLoops;               // 0 = infinito (parar desde fuera)
    private readonly string _csvPath;
    private readonly Process _proc = Process.GetCurrentProcess();
    private readonly long _t0 = Stopwatch.GetTimestamp();

    private readonly DispatcherTimer _drive;      // relanza la vuelta
    private readonly DispatcherTimer _sample;     // muestrea el CSV
    private readonly HashSet<int> _seenThisLoop = new();
    private readonly HapWatchdog? _watch;         // WINCUE_STRESS_WATCH=1

    private int _loops;
    private int _cuesFired;
    private int _idleTicks;

    public StressRunner(MainController controller, CueModel model, int maxLoops)
    {
        _controller = controller;
        _model = model;
        _maxLoops = maxLoops;

        // Watchdog de salud HAP (diagnóstico autónomo del soak): consulta la
        // duración DECLARADA de cada cue en el show para detectar cuentas
        // negativas y cues colgados sin tocar el motor. Un cue en LOOP vive
        // legítimamente más que su duración declarada (que la ruta híbrida
        // NECESITA conocer para la elegibilidad): se reporta 0 = sin chequeo,
        // la misma convención del watchdog para duración natural.
        if (Environment.GetEnvironmentVariable("WINCUE_STRESS_WATCH") == "1")
            _watch = new HapWatchdog(id =>
                model.GetById(id) is { Loop: false } c ? c.Duration : 0.0);

        string? env = Environment.GetEnvironmentVariable("WINCUE_STRESS_CSV");
        _csvPath = env is { Length: > 0 }
            ? env
            : Path.Combine(AppContext.BaseDirectory, "logs",
                $"stress-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(_csvPath)!);
        File.WriteAllText(_csvPath,
            "timestamp,elapsed_s,loops,cues_fired,managed_mb,working_set_mb," +
            "private_mb,handles,gen0,gen1,gen2\r\n");

        _drive = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _drive.Tick += (_, _) => DriveTick();
        _sample = new DispatcherTimer { Interval = TimeSpan.FromSeconds(CsvPeriodSeconds) };
        _sample.Tick += (_, _) => WriteSample();
    }

    public void Start()
    {
        _controller.OnSetPlayhead(0);
        _controller.OnGoPressed();
        WriteSample();                            // línea base t=0
        _drive.Start();
        _sample.Start();
    }

    /// <summary>Vigila la actividad: cuenta cues nuevos de la vuelta y, tras
    /// 3 s sin nada sonando, cierra la vuelta y relanza (o termina).</summary>
    private void DriveTick()
    {
        // Vigía de salud (mismo tick): revisa la instantánea que ve la vista.
        _watch?.Tick(_controller.SnapshotActiveCues(out _), _loops);

        var running = _controller.RunningCueIds;
        if (running.Count > 0)
        {
            _idleTicks = 0;
            foreach (int id in running)
                if (_seenThisLoop.Add(id))
                    _cuesFired++;
            return;
        }

        if (++_idleTicks < IdleTicksParaVuelta)
            return;
        _idleTicks = 0;

        _loops++;
        _seenThisLoop.Clear();
        // Cierre de vuelta: registrar memoria para el análisis de fuga (d).
        if (_watch is not null)
        {
            _proc.Refresh();
            _watch.OnLoopClosed(_loops, _proc.WorkingSet64 / 1048576.0, _proc.HandleCount);
        }
        if (_maxLoops > 0 && _loops >= _maxLoops)
        {
            WriteSample();                        // última foto antes de salir
            _watch?.Summarize();                  // resumen a stdout + log
            _drive.Stop();
            _sample.Stop();
            Application.Current.Shutdown(0);
            return;
        }
        _controller.OnSetPlayhead(0);
        _controller.OnGoPressed();
    }

    private void WriteSample()
    {
        try
        {
            _proc.Refresh();
            double elapsed = Stopwatch.GetElapsedTime(_t0).TotalSeconds;
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{elapsed:0},{_loops},{_cuesFired}," +
                $"{GC.GetTotalMemory(forceFullCollection: false) / 1048576.0:0.0}," +
                $"{_proc.WorkingSet64 / 1048576.0:0.0}," +
                $"{_proc.PrivateMemorySize64 / 1048576.0:0.0}," +
                $"{_proc.HandleCount}," +
                $"{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}\r\n");
            File.AppendAllText(_csvPath, line);
        }
        catch (IOException) { /* el muestreo nunca debe tirar el show */ }
    }
}
