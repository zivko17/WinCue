// =============================================================================
// MainController.cs — Controlador de reproducción estilo QLab (port de
// cue_controller.py)
// =============================================================================
//
// PARADIGMA
//   · PLAYHEAD (standby): índice del cue que GO disparará a continuación.
//   · CUES CONCURRENTES: GO dispara el cue del playhead y AVANZA el playhead,
//     SIN cortar los cues que ya están sonando.
//   · TEMPORIZACIÓN por cue: pre_wait → acción → (post_wait). El encadenado:
//       NONE          — nada automático (GO manual)
//       AUTO_CONTINUE — el siguiente se dispara tras pre_wait+post_wait
//       AUTO_FOLLOW   — el siguiente se dispara cuando ESTE termina su acción
//   · TRANSPORT: GO, Stop All, Panic (fade-out global), Pause/Resume.
//
// HILOS
//   Diseñado para un solo hilo (el de UI). Los motores y el scheduler remiten
//   sus callbacks por su dispatcher; el controlador nunca bloquea.
// =============================================================================

using System.Diagnostics;
using WinCue.Core;

namespace WinCue.Controller;

public class MainController
{
    /// <summary>Duración por defecto del fade global de Panic (segundos).</summary>
    public const double PanicFade = 1.5;

    /// <summary>Se emite cuando cambia el playhead.</summary>
    public event Action<int>? ActiveIndexChanged;

    /// <summary>Registro de eventos del show (GO/STOP/PANIC/carga…) para el log.</summary>
    public event Action<string>? ShowLog;

    // ------------------------------------------------------------------
    // Info de un cue en ejecución
    // ------------------------------------------------------------------

    private sealed class RunningCue
    {
        public required int CueId;
        public required int Index;
        public required CueType Kind;
        public required string Name;
        public required string Number;
        public required string Color;
        public long TStartTicks = Stopwatch.GetTimestamp();
        public double Duration;              // 0 = indeterminado (fin natural)
        public readonly List<IDisposable> Timers = new();
        public bool ManualStop;
        public bool AdvanceChain;
        public bool SelfPaused;              // pausa individual (no pause-all)
    }

    // ------------------------------------------------------------------
    // Estado
    // ------------------------------------------------------------------

    private readonly CueModel _model;
    private readonly IShowView _view;
    private readonly IAudioEngine _audio;
    private readonly IVideoEngine _video;
    private readonly IScheduler _scheduler;
    private readonly SettingsManager _settings;
    private readonly Random _random;

    private int _playhead;
    private bool _paused;
    private long _pauseT0Ticks;
    private bool _lastHadRunning;

    private readonly Dictionary<int, RunningCue> _running = new();
    private readonly Dictionary<int, HashSet<int>> _rrState = new();   // RANDOM round-robin
    private readonly List<IDisposable> _chainTimers = new();

    // Undo/redo: snapshots JSON del modelo (baratos: el show es texto pequeño)
    private const int MaxUndo = 100;
    private readonly List<(string Json, int Playhead)> _undoStack = new();
    private readonly List<(string Json, int Playhead)> _redoStack = new();

    // Portapapeles interno de cues (JSON de una lista de cues)
    private string? _clipboard;

    // Anti doble-GO: instante del último GO aceptado
    private long _lastGoTicks;

    public int Playhead => _playhead;
    public bool IsPaused => _paused;
    public IReadOnlyCollection<int> RunningCueIds => _running.Keys;

    // ------------------------------------------------------------------
    // Construcción
    // ------------------------------------------------------------------

    public MainController(
        CueModel model,
        IShowView view,
        IAudioEngine audio,
        IVideoEngine video,
        IScheduler scheduler,
        SettingsManager settings,
        Random? random = null)
    {
        _model = model;
        _view = view;
        _audio = audio;
        _video = video;
        _scheduler = scheduler;
        _settings = settings;
        _random = random ?? new Random();

        _audio.LatencyReport += OnLatencyReport;
        _audio.ErrorOccurred += OnAudioError;
        _audio.StateChanged += OnAudioStateChanged;
        _audio.LevelReport += (l, r) => _view.UpdateVuMeter(l, r);
        _audio.CueLevelReport += (cueId, l, r) => _view.UpdateCueVuMeter(cueId, l, r);

        _video.LatencyReport += OnLatencyReport;
        _video.ErrorOccurred += OnVideoError;
        _video.StateChanged += OnVideoStateChanged;
        _video.SetWindowHandleProvider(() => _view.GetVideoOutputHandle());

        SetPlayhead(0);
    }

    // ==================================================================
    // TRANSPORT — API pública conectada a la vista
    // ==================================================================

    /// <summary>GO: dispara el cue del playhead y avanza, sin cortar los activos.
    /// Protegido contra el doble-tap accidental (go_min_interval_ms).</summary>
    public void OnGoPressed()
    {
        int minMs = _settings.Get("go_min_interval_ms", 200);
        if (minMs > 0 && _lastGoTicks != 0 &&
            Stopwatch.GetElapsedTime(_lastGoTicks).TotalMilliseconds < minMs)
            return;   // doble-tap: ignorar
        _lastGoTicks = Stopwatch.GetTimestamp();

        var cue = _model.GetCue(_playhead);
        if (cue is null)
            return;
        Log($"GO → {cue.DisplayNumber} · {cue.Name}");
        GoCue(_playhead);
    }

    /// <summary>Stop All: detiene todos los cues. No mueve el playhead.</summary>
    public void OnStopPressed()
    {
        Log("STOP ALL");
        StopEverything(fade: 0.0);
        _view.FlashStatus("■ STOP ALL — todos los cues detenidos");
    }

    /// <summary>Panic: fundido de salida global y parada de todo.</summary>
    public void OnPanic()
    {
        Log("PANIC");
        StopEverything(fade: PanicFade);
        _view.FlashStatus($"⚠ PANIC — fundido de {PanicFade:F0}s y parada");
    }

    /// <summary>FADE ALL: fundido elegante de todo (distinto del PANIC de
    /// emergencia). El tiempo se configura en fade_all_seconds.</summary>
    public void OnFadeAll()
    {
        double t = Math.Max(0.1, _settings.Get("fade_all_seconds", 5.0));
        Log($"FADE ALL ({t:0.#}s)");
        StopEverything(fade: t);
        _view.FlashStatus($"◢ FADE ALL — fundido de {t:0.#}s");
    }

    /// <summary>Id reservado para la audición local (nunca colisiona con cues).</summary>
    public const int AuditionCueId = -9999;

