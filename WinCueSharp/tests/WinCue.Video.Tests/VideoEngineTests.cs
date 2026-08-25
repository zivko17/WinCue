// =============================================================================
// VideoEngineTests.cs — Engine de vídeo con libmpv REAL en modo headless
// =============================================================================
// vo=null + ao=null: mpv decodifica y avanza el reloj de reproducción sin
// ventana, GPU ni dispositivo de audio. Se testea la máquina de estados, las
// propiedades (volumen/rate/geometría) y el ciclo de vida de los players.
//
// Los archivos de prueba se generan una vez por clase con FFmpeg (fixture).
// Si libmpv-2.dll o FFmpeg no están disponibles, los tests se saltan.
// =============================================================================

using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using WinCue.Core;
using WinCue.Video;
using WinCue.Video.Hap;
using WinCue.Video.ProRes;

namespace WinCue.Video.Tests;

public sealed class MediaFixture : IDisposable
{
    public string Dir { get; }
    public string Mp4OneSecond { get; }      // 1 s vídeo+audio
    public string Mp4Half { get; }           // 0.5 s vídeo
    public string Png { get; }
    public bool FFmpegOk { get; }

    public MediaFixture()
    {
        Dir = Path.Combine(Path.GetTempPath(), "wincue_video_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Mp4OneSecond = Path.Combine(Dir, "one.mp4");
        Mp4Half = Path.Combine(Dir, "half.mp4");
        Png = Path.Combine(Dir, "img.png");

        FFmpegOk =
            Run("-f", "lavfi", "-i", "testsrc=duration=1:size=320x240:rate=30",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
                "-shortest", "-pix_fmt", "yuv420p", Mp4OneSecond)
            && Run("-f", "lavfi", "-i", "testsrc=duration=0.5:size=160x120:rate=30",
                "-pix_fmt", "yuv420p", Mp4Half)
            && Run("-f", "lavfi", "-i", "color=c=red:size=64x64", "-frames:v", "1", Png);
    }

    private static bool Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class VideoEngineTests : IClassFixture<MediaFixture>
{
    private readonly MediaFixture _media;

    public VideoEngineTests(MediaFixture media) => _media = media;

    private bool Ready => VideoEngine.MpvAvailable && _media.FFmpegOk;

    /// <summary>Engine headless: vo=null, ao=null, sin HWND.</summary>
    private static VideoEngine MakeEngine() =>
        new(getWindowHandle: () => 0, forcedVo: "null", nullAudio: true);

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(15);
        }
        return condition();
    }

    // ------------------------------------------------------------------
    // Disponibilidad
    // ------------------------------------------------------------------

    // Este NO lleva la guarda `Ready` a propósito: es el canario. Su trabajo es
    // fallar a gritos el día que libmpv deje de encontrarse en la máquina de
    // desarrollo; guardarlo lo convertiría en un test que nunca dice nada.
    // Por eso mismo no puede correr en el CI, donde la DLL no existe (98 MB, no
    // se versiona): allí se excluye por este Trait, explícitamente y a la vista
    // en el comando, en vez de camuflarlo con un `return`.
    [Fact]
    [Trait("Requiere", "libmpv")]
    public void Mpv_IsAvailable_OnThisMachine()
    {
        // Aviso temprano si libmpv-2.dll dejó de encontrarse
        Assert.True(VideoEngine.MpvAvailable,
            "libmpv-2.dll no se pudo cargar (¿se movió libmpv-lgpl/?)");
    }

    [Fact]
    public void SupportedFormats_IncludeVideoAndImages()
    {
        var formats = VideoEngine.ListSupportedFormats();
        Assert.Contains(".mp4", formats);
        Assert.Contains(".mov", formats);
        Assert.Contains(".png", formats);
        Assert.Contains(".webm", formats);
    }

    // ------------------------------------------------------------------
    // Máquina de estados
    // ------------------------------------------------------------------

