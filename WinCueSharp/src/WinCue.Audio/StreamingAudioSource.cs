// =============================================================================
// StreamingAudioSource.cs — Fuente de audio en STREAMING (ffmpeg → ring acotado)
// =============================================================================
// PASO 4 DEL MOTOR. El AUDIO ESPEJO de los cues de vídeo (motor HAP propio y
// ruta híbrida mpv+PLL) deja de decodificar el archivo ENTERO a RAM: un
// subproceso ffmpeg decodifica a f32le por pipe y un hilo lector llena un
// RING ACOTADO (~3 s de audio). Consecuencias:
//   · arranque en ~centenas de ms (lo que tarda ffmpeg en emitir el primer
//     bloque), no en decodificar el archivo entero;
//   · RAM constante e independiente de la duración → desaparece el límite
//     video_audio_max_minutes de la ruta híbrida;
//   · backpressure natural: si el ring está lleno el lector espera y ffmpeg
//     se frena solo por el pipe (nunca hay más de ~2 pasadas × ring en RAM).
//
// COMANDO (el mismo patrón de subproceso que AudioDecoder.DecodeFFmpeg):
//   ffmpeg -hide_banner -v error [-ss trimStart] -i src [-t dur]
//          -vn -sn -dn -f f32le -acodec pcm_f32le -ac 2 -ar {mixRate} pipe:1
// La salida YA está en el formato del mixer (estéreo, mixRate): el engine no
// necesita resamplear (los espejos van SIEMPRE a rate 1.0).
//
// PASADAS (loop / play_count): cada pasada es UN subproceso idéntico. El
// provider avisa con ~4 s de antelación (PrepareNextPass → pre-spawn con su
// ring llenándose en paralelo) y conmuta en BeginNextPass: el empalme lee del
// ring ya lleno de la pasada siguiente — sin hueco. Se eligió relanzar en vez
// de -stream_loop porque cada pasada re-decodifica EXACTAMENTE los mismos
// samples (mismo comando determinista) y el elapsed por pasada (el reloj del
// vídeo) se resetea de forma natural al conmutar.
//
// HILO RT: ReadFrames jamás bloquea (ring SPSC con índices Volatile). Ring
// vacío con proceso vivo = UNDERRUN → devuelve 0 con PassEnded=false y el
// provider emite silencio sin avanzar. Fin real: ffmpeg sale (exit 0) y el
// ring se drena → PassEnded=true.
//
// MUERTE DEL PROCESO: exit ≠ 0 sin Dispose → evento FatalError (stderr en el
// mensaje); el engine lo eleva a ErrorOccurred y el controlador aplica su red
// de seguridad (RestoreInternalAudio: el cue recupera el audio por mpv).
// Dispose SIEMPRE mata los subprocesos (cero ffmpeg zombis).
// =============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace WinCue.Audio;

/// <summary>El spawn del streaming falló (sin ffmpeg, archivo sin pista de
/// audio, ffprobe ilegible…). El engine cae a la ruta RAM clásica.</summary>
public sealed class StreamingSpawnException : Exception
{
    public StreamingSpawnException(string message) : base(message) { }
    public StreamingSpawnException(string message, Exception inner) : base(message, inner) { }
}

public sealed class StreamingAudioSource : ICueAudioSource
{
    private const int OutChannels = 2;          // el formato del mixer es estéreo

    private readonly string _filePath;
    private readonly int _sampleRate;           // mixRate: ffmpeg ya lo entrega así
    private readonly double _regionStart;       // trim in en segundos
    private readonly double _regionSeconds;     // duración de la región; <0 = desconocida
    private readonly int _ringCapacity;         // samples (múltiplo de OutChannels)

    // CA2213: Dispose SÍ los libera (vía Interlocked.Exchange dentro del
    // lock, para no pisarse con BeginNextPass del hilo RT); el analizador no
    // reconoce el patrón.
#pragma warning disable CA2213
    private Pass? _current;
    private Pass? _next;
#pragma warning restore CA2213
    private volatile bool _disposed;
    private volatile bool _fatal;               // spawn de pasada fallido → terminar

    private readonly object _spawnLock = new();
    private readonly List<int> _spawnedPids = new();

    /// <summary>El subproceso murió MAL (exit ≠ 0 sin Dispose) o el spawn de
    /// una pasada siguiente falló. Llega desde un hilo de fondo.</summary>
    public event Action<string>? FatalError;

    /// <summary>PIDs de todos los ffmpeg lanzados por esta fuente (tests:
    /// verificación de cero zombis tras Dispose).</summary>
    public IReadOnlyList<int> SpawnedProcessIds
    {
        get { lock (_spawnedPids) return _spawnedPids.ToArray(); }
    }