    /// <summary>Audición local: reproduce el cue de la fila por el dispositivo
    /// de audición (cascos) SIN pasar por el flujo del show. Toggle.</summary>
    public void AuditionToggle(int row)
    {
        if (_audio.IsPlaying(AuditionCueId))
        {
            _audio.Stop(AuditionCueId);
            _view.FlashStatus("🎧 Audición detenida");
            return;
        }
        var cue = _model.GetCue(row);
        if (cue is null || cue.Type != CueType.Audio || cue.FilePath.Length == 0)
        {
            _view.FlashStatus("Audición: selecciona un cue de AUDIO con archivo");
            return;
        }
        _audio.Play(AuditionCueId, cue.FilePath, new AudioPlayParams(
            Volume: cue.Volume, TrimStart: cue.TrimStart, TrimEnd: cue.TrimEnd,
            OutputDevice: _settings.Get("audition_device_name", "")));
        _view.FlashStatus($"🎧 Audición: {cue.Name}");
    }

    /// <summary>Detiene un cue por id (botón ■ del panel de activos).</summary>
    public void StopCueById(int cueId)
    {
        if (_running.ContainsKey(cueId))
        {
            Log($"STOP → cue id {cueId}");
            EndCue(cueId, markStopped: true);
        }
    }

    /// <summary>Pausa/reanuda SOLO el cue de la fila dada (media en ejecución).
    /// Los timers de duración se congelan y reprograman al reanudar.</summary>
    public void OnPauseCueToggle(int row)
    {
        var cue = _model.GetCue(row);
        if (cue is null || !cue.IsMedia() ||
            !_running.TryGetValue(cue.Id, out var run))
            return;

        if (!run.SelfPaused)
        {
            run.SelfPaused = true;
            if (cue.Type == CueType.Audio) _audio.PauseCue(cue.Id);
            else _video.PauseCue(cue.Id);
            // Congelar el timer de duración (si lo hay)
            foreach (var t in run.Timers) t.Dispose();
            run.Timers.Clear();
            _view.FlashStatus($"⏸ {cue.DisplayNumber} en pausa");
        }
        else
        {
            run.SelfPaused = false;
            double elapsed;
            if (cue.Type == CueType.Audio)
            {
                _audio.ResumeCue(cue.Id);
                elapsed = _audio.GetElapsed(cue.Id);
            }
            else
            {
                _video.ResumeCue(cue.Id);
                elapsed = _video.GetElapsed(cue.Id);
            }
            // Reprogramar el resto de la duración fijada
            if (run.Duration > 0.0 && !cue.Loop)
            {
                double remaining = Math.Max(0.0, run.Duration - elapsed);
                Schedule(run, (int)(remaining * 1000), () => DurationElapsed(cue.Id));
            }
            _view.FlashStatus($"▶ {cue.DisplayNumber} reanudado");
        }
    }

    /// <summary>Volumen master global (0-100) desde el slider del top bar.</summary>
    public void OnMasterVolume(int value)
    {
        double m = Math.Clamp(value / 100.0, 0.0, 1.0);
        _audio.SetMasterVolume(m);
        _video.SetMasterVolume(m);
    }

    /// <summary>Pausa/reanuda todos los motores de reproducción.</summary>
    public void OnPauseToggle()
    {
        if (_running.Count == 0)
            return;
        if (_paused)
        {
            _audio.ResumeAll();
            _video.ResumeAll();
            // reajustar TStart para que el countdown no salte
            long dt = Stopwatch.GetTimestamp() - _pauseT0Ticks;
            foreach (var r in _running.Values)
                r.TStartTicks += dt;
            _paused = false;
        }
        else
        {
            _audio.PauseAll();
            _video.PauseAll();
            _pauseT0Ticks = Stopwatch.GetTimestamp();
            _paused = true;
        }
        _view.UpdateTransport(_paused);
    }

    /// <summary>Detiene el cue de la fila dada, si está en ejecución.</summary>
    public void OnStopSelected(int row)
    {
        var cue = _model.GetCue(row);
        if (cue is not null && _running.ContainsKey(cue.Id))
            EndCue(cue.Id, markStopped: true);
    }

    public void OnPrevPressed()
    {
        if (_model.Count > 0)
            SetPlayhead(Math.Max(0, _playhead - 1));
    }

    public void OnNextPressed()
    {
        if (_model.Count > 0)
            SetPlayhead(Math.Min(_model.Count - 1, _playhead + 1));
    }

    /// <summary>Salta el playhead a un cue por posición (1-based). No dispara.</summary>
    public void OnJumpToCue(int cueNumber)
    {
        int index = cueNumber - 1;
        if (index >= 0 && index < _model.Count)
            SetPlayhead(index);
    }

    /// <summary>Fija el playhead a la fila dada (p.ej. clic del usuario).</summary>
    public void OnSetPlayhead(int row)
    {
        if (row >= 0 && row < _model.Count)
            SetPlayhead(row);
    }

    // ==================================================================
    // NÚCLEO DE DISPARO
    // ==================================================================

    private void GoCue(int idx)
    {
        var cue = _model.GetCue(idx);
        if (cue is null)
            return;

        if (cue.Type == CueType.Group)
        {
            GoGroup(idx, cue);
            return;
        }

        SetPlayhead(idx + 1);
        Trigger(idx, advanceChain: true);
    }

    private void GoGroup(int idx, Cue cue)
    {
        int extent = _model.GroupExtent(idx);

        if (cue.GroupMode == GroupMode.Enter)
        {
            // Grupo transparente: dispara el primer hijo como un GO normal
            var childIndices = _model.DirectChildIndices(cue.Id);
            if (childIndices.Count > 0)
                GoCue(childIndices[0]);
            else
                SetPlayhead(extent);
            return;
        }

        // TIMELINE / GO_NEXT / RANDOM: el playhead salta tras el grupo
        SetPlayhead(extent);
        Trigger(idx, advanceChain: true);
    }

    private void FireGroupChildren(int idx)
    {
        var group = _model.GetCue(idx);
        if (group is null)
            return;
        var childIndices = _model.DirectChildIndices(group.Id);

        switch (group.GroupMode)
        {
            case GroupMode.Timeline:
                foreach (int ci in childIndices)
                    Trigger(ci, advanceChain: false);
                break;
            case GroupMode.GoNext:
                if (childIndices.Count > 0)
                    Trigger(childIndices[0], advanceChain: false);
                break;
            case GroupMode.Random:
                FireRandomChild(group, childIndices);
                break;
            // ENTER no llega aquí (grupo transparente en GoGroup)
        }
    }