    [Fact]
    public void PlayVideo_TransitionsToPlaying_AndStopsAtEnd()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        var states = new List<MediaCueState>();
        var stateLock = new object();
        double latency = -1;
        bool frameReady = false;
        engine.StateChanged += (id, s) => { lock (stateLock) states.Add(s); };
        engine.LatencyReport += (id, ms) => latency = ms;
        engine.FrameReady += id => frameReady = true;

        engine.Play(1, _media.Mp4OneSecond);

        Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Playing, 10_000),
            $"no llegó a PLAYING (estado: {engine.GetState(1)})");
        Assert.True(engine.IsPlaying(1));
        Assert.True(engine.AnyPlaying);

        // mpv conoce la duración y el reloj avanza
        Assert.True(WaitFor(() => engine.GetDuration(1) > 0.9, 5_000),
            $"duración no reportada (fue {engine.GetDuration(1)})");
        Assert.True(WaitFor(() => engine.GetElapsed(1) > 0.1, 5_000),
            "time-pos no avanza");

        // Fin natural → STOPPED
        Assert.True(WaitFor(() => engine.GetState(1) == MediaCueState.Stopped, 15_000),
            $"no llegó a STOPPED (estado: {engine.GetState(1)})");
        Assert.False(engine.IsPlaying(1));
        Assert.True(latency >= 0.0, "no se reportó latencia");
        Assert.True(frameReady, "no se emitió FrameReady");
        lock (stateLock)
        {
            Assert.Contains(MediaCueState.Loading, states);
            Assert.Contains(MediaCueState.Loaded, states);
            Assert.Contains(MediaCueState.Playing, states);
        }
    }

    [Fact]
    public void Stop_TerminatesPlayerImmediately()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        engine.Stop(1);
        Assert.Equal(MediaCueState.Stopped, engine.GetState(1));
        Assert.False(engine.IsPlaying(1));
        Assert.Equal(0.0, engine.GetDuration(1));   // player terminado
    }

    [Fact]
    public void Image_StaysPlaying_UntilStopped()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(1, _media.Png);
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000),
            $"la imagen no llegó a PLAYING (estado: {engine.GetState(1)})");

        // image-display-duration=inf → sigue en PLAYING pasado un tiempo
        Thread.Sleep(600);
        Assert.True(engine.IsPlaying(1), "la imagen no se mantuvo en pantalla");
        engine.Stop(1);
        Assert.Equal(MediaCueState.Stopped, engine.GetState(1));
    }

    [Fact]
    public void Loop_KeepsPlayingPastFileEnd()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(1, _media.Mp4Half, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        // El archivo dura 0.5 s; con loop sigue reproduciendo a los 1.2 s
        Thread.Sleep(1_200);
        Assert.True(engine.IsPlaying(1), "el loop no mantuvo la reproducción");
        engine.Stop(1);
    }

    [Fact]
    public void SingleOutput_SecondVideoTerminatesFirst()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(1), 10_000));

        engine.Play(2, _media.Mp4Half, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.IsPlaying(2), 10_000));

        // Salida única: el player del cue 1 fue TERMINADO al arrancar el 2
        Assert.False(engine.IsPlaying(1), "el primer vídeo no fue terminado");
        engine.StopAll();
        Assert.False(engine.AnyPlaying);
    }

    [Fact]
    public void PauseResume_FreezesClock()
    {
        if (!Ready) return;
        using var engine = MakeEngine();
        engine.Play(1, _media.Mp4OneSecond, new VideoPlayParams(Loop: true));
        Assert.True(WaitFor(() => engine.GetElapsed(1) > 0.1, 10_000));

        engine.PauseAll();
        Thread.Sleep(100);   // dejar drenar el pipeline
        double frozen = engine.GetElapsed(1);
        Thread.Sleep(300);
        Assert.Equal(frozen, engine.GetElapsed(1), 1);

        engine.ResumeAll();
        Assert.True(WaitFor(() => engine.GetElapsed(1) > frozen + 0.05, 5_000),
            "el reloj no avanzó tras reanudar");
        engine.Stop(1);
    }

    // ------------------------------------------------------------------
    // Errores
    // ------------------------------------------------------------------

    [Fact]
    public void MissingFile_EmitsError()
    {
        using var engine = MakeEngine();
        string? error = null;
        engine.ErrorOccurred += (id, msg) => error = msg;
        engine.Play(1, Path.Combine(_media.Dir, "no_existe.mp4"));
        Assert.True(WaitFor(() => error is not null, 5_000));
        Assert.Equal(MediaCueState.Error, engine.GetState(1));
    }

    [Fact]
    public void UnsupportedFormat_EmitsError()
    {
        if (!Ready) return;   // faltaba: el error lo emite mpv, sin mpv no hay error
        using var engine = MakeEngine();
        string bad = Path.Combine(_media.Dir, "raro.xyz");
        File.WriteAllText(bad, "x");
        string? error = null;
        engine.ErrorOccurred += (id, msg) => error = msg;
        engine.Play(1, bad);
        Assert.True(WaitFor(() => error is not null, 5_000));
        Assert.Contains("no soportado", error);
    }

    [Fact]
    public void EmptyPath_EmitsErrorImmediately()
    {
        using var engine = MakeEngine();
        string? error = null;
        engine.ErrorOccurred += (id, msg) => error = msg;
        engine.Play(1, "");
        Assert.NotNull(error);
    }
}

