// =============================================================================
// ProResCuePlayer.cs — Player propio (sin mpv) para cues ProRes en .mov
// =============================================================================
// Gemelo de HapCuePlayer y deliberadamente calcado: la lógica de reproducción
// del motor propio —reloj maestro de audio, pacing, costura de loop, guardián
// de fin, pausa/resume, congelado del saliente— lleva meses de casos reales
// encima y de directos de verdad. Aquí se REUTILIZA, no se reescribe: lo único
// que cambia es de dónde salen los fotogramas (ProResFrameSource en vez de
// HapFrameSource) y qué se sube a la GPU (planos de 16 bits en vez de un DXT).
//
// FramePacer y HapMediaClock se usan TAL CUAL desde WinCue.Video.Hap. No
// dependen del códec —son puros: uno selecciona frame = floor(t·fps), el otro
// mantiene el anclaje contra el reloj de audio— y están cubiertos por sus
// propios tests. Conservan el prefijo Hap por su origen; moverlos de sitio solo
// para renombrarlos habría metido churn en el motor HAP, que es exactamente lo
// que este paso no debe tocar.
//
// CADENCIA: hilo de presentación PROPIO con timeBeginPeriod(1) — el hallazgo
// central del lab de HAP: pacear contra el tick de WPF da 10-25 % de drops por
// aliasing; el hilo propio da 0. El hilo de UI solo copia dentro del Lock.
//
// El PIPELINE y la SESIÓN son del pool del VideoEngine (persistentes, auditoría
// 05); este player solo posee sus hilos y su reloj.
// =============================================================================

using System.Diagnostics;
using WinCue.Core;
using WinCue.Video.Containers;
using WinCue.Video.Hap;

namespace WinCue.Video.ProRes;

internal sealed class ProResCuePlayer : IDisposable
{
    private readonly MovInfo _info;
    private readonly IProResRenderTarget _pipeline;
    private readonly ProResVideoSession _session;
    private readonly ProResFrameSource _source;
    private readonly HapMediaClock _clock;
    private readonly Action<string> _trace;
    private readonly Thread _thread;

    private readonly double _fps;
    private readonly int _segmentFrames;
    private readonly double _segmentSeconds;
    private readonly int _totalPasses;            // int.MaxValue = loop infinito

    // Rectángulo destino en la superficie (letterbox/escala). Se recalcula en
    // vivo desde el main thread (ApplyGeometry) → lectura atómica por caja.
    private sealed record DestRect(int X, int Y, int W, int H);
    private DestRect _dest;

    private volatile bool _stop;
    private volatile bool _paused;
    private long _elapsedMs;                      // local de la pasada (Interlocked)
    private int _firstFrameRaised;

    /// <summary>Primer frame VISIBLE en la superficie (hilo de presentación).</summary>
    public event Action? FirstFrame;

    /// <summary>Fin natural: el reloj rebasó la última pasada (hilo de pres.).</summary>
    public event Action? Ended;

    /// <param name="session">Ring de texturas del cue. LO POSEE EL SLOT DEL POOL,
    /// no el player: se reutiliza entre cues con la misma spec (tamaño + formato
    /// de píxel) para no crear y destruir 190 MB de texturas por cue.</param>
    /// <param name="clock">Reloj externo inyectado (elapsed del audio espejo del
    /// cue en segundos desde su trim, o null si no suena). Se consulta desde el
    /// hilo de presentación: debe ser thread-safe.</param>
    public ProResCuePlayer(string path, MovInfo info, IProResRenderTarget pipeline,
        ProResVideoSession session, VideoPlayParams p, int threadCount,
        Func<double?>? clock, Action<string>? trace = null)
    {
        _info = info;
        _pipeline = pipeline;
        _session = session;
        _trace = trace ?? (_ => { });

        _fps = info.Fps > 1.0 ? info.Fps : 30.0;
        int startFrame = Math.Clamp((int)Math.Round(p.TrimStart * _fps), 0, info.FrameCount - 1);
        int endFrame = p.TrimEnd > 0.0
            ? Math.Clamp((int)Math.Round(p.TrimEnd * _fps), startFrame + 1, info.FrameCount)
            : info.FrameCount;
        _segmentFrames = endFrame - startFrame;
        _segmentSeconds = _segmentFrames / _fps;
        _totalPasses = p.Loop ? int.MaxValue : Math.Max(1, p.PlayCount);

        var geo = p.Geometry ?? new VideoGeometry();
        _dest = ComputeDest(info.Width, info.Height, _pipeline.Width, _pipeline.Height,
            geo.PreserveAspect, p.Scale);

        _clock = new HapMediaClock(_segmentSeconds, _totalPasses, clock);
        _source = new ProResFrameSource(path, info, startFrame, _segmentFrames,
            _totalPasses == int.MaxValue ? -1L : (long)_segmentFrames * _totalPasses,
            threadCount);

        _thread = new Thread(PresentLoop) { IsBackground = true, Name = "prores-present" };
    }