    private void FireRandomChild(Cue group, List<int> childIndices)
    {
        var fired = _rrState.TryGetValue(group.Id, out var f)
            ? f : _rrState[group.Id] = new HashSet<int>();

        List<int> Eligible(bool requireUnfired)
        {
            var result = new List<int>();
            foreach (int ci in childIndices)
            {
                var c = _model.GetCue(ci);
                if (c is null || !c.Armed || _running.ContainsKey(c.Id))
                    continue;
                if (requireUnfired && fired.Contains(c.Id))
                    continue;
                result.Add(ci);
            }
            return result;
        }

        var candidates = Eligible(requireUnfired: true);
        if (candidates.Count == 0)
        {
            fired.Clear();
            candidates = Eligible(requireUnfired: false);
        }
        if (candidates.Count == 0)
            return;
        int chosen = candidates[_random.Next(candidates.Count)];
        var cue = _model.GetCue(chosen);
        if (cue is not null)
            fired.Add(cue.Id);
        Trigger(chosen, advanceChain: false);
    }

    /// <summary>Dispara el cue en idx: pre_wait, acción y encadenado.
    /// advanceChain=false → sub-secuencia (START/grupo) que no mueve el playhead.</summary>
    private void Trigger(int idx, bool advanceChain)
    {
        var cue = _model.GetCue(idx);
        if (cue is null)
            return;

        // ── Second trigger: el cue ya está sonando ──────────────────────
        if (_running.ContainsKey(cue.Id))
        {
            switch (cue.Retrigger)
            {
                case RetriggerMode.None:
                    return;                                  // ignorar 2º disparo
                case RetriggerMode.Stop:
                case RetriggerMode.HardStop:
                    EndCue(cue.Id, markStopped: true);
                    return;
                case RetriggerMode.Panic:
                    EndCue(cue.Id, markStopped: true, fade: PanicFade);
                    return;
                    // HARD_RESTART: continúa abajo (para + reinicia)
            }
        }

        var run = new RunningCue
        {
            CueId = cue.Id,
            Index = idx,
            Kind = cue.Type,
            Name = cue.Name,
            Number = cue.DisplayNumber,
            Color = cue.Color,
            AdvanceChain = advanceChain,
        };
        // Retrigger HARD_RESTART: cancelar la instancia previa del mismo cue
        if (_running.ContainsKey(cue.Id))
            EndCue(cue.Id, markStopped: false);
        _running[cue.Id] = run;

        // AUTO_CONTINUE: timer de cadena INDEPENDIENTE del ciclo de vida del cue
        // (debe disparar aunque la acción termine antes del post_wait)
        if (cue.ContinueMode == ContinueMode.AutoContinue)
        {
            int delay = (int)((cue.PreWait + cue.PostWait) * 1000);
            ScheduleChain(delay, () => ContinueNext(idx, advanceChain));
        }

        // Acción: inmediata o tras pre_wait
        if (cue.PreWait > 0.0)
            Schedule(run, (int)(cue.PreWait * 1000), () => StartAction(idx));
        else
            StartAction(idx);
    }

    private void StartAction(int idx)
    {
        var cue = _model.GetCue(idx);
        if (cue is null)
            return;
        if (!_running.TryGetValue(cue.Id, out var run))
            return;
        run.TStartTicks = Stopwatch.GetTimestamp();

        if (!cue.Armed)
        {
            // Cue desarmado: sin acción, pero respeta el encadenado.
            OnActionEnd(cue.Id);
            return;
        }

        // "Fade & stop others over time" (Triggers)
        ApplyFadeOthers(cue);

        // Exclusividad de GO (Settings)
        if (cue.IsMedia())
            ApplyGoExclusivity(cue);

        switch (cue.Type)
        {
            case CueType.Audio:
                ModelState(idx, CueState.Playing);
                _audio.Play(cue.Id, cue.FilePath, new AudioPlayParams(
                    Volume: cue.Volume, FadeIn: cue.FadeIn, FadeOut: cue.FadeOut,
                    Loop: cue.Loop, TrimStart: cue.TrimStart, TrimEnd: cue.TrimEnd,
                    PlayCount: cue.PlayCount, OutputDevice: cue.AudioOutput));
                run.Duration = cue.Duration;
                if (cue.Duration > 0.0 && !cue.Loop)
                    Schedule(run, (int)(cue.Duration * 1000), () => DurationElapsed(cue.Id));
                PreloadUpcoming();
                break;

            case CueType.Video:
                ModelState(idx, CueState.Playing);
                _video.Play(cue.Id, cue.FilePath, new VideoPlayParams(
                    Volume: cue.Volume, Loop: cue.Loop, Scale: cue.Scale,
                    TrimStart: cue.TrimStart, TrimEnd: cue.TrimEnd,
                    Rate: cue.Rate, PlayCount: cue.PlayCount,
                    PreservePitch: cue.PreservePitch,
                    Geometry: GeoFor(cue)));
                RefreshVideoTally();
                run.Duration = cue.Duration;
                if (cue.Duration > 0.0 && !cue.Loop)
                    Schedule(run, (int)(cue.Duration * 1000), () => DurationElapsed(cue.Id));
                break;

            case CueType.Wait:
                ModelState(idx, CueState.Playing);
                run.Duration = Math.Max(0.0, cue.Duration);
                Schedule(run, Math.Max(50, (int)(cue.Duration * 1000)),
                    () => OnActionEnd(cue.Id));
                break;

            case CueType.Fade:
                ModelState(idx, CueState.Playing);
                run.Duration = Math.Max(0.0, cue.Duration);
                ExecFade(cue);
                Schedule(run, Math.Max(50, (int)(cue.Duration * 1000)),
                    () => OnActionEnd(cue.Id));
                break;

            case CueType.Start:
                ExecStart(cue);
                OnActionEnd(cue.Id);
                break;

            case CueType.Stop:
                ExecStop(cue);
                OnActionEnd(cue.Id);
                break;

            case CueType.Script:
                ExecScript(cue);
                OnActionEnd(cue.Id);
                break;

            case CueType.Group:
                FireGroupChildren(idx);
                OnActionEnd(cue.Id);
                break;

            default:   // MEMO u otros: sin acción
                OnActionEnd(cue.Id);
                break;
        }
    }

    /// <summary>Al disparar un media, corta otros cues según el modo de GO.
    /// El vídeo es SIEMPRE exclusivo (una sola salida).</summary>
    private void ApplyGoExclusivity(Cue cue)
    {
        string mode = _settings.Get("go_mode", "concurrent");
        bool stopAll = mode == "sequential" || (mode == "per_cue" && cue.StopOthers);
        foreach (int otherId in _running.Keys.ToList())
        {
            if (otherId == cue.Id || !_running.TryGetValue(otherId, out var other))
                continue;
            if (stopAll || (cue.Type == CueType.Video && other.Kind == CueType.Video))
                EndCue(otherId, markStopped: true, fade: 0.0);
        }
    }