// =============================================================================
// El desmontaje de los pools cuando AssignCue LANZA
// =============================================================================
// POR QUE EXISTE ESTA CLASE. El arreglo del «slot zombi» —que un AssignCue que
// lanza no deje el slot en el pool con BusyCueId puesto para siempre, y que su
// pipeline no se quede sin liberar— llevaba tres rondas RAZONADO y ninguna
// DEMOSTRADA: ninguna prueba forzaba a AssignCue a fallar, y las rutas HAP y
// ProRes no las habia ejercitado nadie con un compositor detras. Ese hueco es
// por donde se colaron dos regresiones seguidas.
//
// Lo que se afirma, en los TRES pools:
//   (a) la excepcion ORIGINAL llega al llamante — es la unica que le dice que
//       marque _gpuFailed, avise y saque el cue por la ruta clasica; si la
//       sustituye un fallo del desmontaje, el llamante ve algo que no sabe
//       interpretar;
//   (b) el pool NO retiene un slot con BusyCueId puesto — si lo retuviera, ese
//       slot no volveria a casar nunca con la busqueda de slots libres y su
//       pipeline no se liberaria hasta cerrar la aplicacion (en HAP y ProRes,
//       sin latch _gpuFailed que enganche, una fuga POR CADA CUE);
//   (c) el desmontaje llega hasta el final aunque un sink lance por el camino.
//
// POR QUE CON REFLEXION, y no por la puerta principal: los tres Acquire*Slot
// son privados y, con el pool vacio, lo primero que hacen es CONSTRUIR su
// pipeline (device D3D9Ex, interop GL, superficies compartidas). Eso exige GPU
// y esta prueba tiene que correr en el CI, que es justo donde no hay ninguna.
// Asi que se SIEMBRA el pool con un slot ya montado y se entra por la rama de
// REUTILIZACION, que es la que lleva directamente al AssignCue.
//
// El pipeline sembrado se fabrica SIN constructor (todos sus campos a cero):
// no toca la GPU, el `-=` de FrameRendered funciona igual, y su Dispose entra y
// revienta por dentro — que es, ni mas ni menos, el «desmontaje que lanza» del
// hallazgo. Si algun dia estos nombres privados cambian, estas pruebas se ponen
// rojas con un mensaje que dice cual: eso es correcto, no un estorbo.
// =============================================================================

public class VideoEnginePoolTeardownTests
{
    private const string FalloAlAsignar =
        "capa muerta: esta salida se quedo sin compositor GPU";
    private const string FalloAlLimpiar =
        "ClearSurface: el grafo ya no acepta mutaciones";

    /// <summary>Superficie de compositor FALSA. Reproduce lo que hace la de
    /// Secuence en sus caminos de error: AssignCue lanza cuando la capa esta
    /// muerta, y ClearSurface tambien puede lanzar (toma el lock del grafo y
    /// libera recursos D3D11).</summary>
    private sealed class CapaFalsa : IVideoLayerSurface
    {
        public bool LanzaAlAsignar { get; init; }
        public bool LanzaAlLimpiar { get; init; }

