// =============================================================================
// HapConvertService.cs — Conversión de vídeos a HAP .mov desde la app
// =============================================================================
// Alimenta el motor de vídeo propio (WinCue.Video.Hap): convierte el archivo de
// un cue de vídeo a HAP con la MISMA receta verificada del export portable
// (MediaImporter.HapConversionArgs) pero SIN bloquear la UI y con progreso real
// (ffmpeg -progress pipe:1 contra la duración de ffprobe).
//
// DISEÑO
//   · Cola SECUENCIAL: un solo ffmpeg a la vez (no saturar el disco en directo;
//     HAP multiplica el bitrate y la máquina puede estar reproduciendo).
//   · El ffmpeg real corre en segundo plano; TODOS los callbacks hacia el
//     controlador se remiten por el IScheduler (Schedule(0, …)) — en producción
//     eso es el dispatcher de UI, en tests el reloj virtual determinista.
//   · Destino junto al original: <nombre>.hap.mov. Si ya existe se REUTILIZA
//     (no se reconvierte): conversión idempotente entre sesiones.
//   · Cancelable: CancelAll() vacía la cola y mata el ffmpeg en curso (el
//     destino a medias se borra).
// =============================================================================

using System.Diagnostics;

namespace WinCue.Controller;

/// <summary>Resultado de una conversión: si falla, Error lleva el motivo y
/// LogFile la ruta del volcado de stderr de ffmpeg (si se pudo escribir).</summary>
public sealed record HapConversionResult(bool Ok, string Error = "", string LogFile = "");

/// <summary>Ejecuta UNA conversión src → dest reportando progreso (0..100,
/// desde cualquier hilo). Inyectable: los tests usan un runner falso.</summary>
public interface IHapConversionRunner
{
    Task<HapConversionResult> ConvertAsync(
        string src, string dest, Action<double> onProgress, CancellationToken ct);
}

/// <summary>Runner real: ffmpeg por subproceso (mismo patrón que
/// MediaImporter.ConvertToHap) + «-progress pipe:1» por stdout para el
/// progreso. El stderr se conserva en logs/ si la conversión falla.</summary>
public sealed class FfmpegHapConversionRunner : IHapConversionRunner
{
    public Task<HapConversionResult> ConvertAsync(
        string src, string dest, Action<double> onProgress, CancellationToken ct) =>
        Task.Run(() => Convert(src, dest, onProgress, ct), CancellationToken.None);

    private static HapConversionResult Convert(
        string src, string dest, Action<double> onProgress, CancellationToken ct)
    {
        double totalSeconds = MediaImporter.ProbeDurationSeconds(src);
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // -progress pipe:1 imprime bloques clave=valor por stdout cada ~0,5 s
            psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
            foreach (string arg in MediaImporter.HapConversionArgs(src, dest))
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null)
                return new(false, "no se pudo lanzar ffmpeg (¿está en el PATH?)");
            using var kill = ct.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
            });

            // stderr en tarea aparte: drenar SIEMPRE para no bloquear el pipe
            var stderrTask = Task.Run(proc.StandardError.ReadToEnd, CancellationToken.None);
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                // out_time_us / out_time_ms van en MICROsegundos (nombre
                // histórico de ffmpeg conservado por compatibilidad)
                if (totalSeconds <= 0.0)
                    continue;
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    !line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                    continue;
                if (long.TryParse(line["out_time_us=".Length..],
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out long us) && us > 0)
                    onProgress(Math.Clamp(us / 1e6 / totalSeconds * 100.0, 0.0, 100.0));
            }

            string stderr = stderrTask.GetAwaiter().GetResult();
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                TryDelete(dest);
                return new(false, "ffmpeg no terminó (timeout)");
            }
            if (ct.IsCancellationRequested)
            {
                TryDelete(dest);
                return new(false, "cancelada");
            }
            if (proc.ExitCode != 0 || !File.Exists(dest))
            {
                TryDelete(dest);
                return new(false, $"ffmpeg terminó con código {proc.ExitCode}",
                    DumpStderr(src, stderr));
            }
            onProgress(100.0);
            return new(true);
        }
        catch (Exception exc)   // sin ffmpeg / src ilegible: la cola debe seguir
        {
            TryDelete(dest);
            return new(false, exc.Message);
        }
    }

    /// <summary>Conserva el stderr de un ffmpeg fallido en logs/ (diagnóstico:
    /// códec de origen raro, dimensiones no múltiplo de 4, disco lleno…).</summary>
    private static string DumpStderr(string src, string stderr)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"hap-convert-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(file, $"Conversión a HAP fallida: {src}\r\n\r\n{stderr}\r\n");
            return file;
        }
        catch (Exception) { return ""; }   // sin log no se pierde el aviso de fallo
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}

