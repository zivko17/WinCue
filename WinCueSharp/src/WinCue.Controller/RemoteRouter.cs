// =============================================================================
// RemoteRouter.cs — Ruteo de disparo remoto (extraído de MainController,
// paso 3 del plan de docs/auditoria/02-arquitectura.md)
// =============================================================================
// Parsea y rutea las CUATRO fuentes externas de disparo hacia las acciones
// del controlador: OSC (Companion/TouchOSC/consolas), MIDI IN (notas globales
// de transporte + triggers por cue), timecode MTC y wall-clock (hora del día).
// Es un parser+dispatcher puro: NO toca el estado de reproducción — recibe
// las acciones del núcleo como callbacks y consulta el modelo en lectura.
//
// Hilos: solo el hilo de UI (RemoteInputService ya remite por dispatcher).
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller;

public sealed class RemoteRouter
{
    private readonly CueModel _model;
    private readonly SettingsManager _settings;
    private readonly IShowView _view;
    private readonly Action<string> _log;

    // Acciones del núcleo (el router nunca muta _running directamente)
    private readonly Action _go;
    private readonly Action _stopAll;
    private readonly Action _panic;
    private readonly Action _fadeAll;
    private readonly Action _pauseToggle;
    private readonly Action<int> _triggerCueId;    // por id, sin mover playhead
    private readonly Action<int> _triggerAt;       // por índice, sin mover playhead
    private readonly Action<int> _stopCue;         // EndCue markStopped:true
    private readonly Func<int, bool> _isRunning;

    // ── Estado propio (timecode y wall-clock) ──────────────────────────
    private double _tcLastSeconds = -1;
    private readonly HashSet<int> _tcFired = new();
    private string _lastWallclockSecond = "";

    /// <summary>Reloj inyectable (los tests fijan la hora para el wall-clock).</summary>
    public Func<DateTime> NowProvider { get; set; } = () => DateTime.Now;

    public RemoteRouter(
        CueModel model,
        SettingsManager settings,
        IShowView view,
        Action<string> log,
        Action go,
        Action stopAll,
        Action panic,
        Action fadeAll,
        Action pauseToggle,
        Action<int> triggerCueId,
        Action<int> triggerAt,
        Action<int> stopCue,
        Func<int, bool> isRunning)
    {
        _model = model;
        _settings = settings;
        _view = view;
        _log = log;
        _go = go;
        _stopAll = stopAll;
        _panic = panic;
        _fadeAll = fadeAll;
        _pauseToggle = pauseToggle;
        _triggerCueId = triggerCueId;
        _triggerAt = triggerAt;
        _stopCue = stopCue;
        _isRunning = isRunning;
    }

    // ==================================================================
    // OSC
    // ==================================================================

    /// <summary>Rutea un mensaje OSC entrante a la acción correspondiente.
    /// Direcciones: /wincue/go /wincue/stop /wincue/panic /wincue/fadeall
    /// /wincue/pause y /wincue/cue/{número}/go|stop (con alias sin prefijo:
    /// /go /stop /panic /fadeall /pause). Un único argumento numérico 0 se
    /// ignora (convención "release" de Companion/TouchOSC).</summary>
    public void OnOscMessage(string address, IReadOnlyList<object?> args)
    {
        // Botón soltado (arg único == 0): no reaccionar
        if (args.Count == 1 && args[0] is int i0 && i0 == 0) return;
        if (args.Count == 1 && args[0] is float f0 && f0 == 0f) return;

        var parts = address.Trim().TrimEnd('/').ToLowerInvariant()
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;
        if (parts[0] == "wincue")
            parts = parts[1..];
        if (parts.Length == 0)
            return;

        if (parts.Length == 1)
        {
            switch (parts[0])
            {
                case "go": _log($"OSC {address} → GO"); _go(); break;
                case "stop": _log($"OSC {address} → STOP ALL"); _stopAll(); break;
                case "panic": _log($"OSC {address} → PANIC"); _panic(); break;
                case "fadeall": _log($"OSC {address} → FADE ALL"); _fadeAll(); break;
                case "pause": _log($"OSC {address} → PAUSA"); _pauseToggle(); break;
            }
            return;
        }

        // /cue/{número}[/go|/start|/stop]
        if (parts[0] != "cue")
            return;
        string number = parts[1];
        string verb = parts.Length >= 3 ? parts[2] : "go";
        if (verb is "go" or "start")
        {
            TriggerByNumber(number, $"OSC {address}");
        }
        else if (verb == "stop" && FindByNumber(number) is Cue tgt
                 && _isRunning(tgt.Id))
        {
            _log($"OSC {address} → STOP cue {tgt.DisplayNumber}");
            _stopCue(tgt.Id);
        }
    }