    /// <summary>Funde y detiene otros cues activos según el ámbito (Triggers).</summary>
    private void ApplyFadeOthers(Cue cue)
    {
        if (!cue.FadeOthers)
            return;
        double t = Math.Max(0.0, cue.FadeOthersTime);
        foreach (int otherId in _running.Keys.ToList())
        {
            if (otherId == cue.Id)
                continue;
            var other = _model.GetById(otherId);
            if (other is null)
                continue;
            bool inScope = cue.FadeOthersScope is FadeScope.All or FadeScope.List
                || (cue.FadeOthersScope == FadeScope.Peers && other.ParentId == cue.ParentId);
            if (inScope)
                EndCue(otherId, markStopped: true, fade: t);
        }
    }

    /// <summary>Dispara un cue por id sin mover el playhead (hotkey/START externo).</summary>
    public void TriggerCueId(int cueId)
    {
        int idx = _model.IndexOfId(cueId);
        if (idx >= 0)
            Trigger(idx, advanceChain: false);
    }

    private void ContinueNext(int idx, bool advanceChain)
    {
        int nxt = idx + 1;
        if (nxt >= _model.Count)
            return;
        if (advanceChain)
            GoCue(nxt);
        else
            Trigger(nxt, advanceChain: false);
    }

    /// <summary>La acción del cue terminó. Encadena AUTO_FOLLOW y retira el cue.</summary>
    private void OnActionEnd(int cueId, double fade = 0.0)
    {
        if (!_running.TryGetValue(cueId, out var run))
            return;
        int idx = _model.IndexOfId(cueId);
        var cue = _model.GetCue(idx);

        if (cue is not null && cue.ContinueMode == ContinueMode.AutoFollow)
            ContinueNext(idx, run.AdvanceChain);

        EndCue(cueId, markStopped: true, fade: fade);
    }

    /// <summary>La duración fijada expiró antes del fin natural: parar con fade_out.</summary>
    private void DurationElapsed(int cueId)
    {
        var cue = _model.GetById(cueId);
        if (cue is null)
            return;
        if (_running.TryGetValue(cueId, out var run))
            run.ManualStop = true;   // evita doble action-end desde el engine
        double fade = cue.IsMedia() ? cue.FadeOut : 0.0;
        OnActionEnd(cueId, fade: fade);
    }

    // ==================================================================
    // CUES QUE APUNTAN A OTRO CUE
    // ==================================================================

    private void ExecFade(Cue cue)
    {
        var target = cue.TargetId is int tid ? _model.GetById(tid) : null;
        if (target is null)
        {
            _view.FlashStatus("FADE sin objetivo válido — ignorado");
            return;
        }
        bool stopAfter = cue.FadeStopOnDone && cue.Volume <= 0.0001;
        if (_audio.IsPlaying(target.Id))
            _audio.Fade(target.Id, cue.Volume, cue.Duration, stopAfter);
        else if (_video.IsPlaying(target.Id))
            _video.FadeVolume(target.Id, cue.Volume, cue.Duration, stopAfter);
        else
            _view.FlashStatus($"FADE → cue {target.DisplayNumber} no está sonando");
    }

    private void ExecStart(Cue cue)
    {
        if (cue.TargetId is not int tid)
        {
            _view.FlashStatus("START sin objetivo — ignorado");
            return;
        }
        int tgtIdx = _model.IndexOfId(tid);
        if (tgtIdx < 0)
        {
            _view.FlashStatus("START: objetivo inexistente — ignorado");
            return;
        }
        Trigger(tgtIdx, advanceChain: false);
    }

    private void ExecStop(Cue cue)
    {
        if (cue.TargetId is not int tid)
        {
            StopEverything(fade: cue.FadeOut);
            return;
        }
        var tgt = _model.GetById(tid);
        if (tgt is not null && _running.ContainsKey(tgt.Id))
            EndCue(tgt.Id, markStopped: true, fade: cue.FadeOut);
    }

