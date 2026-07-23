// =============================================================================
// HapConvertTests.cs — Conversión a HAP desde la app (servicio + controlador)
// =============================================================================
// Runner de ffmpeg FALSO y determinista: los callbacks del servicio viajan por
// el VirtualScheduler (Advance(0) = "el dispatcher entrega"), y la continuación
// del Task corre síncrona en el hilo que completa el TaskCompletionSource.
// Cubre: progreso, éxito (commit por la ruta de edición oficial + cachés),
// fallo, cancelación, "ya existe → no reconvertir" y cola secuencial.
// =============================================================================

using WinCue.Controller;
using WinCue.Core;

namespace WinCue.Controller.Tests;

/// <summary>Runner falso: cada ConvertAsync queda pendiente hasta que el test
/// llama Succeed()/Fail(); Succeed crea el archivo destino (como ffmpeg).</summary>
public sealed class FakeHapRunner : IHapConversionRunner
{
    public readonly List<(string Src, string Dest)> Calls = new();
    public Action<double>? LastProgress;
    public CancellationToken LastToken;
    public string LastDest = "";
    private TaskCompletionSource<HapConversionResult>? _pending;

    public Task<HapConversionResult> ConvertAsync(
        string src, string dest, Action<double> onProgress, CancellationToken ct)
    {
        Calls.Add((src, dest));
        LastProgress = onProgress;
        LastToken = ct;
        LastDest = dest;
        _pending = new TaskCompletionSource<HapConversionResult>();
        return _pending.Task;
    }

    public void Succeed()
    {
        File.WriteAllText(LastDest, "hap-falso");
        _pending!.SetResult(new HapConversionResult(true));
    }

    public void Fail(string error, string logFile = "") =>
        _pending!.SetResult(new HapConversionResult(false, error, logFile));

    public void CompleteCancelled() =>
        _pending!.SetResult(new HapConversionResult(false, "cancelada"));
}

public sealed class HapConvertTests : IDisposable
{
    private readonly CueModel _model = new();
    private readonly FakeView _view = new();
    private readonly FakeAudioEngine _audio = new();
    private readonly FakeVideoEngine _video = new();
    private readonly VirtualScheduler _clock = new();
    private readonly FakeHapRunner _runner = new();
    private readonly MainController _controller;
    private readonly string _dir;

    public HapConvertTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wincue_hapconv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var settings = new SettingsManager(Path.Combine(_dir, "settings.json"));
        settings.Set("go_min_interval_ms", 0);
        _controller = new MainController(_model, _view, _audio, _video, _clock,
            settings, random: new Random(42), hapRunner: _runner);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Cue de vídeo con un archivo REAL (mp4 de mentira) en _dir.</summary>
    private Cue AddVideoCue(string fileName = "clip.mp4")
    {
        string src = Path.Combine(_dir, fileName);
        File.WriteAllText(src, "h264-falso");
        var cue = new Cue
        {
            Id = _model.NextId(),
            Type = CueType.Video,
            Name = Path.GetFileNameWithoutExtension(fileName),
            FilePath = src,
        };
        _model.AddCue(cue);
        return cue;
    }

    [Fact]
    public void DestPathFor_NombreHapMovJuntoAlOriginal()
    {
        string dest = HapConvertService.DestPathFor(Path.Combine(_dir, "intro.mp4"));
        Assert.Equal(Path.Combine(_dir, "intro.hap.mov"), dest);
    }

    [Fact]
    public void Exito_CommiteaLaRutaNueva_ConUndoYCachesInvalidadas()
    {
        var cue = AddVideoCue();
        string original = cue.FilePath;
        _controller.ConvertCueToHap(0);

        Assert.Single(_runner.Calls);
        Assert.Equal(original, _runner.Calls[0].Src);
        string dest = _runner.Calls[0].Dest;
        Assert.Equal(Path.Combine(_dir, "clip.hap.mov"), dest);

        // Sondeo RANCIO del destino (como si se hubiera sondeado a medias):
        // al completar debe invalidarse y re-sondearse (archivo falso → no-HAP)
        MediaImporter.SeedUnsupportedCodecCache(dest, unsupported: true);

        _runner.Succeed();
        _clock.Advance(0);   // el "dispatcher" entrega la finalización

        Assert.Equal(dest, cue.FilePath);                    // ruta oficial nueva
        Assert.True(_controller.CanUndo);                    // commit con undo
        Assert.Contains(_audio.Invalidated, id => id == cue.Id);
        Assert.Contains(_audio.Invalidated, id => id == MainController.VideoAudioId(cue.Id));
        Assert.Contains(_video.Discarded, id => id == cue.Id);
        // La caché ffprobe se refrescó: el valor sembrado (true) fue re-sondeado
        Assert.False(MediaImporter.IsUnsupportedVideoCodec(dest));
        Assert.Contains(_view.Statuses, s => s.Contains("convertido a HAP", StringComparison.Ordinal));

        _controller.Undo();
        Assert.Equal(original, _model.Cues[0].FilePath);     // deshacer restaura
    }