        /// <summary>ClearSurface llego a esta capa (aunque luego lanzara).</summary>
        public bool Limpiada { get; private set; }

        public void SetSurface(nint d3d9Surface, int width, int height) { }

        public void InvalidateFrame() { }

        public void AssignCue(int cueId)
        {
            if (LanzaAlAsignar)
                throw new InvalidOperationException(FalloAlAsignar);
        }

        public void ReleaseCue(int cueId) { }

        public void ClearSurface()
        {
            Limpiada = true;
            if (LanzaAlLimpiar)
                throw new InvalidOperationException(FalloAlLimpiar);
        }
    }

    // ── Andamio de reflexion ──────────────────────────────────────────

    private static readonly Type Motor = typeof(VideoEngine);

    /// <summary>Motor headless: no arranca mpv ni pide GPU en el constructor.</summary>
    private static VideoEngine MotorHeadless() =>
        new(getWindowHandle: () => 0, forcedVo: "null", nullAudio: true);

    private static IList PoolDe(VideoEngine engine, string campo)
    {
        FieldInfo fi = Motor.GetField(campo, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"VideoEngine.{campo} ya no existe");
        return (IList)(fi.GetValue(engine)
            ?? throw new InvalidOperationException($"VideoEngine.{campo} es null"));
    }

    /// <summary>Los cues que el pool sigue teniendo RESERVADOS. Vacio es lo
    /// unico correcto tras un arranque fallido: un id aqui es el slot zombi.</summary>
    private static List<int> CuesReservados(VideoEngine engine, string campoPool)
    {
        var reservados = new List<int>();
        foreach (object? slot in PoolDe(engine, campoPool))
        {
            if (slot is null)
                continue;
            FieldInfo fi = slot.GetType().GetField("BusyCueId")
                ?? throw new InvalidOperationException("El slot ya no tiene BusyCueId");
            if (fi.GetValue(slot) is int cue)
                reservados.Add(cue);
        }
        return reservados;
    }

    /// <summary>Un slot del pool YA MONTADO, sin pasar por su constructor: es
    /// la unica forma de llegar a la rama de reutilizacion sin GPU.</summary>
    private static object SlotSembrado(string tipoAnidado,
        params (string Campo, object? Valor)[] campos)
    {
        Type t = Motor.GetNestedType(tipoAnidado, BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"VideoEngine.{tipoAnidado} ya no existe");
        object slot = RuntimeHelpers.GetUninitializedObject(t);
        foreach ((string campo, object? valor) in campos)
        {
            FieldInfo fi = t.GetField(campo)
                ?? throw new InvalidOperationException($"{tipoAnidado}.{campo} ya no existe");
            fi.SetValue(slot, valor);
        }
        return slot;
    }

    /// <summary>Pipeline INERTE: campos a cero, ni un recurso de GPU detras.</summary>
    private static object PipelineInerte(Type tipo) =>
        RuntimeHelpers.GetUninitializedObject(tipo);

