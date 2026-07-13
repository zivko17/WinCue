// =============================================================================
// Fakes.cs — Dobles de prueba: scheduler virtual, motores falsos, vista falsa
// =============================================================================
// Permiten testear el controlador de forma DETERMINISTA: el tiempo avanza con
// Advance(ms) y los "motores" registran llamadas y simulan fines naturales.
// =============================================================================

using WinCue.Controller;
using WinCue.Core;

namespace WinCue.Controller.Tests;

// ---------------------------------------------------------------------------
// Scheduler de tiempo virtual
// ---------------------------------------------------------------------------

public sealed class VirtualScheduler : IScheduler
{
    private long _now;
    private readonly List<Entry> _entries = new();

    private sealed class Entry
    {
        public long Due;
        public required Action Fn;
        public bool Cancelled;
    }

    private sealed class Canceller(Entry e) : IDisposable
    {
        public void Dispose() => e.Cancelled = true;
    }

    public IDisposable Schedule(int ms, Action fn)
    {
        var e = new Entry { Due = _now + ms, Fn = fn };
        _entries.Add(e);
        return new Canceller(e);
    }

    /// <summary>Avanza el reloj virtual ejecutando los timers vencidos en orden.</summary>
    public void Advance(int ms)
    {
        long target = _now + ms;
        while (true)
        {
            Entry? next = null;
            foreach (var e in _entries)
                if (!e.Cancelled && e.Due <= target && (next is null || e.Due < next.Due))
                    next = e;
            if (next is null)
                break;
            _now = next.Due;
            _entries.Remove(next);
            next.Fn();
        }
        _now = target;
    }

    public int PendingCount => _entries.Count(e => !e.Cancelled);
}

// ---------------------------------------------------------------------------
// Motor de audio falso
// ---------------------------------------------------------------------------

public sealed class FakeAudioEngine : IAudioEngine
{
    public event Action<int, MediaCueState>? StateChanged;
    public event Action<int, double>? LatencyReport;
    public event Action<int, string>? ErrorOccurred;
    public event Action<float>? LevelReport;

    public readonly List<(int CueId, string Path, AudioPlayParams P)> Plays = new();
    public readonly List<(int CueId, double Fade)> Stops = new();
    public readonly List<(int CueId, double Target, double Duration, bool StopAfter)> Fades = new();
    public readonly List<int> Preloads = new();
    public readonly List<int> Invalidated = new();
    public readonly Dictionary<int, double> Volumes = new();
    public readonly Dictionary<int, double> Durations = new();
    public readonly Dictionary<int, double> Elapsed = new();
    public readonly HashSet<int> Playing = new();
    public double Master = 1.0;
    public int StopAllCalls;
    public double LastStopAllFade = -1;
    public bool PausedAll, ResumedAll;

    public void Play(int cueId, string filePath, AudioPlayParams? p = null)
    {
        Plays.Add((cueId, filePath, p ?? new AudioPlayParams()));
        Playing.Add(cueId);
    }

    public void Preload(int cueId, string filePath) => Preloads.Add(cueId);

    public void Stop(int cueId, double fade = 0.0)
    {
        Stops.Add((cueId, fade));
        Playing.Remove(cueId);
    }

    public void StopAll(double fade = 0.0)
    {
        StopAllCalls++;
        LastStopAllFade = fade;
        Playing.Clear();
    }

    public bool Fade(int cueId, double targetVolume, double duration, bool stopAfter = false)
    {
        Fades.Add((cueId, targetVolume, duration, stopAfter));
        return Playing.Contains(cueId);
    }

    public void SetVolume(int cueId, double volume) => Volumes[cueId] = volume;
    public void SetMasterVolume(double volume) => Master = volume;
    public void PauseAll() => PausedAll = true;
    public void ResumeAll() => ResumedAll = true;
    public bool IsPlaying(int cueId) => Playing.Contains(cueId);
    public double GetDuration(int cueId) => Durations.GetValueOrDefault(cueId);
    public double GetElapsed(int cueId) => Elapsed.GetValueOrDefault(cueId);
    public void InvalidateCache(int cueId) => Invalidated.Add(cueId);
    public void Cleanup() { }

    /// <summary>Simula el fin natural del audio (como el mixer real).</summary>
    public void EndNaturally(int cueId)
    {
        Playing.Remove(cueId);
        StateChanged?.Invoke(cueId, MediaCueState.Stopped);
    }

    public void RaiseLatency(int cueId, double ms) => LatencyReport?.Invoke(cueId, ms);
    public void RaiseError(int cueId, string msg) => ErrorOccurred?.Invoke(cueId, msg);
    public void RaiseLevel(float rms) => LevelReport?.Invoke(rms);
}

// ---------------------------------------------------------------------------
// Motor de vídeo falso
// ---------------------------------------------------------------------------

public sealed class FakeVideoEngine : IVideoEngine
{
#pragma warning disable CS0067   // eventos del contrato sin uso en los tests actuales
    public event Action<int, MediaCueState>? StateChanged;
    public event Action<int, double>? LatencyReport;
    public event Action<int, string>? ErrorOccurred;
    public event Action<int>? FrameReady;
#pragma warning restore CS0067