    /// <summary>Duración EFECTIVA de una pasada (trim aplicado), en segundos.</summary>
    public double GetDuration() => _segmentSeconds;

    /// <summary>Segundos reproducidos de la pasada actual, desde el punto de
    /// entrada (misma semántica que mpv con loop-file: se resetea por pasada).</summary>
    public double GetElapsed() => Interlocked.Read(ref _elapsedMs) / 1000.0;

    /// <summary>Arranca los hilos (llamar tras cablear los eventos).</summary>
    public void Start() => _thread.Start();

    /// <summary>Pausa/reanuda la presentación (el audio se pausa aparte, por el
    /// controlador; el reloj congela y re-ancla solo).</summary>
    public void SetPaused(bool paused)
    {
        if (paused == _paused)
            return;
        if (paused)
        {
            _clock.Pause();
            _paused = true;
        }
        else
        {
            _clock.Resume();
            _paused = false;
        }
    }

    /// <summary>Recalcula el rectángulo destino en vivo (modo FULL: ajuste con
    /// aspecto + escala del cue; CUSTOM no entra por este motor).</summary>
    public void ApplyGeometry(VideoGeometry geo, double scalePercent) =>
        _dest = ComputeDest(_info.Width, _info.Height, _pipeline.Width, _pipeline.Height,
            geo.PreserveAspect, scalePercent);

    private static DestRect ComputeDest(int videoW, int videoH, int surfW, int surfH,
        bool preserveAspect, double scalePercent)
    {
        double zoom = Math.Max(0.01, scalePercent) / 100.0;
        double w, h;
        if (preserveAspect)
        {
            double fit = Math.Min((double)surfW / videoW, (double)surfH / videoH);
            w = videoW * fit * zoom;
            h = videoH * fit * zoom;
        }
        else
        {
            w = surfW * zoom;
            h = surfH * zoom;
        }

        return new DestRect(
            (int)Math.Round((surfW - w) / 2.0),
            (int)Math.Round((surfH - h) / 2.0),
            Math.Max(1, (int)Math.Round(w)),
            Math.Max(1, (int)Math.Round(h)));
    }

    // ------------------------------------------------------------------
    // Hilo de presentación
    // ------------------------------------------------------------------

