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
        public bool ActionStarted;           // false = aún en pre_wait
        public double Offset;                // arranque con seek (play-desde-cursor)
        public bool SuppressChain;           // la proyección ya trae la cadena resuelta
        public double? PreWaitOverride;      // espera programada del timeline (≠ pre_wait)
        public bool SuppressVisualFadeOut;   // crossfade misma pista: el entrante
                                             // lo cubre fundiendo — no oscurecer
    }

    /// <summary>Elemento de "reproducir desde el cursor del timeline":
    /// Offset &gt; 0 = el bloque cruza el cursor (arrancar YA con seek);
    /// Delay &gt; 0 = el bloque empieza más tarde (programar su acción).</summary>
    public sealed record TimelinePlayItem(int CueId, double Offset, double Delay);

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
    private readonly ShowFileService _files;
    private readonly CueListEditor _listEditor;
    private readonly RemoteRouter _remote;
    private readonly UndoService _undo;

    private int _playhead;
    private bool _paused;
    private long _pauseT0Ticks;
    private bool _lastHadRunning;

    private readonly Dictionary<int, RunningCue> _running = new();
    private readonly Dictionary<int, HashSet<int>> _rrState = new();   // RANDOM round-robin
    private readonly List<IDisposable> _chainTimers = new();

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
        _video.SetLayerHandleProvider(
            (screen, layer) => _view.GetVideoOutputLayerHandle(screen, layer));

        // Undo/redo extraído (paso 4): el controlador envuelve Push() en
        // PushUndo() para mantener el flag HasUnsavedChanges en su sitio.
        _undo = new UndoService(
            model, view,
            getPlayhead: () => _playhead,
            setPlayhead: SetPlayhead);

        // Persistencia extraída (paso 1 de la auditoría 02): el servicio recibe
        // como callbacks lo poco que necesita del núcleo — el controlador sigue
        // siendo el único dueño del estado de reproducción.
        _files = new ShowFileService(
            model, view,
            log: Log,
            pushUndo: PushUndo,
            beforeLoad: () =>
            {
                StopEverything(fade: 0.0);
                _video.DiscardStandby();   // los ids del show anterior ya no valen
            },
            setPlayhead: SetPlayhead,
            invalidateAudioCache: _audio.InvalidateCache);

        // Edición de lista extraída (paso 2): manipulación pura del modelo.
        // Los dos únicos contactos con el estado de reproducción entran como
        // callbacks: parar un cue sonando antes de borrarlo y olvidar el
        // round-robin de un grupo deshecho.
        _listEditor = new CueListEditor(
            model, view,
            pushUndo: PushUndo,
            setPlayhead: SetPlayhead,
            stopIfRunning: cueId =>
            {
                if (_running.ContainsKey(cueId))
                    EndCue(cueId, markStopped: false);
            },
            forgetGroupState: groupId => _rrState.Remove(groupId));

        // Ruteo remoto extraído (paso 3): OSC/MIDI/MTC/wall-clock parsean y
        // despachan hacia estas acciones — nunca mutan _running directamente.
        _remote = new RemoteRouter(
            model, settings, view,
            log: Log,
            go: OnGoPressed,
            stopAll: OnStopPressed,
            panic: OnPanic,
            fadeAll: OnFadeAll,
            pauseToggle: OnPauseToggle,
            triggerCueId: TriggerCueId,
            triggerAt: idx => Trigger(idx, advanceChain: false),
            stopCue: cueId => EndCue(cueId, markStopped: true),
            isRunning: _running.ContainsKey);

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

    /// <summary>Cue que suena por la audición local (para EQ/RTA en vivo).</summary>
    private int _auditionSourceCueId = -1;

    /// <summary>Audición local: reproduce el cue de la fila por el dispositivo
    /// de audición (cascos) SIN pasar por el flujo del show. Toggle.</summary>
    public void AuditionToggle(int row)
    {
        if (_audio.IsPlaying(AuditionCueId))
        {
            _audio.Stop(AuditionCueId);
            _auditionSourceCueId = -1;
            _view.FlashStatus("🎧 Audición detenida");
            return;
        }
        var cue = _model.GetCue(row);
        if (cue is null || cue.Type != CueType.Audio || cue.FilePath.Length == 0)
        {
            _view.FlashStatus("Audición: selecciona un cue de AUDIO con archivo");
            return;
        }
        _auditionSourceCueId = cue.Id;
        _audio.Play(AuditionCueId, cue.FilePath, new AudioPlayParams(
            Volume: cue.Volume, TrimStart: cue.TrimStart, TrimEnd: cue.TrimEnd,
            OutputDevice: _settings.Get("audition_device_name", ""),
            Eq: EqFor(cue)));
        _view.FlashStatus($"🎧 Audición: {cue.Name}");
    }

    /// <summary>Cadena de EQ del cue para el motor (ganancia + filtros).</summary>
    private static AudioEqParams EqFor(Cue c) => new(
        GainDb: c.GainDb,
        EqEnabled: c.EqEnabled,
        HpfOn: c.HpfOn, HpfFreq: c.HpfFreq,
        LpfOn: c.LpfOn, LpfFreq: c.LpfFreq,
        Bands: new[]
        {
            new EqBand(c.Eq1Freq, c.Eq1Gain, c.Eq1Q),
            new EqBand(c.Eq2Freq, c.Eq2Gain, c.Eq2Q),
            new EqBand(c.Eq3Freq, c.Eq3Gain, c.Eq3Q),
            new EqBand(c.Eq4Freq, c.Eq4Gain, c.Eq4Q),
        },
        Comp: new AudioCompParams(
            c.CompEnabled, c.CompThreshold, c.CompRatio,
            c.CompAttack, c.CompRelease, c.CompMakeup));

    /// <summary>Espectro para el RTA del inspector: del cue sonando, o de su
    /// audición local si es lo que está sonando.</summary>
    public float[]? GetAudioSpectrum(int cueId)
    {
        var s = _audio.GetSpectrum(cueId);
        if (s is null && cueId == _auditionSourceCueId)
            s = _audio.GetSpectrum(AuditionCueId);
        return s;
    }

    /// <summary>Reducción de ganancia del compresor (dB ≥ 0) para el medidor
    /// GR del inspector: del cue sonando, o de su audición local.</summary>
    public float GetAudioGainReduction(int cueId)
    {
        float gr = _audio.GetGainReductionDb(cueId);
        if (gr <= 0f && cueId == _auditionSourceCueId)
            gr = _audio.GetGainReductionDb(AuditionCueId);
        return gr;
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
    // CONTROL REMOTO — implementación en RemoteRouter (OSC/MIDI/MTC/reloj)
    // ==================================================================

    /// <summary>Rutea un mensaje OSC entrante a la acción correspondiente.</summary>
    public void OnOscMessage(string address, IReadOnlyList<object?> args) =>
        _remote.OnOscMessage(address, args);

    /// <summary>Mensaje MIDI entrante: notas globales de GO/STOP y triggers por cue.</summary>
    public void OnMidiMessage(MidiTriggerType type, int channel, int data1, int data2) =>
        _remote.OnMidiMessage(type, channel, data1, data2);

    /// <summary>Posición de timecode entrante (segundos desde 00:00:00:00).</summary>
    public void OnTimecode(double seconds) => _remote.OnTimecode(seconds);

    /// <summary>"HH:MM:SS:FF" (frames a fps dado), "HH:MM:SS.mmm" o "MM:SS".</summary>
    public static double? ParseTimecode(string text, double fps = 30.0) =>
        RemoteRouter.ParseTimecode(text, fps);

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

    /// <summary>Arranca la acción del cue. offset &gt; 0 = "reproducir desde
    /// el cursor": el media hace seek a trim_start+offset y el timer de fin
    /// cuenta solo lo que queda; los tiempos mostrados suman el offset.</summary>
    private void StartAction(int idx, double offset = 0.0)
    {
        var cue = _model.GetCue(idx);
        if (cue is null)
            return;
        if (!_running.TryGetValue(cue.Id, out var run))
            return;
        if (offset > 0.0)
            offset = Math.Min(offset, Math.Max(0.0, ActionDuration(cue) - 0.05));
        run.Offset = offset;
        run.ActionStarted = true;
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
                    Volume: cue.Volume,
                    FadeIn: Math.Max(0.0, cue.FadeIn - offset),
                    FadeOut: cue.FadeOut,
                    Loop: cue.Loop,
                    TrimStart: cue.TrimStart + offset, TrimEnd: cue.TrimEnd,
                    PlayCount: cue.PlayCount, OutputDevice: cue.AudioOutput,
                    Eq: EqFor(cue), Rate: cue.Rate));
                run.Duration = ActionDuration(cue);
                if (run.Duration > 0.0 && !cue.Loop)
                    Schedule(run,
                        (int)(Math.Max(0.0, run.Duration - offset) / RateOf(cue) * 1000),
                        () => DurationElapsed(cue.Id));
                PreloadUpcoming();
                break;

            case CueType.Video:
                ModelState(idx, CueState.Playing);
                // Fade de entrada pendiente (con offset, lo que quede): además
                // de la opacidad, es la señal de CROSSFADE en la misma pista
                // (el motor mantiene vivo al saliente durante el fundido)
                double targetOpacity = cue.Opacity / 100.0;
                double fadeLeft = Math.Max(0.0, cue.FadeIn - offset);
                _video.Play(cue.Id, cue.FilePath, VideoParamsFor(cue, offset));
                RefreshVideoTally();
                if (fadeLeft > 0.0)
                {
                    // CROSSFADE en la misma pista: el saliente queda DEBAJO
                    // del entrante — que NO funda su opacidad (el cruce se
                    // hundiría a negro); su volumen sí funde y su fin lo retira.
                    foreach (var (ocid, orun) in _running)
                    {
                        if (ocid == cue.Id || orun.Kind != CueType.Video)
                            continue;
                        if (_model.GetById(ocid) is not Cue oc
                            || VideoLayerOf(oc) != VideoLayerOf(cue))
                            continue;
                        if (!CanvasMode && ScreenOf(oc) != ScreenOf(cue))
                            continue;
                        orun.SuppressVisualFadeOut = true;
                        _view.SetVideoCueOpacity(ocid, oc.Opacity / 100.0);
                    }
                    SetCueLayerOpacity(cue, 0.0);
                    SetCueLayerOpacity(cue, targetOpacity, fadeLeft);
                }
                else
                {
                    SetCueLayerOpacity(cue, targetOpacity);
                }
                run.Duration = ActionDuration(cue);
                if (run.Duration > 0.0 && !cue.Loop)
                {
                    Schedule(run,
                        (int)(Math.Max(0.0, run.Duration - offset) / RateOf(cue) * 1000),
                        () => VideoEndCheck(cue.Id, 0));
                    // El fade_out del VÍDEO vive DENTRO de sus últimos
                    // fade_out segundos (como lo dibuja el timeline y como
                    // hace el audio), no a partir del final del bloque.
                    double fo = Math.Clamp(cue.FadeOut, 0.0, run.Duration);
                    if (fo > 0.01)
                    {
                        double startIn = Math.Max(0.0, run.Duration - offset - fo);
                        Schedule(run, (int)(startIn / RateOf(cue) * 1000),
                            () => BeginVideoFadeOut(cue.Id, fo, 0));
                    }
                }
                break;

            case CueType.Wait:
                ModelState(idx, CueState.Playing);
                run.Duration = Math.Max(0.0, cue.Duration);
                Schedule(run,
                    Math.Max(50, (int)(Math.Max(0.0, cue.Duration - offset) * 1000)),
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

    /// <summary>Velocidad de reproducción (el timer de fin corre en tiempo de
    /// PARED: un media de 60 s a rate 0,5 tarda 120 s reales).</summary>
    private static double RateOf(Cue c) => Math.Clamp(c.Rate, 0.25, 4.0);

    /// <summary>Duración efectiva de la ACCIÓN: en media, con el TRIM aplicado
    /// (el countdown y el timer de fin deben medir lo que va a sonar de
    /// verdad, no la longitud del archivo).</summary>
    private static double ActionDuration(Cue c)
    {
        if (!c.IsMedia())
            return Math.Max(0.0, c.Duration);
        double end = c.TrimEnd > 0
            ? (c.Duration > 0 ? Math.Min(c.TrimEnd, c.Duration) : c.TrimEnd)
            : c.Duration;
        return Math.Max(0.0, end - c.TrimStart);
    }

    /// <summary>Pista de prioridad visual de un cue de vídeo: la del timeline
    /// (0 = arriba tapa a las demás); sin fijar (-1) → pista 2 por defecto,
    /// que preserva el comportamiento clásico "un vídeo sustituye al otro".</summary>
    private static int VideoLayerOf(Cue c) =>
        c.TimelineLayer < 0 ? 2 : Math.Min(c.TimelineLayer, 7);

    /// <summary>Salida de vídeo del cue (0 = Salida 1 … 3 = Salida 4).</summary>
    private static int ScreenOf(Cue c) => Math.Clamp(c.VideoOutput, 0, 3);

    // ------------------------------------------------------------------
    // MODO CANVAS (estilo Watchout): los cues se colocan en un canvas
    // global y cada salida muestra su recorte. Solo con compositor GPU.
    // ------------------------------------------------------------------

    /// <summary>true si el show opera en modo canvas (Settings → Vídeo).</summary>
    private bool CanvasMode =>
        _settings.Get("video_canvas_mode", false) &&
        _settings.Get("video_compositor", false);

    /// <summary>Tamaño del canvas global (settings surface_*; 0 → FHD).</summary>
    private (int W, int H) CanvasDims()
    {
        int w = _settings.Get("surface_width", 0);
        int h = _settings.Get("surface_height", 0);
        return (w > 0 ? w : 1920, h > 0 ? h : 1080);
    }

    /// <summary>Recortes de las salidas HABILITADAS (la 1 siempre lo está).
    /// Región por salida en settings "x,y,w,h"; ausente → canvas completo.</summary>
    private IReadOnlyList<CanvasView> CanvasViews()
    {
        var (cw, ch) = CanvasDims();
        var list = new List<CanvasView>();
        for (int s = 0; s < 4; s++)
        {
            if (s > 0 && !_settings.Get($"video_output_enabled{s + 1}", false))
                continue;
            string key = s == 0 ? "video_output_region" : $"video_output_region{s + 1}";
            list.Add(ParseRegion(_settings.Get(key, ""), s, cw, ch));
        }
        return list;
    }

    /// <summary>Parsea "x,y,w,h" y lo recorta al canvas; inválido → completo.</summary>
    public static CanvasView ParseRegion(string text, int screen, int cw, int ch)
    {
        var parts = (text ?? "").Split(',');
        if (parts.Length == 4
            && int.TryParse(parts[0].Trim(), out int x)
            && int.TryParse(parts[1].Trim(), out int y)
            && int.TryParse(parts[2].Trim(), out int w)
            && int.TryParse(parts[3].Trim(), out int h)
            && w > 0 && h > 0)
        {
            x = Math.Clamp(x, 0, Math.Max(0, cw - 16));
            y = Math.Clamp(y, 0, Math.Max(0, ch - 16));
            w = Math.Clamp(w, 16, cw - x);
            h = Math.Clamp(h, 16, ch - y);
            return new CanvasView(screen, x, y, w, h);
        }
        return new CanvasView(screen, 0, 0, cw, ch);
    }

    /// <summary>Opacidad del vídeo del cue (compositor GPU): cada cue tiene su
    /// superficie propia en todas las salidas donde se ve — la vista la
    /// localiza por id, valga clásico multi-salida o modo canvas.</summary>
    private void SetCueLayerOpacity(Cue cue, double opacity, double fadeSeconds = 0.0) =>
        _view.SetVideoCueOpacity(cue.Id, opacity, fadeSeconds);

    /// <summary>Al disparar un media, corta otros cues según el modo de GO.
    /// El vídeo es exclusivo POR PISTA: misma pista → sustituye; pistas
    /// distintas conviven (la superior tapa a la inferior).</summary>
    private void ApplyGoExclusivity(Cue cue)
    {
        string mode = _settings.Get("go_mode", "concurrent");
        bool stopAll = mode == "sequential" || (mode == "per_cue" && cue.StopOthers);
        foreach (int otherId in _running.Keys.ToList())
        {
            if (otherId == cue.Id || !_running.TryGetValue(otherId, out var other))
                continue;
            // En modo canvas la pista es GLOBAL (el vídeo vive en todas las
            // salidas); en clásico, exclusiva por (salida, pista).
            // CROSSFADE: si el entrante trae fade_in, el saliente de su misma
            // pista NO se corta aquí — se funde y lo retira su propio fin
            // (el motor mantiene ambos vivos durante el fundido).
            bool sameVideoLayer = cue.Type == CueType.Video
                && other.Kind == CueType.Video
                && cue.FadeIn <= 0.0
                && _model.GetById(otherId) is Cue oc
                && VideoLayerOf(oc) == VideoLayerOf(cue)
                && (CanvasMode || ScreenOf(oc) == ScreenOf(cue));
            if (stopAll || sameVideoLayer)
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

    /// <summary>Reproduce el show DESDE un punto del timeline (cursor de
    /// edición): los bloques que cruzan ese instante arrancan YA con seek a
    /// su offset, y los posteriores del mismo segmento (hasta el próximo GO
    /// manual) se programan a su hora relativa. La proyección del timeline
    /// ya trae las cadenas resueltas → los encadenados de cada cue se
    /// suprimen para no duplicar disparos.</summary>
    public void PlayFromTimeline(IReadOnlyList<TimelinePlayItem> items)
    {
        StopEverything(fade: 0.0);
        int fired = 0;
        foreach (var item in items)
        {
            int idx = _model.IndexOfId(item.CueId);
            var cue = _model.GetCue(idx);
            if (cue is null || !cue.Armed || cue.Type == CueType.Group)
                continue;
            var run = new RunningCue
            {
                CueId = cue.Id,
                Index = idx,
                Kind = cue.Type,
                Name = cue.Name,
                Number = cue.DisplayNumber,
                Color = cue.Color,
                AdvanceChain = false,
                SuppressChain = true,
                PreWaitOverride = item.Delay > 0.001 ? item.Delay : null,
            };
            // Reemplaza cualquier instancia previa (StopEverything ya limpió)
            _running[cue.Id] = run;
            if (item.Delay > 0.001)
            {
                int i = idx;
                Schedule(run, (int)(item.Delay * 1000), () => StartAction(i));
            }
            else
            {
                StartAction(idx, Math.Max(0.0, item.Offset));
            }
            fired++;
        }
        Log($"Timeline: reproducir desde el cursor — {fired} cue(s) del segmento");
        _view.FlashStatus(fired > 0
            ? $"▶ Reproduciendo desde el cursor ({fired} cue(s))"
            : "Nada que reproducir en ese punto del timeline.");
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

        // SuppressChain: al reproducir desde el cursor, la proyección ya trae
        // todos los bloques del segmento programados — encadenar duplicaría.
        if (cue is not null && cue.ContinueMode == ContinueMode.AutoFollow
            && !run.SuppressChain)
            ContinueNext(idx, run.AdvanceChain);

        EndCue(cueId, markStopped: true, fade: fade);
    }

    /// <summary>Arranca el fundido de SALIDA de un vídeo (volumen + opacidad)
    /// dentro de sus últimos fade_out segundos de CONTENIDO. El reloj de
    /// pared y la posición real del vídeo derivan (latencia de arranque de
    /// mpv ~100-300 ms): con fades cortos ese desfase se come el fundido,
    /// así que el timer se CORRIGE contra la posición real y se reprograma
    /// si llegó pronto. No detiene el cue: el fin llega con el fundido hecho.</summary>
    private void BeginVideoFadeOut(int cueId, double contentFadeSeconds, int attempt)
    {
        if (!_running.TryGetValue(cueId, out var run) || run.ManualStop)
            return;
        if (!_video.IsPlaying(cueId) || _model.GetById(cueId) is not Cue cue)
            return;

        // Base sin telemetría del motor: el fade no puede durar más de lo
        // que queda de bloque desde el punto de arranque (offset)
        double wall = Math.Min(contentFadeSeconds,
            Math.Max(0.05, run.Duration - run.Offset)) / RateOf(cue);
        double eng = _video.GetElapsed(cueId);
        if (eng > 0.0)
        {
            double pos = eng + run.Offset;                       // contenido real
            double target = run.Duration - contentFadeSeconds;   // inicio del fade
            if (pos + 0.03 < target && attempt < 60)
            {
                // Llegamos pronto (deriva): reprogramar lo que falta de verdad
                Schedule(run, Math.Max(16, (int)((target - pos) / RateOf(cue) * 1000)),
                    () => BeginVideoFadeOut(cueId, contentFadeSeconds, attempt + 1));
                return;
            }
            wall = Math.Max(0.05, (run.Duration - pos) / RateOf(cue));
        }

        _video.FadeVolume(cueId, 0.0, wall, stopAfter: false);
        // Crossfade en la MISMA pista: el entrante (encima) ya funde de 0→1
        // sobre este — oscurecer también al saliente hundiría el cruce a
        // negro. Se mantiene opaco y lo retira su fin (el volumen SÍ funde).
        if (!run.SuppressVisualFadeOut)
            SetCueLayerOpacity(cue, 0.0, wall);
    }

    /// <summary>Fin de vídeo corregido por deriva: si la posición real aún no
    /// llegó al final (arrancó con latencia), se reprograma lo que falta —
    /// clave para que los fades CORTOS no se corten a medias.</summary>
    private void VideoEndCheck(int cueId, int attempt)
    {
        if (_running.TryGetValue(cueId, out var run) && !run.ManualStop
            && _video.IsPlaying(cueId))
        {
            double eng = _video.GetElapsed(cueId);
            if (eng > 0.0 && attempt < 60
                && _model.GetById(cueId) is Cue cue)
            {
                double remaining = run.Duration - (eng + run.Offset);
                if (remaining > 0.08)
                {
                    Schedule(run, Math.Max(16, (int)(remaining / RateOf(cue) * 1000)),
                        () => VideoEndCheck(cueId, attempt + 1));
                    return;
                }
            }
        }
        DurationElapsed(cueId);
    }

    /// <summary>La duración fijada expiró antes del fin natural: parar. El
    /// AUDIO funde su cola (el motor la aplica sample-accurate); el VÍDEO ya
    /// llegó fundido (BeginVideoFadeOut) → corte directo sin doble fade.</summary>
    private void DurationElapsed(int cueId)
    {
        var cue = _model.GetById(cueId);
        if (cue is null)
            return;
        if (_running.TryGetValue(cueId, out var run))
            run.ManualStop = true;   // evita doble action-end desde el engine
        double fade = cue.Type == CueType.Audio ? cue.FadeOut : 0.0;
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
        {
            _audio.Fade(target.Id, cue.Volume, cue.Duration, stopAfter);
        }
        else if (_video.IsPlaying(target.Id))
        {
            _video.FadeVolume(target.Id, cue.Volume, cue.Duration, stopAfter);
            // La rampa que dibuja el timeline es también VISUAL: la opacidad
            // acompaña al volumen (proporcional a la opacidad base del cue)
            SetCueLayerOpacity(target,
                Math.Clamp(cue.Volume, 0.0, 1.0) * target.Opacity / 100.0,
                Math.Max(0.0, cue.Duration));
        }
        else
        {
            _view.FlashStatus($"FADE → cue {target.DisplayNumber} no está sonando");
        }
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
            {
                _video.FadeVolume(cueId, 0.0, fade, stopAfter: true);
                // Fundido VISUAL a la par que el de audio (compositor)
                if (_model.GetById(cueId) is Cue vc)
                    SetCueLayerOpacity(vc, 0.0, fade);
            }
            else
            {
                _video.Stop(cueId);
            }
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

    /// <summary>Reloj inyectable (los tests fijan la hora para el wall-clock;
    /// vive en el RemoteRouter, que es quien evalúa los disparos por hora).</summary>
    public Func<DateTime> NowProvider
    {
        get => _remote.NowProvider;
        set => _remote.NowProvider = value;
    }

    public void Tick()
    {
        _remote.CheckWallclock();

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
            // Arranque con seek (play-desde-cursor): el motor cuenta desde su
            // punto de entrada — los tiempos lógicos suman el offset
            elapsed += run.Offset;

            double? remaining = null, fraction = null;
            if (run.Duration > 0.0)
            {
                remaining = Math.Max(0.0, run.Duration - elapsed);
                fraction = Math.Min(1.0, elapsed / run.Duration);
            }

            // Fase de pre_wait: aún no hay acción; mostrar cuánto falta
            // (PreWaitOverride = espera programada por el timeline)
            double? preWaitRemaining = null;
            if (!run.ActionStarted && _model.GetById(run.CueId) is Cue rc)
            {
                double pw = run.PreWaitOverride ?? rc.PreWait;
                if (pw > 0.0)
                    preWaitRemaining = Math.Max(0.0, pw - elapsed);
            }

            rows.Add(new ActiveCueInfo(
                run.CueId, run.Number, run.Name, run.Kind, run.Color,
                elapsed, remaining, fraction, preWaitRemaining));
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

        // Precargar el media del standby: el GO no paga la verificación (y
        // con compositor + salida visible, el vídeo queda EN PAUSA listo)
        var next = _model.GetCue(_playhead);
        if (next is { Type: CueType.Video, FilePath.Length: > 0 })
        {
            PrepareVideoStandby(next);
        }
        else
        {
            _video.DiscardStandby();   // el standby ya no es un vídeo
            if (next is { Type: CueType.Audio, FilePath.Length: > 0 })
                _audio.Preload(next.Id, next.FilePath);
        }
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
        var top = TopVideo(requirePlaying: true);
        if (top is null)
            return null;
        double offset = _running.TryGetValue(top.Id, out var run) ? run.Offset : 0.0;
        return (top.FilePath, _video.GetElapsed(top.Id) + offset);
    }

    /// <summary>Vídeo visible en la salida: el de la pista MÁS ALTA activa,
    /// priorizando la Salida 1 (el monitor PGM refleja la salida principal).</summary>
    private Cue? TopVideo(bool requirePlaying)
    {
        Cue? top = null;
        var topSlot = (Screen: int.MaxValue, Layer: int.MaxValue);
        foreach (var (cid, r) in _running)
        {
            if (r.Kind != CueType.Video || _model.GetById(cid) is not Cue c)
                continue;
            if (requirePlaying && !_video.IsPlaying(cid))
                continue;
            var slot = (Screen: ScreenOf(c), Layer: VideoLayerOf(c));
            if (slot.Screen < topSlot.Screen
                || (slot.Screen == topSlot.Screen && slot.Layer < topSlot.Layer))
            {
                topSlot = slot;
                top = c;
            }
        }
        return top;
    }

    private void RefreshVideoTally()
    {
        // Pistas activas POR SALIDA → cada ventana oculta sus superficies
        // vacías. Se notifican TODAS las salidas (también las vacías, para
        // que limpien). (La opacidad NO se toca aquí: la fijan los fades de
        // StartAction y EndCue; pisarla cortaría los crossfades en curso.)
        var activeBy = new Dictionary<int, List<int>>();
        bool canvasMode = CanvasMode;
        var canvasScreens = canvasMode
            ? CanvasViews().Select(v => v.Screen).ToHashSet()
            : null;
        foreach (var (cid, r) in _running)
            if (r.Kind == CueType.Video && _video.IsPlaying(cid)
                && _model.GetById(cid) is Cue c)
            {
                int layer = VideoLayerOf(c);
                // Canvas: el vídeo vive en TODAS las salidas habilitadas
                foreach (int screen in canvasMode
                    ? canvasScreens! : (IEnumerable<int>)new[] { ScreenOf(c) })
                {
                    if (!activeBy.TryGetValue(screen, out var list))
                        activeBy[screen] = list = new List<int>();
                    list.Add(layer);
                }
            }
        for (int screen = 0; screen < 4; screen++)
            _view.SetActiveVideoLayers(screen,
                activeBy.GetValueOrDefault(screen) ?? (IReadOnlyCollection<int>)Array.Empty<int>());
        // Tally: SOLO lo que está de verdad en pantalla — un cue en pre-wait
        // aún no "es" la salida (anunciarlo confunde al operador)
        _view.SetOutputContent(TopVideo(requirePlaying: true)?.Name);

        // Desglose por salida para el tooltip del tally (qué hay en cada una)
        var detail = new System.Text.StringBuilder(canvasMode ? "MODO CANVAS\n" : "");
        for (int screen = 0; screen < 4; screen++)
        {
            var names = new List<string>();
            foreach (var (cid, r) in _running)
                if (r.Kind == CueType.Video && _video.IsPlaying(cid)
                    && _model.GetById(cid) is Cue c
                    && (canvasMode ? canvasScreens!.Contains(screen)
                                   : ScreenOf(c) == screen))
                    names.Add($"{c.Name} · pista {VideoLayerOf(c) + 1}");
            if (screen == 0 || names.Count > 0)
                detail.AppendLine($"S{screen + 1}: " +
                    (names.Count > 0 ? string.Join(" | ", names) : "negro"));
        }
        _view.SetOutputDetails(detail.ToString().TrimEnd());
    }

    private void PreloadUpcoming()
    {
        var nxt = _model.GetCue(_playhead);
        if (nxt is { Type: CueType.Audio } && nxt.FilePath.Length > 0)
            _audio.Preload(nxt.Id, nxt.FilePath);
    }

    /// <summary>Parámetros de reproducción del vídeo de un cue. FACTORIZADO:
    /// los usa el GO (StartAction) y la precarga en pausa del standby — el
    /// GO instantáneo exige que ambos sean EQUIVALENTES.</summary>
    private VideoPlayParams VideoParamsFor(Cue cue, double offset) => new(
        Volume: cue.Volume, Loop: cue.Loop, Scale: cue.Scale,
        TrimStart: cue.TrimStart + offset, TrimEnd: cue.TrimEnd,
        Rate: cue.Rate, PlayCount: cue.PlayCount,
        PreservePitch: cue.PreservePitch,
        Geometry: GeoFor(cue),
        Layer: VideoLayerOf(cue),
        Screen: ScreenOf(cue),
        CanvasOutputs: CanvasMode ? CanvasViews() : null,
        HandoverSeconds: Math.Max(0.0, cue.FadeIn - offset) / RateOf(cue));

    /// <summary>Precarga del vídeo en STANDBY: con compositor y la salida YA
    /// visible, deja el player creado y PAUSADO en su primer frame con su
    /// superficie a opacidad 0 (el GO solo despausa → disparo instantáneo);
    /// si la salida no está abierta (modo EDIT), verificación clásica — la
    /// precarga nunca debe ABRIR ventanas sola.</summary>
    private void PrepareVideoStandby(Cue cue)
    {
        bool visible = CanvasMode
            ? _view.IsVideoOutputVisible(0)
            : _view.IsVideoOutputVisible(ScreenOf(cue));
        if (visible && !_running.ContainsKey(cue.Id))
        {
            _video.PrepareStandby(cue.Id, cue.FilePath, VideoParamsFor(cue, 0.0));
            _view.SetVideoCueOpacity(cue.Id, 0.0);   // preparado ≠ en pantalla
        }
        else
        {
            _video.PreloadNext(cue.Id, cue.FilePath);
        }
    }

    private VideoGeometry GeoFor(Cue cue)
    {
        // En modo canvas la geometría es relativa al CANVAS global (el vídeo
        // se coloca en el canvas y las salidas lo recortan); en clásico, a la
        // superficie de la salida del cue.
        double sw, sh;
        if (CanvasMode)
        {
            var (cw, ch) = CanvasDims();
            (sw, sh) = (cw, ch);
        }
        else
        {
            (sw, sh) = _view.SurfaceDimensions(ScreenOf(cue));
        }
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
    // UNDO / REDO — implementación en UndoService
    // ==================================================================

    /// <summary>true si hubo ediciones desde el último Guardar/Cargar
    /// (la vista lo usa para confirmar el cierre).</summary>
    public bool HasUnsavedChanges { get; private set; }

    /// <summary>Apunta el estado en la pila de deshacer y marca el show como
    /// modificado. Es el callback que reciben los servicios de edición.</summary>
    private void PushUndo()
    {
        HasUnsavedChanges = true;
        _undo.Push();
    }

    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    public void Undo() => _undo.Undo();
    public void Redo() => _undo.Redo();

    // ==================================================================
    // COPIAR / PEGAR / DUPLICAR / MOVER — implementación en CueListEditor
    // ==================================================================

    /// <summary>Copia los cues de las filas dadas (con sus descendientes).</summary>
    public void CopyCues(IReadOnlyList<int> rows) => _listEditor.CopyCues(rows);

    /// <summary>Pega el portapapeles debajo de la fila dada, con ids nuevos.</summary>
    public void PasteCues(int row) => _listEditor.PasteCues(row);

    /// <summary>Duplica las filas dadas justo debajo (sin tocar el portapapeles).</summary>
    public void DuplicateCues(IReadOnlyList<int> rows) => _listEditor.DuplicateCues(rows);

    /// <summary>Mueve un cue de fila (drag &amp; drop de la tabla o del timeline).</summary>
    public void OnMoveCue(int fromRow, int toRow) => _listEditor.OnMoveCue(fromRow, toRow);

    // ==================================================================
    // GESTIÓN DE LA LISTA — implementación en CueListEditor
    // ==================================================================

    public void AddNewCue(CueType cueType = CueType.Audio) => _listEditor.AddNewCue(cueType);

    /// <summary>Inserta un cue nuevo del tipo dado justo debajo de la fila row.</summary>
    public void OnInsertCue(CueType cueType, int row) => _listEditor.OnInsertCue(cueType, row);

    /// <summary>Elimina el cue de la fila dada (deteniéndolo antes si sonaba).</summary>
    public void OnDeleteCue(int row) => _listEditor.OnDeleteCue(row);

    /// <summary>Renumera todos los cues 1..n en orden de lista (con undo).</summary>
    public void RenumberCues() => _listEditor.RenumberCues();

    /// <summary>Crea un GROUP que contiene los cues seleccionados (y descendientes).</summary>
    public void OnGroupSelected(IReadOnlyList<int> rows) => _listEditor.OnGroupSelected(rows);

    /// <summary>Deshace un grupo: promueve sus hijos directos y elimina el GROUP.</summary>
    public void OnUngroup(int row) => _listEditor.OnUngroup(row);

    /// <summary>Contrae/expande un grupo (oculta/muestra sus descendientes).</summary>
    public void OnToggleCollapse(int row) => _listEditor.OnToggleCollapse(row);

    /// <summary>Crea cues a partir de archivos (drag-drop / importación).</summary>
    public void AddCuesFromFiles(int targetRow, IReadOnlyList<string> paths) =>
        _listEditor.AddCuesFromFiles(targetRow, paths);

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

        // El vídeo en STANDBY quedó precargado con los parámetros de antes de
        // la edición: re-preparar (o degradar a verificación) con los nuevos
        if (cue.Type == CueType.Video && row == _playhead
            && !_running.ContainsKey(cue.Id))
        {
            _video.DiscardStandby(cue.Id);
            if (cue.FilePath.Length > 0)
                PrepareVideoStandby(cue);
        }

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
        // La EQ se refleja también en la AUDICIÓN (no vive en _running)
        if (fields.Keys.Any(IsEqKey))
        {
            if (_audio.IsPlaying(cue.Id))
                _audio.SetEq(cue.Id, EqFor(cue));
            if (cue.Id == _auditionSourceCueId && _audio.IsPlaying(AuditionCueId))
                _audio.SetEq(AuditionCueId, EqFor(cue));
        }

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
        // Opacidad de la pista en vivo (compositor GPU)
        if (cue.Type == CueType.Video && _video.IsPlaying(cue.Id)
            && fields.ContainsKey("opacity"))
        {
            SetCueLayerOpacity(cue, cue.Opacity / 100.0);
        }
    }

    private static bool IsEqKey(string key) =>
        key is "gain_db" or "eq_enabled" or "hpf_on" or "hpf_freq"
            or "lpf_on" or "lpf_freq"
        || key.StartsWith("eq", StringComparison.Ordinal)
        || key.StartsWith("comp", StringComparison.Ordinal);

    // ------------------------------------------------------------------
    // Guardar / cargar show en JSON — implementación en ShowFileService
    // (la vista pone los diálogos; aquí solo queda la delegación y el
    // flag HasUnsavedChanges, que pertenece al ciclo de edición)
    // ------------------------------------------------------------------

    /// <summary>Guarda el show. El media que vive junto al .json (o debajo) se
    /// guarda con RUTA RELATIVA → el show es portátil entre máquinas.</summary>
    public void SaveShow(string path)
    {
        if (_files.SaveShow(path))
            HasUnsavedChanges = false;
    }

    /// <summary>Guardado silencioso para el autosave (sin diálogos ni status).</summary>
    public bool Autosave(string path) => _files.Autosave(path);

    /// <summary>Carga un show: resuelve rutas relativas contra la carpeta del
    /// .json y marca en ERROR los cues cuyo media no existe.</summary>
    public void LoadShow(string path)
    {
        if (_files.LoadShow(path))
            HasUnsavedChanges = false;
    }

    /// <summary>Cues de media/script cuyo archivo NO existe en disco.</summary>
    public int MissingMediaCount() => _files.MissingMediaCount();

    /// <summary>Relocaliza los medios faltantes buscando POR NOMBRE DE
    /// ARCHIVO (recursivo) en la carpeta dada — el caso "show movido de
    /// máquina". Devuelve cuántos se recuperaron.</summary>
    public int RelocateMedia(string folder) => _files.RelocateMedia(folder);

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