    public readonly List<(int CueId, string Path, VideoPlayParams P)> Plays = new();
    public readonly List<int> Stops = new();
    public readonly List<(int CueId, double Target, double Duration, bool StopAfter)> Fades = new();
    public readonly List<(int CueId, VideoGeometry Geo)> GeometryApplied = new();
    public readonly Dictionary<int, double> Volumes = new();
    public readonly Dictionary<int, double> Durations = new();
    public readonly Dictionary<int, double> Elapsed = new();
    public readonly HashSet<int> Playing = new();
    public double Master = 1.0;
    public int StopAllCalls;
    public bool PausedAll, ResumedAll;
    public Func<nint>? HandleProvider;

    public void Play(int cueId, string filePath, VideoPlayParams? p = null)
    {
        Plays.Add((cueId, filePath, p ?? new VideoPlayParams()));
        Playing.Add(cueId);
    }

    public void PreloadNext(int cueId, string filePath) { }

    public void Stop(int cueId)
    {
        Stops.Add(cueId);
        Playing.Remove(cueId);
    }

    public void StopAll()
    {
        StopAllCalls++;
        Playing.Clear();
    }

    public bool FadeVolume(int cueId, double target, double duration, bool stopAfter = false)
    {
        Fades.Add((cueId, target, duration, stopAfter));
        return Playing.Contains(cueId);
    }

    public void SetVolume(int cueId, double volume) => Volumes[cueId] = volume;
    public void SetMasterVolume(double volume) => Master = volume;
    public void PauseAll() => PausedAll = true;
    public void ResumeAll() => ResumedAll = true;
    public bool IsPlaying(int cueId) => Playing.Contains(cueId);
    public double GetDuration(int cueId) => Durations.GetValueOrDefault(cueId);
    public double GetElapsed(int cueId) => Elapsed.GetValueOrDefault(cueId);
    public void ApplyGeometry(int cueId, VideoGeometry geo) => GeometryApplied.Add((cueId, geo));
    public void SetWindowHandleProvider(Func<nint> getWindowHandle) => HandleProvider = getWindowHandle;
    public void Cleanup() { }

    public void EndNaturally(int cueId)
    {
        Playing.Remove(cueId);
        StateChanged?.Invoke(cueId, MediaCueState.Stopped);
    }
}

// ---------------------------------------------------------------------------
// Vista falsa
// ---------------------------------------------------------------------------

public sealed class FakeView : IShowView
{
    public readonly List<string> Statuses = new();
    public readonly List<(string Title, string Message)> Errors = new();
    public readonly List<int> PlayheadHistory = new();
    public readonly List<int> RefreshedRows = new();
    public readonly List<Cue> Appended = new();
    public int ReloadCount, HotkeyRebuilds, GroupVisibilityApplies;
    public bool? LastTransportPaused;
    public double? LastCountdown = double.NaN;
    public IReadOnlyList<ActiveCueInfo> LastActiveCues = Array.Empty<ActiveCueInfo>();
    public float LastVu = -1;
    public string? LastOutputContent = "<sin-fijar>";

    public int CurrentPlayhead => PlayheadHistory.Count > 0 ? PlayheadHistory[^1] : -1;

    public void FlashStatus(string message) => Statuses.Add(message);
    public void ShowError(string title, string message) => Errors.Add((title, message));
    public void SetPlayhead(int index) => PlayheadHistory.Add(index);
    public void RefreshRow(int index) => RefreshedRows.Add(index);
    public void ReloadTable() => ReloadCount++;
    public void AppendCueRow(Cue cue) => Appended.Add(cue);
    public void ApplyGroupVisibility() => GroupVisibilityApplies++;
    public void RebuildHotkeys() => HotkeyRebuilds++;
    public void UpdateTransport(bool paused) => LastTransportPaused = paused;
    public void UpdateCountdown(double? secondsRemaining) => LastCountdown = secondsRemaining;
    public void UpdateActiveCues(IReadOnlyList<ActiveCueInfo> rows) => LastActiveCues = rows;
    public void UpdateVuMeter(float rms) => LastVu = rms;
    public nint GetVideoOutputHandle() => 0;
    public (double Width, double Height) SurfaceDimensions() => (1920.0, 1080.0);
    public void SetOutputContent(string? videoName) => LastOutputContent = videoName;
}

// ---------------------------------------------------------------------------
// Base común de los tests del controlador
// ---------------------------------------------------------------------------

public class ControllerTestBase : IDisposable
{
    protected readonly CueModel Model = new();
    protected readonly FakeView View = new();
    protected readonly FakeAudioEngine Audio = new();
    protected readonly FakeVideoEngine Video = new();
    protected readonly VirtualScheduler Clock = new();
    protected readonly SettingsManager Settings;
    protected readonly MainController Controller;
    private readonly string _dir;

    public ControllerTestBase()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wincue_ctl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Settings = new SettingsManager(Path.Combine(_dir, "settings.json"));
        Controller = new MainController(Model, View, Audio, Video, Clock, Settings,
            random: new Random(42));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    protected string TempDir => _dir;

    /// <summary>Añade un cue al modelo directamente (sin pasar por la vista).</summary>
    protected Cue AddCue(CueType type, string name = "", double duration = 0.0,
        Action<Cue>? setup = null)
    {
        var cue = new Cue
        {
            Id = Model.NextId(),
            Type = type,
            Name = name.Length > 0 ? name : $"{type} {Model.NextId()}",
            Duration = duration,
            FilePath = type is CueType.Audio or CueType.Video ? @"C:\fake\media.wav" : "",
        };
        setup?.Invoke(cue);
        Model.AddCue(cue);
        return cue;
    }
}