    /// <summary>MpvGpuRenderer.Dispose() pone _shutdown ANTES de tocar nada
    /// mas, asi que este campo dice si el pipeline llego a recibir su Dispose
    /// — la unica forma de AFIRMAR, y no suponer, que la liberacion ocurrio
    /// aunque el desmontaje de los sinks lanzara por el camino.</summary>
    private static bool RecibioDispose(object pipelineMpv)
    {
        FieldInfo fi = typeof(MpvGpuRenderer)
            .GetField("_shutdown", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MpvGpuRenderer._shutdown ya no existe");
        return fi.GetValue(pipelineMpv) is true;
    }

    private static void Adquirir(VideoEngine engine, string metodo, params object?[] args)
    {
        MethodInfo mi = Motor.GetMethod(metodo, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"VideoEngine.{metodo} ya no existe");
        // DoNotWrapExceptions: se quiere la excepcion ORIGINAL tal y como la ve
        // el llamante de verdad, no envuelta en TargetInvocationException — que
        // es justo lo que afirma el punto (a).
        _ = mi.Invoke(engine, BindingFlags.DoNotWrapExceptions, binder: null, args, culture: null);
    }

    // ── Las tres rutas ────────────────────────────────────────────────

    [Fact]
    public void PoolMpv_AssignCueQueLanza_DevuelveElFalloOriginalYNoDejaSlotZombi()
    {
        using var engine = MotorHeadless();
        // La primera lanza al asignar Y al limpiar; la segunda solo apunta si
        // la limpiaron. Asi se ve el agravante de la ruta mpv: un sink que
        // lanza no puede dejar a los siguientes sin desenganchar.
        var mala = new CapaFalsa { LanzaAlAsignar = true, LanzaAlLimpiar = true };
        var buena = new CapaFalsa();
        object pipeline = PipelineInerte(typeof(MpvGpuRenderer));
        PoolDe(engine, "_gpuPool").Add(SlotSembrado("GpuSlot",
            ("Pipeline", pipeline),
            ("Sinks", new List<IVideoLayerSurface> { mala, buena }),
            ("Screen", 0), ("Layer", 2), ("Spec", "1920x1080")));

        try
        {
            var exc = Assert.Throws<InvalidOperationException>(() =>
                Adquirir(engine, "AcquireGpuSlot", 7, 0, 2, 1920, 1080, null));
            Assert.Equal(FalloAlAsignar, exc.Message);   // (a)

            Assert.Empty(CuesReservados(engine, "_gpuPool"));   // (b)

            Assert.True(buena.Limpiada,                          // (c)
                "el segundo sink se quedo enganchado porque el primero lanzo");
            Assert.True(RecibioDispose(pipeline),
                "el pipeline se quedo sin liberar y ya no esta en el pool: fuga");
        }
        finally
        {
            PoolDe(engine, "_gpuPool").Clear();
        }
    }

    [Fact]
    public void PoolHap_AssignCueQueLanza_DevuelveElFalloOriginalYNoDejaSlotZombi()
    {
        using var engine = MotorHeadless();
        var mala = new CapaFalsa { LanzaAlAsignar = true, LanzaAlLimpiar = true };
        PoolDe(engine, "_hapPool").Add(SlotSembrado("HapSlot",
            ("Pipeline", PipelineInerte(typeof(HapGpuPipeline))),
            ("Sink", mala),
            ("Screen", 0), ("Layer", 1), ("Spec", "1280x720")));

        try
        {
            var exc = Assert.Throws<InvalidOperationException>(() =>
                Adquirir(engine, "AcquireHapSlot", 9, 0, 1, 1280, 720));
            Assert.Equal(FalloAlAsignar, exc.Message);

            // Sin _gpuFailed que enganche, un slot reservado aqui seria una
            // fuga de pipeline HAP por cada cue disparado.
            Assert.Empty(CuesReservados(engine, "_hapPool"));
            Assert.True(mala.Limpiada, "el sink HAP no se desengancho");
        }
        finally
        {
            PoolDe(engine, "_hapPool").Clear();
        }
    }

    [Fact]
    public void PoolProRes_AssignCueQueLanza_DevuelveElFalloOriginalYNoDejaSlotZombi()
    {
        using var engine = MotorHeadless();
        var mala = new CapaFalsa { LanzaAlAsignar = true, LanzaAlLimpiar = true };
        // La SESION se siembra con la firma exacta del video entrante: asi el
        // metodo la da por buena y no intenta crear texturas (que necesitarian
        // GPU) antes de llegar al AssignCue, que es lo que aqui se prueba.
        PixelLayout layout = default;
        PoolDe(engine, "_proresPool").Add(SlotSembrado("ProResSlot",
            ("Pipeline", PipelineInerte(typeof(ProResGpuPipeline))),
            ("Sink", mala),
            ("Screen", 0), ("Layer", 3), ("Spec", "1280x720"),
            ("Session", PipelineInerte(typeof(ProResVideoSession))),
            ("SessionSpec", ProResVideoSession.SpecOf(1920, 1080, layout))));

        try
        {
            var exc = Assert.Throws<InvalidOperationException>(() =>
                Adquirir(engine, "AcquireProResSlot", 11, 0, 3, 1280, 720, 1920, 1080, layout));
            Assert.Equal(FalloAlAsignar, exc.Message);

            Assert.Empty(CuesReservados(engine, "_proresPool"));
            Assert.True(mala.Limpiada, "el sink ProRes no se desengancho");
        }
        finally
        {
            PoolDe(engine, "_proresPool").Clear();
        }
    }
}

// =============================================================================
// El copiador de presentacion: CON fence solo para quien lo pida
// =============================================================================
// POR QUE EXISTE ESTA CLASE. WinCue esta en produccion y hospeda la superficie
// de presentacion en SU device D3D9Ex: no necesita fence, y no debe pagarlo.
// CopyPresentFenced BLOQUEA esperando a la GPU, una vez por fotograma NUEVO y
// por capa, dentro del lock del grafo. Meterselo a WinCue seria una regresion
// de rendimiento invisible en el diff —una linea, tres sitios— y visible en
// sala; hasta ahora NADA en la suite lo afirmaba.
//
// Se prueba la REGLA (VideoEngine.FrameCopierFor), que es por donde pasan los
// tres pools. Entrar por la puerta principal no sirve: el cableado del copiador
// solo ocurre al CREAR el slot, y crear un slot construye un pipeline de verdad
// (device D3D9Ex, interop GL, superficies) que exige GPU — y esto tiene que
// correr en el CI, que es justo donde no hay ninguna.
// =============================================================================

public class VideoEngineFrameCopierTests
{
    /// <summary>El sink de WINCUE: implementa el interfaz y NO redefine
    /// NeedsCrossDeviceFence, o sea que se queda con el false por defecto del
    /// contrato. Igual que CompositorLayerControl.</summary>
    private sealed class SinkPorDefecto : IVideoLayerSurface
    {
        public void SetSurface(nint d3d9Surface, int width, int height) { }

