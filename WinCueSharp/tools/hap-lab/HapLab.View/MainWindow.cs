using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace HapLab.View;

/// <summary>Quién marca la cadencia de presentación.</summary>
internal enum PaceMode
{
    /// <summary>
    /// Hilo propio a la cadencia del clip (la arquitectura del compositor real
    /// de WinCue: el motor escribe a su ritmo en la superficie compartida y el
    /// hilo de UI solo copia dentro del Lock, coalescido). Modo por defecto.
    /// </summary>
    Thread,

    /// <summary>
    /// Todo en CompositionTarget.Rendering con reloj del compositor. Documenta
    /// el aliasing del timer de render de WPF (ver INFORME, Paso 2).
    /// </summary>
    Compositor,
}

/// <summary>
/// Ventana del laboratorio: presenta el vídeo HAP por GPU con la técnica de
/// superficies del compositor real de WinCue (D3D11 → shared handle → D3D9Ex →
/// D3DImage, escritura SIEMPRE dentro del Lock + AddDirtyRect) y muestra un
/// overlay de estadísticas. El selector de frames (frame = floor(t·fps)) es
/// <see cref="FramePacer"/>, puro y testeado en headless.
/// </summary>
#pragma warning disable CA1001 // Window no es IDisposable; los recursos se liberan en OnClosed (ciclo de vida WPF)
internal sealed class MainWindow : Window
#pragma warning restore CA1001
{
    private readonly string _path;
    private readonly MovInfo _info;
    private readonly HapTextureFormat _format;
    private readonly double _benchSeconds;   // 0 = reproducir el clip entero
    private readonly UploadMode _requestedMode;
    private readonly PaceMode _paceMode;

    private readonly Image _image;
    private readonly TextBlock _overlay;
    private readonly Stopwatch _clock = new();
    private readonly BenchStats _stats = new();
    private readonly FramePacer _pacer;
    private readonly DispatcherTimer _overlayTimer;

    private D3D11Renderer? _renderer;
    private D3D9Presenter? _presenter;
    private FrameSource? _source;
    private D3DImage? _d3dImage;
    private Thread? _worker;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private TimeSpan _firstRenderingTime = TimeSpan.MinValue;
    private TimeSpan _cpuStart;
    private int _slot;
    private long _ticks;
    private int _presentPending;    // 0/1 — coalescer de presentaciones (modo Thread)
    private int _uiPresents;        // copias present hechas por el hilo de UI
    private int _uiCoalesced;       // frames que el UI no llegó a copiar (coalescidos)
    private volatile bool _workerStop;
    private bool _finished;

    /// <summary>Ruta del PNG de verificación (--dump-frame) o null.</summary>
    public string? DumpFramePath { get; set; }

    /// <summary>Índice del frame a volcar (--dump-index, por defecto 30).</summary>
    public int DumpFrameIndex { get; set; } = 30;

    private bool _dumped;

    public MainWindow(string path, MovInfo info, HapTextureFormat format,
        double benchSeconds, UploadMode requestedMode, PaceMode paceMode)
    {
        _path = path;
        _info = info;
        _format = format;
        _benchSeconds = benchSeconds;
        _requestedMode = requestedMode;
        _paceMode = paceMode;
        _pacer = new FramePacer(info.Fps, info.FrameCount);

        Title = $"HapLab.View — {System.IO.Path.GetFileName(path)}";
        Width = 1280;
        Height = 760;
        Background = Brushes.Black;

        _image = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        _overlay = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Text = "inicializando…",
        };
        var grid = new Grid();
        _ = grid.Children.Add(_image);
        _ = grid.Children.Add(_overlay);
        Content = grid;

        _overlayTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _overlayTimer.Tick += (_, _) => UpdateOverlay();
    }

    /// <summary>Código de salida del proceso (0 = ok, 1 = error de runtime).</summary>
    public int ExitCode { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint hwnd = new WindowInteropHelper(this).Handle;
        _renderer = new D3D11Renderer(_info.Width, _info.Height, _format, _requestedMode);
        _presenter = new D3D9Presenter(hwnd, _info.Width, _info.Height,
            _renderer.SharedHandle, _renderer.AdapterLuid);

        _d3dImage = new D3DImage();
        _d3dImage.Lock();
        _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _presenter.PresentSurfacePointer);
        _d3dImage.Unlock();
        _image.Source = _d3dImage;

        _source = new FrameSource(_path, _info, _format, parallelChunks: true);

        using (Process proc = Process.GetCurrentProcess())
        {
            _cpuStart = proc.TotalProcessorTime;
        }

        _overlayTimer.Start();
        if (_paceMode == PaceMode.Compositor)
        {
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "hap-present" };
            _worker.Start();
        }
    }

    // ------------------------------------------------------------------
    //  Modo Thread (por defecto): cadencia propia + copia coalescida en UI,
    //  la MISMA coreografía que MpvGpuRenderer + InvalidateFrame de WinCue
    // ------------------------------------------------------------------

    private void WorkerLoop()
    {
        if (_renderer is null || _source is null)
        {
            return;
        }

        _ = WinMm.BeginPeriod(1);
        try
        {
            // El reloj arranca cuando el primer frame ya está decodificado
            // (evita drops espurios de arranque: apertura de fichero, JIT…)
            while (!_workerStop && !_source.TryGet(0, out _))
            {
                if (_source.Error is not null)
                {
                    FailFromWorker();
                    return;
                }

                Thread.Sleep(1);
            }

            _clock.Start();
            double period = 1.0 / _info.Fps;
            double msPerTick = 1000.0 / Stopwatch.Frequency;

            while (!_workerStop)
            {
                if (_source.Error is not null)
                {
                    FailFromWorker();
                    return;
                }

                double t = _clock.Elapsed.TotalSeconds;
                if (_benchSeconds > 0 && t >= _benchSeconds)
                {
                    break;
                }

                _ticks++;
                PacerStep step = _pacer.Peek(t);
                if (step.Finished && !step.IsNew)
                {
                    break;
                }

                if (!step.IsNew)
                {
                    // Dormir hasta el inicio del período del siguiente frame
                    SleepUntil(_clock, (step.FrameIndex + 1) * period);
                    continue;
                }

                if (!_source.TryGet(step.FrameIndex, out byte[] dxt))
                {
                    // El decoder va tarde: reintentar sin confirmar el frame
                    _stats.AddLatePresent();
                    Thread.Sleep(0);
                    continue;
                }

                long t0 = Stopwatch.GetTimestamp();
                _renderer.Upload(_slot, dxt);
                long t1 = Stopwatch.GetTimestamp();
                _renderer.Render(_slot);   // draw + Flush del contexto D3D11
                long t2 = Stopwatch.GetTimestamp();
                _stats.AddUpload((t1 - t0) * msPerTick);
                _stats.AddRender((t2 - t1) * msPerTick);

                MaybeDumpFrame(step.FrameIndex);
                _pacer.Commit(step);
                _source.Consume(step.FrameIndex);
                _slot = (_slot + 1) % 3;

                // Coalescer de presentación (patrón InvalidateFrame de WinCue):
                // si el UI aún no copió la anterior, no se encola otra
                if (Interlocked.CompareExchange(ref _presentPending, 1, 0) != 0)
                {
                    _ = Interlocked.Increment(ref _uiCoalesced);
                }
                else
                {
                    _ = Dispatcher.BeginInvoke(PresentOnUi);
                }
            }
        }
        finally
        {
            _ = WinMm.EndPeriod(1);
        }

        _ = Dispatcher.BeginInvoke(Finish);
    }

    /// <summary>Volcado de verificación: readback del RT y PNG (solo una vez,
    /// en el primer frame ≥ DumpFrameIndex). Fuera del camino caliente.</summary>
    private void MaybeDumpFrame(int frameIndex)
    {
        if (_dumped || DumpFramePath is null || frameIndex < DumpFrameIndex || _renderer is null)
        {
            return;
        }

        _dumped = true;
        byte[] bgra = _renderer.ReadbackRenderTarget();
        string dumpPath = DumpFramePath;
        int w = _info.Width;
        int h = _info.Height;
        _ = Dispatcher.BeginInvoke(() =>
        {
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
                w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using System.IO.FileStream fs = System.IO.File.Create(dumpPath);
            encoder.Save(fs);
            Console.WriteLine($"Frame {frameIndex} volcado a {dumpPath}");
        });
    }

    private void FailFromWorker()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_source?.Error is not null)
            {
                Console.Error.WriteLine($"Error del hilo decodificador: {_source.Error.Message}");
            }

            ExitCode = 1;
            Finish();
        });
    }

    private static void SleepUntil(Stopwatch clock, double targetSeconds)
    {
        while (true)
        {
            double remaining = targetSeconds - clock.Elapsed.TotalSeconds;
            if (remaining <= 0)
            {
                return;
            }

            if (remaining > 0.002)
            {
                Thread.Sleep(1);   // granularidad real ~1-2 ms con timeBeginPeriod(1)
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    /// <summary>Copia compartida→presentación en el hilo de UI, DENTRO del
    /// Lock del D3DImage (contrato de la auditoría 05 de WinCue).</summary>
    private void PresentOnUi()
    {
        _ = Interlocked.Exchange(ref _presentPending, 0);
        if (_finished || _presenter is null || _d3dImage is null || !_d3dImage.IsFrontBufferAvailable)
        {
            return;
        }

        long t0 = Stopwatch.GetTimestamp();
        _d3dImage.Lock();
        _presenter.CopyPresent();
        _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _info.Width, _info.Height));
        _d3dImage.Unlock();
        _stats.AddCopy((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        _uiPresents++;
    }

    // ------------------------------------------------------------------
    //  Modo Compositor: todo en CompositionTarget.Rendering
    // ------------------------------------------------------------------

    private void OnRendering(object? sender, EventArgs e)
    {
        // CompositionTarget.Rendering puede dispararse varias veces por frame
        // de composición con el mismo RenderingTime: deduplicar
        if (e is RenderingEventArgs re)
        {
            if (re.RenderingTime == _lastRenderingTime)
            {
                return;
            }

            if (_firstRenderingTime == TimeSpan.MinValue)
            {
                _firstRenderingTime = re.RenderingTime;
            }

            _lastRenderingTime = re.RenderingTime;
        }

        if (_finished || _renderer is null || _presenter is null || _source is null || _d3dImage is null)
        {
            return;
        }

        if (_source.Error is not null)
        {
            Console.Error.WriteLine($"Error del hilo decodificador: {_source.Error.Message}");
            ExitCode = 1;
            Finish();
            return;
        }

        // El reloj no arranca hasta que el frame 0 está decodificado (evita
        // el drop espurio de arranque mientras aparece la ventana)
        if (!_clock.IsRunning)
        {
            if (!_source.TryGet(0, out _))
            {
                return;
            }

            _clock.Start();
            _firstRenderingTime = _lastRenderingTime;
        }

        _ticks++;
        double elapsed = _clock.Elapsed.TotalSeconds;

        // HALLAZGO del lab (ver INFORME, Paso 2): pacear con Stopwatch (reloj
        // de pared) contra los ticks con jitter del timer de render de WPF da
        // ~25 % de drops por puro aliasing; con el reloj del COMPOSITOR
        // (RenderingTime, la línea de tiempo con la que el DWM presentará este
        // pase) baja pero no desaparece. El Stopwatch queda para el resumen.
        double presentationTime = e is RenderingEventArgs rt
            ? (rt.RenderingTime - _firstRenderingTime).TotalSeconds
            : elapsed;
        PacerStep step = _pacer.Peek(presentationTime);

        bool benchDone = _benchSeconds > 0 && elapsed >= _benchSeconds;
        if (benchDone || (step.Finished && !step.IsNew))
        {
            Finish();
            return;
        }

        if (!step.IsNew)
        {
            return;
        }

        if (!_source.TryGet(step.FrameIndex, out byte[] dxt))
        {
            _stats.AddLatePresent();
            return;
        }

        double msPerTick = 1000.0 / Stopwatch.Frequency;
        long t0 = Stopwatch.GetTimestamp();
        _renderer.Upload(_slot, dxt);
        long t1 = Stopwatch.GetTimestamp();
        _renderer.Render(_slot);   // draw + Flush del contexto D3D11
        long t2 = Stopwatch.GetTimestamp();

        if (_d3dImage.IsFrontBufferAvailable)
        {
            _d3dImage.Lock();
            // Escritura al back buffer DENTRO del Lock (contrato D3DImage,
            // lección de la auditoría 05 de WinCue)
            _presenter.CopyPresent();
            _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _info.Width, _info.Height));
            _d3dImage.Unlock();
            _uiPresents++;
        }

        long t3 = Stopwatch.GetTimestamp();
        _stats.AddUpload((t1 - t0) * msPerTick);
        _stats.AddRender((t2 - t1) * msPerTick);
        _stats.AddCopy((t3 - t2) * msPerTick);
        _pacer.Commit(step);
        _source.Consume(step.FrameIndex);
        _slot = (_slot + 1) % 3;
    }

    // ------------------------------------------------------------------
    //  Overlay, resumen y cierre
    // ------------------------------------------------------------------

    private void UpdateOverlay()
    {
        if (_renderer is null)
        {
            return;
        }

        double elapsed = _clock.Elapsed.TotalSeconds;
        double fps = elapsed > 0 ? _pacer.PresentedCount / elapsed : 0;
        (double up50, double up95, _) = _stats.Upload;
        (double rd50, _, _) = _stats.Render;
        (double cp50, _, _) = _stats.Copy;
        _overlay.Text = string.Create(CultureInfo.InvariantCulture,
            $"{_info.Width}x{_info.Height} {_info.VideoFourcc} @ {_info.Fps:F0} fps — {_renderer.AdapterName} — pace {_paceMode}\n" +
            $"present {fps,6:F1} fps | frames {_pacer.PresentedCount}/{_info.FrameCount} | drops {_pacer.TotalDropped} | late {_stats.LatePresents} | ui {_uiPresents}+{_uiCoalesced}c\n" +
            $"subida {up50:F2} ms p50 ({up95:F2} p95, {_renderer.Mode}) | render {rd50:F2} ms | copia {cp50:F2} ms | t {elapsed,5:F1} s");
    }

    private void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (_paceMode == PaceMode.Compositor)
        {
            CompositionTarget.Rendering -= OnRendering;
        }

        StopWorker();
        _overlayTimer.Stop();
        _clock.Stop();
        PrintSummary();
        Application.Current.Shutdown(ExitCode);
    }

    private void StopWorker()
    {
        _workerStop = true;
        if (_worker is not null && _worker.IsAlive && Thread.CurrentThread != _worker)
        {
            _ = _worker.Join(2000);
        }

        _worker = null;
    }

    private void PrintSummary()
    {
        if (_renderer is null || _source is null)
        {
            return;
        }

        double elapsed = _clock.Elapsed.TotalSeconds;
        double cpuPct = 0;
        using (Process proc = Process.GetCurrentProcess())
        {
            double cpuMs = (proc.TotalProcessorTime - _cpuStart).TotalMilliseconds;
            cpuPct = elapsed > 0 ? cpuMs / (elapsed * 1000.0 * Environment.ProcessorCount) * 100.0 : 0;
        }

        (double up50, double up95, double upMax) = _stats.Upload;
        (double rd50, double rd95, double rdMax) = _stats.Render;
        (double cp50, double cp95, double cpMax) = _stats.Copy;
        (double dec50, double dec95, double decMax) =
            BenchStats.Summarize(_source.DecodeMs.Take(_source.DecodedCount).ToArray());
        double fps = elapsed > 0 ? _pacer.PresentedCount / elapsed : 0;
        double dropPct = _pacer.PresentedCount + _pacer.TotalDropped > 0
            ? 100.0 * _pacer.TotalDropped / (_pacer.PresentedCount + _pacer.TotalDropped)
            : 0;

        var sb = new StringBuilder();
        _ = sb.AppendLine();
        _ = sb.AppendLine("== Resumen de presentación ==");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Clip                 : {System.IO.Path.GetFileName(_path)} ({_info.Width}x{_info.Height} {_info.VideoFourcc} @ {_info.Fps:F2} fps, {_info.FrameCount} frames)");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Adaptador            : {_renderer.AdapterName} (subida {_renderer.Mode}, pace {_paceMode})");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Presentados          : {_pacer.PresentedCount} frames en {elapsed:F1} s ({fps:F1} fps efectivos, {_ticks} ticks)");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Drops                : {_pacer.TotalDropped} ({dropPct:F2} %) | late-presents: {_stats.LatePresents}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Copias UI (D3DImage) : {_uiPresents} ({_uiCoalesced} coalescidas por el hilo de UI)");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Subida textura       : p50 {up50:F2} | p95 {up95:F2} | max {upMax:F2} ms");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Render D3D11 (+Flush): p50 {rd50:F2} | p95 {rd95:F2} | max {rdMax:F2} ms");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Copia present (Lock) : p50 {cp50:F2} | p95 {cp95:F2} | max {cpMax:F2} ms");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Decode (hilo aparte) : p50 {dec50:F2} | p95 {dec95:F2} | max {decMax:F2} ms");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"CPU proceso          : {cpuPct:F1} % de {Environment.ProcessorCount} núcleos");
        Console.WriteLine(sb.ToString());

        var json = new
        {
            file = System.IO.Path.GetFileName(_path),
            codec = _info.VideoFourcc,
            width = _info.Width,
            height = _info.Height,
            fps = Math.Round(_info.Fps, 3),
            adapter = _renderer.AdapterName,
            upload_mode = _renderer.Mode.ToString(),
            pace = _paceMode.ToString(),
            seconds = Math.Round(elapsed, 2),
            presented = _pacer.PresentedCount,
            drops = _pacer.TotalDropped,
            drop_pct = Math.Round(dropPct, 3),
            late = _stats.LatePresents,
            ui_presents = _uiPresents,
            ui_coalesced = _uiCoalesced,
            present_fps = Math.Round(fps, 2),
            upload_ms_p50 = Math.Round(up50, 3),
            upload_ms_p95 = Math.Round(up95, 3),
            upload_ms_max = Math.Round(upMax, 3),
            render_ms_p50 = Math.Round(rd50, 3),
            render_ms_p95 = Math.Round(rd95, 3),
            copy_ms_p50 = Math.Round(cp50, 3),
            copy_ms_p95 = Math.Round(cp95, 3),
            decode_ms_p50 = Math.Round(dec50, 3),
            decode_ms_p95 = Math.Round(dec95, 3),
            cpu_pct = Math.Round(cpuPct, 1),
        };
        Console.WriteLine(JsonSerializer.Serialize(json));
    }

    protected override void OnClosed(EventArgs e)
    {
        // Cierre manual antes de acabar: imprimir lo que haya y liberar TODO
        // en orden inverso (superficies UNA vez por sesión, auditoría 05)
        if (!_finished)
        {
            Finish();
        }

        StopWorker();
        if (_d3dImage is not null)
        {
            _d3dImage.Lock();
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, 0);
            _d3dImage.Unlock();
            _d3dImage = null;
        }

        _source?.Dispose();
        _source = null;
        _presenter?.Dispose();
        _presenter = null;
        _renderer?.Dispose();
        _renderer = null;
        base.OnClosed(e);
    }
}