    // ==================================================================
    // MIDI IN
    // ==================================================================

    /// <summary>Mensaje MIDI entrante: notas globales de GO/STOP (Settings →
    /// Red) y triggers por cue (Triggers → MIDI del inspector).
    /// data1 = nota/CC/programa; data2 = velocity/valor.</summary>
    public void OnMidiMessage(MidiTriggerType type, int channel, int data1, int data2)
    {
        // Convención MIDI: NoteOn con velocity 0 equivale a NoteOff
        if (type == MidiTriggerType.NoteOn && data2 == 0)
            type = MidiTriggerType.NoteOff;

        // Notas globales de transporte (cualquier canal)
        if (type == MidiTriggerType.NoteOn)
        {
            if (data1 == _settings.Get("midi_go_note", 60))
            {
                _log($"MIDI nota {data1} → GO");
                _go();
                return;
            }
            if (data1 == _settings.Get("midi_stop_note", 61))
            {
                _log($"MIDI nota {data1} → STOP ALL");
                _stopAll();
                return;
            }
        }

        // Triggers por cue
        for (int i = 0; i < _model.Count; i++)
        {
            var c = _model.Cues[i];
            if (c.MidiEnabled && c.MidiType == type && c.MidiChannel == channel
                && c.MidiNote == data1
                && (c.MidiVelocity == -1 || c.MidiVelocity == data2))
            {
                _log($"MIDI {type} ch{channel} {data1}/{data2} → {c.DisplayNumber} · {c.Name}");
                _triggerAt(i);
            }
        }
    }

    // ==================================================================
    // Timecode (MTC)
    // ==================================================================

    /// <summary>Posición de timecode entrante (segundos desde 00:00:00:00).
    /// Dispara los cues con Triggers → Timecode cuya marca se acaba de cruzar
    /// (una vez por pasada; un salto atrás &gt; 0,5 s re-arma todo).</summary>
    public void OnTimecode(double seconds)
    {
        if (seconds < _tcLastSeconds - 0.5)
            _tcFired.Clear();   // rebobinado / nueva pasada
        _tcLastSeconds = seconds;

        for (int i = 0; i < _model.Count; i++)
        {
            var c = _model.Cues[i];
            if (!c.TimecodeEnabled || !c.Armed || _tcFired.Contains(c.Id)
                || _isRunning(c.Id))
                continue;
            if (ParseTimecode(c.TimecodeValue) is not double mark)
                continue;
            if (seconds >= mark && seconds - mark < 1.0)
            {
                _tcFired.Add(c.Id);
                _log($"TIMECODE {c.TimecodeValue} → {c.DisplayNumber} · {c.Name}");
                _triggerAt(i);
            }
        }
    }

    /// <summary>"HH:MM:SS:FF" (frames a fps dado), "HH:MM:SS.mmm" o "MM:SS".</summary>
    public static double? ParseTimecode(string text, double fps = 30.0)
    {
        var parts = text.Trim().Split(':');
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return parts.Length switch
            {
                4 => int.Parse(parts[0]) * 3600.0 + int.Parse(parts[1]) * 60.0
                     + int.Parse(parts[2]) + int.Parse(parts[3]) / Math.Max(1.0, fps),
                3 => int.Parse(parts[0]) * 3600.0 + int.Parse(parts[1]) * 60.0
                     + double.Parse(parts[2].Replace(',', '.'), inv),
                2 => int.Parse(parts[0]) * 60.0
                     + double.Parse(parts[1].Replace(',', '.'), inv),
                _ => null,
            };
        }
        catch (Exception exc) when (exc is FormatException or OverflowException)
        {
            return null;
        }
    }

    // ==================================================================
    // Wall-clock (hora del día)
    // ==================================================================

    /// <summary>Disparo por hora del día (Triggers → Wall-clock). Se evalúa una
    /// vez por segundo desde el Tick de la UI.</summary>
    public void CheckWallclock()
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
                !_isRunning(c.Id))
            {
                _log($"WALL-CLOCK {hhmmss} → {c.DisplayNumber} · {c.Name}");
                _triggerAt(i);
            }
        }
    }

    // ------------------------------------------------------------------
    // Búsqueda por número de cue
    // ------------------------------------------------------------------

    private Cue? FindByNumber(string number) =>
        _model.Cues.FirstOrDefault(c => c.DisplayNumber == number);

    private void TriggerByNumber(string number, string source)
    {
        if (FindByNumber(number) is Cue cue)
        {
            _log($"{source} → GO cue {cue.DisplayNumber} · {cue.Name}");
            _triggerCueId(cue.Id);
        }
        else
        {
            _view.FlashStatus($"{source}: no hay cue con número «{number}»");
        }
    }
}