    /// <summary>PID del ffmpeg de la pasada actual (tests), o null.</summary>
    public int? CurrentProcessId => _current?.ProcessId;

    /// <summary>Crea la fuente: sondea el archivo con ffprobe (¿tiene audio?,
    /// duración) y LANZA el primer subproceso — el ring empieza a llenarse ya
    /// (preload = construir y aparcar; GO = empezar a consumir). Lanza
    /// StreamingSpawnException si no se puede (→ fallback RAM del engine).</summary>
    public StreamingAudioSource(
        string filePath, int sampleRate,
        double trimStart = 0.0, double trimEnd = 0.0,
        double ringSeconds = 3.0)
    {
        if (!File.Exists(filePath))
            throw new StreamingSpawnException($"Archivo no encontrado: '{filePath}'");
        _filePath = filePath;
        _sampleRate = sampleRate;

        double duration = ProbeAudioDuration(filePath);   // <0 = desconocida

        // La MISMA regla defensiva de trim que la ruta RAM: región inválida
        // (out ≤ in) → archivo completo.
        double s = Math.Max(0.0, trimStart);
        double e = trimEnd > 0.0 ? trimEnd : duration;
        if (duration >= 0.0)
        {
            s = Math.Min(s, duration);
            if (e < 0.0 || e > duration) e = duration;
        }
        if (e >= 0.0 && e <= s) { s = 0.0; e = duration; }
        _regionStart = s;
        _regionSeconds = e >= 0.0 ? Math.Max(0.0, e - s) : -1.0;

        int cap = (int)(Math.Clamp(ringSeconds, 0.5, 10.0) * sampleRate) * OutChannels;
        _ringCapacity = Math.Max(OutChannels * 1024, cap);

        _current = SpawnPass(firstPass: true);
    }

    public int SampleRate => _sampleRate;

    public int Channels => OutChannels;

    public long TotalFrames => _regionSeconds >= 0.0
        ? (long)(_regionSeconds * _sampleRate)
        : -1;

    public bool PassEnded
    {
        get
        {
            if (_disposed || _fatal)
                return true;
            var p = _current;
            return p is null || p.SpawnFailed || (p.Eof && p.Available == 0);
        }
    }

    public int ReadFrames(float[] dest, int offset, int frames)
    {
        if (_disposed || _fatal)
            return 0;
        var p = _current;
        if (p is null)
            return 0;
        return p.Read(dest, offset, frames);
    }

    public void PrepareNextPass()
    {
        if (_disposed || _fatal || _next is not null)
            return;
        lock (_spawnLock)
        {
            if (_disposed || _next is not null)
                return;
            _next = SpawnPassAsync();
        }
    }

    public bool BeginNextPass()
    {
        if (_disposed || _fatal)
            return false;

        Pass? next;
        lock (_spawnLock)
        {
            next = _next;
            _next = null;
            // Sin pre-spawn (pasada cortísima o duración desconocida): spawn
            // tardío — el hueco lo cubre el silencio de underrun del provider.
            next ??= SpawnPassAsync();
        }
        var old = Interlocked.Exchange(ref _current, next);
        if (old is not null)
            Task.Run(old.Dispose);   // ya está en EOF: liberar fuera del hilo RT
        return !next.SpawnFailed;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Pass? cur, nxt;
        lock (_spawnLock)
        {
            cur = Interlocked.Exchange(ref _current, null);
            nxt = _next;
            _next = null;
        }
        cur?.Dispose();
        nxt?.Dispose();
    }

    // ------------------------------------------------------------------
    // Spawn de pasadas
    // ------------------------------------------------------------------

    /// <summary>Lanza una pasada de forma SÍNCRONA (constructor: el fallo debe
    /// propagarse para que el engine caiga a la ruta RAM).</summary>
    private Pass SpawnPass(bool firstPass)
    {
        var pass = new Pass(_ringCapacity, OutChannels);
        try
        {
            pass.Start(BuildArguments(), OnPassDied, RegisterPid);
        }
        catch (Exception exc) when (firstPass)
        {
            pass.Dispose();
            throw new StreamingSpawnException(
                $"No se pudo lanzar ffmpeg para '{_filePath}': {exc.Message}", exc);
        }
        return pass;
    }

    /// <summary>Lanza una pasada en SEGUNDO PLANO (pre-spawn / relanzo desde el
    /// hilo RT: aquí no se puede bloquear). Un fallo marca la fuente FATAL.</summary>
    private Pass SpawnPassAsync()
    {
        var pass = new Pass(_ringCapacity, OutChannels);
        Task.Run(() =>
        {
            try
            {
                if (!_disposed)
                    pass.Start(BuildArguments(), OnPassDied, RegisterPid);
            }
            catch (Exception exc)
            {
                pass.MarkSpawnFailed();
                _fatal = true;
                if (!_disposed)
                    FatalError?.Invoke(
                        $"Relanzo de ffmpeg fallido para '{_filePath}': {exc.Message}");
            }
        });
        return pass;
    }