    [Fact]
    public void Progreso_SeInformaPorFlashStatus_SinSpamPorPorcentaje()
    {
        AddVideoCue();
        _controller.ConvertCueToHap(0);

        _runner.LastProgress!(43.2);
        _runner.LastProgress!(43.9);   // mismo % entero: no debe repetir aviso
        _clock.Advance(0);

        Assert.Single(_view.Statuses, s => s.Contains("43%", StringComparison.Ordinal));
        Assert.Contains(_view.Statuses, s => s.Contains("Convirtiendo a HAP", StringComparison.Ordinal)
            && s.Contains("clip", StringComparison.Ordinal));
    }

    [Fact]
    public void Fallo_NoTocaElCueYAvisa()
    {
        var cue = AddVideoCue();
        string original = cue.FilePath;
        _controller.ConvertCueToHap(0);

        _runner.Fail("dimensiones no múltiplo de 4", @"C:\logs\hap-convert-x.log");
        _clock.Advance(0);

        Assert.Equal(original, cue.FilePath);
        Assert.False(_controller.CanUndo);   // sin commit no hay undo
        Assert.Contains(_view.Statuses, s => s.Contains('⚠', StringComparison.Ordinal)
            && s.Contains("falló", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancelacion_VaciaLaColaYCancelaElTrabajoEnCurso()
    {
        var c1 = AddVideoCue("uno.mp4");
        var c2 = AddVideoCue("dos.mp4");
        _controller.ConvertCueToHap(0);
        _controller.ConvertCueToHap(1);
        Assert.Single(_runner.Calls);        // cola secuencial: solo el primero

        _controller.CancelHapConversions();
        Assert.True(_runner.LastToken.IsCancellationRequested);

        _runner.CompleteCancelled();         // el runner real devuelve fallo al matarse
        _clock.Advance(0);

        Assert.Single(_runner.Calls);        // el segundo NUNCA llegó a arrancar
        Assert.EndsWith("uno.mp4", c1.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("dos.mp4", c2.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void DestinoYaExiste_ReutilizaSinReconvertir()
    {
        var cue = AddVideoCue();
        string dest = HapConvertService.DestPathFor(cue.FilePath);
        File.WriteAllText(dest, "hap-de-otra-sesion");

        _controller.ConvertCueToHap(0);
        _clock.Advance(0);

        Assert.Empty(_runner.Calls);         // no se reconvierte
        Assert.Equal(dest, cue.FilePath);    // pero SÍ se commitea la ruta
        Assert.Contains(_view.Statuses, s => s.Contains("ya estaba convertido", StringComparison.Ordinal));
    }

    [Fact]
    public void ConvertirTodos_EncolaSoloConvertiblesYProcesaEnSerie()
    {
        var c1 = AddVideoCue("a.mp4");
        var c2 = AddVideoCue("b.mp4");
        // Un HAP ya convertido (sembrado) y un cue de audio: NO deben encolarse
        var hap = AddVideoCue("ya.mov");
        MediaImporter.SeedUnsupportedCodecCache(hap.FilePath, unsupported: true);
        _model.AddCue(new Cue { Id = _model.NextId(), Type = CueType.Audio, Name = "au" });

        int queued = _controller.ConvertAllVideosToHap();
        Assert.Equal(2, queued);
        Assert.Single(_runner.Calls);        // uno a la vez

        _runner.Succeed();
        _clock.Advance(0);
        Assert.Equal(2, _runner.Calls.Count);   // al acabar el 1º arranca el 2º
        Assert.EndsWith("a.hap.mov", c1.FilePath, StringComparison.Ordinal);

        _runner.Succeed();
        _clock.Advance(0);
        Assert.EndsWith("b.hap.mov", c2.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("ya.mov", hap.FilePath, StringComparison.Ordinal);   // intacto
    }

    [Fact]
    public void EncolarDosVecesElMismoCue_SeIgnora()
    {
        AddVideoCue();
        _controller.ConvertCueToHap(0);
        _controller.ConvertCueToHap(0);
        Assert.Single(_runner.Calls);
        Assert.Contains(_view.Statuses, s => s.Contains("ya está en la cola", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletarConElCuePlayhead_RepreparaElStandbyConLaRutaNueva()
    {
        var cue = AddVideoCue();
        // playhead ya está en la fila 0; el commit debe re-preparar el standby
        _controller.ConvertCueToHap(0);
        _runner.Succeed();
        _clock.Advance(0);

        Assert.Contains(_video.Discarded, id => id == cue.Id);
        // Sin compositor/salida visible la re-preparación degrada a PreloadNext
        Assert.Contains(_video.Preloaded, id => id == cue.Id);
    }
}