    private void PresentLoop()
    {
        _ = WinMm.BeginPeriod(1);
        try
        {
            // Esperar el primer frame decodificado (sin reloj: evita drops
            // espurios de arranque — apertura de fichero, carga de las DLL, JIT)
            while (!_stop && !_source.TryGet(0, out _))
            {
                if (_source.Error is not null)
                {
                    _trace($"prores decode falló en el arranque: {_source.Error.Message}");
                    RaiseEnded();
                    return;
                }
                Thread.Sleep(1);
            }
            if (_stop)
                return;

            // Las constantes de color salen del PRIMER frame: manda el bitstream
            // (AVFrame.colorspace), con el colr del contenedor de respaldo.
            int slot = 0;
            if (_source.TryGet(0, out ProResFrameSlot first))
            {
                _session.SetColor(ColorParams.ForFrame(
                    _session.Layout, first.ColorSpace, first.ColorRange, _info.Colr));
                // Pintar el primer frame ANTES de arrancar el reloj (equivale al
                // PlaybackRestart de mpv: latencia medible + FrameReady)
                Present(first, slot, 0);
                slot = 1;
            }
            _clock.Start();

            var pacer = new FramePacer(_fps, _segmentFrames);
            int pacerPass = 0;
            long lastDropLog = 0;
            int lateSpins = 0;

            // GUARDIÁN DE FIN (misma red que en HAP): el fin natural debe llegar
            // SIEMPRE en un cue finito, haga lo que haga el reloj maestro. Se
            // mide tiempo de pared ACTIVO (sin pausas); con el reloj sano jamás
            // se alcanza.
            double expectedTotal = _totalPasses == int.MaxValue
                ? double.PositiveInfinity
                : _segmentSeconds * _totalPasses;
            double guardMargin = double.IsInfinity(expectedTotal)
                ? 0.0
                : Math.Max(1.0, expectedTotal * 0.02);
            long tPrev = Stopwatch.GetTimestamp();
            double activeSeconds = 0.0;

            while (!_stop)
            {
                if (_source.Error is not null)
                {
                    _trace($"prores decode murió: {_source.Error.Message}");
                    break;
                }

                long tNow = Stopwatch.GetTimestamp();
                if (!_paused)
                    activeSeconds += (double)(tNow - tPrev) / Stopwatch.Frequency;
                tPrev = tNow;

                if (_paused)
                {
                    Thread.Sleep(5);
                    continue;
                }

                if (activeSeconds > expectedTotal + guardMargin)
                {
                    _trace($"prores guardián de fin: {activeSeconds:F2}s activos > " +
                        $"{expectedTotal:F2}s + {guardMargin:F2}s — fin forzado " +
                        $"(resyncs {_clock.Resyncs})");
                    break;
                }

                HapClockSample s = _clock.Now();
                _ = Interlocked.Exchange(ref _elapsedMs, (long)(s.LocalSeconds * 1000.0));

                if (s.Pass != pacerPass)
                {
                    _trace($"prores pasada {pacerPass}→{s.Pass} (drops acumulados {pacer.TotalDropped})");
                    pacer = new FramePacer(_fps, _segmentFrames);
                    pacerPass = s.Pass;
                }

                PacerStep step = pacer.Peek(s.LocalSeconds);
                bool lastPass = _totalPasses != int.MaxValue && s.Pass >= _totalPasses - 1;
                if (step.Finished && !step.IsNew && lastPass)
                    break;   // fin natural

                if (!step.IsNew)
                {
                    // Dormir hasta el inicio del período del siguiente frame,
                    // releyendo el reloj (el audio puede re-anclarlo)
                    double remaining = (step.FrameIndex + 1) / _fps - s.LocalSeconds;
                    if (remaining > 0.003)
                        Thread.Sleep(1);
                    else
                        Thread.Sleep(0);
                    continue;
                }

                long logical = (long)s.Pass * _segmentFrames + step.FrameIndex;
                if (!_source.TryGet(logical, out ProResFrameSlot frame))
                {
                    // COSTURA DE LOOP / RESYNC HACIA DELANTE (ver SkipTo)
                    _source.SkipTo(logical);
                    if (++lateSpins > 1000)
                    {
                        _trace("prores presentación: decoder >1 s tarde, abandono");
                        break;
                    }
                    Thread.Sleep(1);
                    continue;
                }
                lateSpins = 0;

                Present(frame, slot, logical);
                pacer.Commit(step);
                if (step.Dropped > 0 && Environment.TickCount64 - lastDropLog > 5000)
                {
                    lastDropLog = Environment.TickCount64;
                    _trace($"prores drops acumulados: {pacer.TotalDropped} (resyncs {_clock.Resyncs})");
                }
                slot = (slot + 1) % ProResVideoSession.RingSize;
            }
        }
        catch (Exception exc)
        {
            _trace($"prores presentación murió: {exc.Message}");
        }
        finally
        {
            _ = WinMm.EndPeriod(1);
        }

        RaiseEnded();
    }

    private void Present(ProResFrameSlot frame, int slot, long logical)
    {
        DestRect d = _dest;
        _pipeline.Upload(_session, slot, frame);
        _pipeline.Render(_session, slot, d.X, d.Y, d.W, d.H);
        _source.Consume(logical);
        _pipeline.NotifyFrame(logical / _fps);
        if (Interlocked.Exchange(ref _firstFrameRaised, 1) == 0)
        {
            try { FirstFrame?.Invoke(); } catch (Exception) { }
        }
    }

    private void RaiseEnded()
    {
        if (_stop)
            return;   // parada ordenada: no es fin natural
        try { Ended?.Invoke(); } catch (Exception) { }
    }

    /// <summary>Parada limpia y liberación SIN fugas: hilos parados y unidos, y
    /// los frames de FFmpeg del ring soltados (63 MB cada uno en 4K 4444). El
    /// pipeline y la SESIÓN son del pool y sobreviven al cue.</summary>
    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _ = _thread.Join(2000);
        _source.Dispose();
    }
}