// CA1001: la CancellationTokenSource se libera deterministamente en Finish()
// y CancelAll(); el servicio vive tanto como el controlador (no hay Dispose).
#pragma warning disable CA1001
/// <summary>Cola secuencial de conversiones a HAP para el controlador.</summary>
public sealed class HapConvertService
{
#pragma warning restore CA1001
    /// <summary>Trabajo de conversión de un cue: origen y destino absolutos.</summary>
    public sealed record Job(int CueId, string Source, string Dest);

    private readonly IHapConversionRunner _runner;
    private readonly IScheduler _scheduler;
    private readonly Action<Job, double> _onProgress;
    private readonly Action<Job, HapConversionResult, bool> _onCompleted;   // (job, resultado, reutilizado)

    private readonly Queue<Job> _queue = new();
    private readonly HashSet<int> _enqueuedCueIds = new();
    private Job? _current;
    private CancellationTokenSource? _cts;

    /// <param name="onProgress">Progreso 0..100 del trabajo en curso (llega ya
    /// remitido por el scheduler, es decir, en el hilo del controlador).</param>
    /// <param name="onCompleted">Fin de un trabajo (mismo hilo); el bool es
    /// «reutilizado»: el destino ya existía y no se reconvirtió.</param>
    public HapConvertService(
        IHapConversionRunner runner, IScheduler scheduler,
        Action<Job, double> onProgress,
        Action<Job, HapConversionResult, bool> onCompleted)
    {
        _runner = runner;
        _scheduler = scheduler;
        _onProgress = onProgress;
        _onCompleted = onCompleted;
    }

    /// <summary>Destino oficial de una conversión: junto al original, con la
    /// extensión doble «.hap.mov» (autoexplicativa y estable → reutilizable).</summary>
    public static string DestPathFor(string src) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(src)) ?? "",
        Path.GetFileNameWithoutExtension(src) + ".hap.mov");

    /// <summary>true si hay un trabajo en curso o en cola.</summary>
    public bool IsBusy => _current is not null || _queue.Count > 0;

    /// <summary>Trabajos pendientes (incluido el que está en curso).</summary>
    public int PendingCount => _queue.Count + (_current is null ? 0 : 1);

    /// <summary>Encola la conversión del cue. false si ese cue ya está en
    /// cola o en curso (no se duplica trabajo).</summary>
    public bool Enqueue(int cueId, string sourcePath)
    {
        if (!_enqueuedCueIds.Add(cueId))
            return false;
        string src = Path.GetFullPath(sourcePath);
        _queue.Enqueue(new Job(cueId, src, DestPathFor(src)));
        Pump();
        return true;
    }

    /// <summary>Vacía la cola y cancela el ffmpeg en curso (su fin llegará por
    /// onCompleted con Ok=false). Los encolados aún no empezados se descartan
    /// en silencio.</summary>
    public void CancelAll()
    {
        foreach (var job in _queue)
            _enqueuedCueIds.Remove(job.CueId);
        _queue.Clear();
        _cts?.Cancel();
    }

    /// <summary>Remite fn al hilo del controlador (dispatcher en producción,
    /// reloj virtual en tests) — el mismo canal que usan los motores.</summary>
    private void Post(Action fn) => _scheduler.Schedule(0, fn);

    private void Pump()
    {
        if (_current is not null || _queue.Count == 0)
            return;
        var job = _queue.Dequeue();
        _current = job;

        // Idempotencia: si el destino ya existe (conversión de otra sesión),
        // se reutiliza tal cual — reconvertir sería pagar minutos de ffmpeg
        // para producir el mismo archivo.
        if (File.Exists(job.Dest))
        {
            Post(() => Finish(job, new HapConversionResult(true), reused: true));
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _runner.ConvertAsync(job.Source, job.Dest,
                pct => Post(() => { if (_current == job) _onProgress(job, pct); }),
                cts.Token)
            .ContinueWith(t =>
            {
                var result = t.Status == TaskStatus.RanToCompletion
                    ? t.Result
                    : new HapConversionResult(false,
                        t.Exception?.GetBaseException().Message ?? "cancelada");
                Post(() => Finish(job, result, reused: false));
            },
            CancellationToken.None,
            // ExecuteSynchronously: con runners falsos (tests) la continuación
            // corre en el hilo que completa la tarea → determinista
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Finish(Job job, HapConversionResult result, bool reused)
    {
        _current = null;
        _cts?.Dispose();
        _cts = null;
        _enqueuedCueIds.Remove(job.CueId);
        _onCompleted(job, result, reused);
        Pump();
    }
}