    private void ExecScript(Cue cue)
    {
        string path = cue.FilePath.Trim();
        if (path.Length == 0)
        {
            _view.FlashStatus("SCRIPT sin archivo — ignorado");
            return;
        }
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            ProcessStartInfo psi = ext switch
            {
                ".py" => new ProcessStartInfo("python", $"\"{path}\""),
                ".bat" or ".cmd" => new ProcessStartInfo("cmd", $"/c \"{path}\""),
                ".ps1" => new ProcessStartInfo("powershell",
                    $"-ExecutionPolicy Bypass -File \"{path}\""),
                _ => new ProcessStartInfo(path) { UseShellExecute = true },
            };
            Process.Start(psi);
            _view.FlashStatus($"SCRIPT lanzado: {Path.GetFileName(path)}");
        }
        catch (Exception exc)
        {
            _view.FlashStatus($"SCRIPT falló: {exc.Message}");
        }
    }

    // ==================================================================
    // FIN DE CUE / STOP GLOBAL
    // ==================================================================

    /// <summary>Retira un cue de la ejecución: cancela timers y para su motor.</summary>
    private void EndCue(int cueId, bool markStopped, double fade = 0.0)
    {
        if (!_running.Remove(cueId, out var run))
            return;
        foreach (var t in run.Timers)
            t.Dispose();
        run.Timers.Clear();

        if (run.Kind == CueType.Audio && _audio.IsPlaying(cueId))
        {
            _audio.Stop(cueId, fade);
        }
        else if (run.Kind == CueType.Video)
        {
            // Llamar Stop aunque el vídeo haya hecho end-file: termina el player
            // y limpia a negro para que su último frame no quede pegado.
            if (fade > 0.0 && _video.IsPlaying(cueId))
                _video.FadeVolume(cueId, 0.0, fade, stopAfter: true);
            else
                _video.Stop(cueId);
        }

        if (markStopped)
        {
            int idx = _model.IndexOfId(cueId);
            if (idx >= 0)
                ModelState(idx, CueState.Stopped);
        }
    }

    private void StopEverything(double fade)
    {
        // Cancelar continuaciones pendientes (AUTO_CONTINUE)
        foreach (var t in _chainTimers)
            t.Dispose();
        _chainTimers.Clear();

        foreach (int cueId in _running.Keys.ToList())
            EndCue(cueId, markStopped: true, fade: fade);

        if (fade <= 0.0)
        {
            // Parada inmediata: barrido de seguridad de ambos motores.
            _audio.StopAll(0.0);
            _video.StopAll();
        }
        else
        {
            // Con fundido: barrer solo el audio suelto; los vídeos ya se funden
            // por cue en EndCue — NO cortarlos.
            _audio.StopAll(fade);
        }

        _paused = false;
        _view.UpdateTransport(false);
        _view.UpdateCountdown(null);
        _view.UpdateActiveCues(Array.Empty<ActiveCueInfo>());
        _view.UpdateVuMeter(0.0f, 0.0f);
        RefreshVideoTally();
    }

    // ==================================================================
    // SEÑALES DE LOS MOTORES
    // ==================================================================

    private void OnAudioStateChanged(int cueId, MediaCueState state)
    {
        if (state == MediaCueState.Stopped &&
            _running.TryGetValue(cueId, out var run) && !run.ManualStop)
        {
            OnActionEnd(cueId);   // fin natural del audio → encadenar
        }
    }

    private void OnVideoStateChanged(int cueId, MediaCueState state)
    {
        if (state == MediaCueState.Stopped)
        {
            if (_running.TryGetValue(cueId, out var run) && !run.ManualStop)
                OnActionEnd(cueId);
            RefreshVideoTally();
        }
    }

    private void OnLatencyReport(int cueId, double ms)
    {
        int idx = _model.IndexOfId(cueId);
        if (idx >= 0)
        {
            _model.SetLatency(idx, ms);
            _view.RefreshRow(idx);
        }
    }

    private void OnAudioError(int cueId, string message)
    {
        int idx = _model.IndexOfId(cueId);
        if (idx >= 0)
        {
            _model.SetState(idx, CueState.Error);
            _view.RefreshRow(idx);
        }
        _running.Remove(cueId);
        _view.FlashStatus($"Audio: {message}");
    }

    private void OnVideoError(int cueId, string message)
    {
        if (message.StartsWith("[AVISO]", StringComparison.Ordinal))
        {
            _view.FlashStatus(message);
            return;
        }
        int idx = _model.IndexOfId(cueId);
        if (idx >= 0)
        {
            _model.SetState(idx, CueState.Error);
            _view.RefreshRow(idx);
        }
        _running.Remove(cueId);
        _view.FlashStatus($"Vídeo: {message}");
    }


    // ==================================================================
    // TICK — panel de activos + countdown (la UI lo llama cada ~100 ms)
    // ==================================================================

    /// <summary>Reloj inyectable (los tests fijan la hora para el wall-clock).</summary>
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.Now;

    private string _lastWallclockSecond = "";

    /// <summary>Disparo por hora del día (Triggers → Wall-clock). Se evalúa una
    /// vez por segundo desde el Tick de la UI.</summary>
    private void CheckWallclock()
    {
        var now = NowProvider();
        string hhmmss = now.ToString("HH:mm:ss");
        if (hhmmss == _lastWallclockSecond)
            return;
        _lastWallclockSecond = hhmmss;

        int dayBit = ((int)now.DayOfWeek + 6) % 7;   // lunes=0 … domingo=6
        for (int i = 0; i < _model.Count; i++)
        {
            var c = _model.Cues[i];
            if (c.WallclockEnabled && c.Armed &&
                c.WallclockTime == hhmmss &&
                ((c.WallclockDays >> dayBit) & 1) == 1 &&
                !_running.ContainsKey(c.Id))
            {
                Log($"WALL-CLOCK {hhmmss} → {c.DisplayNumber} · {c.Name}");
                Trigger(i, advanceChain: false);
            }
        }
    }

    public void Tick()
    {
        CheckWallclock();

        if (_running.Count == 0)
        {
            if (_lastHadRunning)
            {
                _view.UpdateActiveCues(Array.Empty<ActiveCueInfo>());
                _view.UpdateCountdown(null);
                _lastHadRunning = false;
            }
            return;
        }
        _lastHadRunning = true;

        var rows = new List<ActiveCueInfo>();
        double? primaryRemaining = null;
        foreach (var run in _running.Values)
        {
            // Rellenar la duración real de un media (0 → longitud natural)
            if (run.Duration <= 0.0 && run.Kind is CueType.Audio or CueType.Video)
            {
                double d = run.Kind == CueType.Audio
                    ? _audio.GetDuration(run.CueId)
                    : _video.GetDuration(run.CueId);
                if (d > 0.0)
                    run.Duration = d;
            }

            // Preferir la posición real del motor (más exacta que el reloj)
            double elapsed = _paused
                ? Stopwatch.GetElapsedTime(run.TStartTicks, _pauseT0Ticks).TotalSeconds
                : Stopwatch.GetElapsedTime(run.TStartTicks).TotalSeconds;
            if (run.Kind == CueType.Audio)
            {
                double e = _audio.GetElapsed(run.CueId);
                if (e > 0.0) elapsed = e;
            }
            else if (run.Kind == CueType.Video)
            {
                double e = _video.GetElapsed(run.CueId);
                if (e > 0.0) elapsed = e;
            }

            double? remaining = null, fraction = null;
            if (run.Duration > 0.0)
            {
                remaining = Math.Max(0.0, run.Duration - elapsed);
                fraction = Math.Min(1.0, elapsed / run.Duration);
            }
            rows.Add(new ActiveCueInfo(
                run.CueId, run.Number, run.Name, run.Kind, run.Color,
                elapsed, remaining, fraction));
            if (remaining is double r && (primaryRemaining is null || r < primaryRemaining))
                primaryRemaining = r;
        }

        _view.UpdateActiveCues(rows);
        _view.UpdateCountdown(primaryRemaining);
    }

    // ==================================================================
    // PLAYHEAD / MODELO / VISTA
    // ==================================================================

    private void Log(string message) => ShowLog?.Invoke(message);

    private void SetPlayhead(int index)
    {
        int count = _model.Count;
        _playhead = count == 0 ? 0 : Math.Clamp(index, 0, count);  // count = "más allá del final"
        ActiveIndexChanged?.Invoke(_playhead);
        _view.SetPlayhead(_playhead);
    }

    private void ModelState(int index, CueState state)
    {
        _model.SetState(index, state);
        _view.RefreshRow(index);
    }

    /// <summary>Vídeo actualmente en la salida (para el monitor PGM):
    /// (ruta, segundos reproducidos), o null si la salida está en negro.
    /// Port de _update_program_monitor de Python.</summary>
    public (string FilePath, double Elapsed)? GetProgramVideo()
    {
        Cue? last = null;
        foreach (var (cid, r) in _running)
            if (r.Kind == CueType.Video && _video.IsPlaying(cid)
                && _model.GetById(cid) is Cue c)
                last = c;
        return last is null ? null : (last.FilePath, _video.GetElapsed(last.Id));
    }

    private void RefreshVideoTally()
    {
        Cue? last = null;
        foreach (var (cid, r) in _running)
            if (r.Kind == CueType.Video && _model.GetById(cid) is Cue c)
                last = c;
        _view.SetOutputContent(last?.Name);
    }

    private void PreloadUpcoming()
    {
        var nxt = _model.GetCue(_playhead);
        if (nxt is { Type: CueType.Audio } && nxt.FilePath.Length > 0)
            _audio.Preload(nxt.Id, nxt.FilePath);
    }

    private VideoGeometry GeoFor(Cue cue)
    {
        var (sw, sh) = _view.SurfaceDimensions();
        return new VideoGeometry(
            Mode: cue.GeoMode == GeoMode.Custom ? "CUSTOM" : "FULL",
            PreserveAspect: cue.PreserveAspect,
            TranslateX: cue.TranslateX, TranslateY: cue.TranslateY,
            ScaleX: cue.ScaleX, RotateZ: cue.RotateZ,
            SurfW: sw, SurfH: sh);
    }

    /// <summary>Timer asociado a un cue en curso: se cancela al terminar/parar.</summary>
    private void Schedule(RunningCue run, int ms, Action fn) =>
        run.Timers.Add(_scheduler.Schedule(Math.Max(0, ms), fn));

    /// <summary>Timer de continuación independiente del ciclo de vida de un cue.
    /// Solo lo cancela Stop All / Panic. Se auto-retira al dispararse.</summary>
    private void ScheduleChain(int ms, Action fn)
    {
        IDisposable? handle = null;
        handle = _scheduler.Schedule(Math.Max(0, ms), () =>
        {
            if (handle is not null)
                _chainTimers.Remove(handle);
            fn();
        });
        _chainTimers.Add(handle);
    }

    // ==================================================================
    // UNDO / REDO — snapshots JSON del modelo
    // ==================================================================

    private long _lastUndoPushTicks;

    private void PushUndo()
    {
        _redoStack.Clear();
        // Coalescencia de ráfagas: un slider o un arrastre commitean decenas de
        // veces por segundo — solo la PRIMERA instantánea del gesto va al undo.
        if (_lastUndoPushTicks != 0 &&
            Stopwatch.GetElapsedTime(_lastUndoPushTicks).TotalMilliseconds < 800)
            return;
        _lastUndoPushTicks = Stopwatch.GetTimestamp();
        _undoStack.Add((_model.ToJson(), _playhead));
        if (_undoStack.Count > MaxUndo)
            _undoStack.RemoveAt(0);
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Undo()
    {
        if (_undoStack.Count == 0)
            return;
        _redoStack.Add((_model.ToJson(), _playhead));
        var (json, playhead) = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreSnapshot(json, playhead);
        _view.FlashStatus("↶ Deshacer");
    }

    public void Redo()
    {
        if (_redoStack.Count == 0)
            return;
        _undoStack.Add((_model.ToJson(), _playhead));
        var (json, playhead) = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreSnapshot(json, playhead);
        _view.FlashStatus("↷ Rehacer");
    }

    private void RestoreSnapshot(string json, int playhead)
    {
        // No detiene la reproducción: los motores van por id, y las instancias
        // en _running conservan copia de los datos que necesitan.
        try
        {
            _model.ReplaceAll(CueModel.FromJson(json));
        }
        catch (FormatException)
        {
            return;   // snapshot corrupto: no tocar el modelo
        }
        _view.ReloadTable();
        SetPlayhead(playhead);
    }

    // ==================================================================
    // COPIAR / PEGAR / DUPLICAR
    // ==================================================================

    /// <summary>Copia los cues de las filas dadas (con sus descendientes).</summary>
    public void CopyCues(IReadOnlyList<int> rows)
    {
        string? json = SerializeRows(rows);
        if (json is null)
            return;
        _clipboard = json;
        _view.FlashStatus("Cues copiados");
    }

    /// <summary>Pega el portapapeles debajo de la fila dada, con ids nuevos.</summary>
    public void PasteCues(int row)
    {
        if (_clipboard is null)
            return;
        InsertSerialized(_clipboard, row);
    }

    /// <summary>Duplica las filas dadas justo debajo (sin tocar el portapapeles).</summary>
    public void DuplicateCues(IReadOnlyList<int> rows)
    {
        string? json = SerializeRows(rows);
        if (json is null)
            return;
        InsertSerialized(json, rows.Max());
    }

    private string? SerializeRows(IReadOnlyList<int> rows)
    {
        var valid = rows.Where(r => r >= 0 && r < _model.Count)
                        .Distinct().OrderBy(r => r).ToList();
        if (valid.Count == 0)
            return null;
        // Incluir descendientes de los grupos seleccionados (en orden de lista)
        var ids = new HashSet<int>();
        foreach (int r in valid)
        {
            var c = _model.Cues[r];
            ids.Add(c.Id);
            foreach (var d in _model.Descendants(c.Id))
                ids.Add(d.Id);
        }
        var block = _model.Cues.Where(c => ids.Contains(c.Id)).ToList();
        return System.Text.Json.JsonSerializer.Serialize(block, CueJson);
    }

    private void InsertSerialized(string json, int afterRow)
    {
        List<Cue> cues;
        try
        {
            cues = System.Text.Json.JsonSerializer.Deserialize<List<Cue>>(json, CueJson)
                ?? new List<Cue>();
        }
        catch (System.Text.Json.JsonException) { return; }
        if (cues.Count == 0)
            return;

        PushUndo();

        // Ids nuevos preservando la jerarquía interna del bloque
        var idMap = new Dictionary<int, int>();
        int nextId = _model.NextId();
        foreach (var c in cues)
            idMap[c.Id] = nextId++;
        var refCue = afterRow >= 0 ? _model.GetCue(afterRow) : null;
        foreach (var c in cues)
        {
            int oldId = c.Id;
            c.Id = idMap[oldId];
            c.ParentId = c.ParentId is int pid && idMap.TryGetValue(pid, out int npid)
                ? npid                     // padre dentro del bloque pegado
                : refCue?.ParentId;        // nivel superior: hereda contexto
            c.State = CueState.Idle;
            c.LatencyMs = null;
        }

        int insertAt = afterRow >= 0 ? afterRow + 1 : _model.Count;
        for (int i = 0; i < cues.Count; i++)
            _model.InsertCue(insertAt + i, cues[i]);
        _view.ReloadTable();
        SetPlayhead(insertAt);
        _view.FlashStatus($"{cues.Count} cue(s) pegados");
    }

    private static readonly System.Text.Json.JsonSerializerOptions CueJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Mueve un cue de fila (drag &amp; drop de la tabla).</summary>
    public void OnMoveCue(int fromRow, int toRow)
    {
        if (fromRow == toRow ||
            fromRow < 0 || fromRow >= _model.Count ||
            toRow < 0 || toRow >= _model.Count)
            return;
        PushUndo();
        _model.MoveCue(fromRow, toRow);
        _view.ReloadTable();
        SetPlayhead(toRow);
    }

    // ==================================================================
    // GESTIÓN DE LA LISTA (añadir / importar / editar / guardar)
    // ==================================================================

    public void AddNewCue(CueType cueType = CueType.Audio)
    {
        PushUndo();
        var cue = new Cue
        {
            Id = _model.NextId(),
            Type = cueType,
            Name = $"{Capitalize(cueType)} cue",
            Duration = cueType is CueType.Wait or CueType.Fade ? 3.0 : 0.0,
            Number = _model.NextNumber(),
        };
        _model.AddCue(cue);
        _view.AppendCueRow(cue);
        SetPlayhead(_model.Count - 1);
    }

    /// <summary>Inserta un cue nuevo del tipo dado justo debajo de la fila row.</summary>
    public void OnInsertCue(CueType cueType, int row)
    {
        PushUndo();
        int insertAt = row >= 0 ? row + 1 : _model.Count;
        // Heredar el grupo del cue de referencia (mantiene contigüidad)
        var refCue = row >= 0 ? _model.GetCue(row) : null;
        var cue = new Cue
        {
            Id = _model.NextId(),
            Type = cueType,
            Name = $"{Capitalize(cueType)} cue",
            Duration = cueType is CueType.Wait or CueType.Fade ? 3.0 : 0.0,
            Number = _model.NextNumber(),
            ParentId = refCue?.ParentId,
        };
        _model.InsertCue(insertAt, cue);
        _view.ReloadTable();
        SetPlayhead(Math.Min(insertAt, _model.Count - 1));
    }

    /// <summary>Elimina el cue de la fila dada (deteniéndolo antes si sonaba).</summary>
    public void OnDeleteCue(int row)
    {
        var cue = _model.GetCue(row);
        if (cue is null)
            return;
        PushUndo();
        if (_running.ContainsKey(cue.Id))
            EndCue(cue.Id, markStopped: false);
        _model.RemoveCue(row);
        _view.ReloadTable();
        int count = _model.Count;
        SetPlayhead(count > 0 ? Math.Min(row, count - 1) : 0);
    }

    private static string Capitalize(CueType t)
    {
        string s = t.ToString().ToUpperInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // Grupos: agrupar / desagrupar / colapsar
    // ------------------------------------------------------------------

    /// <summary>Crea un GROUP que contiene los cues seleccionados (y descendientes).</summary>
    public void OnGroupSelected(IReadOnlyList<int> rows)
    {
        var valid = rows.Where(r => r >= 0 && r < _model.Count).Distinct().OrderBy(r => r).ToList();
        if (valid.Count == 0)
            return;
        PushUndo();
        var cues = _model.Cues;
        var selCues = valid.Select(r => cues[r]).ToList();

        // Conjunto a mover = seleccionados + todos sus descendientes
        var moveIds = new HashSet<int>();
        foreach (var c in selCues)
        {
            moveIds.Add(c.Id);
            foreach (var d in _model.Descendants(c.Id))
                moveIds.Add(d.Id);
        }
        var block = cues.Where(c => moveIds.Contains(c.Id)).ToList();   // conserva orden

        int gid = _model.NextId();
        var group = new Cue
        {
            Id = gid, Type = CueType.Group, Name = "Grupo", Duration = 0.0,
            Number = _model.NextNumber(),
            ParentId = selCues[0].ParentId, GroupMode = GroupMode.Enter,
        };
        // Solo los seleccionados de nivel superior cuelgan del nuevo grupo
        foreach (var c in selCues)
            c.ParentId = gid;

        int first = valid[0];
        var result = new List<Cue>();
        for (int i = 0; i < cues.Count; i++)
        {
            if (i == first)
            {
                result.Add(group);
                result.AddRange(block);
            }
            if (moveIds.Contains(cues[i].Id))
                continue;
            result.Add(cues[i]);
        }
        _model.ReplaceAll(result);
        _view.ReloadTable();
        int gi = _model.IndexOfId(gid);
        SetPlayhead(gi >= 0 ? gi : 0);
        _view.FlashStatus($"{selCues.Count} cue(s) agrupados");
    }

    /// <summary>Deshace un grupo: promueve sus hijos directos y elimina el GROUP.</summary>
    public void OnUngroup(int row)
    {
        var group = _model.GetCue(row);
        if (group is null || group.Type != CueType.Group)
            return;
        PushUndo();
        foreach (var c in _model.DirectChildren(group.Id))
            c.ParentId = group.ParentId;
        _rrState.Remove(group.Id);
        _model.RemoveCue(row);
        _view.ReloadTable();
        int count = _model.Count;
        SetPlayhead(count > 0 ? Math.Min(row, count - 1) : 0);
        _view.FlashStatus("Grupo deshecho");
    }

    /// <summary>Contrae/expande un grupo (oculta/muestra sus descendientes).</summary>
    public void OnToggleCollapse(int row)
    {
        var cue = _model.GetCue(row);
        if (cue is null || cue.Type != CueType.Group)
            return;
        cue.Collapsed = !cue.Collapsed;
        _view.RefreshRow(row);
        _view.ApplyGroupVisibility();
    }

    /// <summary>Crea cues a partir de archivos (drag-drop / importación).
    /// La duración real del archivo se lee con ffprobe al importar.</summary>
    public void AddCuesFromFiles(int targetRow, IReadOnlyList<string> paths)
    {
        PushUndo();
        int insertAt = targetRow >= 0 ? targetRow : _model.Count;
        int created = 0;
        foreach (string path in paths)
        {
            var result = MediaImporter.ValidateFile(path);
            if (!result.Valid)
                continue;
            var ctype = result.MediaType == "VIDEO" ? CueType.Video : CueType.Audio;
            var cue = new Cue
            {
                Id = _model.NextId(),
                Type = ctype,
                Name = Path.GetFileNameWithoutExtension(path),
                Duration = MediaImporter.ProbeDurationSeconds(result.FilePath),
                Number = _model.NextNumber(),
                FilePath = result.FilePath,
            };
            _model.InsertCue(insertAt + created, cue);
            created++;
        }
        if (created > 0)
        {
            _view.ReloadTable();
            SetPlayhead(Math.Min(insertAt, _model.Count - 1));
            _view.FlashStatus($"{created} cue(s) creado(s) desde archivo.");
        }
    }

    // ------------------------------------------------------------------
    // Edición desde el inspector
    // ------------------------------------------------------------------

    /// <summary>Aplica los cambios del inspector a un cue. Los cambios de
    /// volumen/geometría se reflejan EN VIVO si el cue está sonando.</summary>
    public void OnCueEditCommitted(int row, IReadOnlyDictionary<string, object?> fields)
    {
        var cue = _model.GetCue(row);
        if (cue is null)
            return;

        PushUndo();
        bool fileChanged = fields.TryGetValue("file_path", out var fp)
            && !string.Equals(fp?.ToString() ?? "", cue.FilePath, StringComparison.Ordinal);

        if (fields.ContainsKey("file_path") && !fields.ContainsKey("type"))
        {
            // Drag & drop: dejar que MediaImporter detecte el tipo
            MediaImporter.AssignToCue(cue, fields["file_path"]?.ToString() ?? "");
            _model.UpdateCue(row, cue);
            _view.RefreshRow(row);
            if (row == _playhead)
                _view.SetPlayhead(_playhead);
            return;
        }

        foreach (var (key, value) in fields)
            CueFields.Apply(cue, key, value);

        if (fileChanged)
        {
            cue.LatencyMs = null;
            _audio.InvalidateCache(cue.Id);   // invalidar caché de audio
        }

        if (fields.ContainsKey("hotkey"))
            _view.RebuildHotkeys();

        ApplyLiveEdits(cue, fields);

        _view.RefreshRow(row);
        if (row == _playhead)
            _view.SetPlayhead(_playhead);
    }

    private static readonly HashSet<string> GeoKeys = new()
    {
        "geo_mode", "preserve_aspect", "translate_x", "translate_y",
        "scale_x", "scale_y", "rotate_z",
    };

    private void ApplyLiveEdits(Cue cue, IReadOnlyDictionary<string, object?> fields)
    {
        if (!_running.ContainsKey(cue.Id))
            return;
        if (fields.ContainsKey("volume"))
        {
            if (_audio.IsPlaying(cue.Id))
                _audio.SetVolume(cue.Id, cue.Volume);
            if (_video.IsPlaying(cue.Id))
                _video.SetVolume(cue.Id, cue.Volume);
        }
        if (cue.Type == CueType.Video && _video.IsPlaying(cue.Id)
            && fields.Keys.Any(GeoKeys.Contains))
        {
            _video.ApplyGeometry(cue.Id, GeoFor(cue));
        }
    }

    // ------------------------------------------------------------------
    // Guardar / cargar show en JSON (la vista pone los diálogos)
    // ------------------------------------------------------------------

    /// <summary>Guarda el show. El media que vive junto al .json (o debajo) se
    /// guarda con RUTA RELATIVA → el show es portátil entre máquinas.</summary>
    public void SaveShow(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            path += ".json";
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";

        // Relativizar temporalmente y restaurar tras serializar
        var originals = new Dictionary<int, string>();
        foreach (var c in _model.Cues)
        {
            if (c.FilePath.Length == 0 || !Path.IsPathRooted(c.FilePath) || dir.Length == 0)
                continue;
            string rel = Path.GetRelativePath(dir, c.FilePath);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            {
                originals[c.Id] = c.FilePath;
                c.FilePath = rel;
            }
        }
        try
        {
            File.WriteAllText(path, _model.ToJson());
            Log($"Show guardado: {path}");
            _view.FlashStatus($"Show guardado: {Path.GetFileName(path)}");
        }
        catch (IOException exc)
        {
            _view.ShowError("Error al guardar", exc.Message);
        }
        finally
        {
            foreach (var c in _model.Cues)
                if (originals.TryGetValue(c.Id, out string? orig))
                    c.FilePath = orig;
        }
    }

    /// <summary>Guardado silencioso para el autosave (sin diálogos ni status).</summary>
    public bool Autosave(string path)
    {
        if (_model.Count == 0)
            return false;
        try
        {
            File.WriteAllText(path, _model.ToJson());
            return true;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Carga un show: resuelve rutas relativas contra la carpeta del
    /// .json y marca en ERROR los cues cuyo media no existe.</summary>
    public void LoadShow(string path)
    {
        List<Cue> newCues;
        try
        {
            newCues = CueModel.FromJson(File.ReadAllText(path));
        }
        catch (Exception exc) when (exc is IOException or FormatException)
        {
            _view.ShowError("Error al cargar", exc.Message);
            return;
        }
        if (newCues.Count == 0)
        {
            _view.ShowError("Show vacío", "El archivo no contiene ningún cue.");
            return;
        }

        PushUndo();   // deshacer la carga restaura el show anterior
        StopEverything(fade: 0.0);

        // Rutas relativas → absolutas respecto a la carpeta del show
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        foreach (var c in newCues)
            if (c.FilePath.Length > 0 && !Path.IsPathRooted(c.FilePath) && dir.Length > 0)
                c.FilePath = Path.GetFullPath(Path.Combine(dir, c.FilePath));

        _model.ReplaceAll(newCues);

        // Media faltante → estado ERROR visible en la tabla
        int missing = 0;
        for (int i = 0; i < _model.Count; i++)
        {
            var c = _model.Cues[i];
            if ((c.IsMedia() || c.Type == CueType.Script) &&
                c.FilePath.Length > 0 && !File.Exists(c.FilePath))
            {
                _model.SetState(i, CueState.Error);
                missing++;
            }
        }

        _view.ReloadTable();
        SetPlayhead(0);
        Log($"Show cargado: {path}");
        _view.FlashStatus(missing > 0
            ? $"Show cargado — ⚠ {missing} archivo(s) de media NO encontrados"
            : $"Show cargado: {Path.GetFileName(path)}");
    }

    // ------------------------------------------------------------------
    // Cierre limpio
    // ------------------------------------------------------------------

    /// <summary>Detiene todo y libera los motores (llamar al cerrar la app).</summary>
    public void Shutdown()
    {
        foreach (var t in _chainTimers)
            t.Dispose();
        _chainTimers.Clear();
        foreach (int cueId in _running.Keys.ToList())
            EndCue(cueId, markStopped: false);
        _audio.Cleanup();
        _video.Cleanup();
    }
}