        public void InvalidateFrame() { }

        public void AssignCue(int cueId) { }

        public void ReleaseCue(int cueId) { }

        public void ClearSurface() { }
    }

    /// <summary>El sink de SECUENCE: abre la superficie en otro device (D3D11)
    /// y por eso pide la barrera. Igual que CapaCompositor.</summary>
    private sealed class SinkQuePideFence : IVideoLayerSurface
    {
        public bool NeedsCrossDeviceFence => true;

        public void SetSurface(nint d3d9Surface, int width, int height) { }

        public void InvalidateFrame() { }

        public void AssignCue(int cueId) { }

        public void ReleaseCue(int cueId) { }

        public void ClearSurface() { }
    }

    [Fact]
    public void SinkPorDefecto_RecibeElCopiadorSINFence()
    {
        bool sinFence = false;
        bool conFence = false;
        Action copiador = VideoEngine.FrameCopierFor(new SinkPorDefecto(),
            sinFence: () => sinFence = true,
            conFence: () => conFence = true);

        copiador();

        Assert.True(sinFence,
            "el sink por defecto (WinCue) no recibio CopyPresent: si le llega el " +
            "fenceado, paga un bloqueo de GPU por fotograma y por capa");
        Assert.False(conFence,
            "el sink por defecto (WinCue) recibio el copiador FENCEADO");
    }

    [Fact]
    public void SinkQuePideFence_RecibeElCopiadorCONFence()
    {
        bool sinFence = false;
        bool conFence = false;
        Action copiador = VideoEngine.FrameCopierFor(new SinkQuePideFence(),
            sinFence: () => sinFence = true,
            conFence: () => conFence = true);

        copiador();

        // El simetrico, para que la prueba de arriba no pueda salir verde
        // porque el metodo devuelva SIEMPRE el mismo copiador.
        Assert.True(conFence,
            "el sink de Secuence no recibio CopyPresentFenced: sin la barrera " +
            "entre devices, D3D11 lee negro de forma no determinista");
        Assert.False(sinFence,
            "el sink de Secuence recibio el copiador sin fence");
    }
}