    private void RegisterPid(int pid)
    {
        lock (_spawnedPids)
            _spawnedPids.Add(pid);
    }

    /// <summary>El proceso de una pasada terminó MAL (exit ≠ 0) sin que
    /// nadie lo matara por Dispose: subir el stderr al engine.</summary>
    private void OnPassDied(int exitCode, string stderr)
    {
        if (_disposed)
            return;
        FatalError?.Invoke(
            $"ffmpeg terminó con código {exitCode} decodificando '{_filePath}'" +
            (stderr.Length > 0 ? $": {stderr}" : "."));
    }

    private ProcessStartInfo BuildArguments()
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        if (_regionStart > 0.0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(_regionStart.ToString("0.######", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(_filePath);
        if (_regionSeconds >= 0.0)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(_regionSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-vn"); psi.ArgumentList.Add("-sn"); psi.ArgumentList.Add("-dn");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("-acodec"); psi.ArgumentList.Add("pcm_f32le");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(OutChannels.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(_sampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("pipe:1");
        return psi;
    }

    // ------------------------------------------------------------------
    // ffprobe: ¿hay pista de audio? ¿cuánto dura?
    // ------------------------------------------------------------------

    /// <summary>Duración en segundos del audio (stream a:0, o la del formato
    /// si el stream no la declara); −1 si no se conoce. Lanza
    /// StreamingSpawnException si el archivo NO tiene pista de audio (paridad
    /// con la ruta RAM, cuyo decode falla → el controlador restaura mpv).</summary>
    private static double ProbeAudioDuration(string filePath)
    {
        var psi = new ProcessStartInfo("ffprobe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("a:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_type,duration:format=duration");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(filePath);

        string output;
        try
        {
            using var proc = Process.Start(psi)
                ?? throw new StreamingSpawnException("No se pudo lanzar ffprobe.");
            output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(); } catch (InvalidOperationException) { }
                throw new StreamingSpawnException($"ffprobe no respondió para '{filePath}'.");
            }
        }
        catch (StreamingSpawnException) { throw; }
        catch (Exception exc)
        {
            throw new StreamingSpawnException($"ffprobe falló para '{filePath}': {exc.Message}", exc);
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)
                || streams.GetArrayLength() == 0)
                throw new StreamingSpawnException($"'{filePath}' no tiene pista de audio.");

            double dur = -1.0;
            var stream = streams[0];
            if (stream.TryGetProperty("duration", out var sd)
                && double.TryParse(sd.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double v1) && v1 > 0.0)
                dur = v1;
            else if (doc.RootElement.TryGetProperty("format", out var fmt)
                && fmt.TryGetProperty("duration", out var fd)
                && double.TryParse(fd.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double v2) && v2 > 0.0)
                dur = v2;
            return dur;
        }
        catch (StreamingSpawnException) { throw; }
        catch (JsonException exc)
        {
            throw new StreamingSpawnException(
                $"ffprobe devolvió JSON ilegible para '{filePath}': {exc.Message}", exc);
        }
    }

    // ------------------------------------------------------------------
    // Pass — un subproceso ffmpeg + su ring SPSC
    // ------------------------------------------------------------------

    /// <summary>Una pasada: proceso ffmpeg, hilo lector y ring acotado.
    /// SPSC: el hilo lector solo escribe Head, el hilo RT solo escribe Tail
    /// (ambos con Volatile) — ReadFrames nunca toma locks ni bloquea.</summary>
    private sealed class Pass : IDisposable
    {
        private readonly float[] _ring;
        private readonly int _channels;
        private long _head;                  // samples escritos (acumulado)
        private long _tail;                  // samples leídos (acumulado)

        private Process? _proc;
        private Thread? _reader;
        private volatile bool _eof;
        private volatile bool _spawnFailed;
        private volatile bool _disposed;
        private readonly System.Text.StringBuilder _stderr = new();

        public Pass(int ringCapacity, int channels)
        {
            _ring = new float[ringCapacity];
            _channels = channels;
        }

        public bool Eof => _eof;

        public bool SpawnFailed => _spawnFailed;

        public int? ProcessId
        {
            get { try { return _proc?.Id; } catch (InvalidOperationException) { return null; } }
        }

        public int Available => (int)(Volatile.Read(ref _head) - Volatile.Read(ref _tail));

        public void MarkSpawnFailed()
        {
            _spawnFailed = true;
            _eof = true;
        }

