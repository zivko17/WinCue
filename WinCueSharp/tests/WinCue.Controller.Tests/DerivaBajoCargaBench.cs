// =============================================================================
// DerivaBajoCargaBench.cs — Medida REAL de deriva con el PC cargado
// =============================================================================
//
// No es un test: es el BANCO con el que se compara antes/después. Corre con el
// planificador de PRODUCCIÓN (TimerScheduler, System.Threading.Timer) y con los
// callbacks bombeados por UN SOLO hilo, como hace el dispatcher de WPF — que es
// donde el PC cargado del cliente hace daño de verdad.
//
// Reproduce el caso de Andrés: una cadena larga de AUTO_CONTINUE mientras la
// máquina está ocupada, y mide cuánto se ha ido el show respecto a su tiempo
// nominal. Está DESACTIVADO por defecto (tarda segundos y depende de la carga
// de la máquina): se enciende con la variable de entorno.
//
//   $env:WINCUE_BENCH_DERIVA = "1"      # obligatoria para que corra
//   $env:WINCUE_BENCH_CARGA  = "16"     # hilos quemando CPU (0 = sin carga)
//   $env:WINCUE_BENCH_CUES   = "100"    # eslabones de la cadena
//   $env:WINCUE_BENCH_OUT    = "…\deriva.txt"   # dónde deja el resultado
//
// A PROPÓSITO no usa nada del ShowClock: así el MISMO fichero compila y mide
// igual en el commit base, y la comparación antes/después es honesta.
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using WinCue.Core;

namespace WinCue.Controller.Tests;

public class DerivaBajoCargaBench
{
    [Fact]
    public void CadenaLarga_ConLaMaquinaCargada_CuantoSeVaElShow()
    {
        if (Environment.GetEnvironmentVariable("WINCUE_BENCH_DERIVA") != "1")
            return;   // apagado en la ejecución normal de la suite

        int cues = Leer("WINCUE_BENCH_CUES", 100);
        int hilosCarga = Leer("WINCUE_BENCH_CARGA", Environment.ProcessorCount);
        double paso = Leer("WINCUE_BENCH_PASO_MS", 50) / 1000.0;
        double nominal = (cues - 1) * paso;

        // ── Carga: hilos quemando CPU para que el bombeo compita ────────
        using var stop = new CancellationTokenSource();
        var quemadores = new List<Thread>();
        for (int i = 0; i < hilosCarga; i++)
        {
            var t = new Thread(() =>
            {
                double x = 1.0001;
                while (!stop.IsCancellationRequested)
                    x = Math.Sqrt(x * 1.0000001) + 1.0;
                GC.KeepAlive(x);
            })
            { IsBackground = true };
            t.Start();
            quemadores.Add(t);
        }

        // ── Bombeo de un solo hilo (el papel del dispatcher de WPF) ─────
        // Al cerrar quedan timers vivos que intentan encolar: se tragan sin
        // ruido (si no, tiran el proceso de test justo al terminar la medida).
        using var cola = new BlockingCollection<Action>();
        void Encolar(Action a)
        {
            // ObjectDisposedException hereda de InvalidOperationException: un
            // solo catch cubre las dos formas de "la cola ya está cerrada".
            try { if (!cola.IsAddingCompleted) cola.Add(a); }
            catch (InvalidOperationException) { /* cerrando */ }
        }
        var bomba = new Thread(() =>
        {
            foreach (var a in cola.GetConsumingEnumerable())
                a();
        })
        { IsBackground = true };
        bomba.Start();

        // ── Show: cadena de cues instantáneos con AUTO_CONTINUE ─────────
        var model = new CueModel();
        for (int i = 0; i < cues; i++)
        {
            model.AddCue(new Cue
            {
                Id = model.NextId(),
                Type = CueType.Memo,          // sin motores: se mide EL RELOJ
                Name = $"eslabón {i}",
                ContinueMode = ContinueMode.AutoContinue,
                PostWait = paso,
            });
        }

        string dir = Path.Combine(Path.GetTempPath(), "wincue_bench_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsManager(Path.Combine(dir, "settings.json"));
        var scheduler = new TimerScheduler(Encolar);
        var controller = new MainController(model, new FakeView(),
            new FakeAudioEngine(), new FakeVideoEngine(), scheduler, settings);

        var marcas = new List<long>(cues + 1);
        using var terminado = new ManualResetEventSlim(false);
        controller.ActiveIndexChanged += _ =>
        {
            marcas.Add(Stopwatch.GetTimestamp());
            if (marcas.Count >= cues)
                terminado.Set();
        };

        Encolar(() => { controller.OnSetPlayhead(0); marcas.Clear(); });
        Encolar(controller.OnGoPressed);

        bool ok = terminado.Wait(TimeSpan.FromSeconds(30 + nominal * 3));
        stop.Cancel();                       // primero la carga: el cierre respira
        Encolar(controller.OnStopPressed);   // cancela los timers de cadena vivos
        Thread.Sleep(150);
        cola.CompleteAdding();
        foreach (var t in quemadores)
            t.Join(TimeSpan.FromSeconds(2));
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }

        Assert.True(ok, $"la cadena no terminó: {marcas.Count}/{cues} eslabones");

        double real = Stopwatch.GetElapsedTime(marcas[0], marcas[^1]).TotalSeconds;
        double deriva = real - nominal;

        // Peor salto entre eslabones consecutivos (lo que se ve como "tirón")
        double peor = 0.0;
        for (int i = 1; i < marcas.Count; i++)
            peor = Math.Max(peor,
                Stopwatch.GetElapsedTime(marcas[i - 1], marcas[i]).TotalSeconds - paso);

        string informe = string.Create(CultureInfo.InvariantCulture,
            $"cues={cues} paso={paso * 1000:0}ms carga={hilosCarga} hilos\n" +
            $"nominal={nominal:0.000}s real={real:0.000}s " +
            $"deriva={deriva * 1000:+0;-0;0}ms ({deriva / nominal * 100:0.00}%) " +
            $"peor_salto={peor * 1000:0.0}ms\n");
        Console.Write(informe);
        string? salida = Environment.GetEnvironmentVariable("WINCUE_BENCH_OUT");
        if (salida is { Length: > 0 })
            File.AppendAllText(salida, informe);
    }

    private static int Leer(string nombre, int porDefecto) =>
        int.TryParse(Environment.GetEnvironmentVariable(nombre), out int v) && v >= 0
            ? v : porDefecto;
}