        /// <summary>Arranca ffmpeg y el hilo lector. onDied(exitCode, stderr)
        /// se invoca si el proceso muere MAL sin haber sido Dispose-ado.</summary>
        public void Start(ProcessStartInfo psi, Action<int, string> onDied, Action<int> registerPid)
        {
            if (_disposed)
                return;   // la fuente se liberó antes de que el spawn corriera
            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start devolvió null.");
            _proc = proc;
            registerPid(proc.Id);
            if (_disposed)
            {
                // Carrera spawn-async vs Dispose: el proceso nació tras la
                // liberación → matarlo aquí mismo (cero zombis, siempre)
                try { proc.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                proc.Dispose();
                _proc = null;
                return;
            }

            // stderr en paralelo (si se dejara llenar el pipe, ffmpeg se
            // bloquearía escribiendo errores y el lector se colgaría)
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 } && _stderr.Length < 4096)
                    lock (_stderr) _stderr.AppendLine(e.Data);
            };
            proc.BeginErrorReadLine();

            var reader = new Thread(() => ReaderLoop(proc, onDied))
            {
                IsBackground = true,
                Name = "wincue-audio-stream",
            };
            _reader = reader;
            reader.Start();
        }

        /// <summary>Hilo lector: pipe → ring con backpressure (espera si el
        /// ring está lleno; ffmpeg se frena solo por el pipe).</summary>
        private void ReaderLoop(Process proc, Action<int, string> onDied)
        {
            var bytes = new byte[64 * 1024];
            int pending = 0;   // bytes sueltos (float incompleto) del read anterior
            try
            {
                var stdout = proc.StandardOutput.BaseStream;
                while (!_disposed)
                {
                    int freeSamples = _ring.Length - Available;
                    int maxBytes = Math.Min(bytes.Length - pending, freeSamples * 4 - pending);
                    if (maxBytes <= 0)
                    {
                        Thread.Sleep(5);   // ring lleno: backpressure
                        continue;
                    }
                    int n = stdout.Read(bytes, pending, maxBytes);
                    if (n <= 0)
                        break;             // EOF del pipe
                    int total = pending + n;
                    int usable = total - (total % 4);
                    WriteToRing(bytes, usable / 4);
                    pending = total - usable;
                    if (pending > 0)
                        Buffer.BlockCopy(bytes, usable, bytes, 0, pending);
                }
            }
            catch (Exception) { /* pipe roto / proceso matado: tratar como EOF */ }

            int exitCode = 0;
            try
            {
                if (!proc.WaitForExit(5_000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    proc.WaitForExit(2_000);
                }
                exitCode = proc.HasExited ? proc.ExitCode : -1;
            }
            catch (Exception) { exitCode = -1; }

            _eof = true;
            if (exitCode != 0 && !_disposed)
            {
                string err;
                lock (_stderr) err = _stderr.ToString().Trim();
                onDied(exitCode, err);
            }
        }

        private void WriteToRing(byte[] bytes, int samples)
        {
            long head = _head;   // solo este hilo escribe head
            int cap = _ring.Length;
            int idx = (int)(head % cap);
            int first = Math.Min(samples, cap - idx);
            Buffer.BlockCopy(bytes, 0, _ring, idx * 4, first * 4);
            if (samples > first)
                Buffer.BlockCopy(bytes, first * 4, _ring, 0, (samples - first) * 4);
            Volatile.Write(ref _head, head + samples);
        }

        /// <summary>Lee hasta frames frames COMPLETOS del ring (hilo RT, sin
        /// bloquear). 0 = ring vacío (underrun o EOF drenado).</summary>
        public int Read(float[] dest, int offset, int frames)
        {
            if (_spawnFailed)
                return 0;
            long head = Volatile.Read(ref _head);
            long tail = _tail;   // solo este hilo escribe tail
            int availFrames = (int)(head - tail) / _channels;
            int take = Math.Min(frames, availFrames);
            if (take <= 0)
                return 0;

            int samples = take * _channels;
            int cap = _ring.Length;
            int idx = (int)(tail % cap);
            int first = Math.Min(samples, cap - idx);
            Array.Copy(_ring, idx, dest, offset, first);
            if (samples > first)
                Array.Copy(_ring, 0, dest, offset + first, samples - first);
            Volatile.Write(ref _tail, tail + samples);
            return take;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            var proc = _proc;
            if (proc is not null)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2_000);
                    }
                }
                catch (InvalidOperationException) { /* ya liberado */ }
                catch (System.ComponentModel.Win32Exception) { /* ya muerto */ }
            }
            _reader?.Join(2_000);
            proc?.Dispose();
            _proc = null;
        }
    }
}
