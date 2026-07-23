// =============================================================================
// HapWatchdogTests.cs — Vigía de salud del motor HAP durante el soak
// =============================================================================
// Lógica pura (sin hilos ni GPU): se le alimentan instantáneas sintéticas de
// cues activos (las mismas ActiveCueInfo que ve la vista) y muestras de memoria
// por vuelta, y se comprueba que caza las cuatro anomalías que jamás deberían
// pasar en una cadena HAP sana — y que NO grita con una cadena sana. Blindaje
// del bug de directo de julio 2026 (audio espejo atrasado → cuenta negativa).
// =============================================================================

using WinCue.Controller;
using WinCue.Core;

namespace WinCue.Controller.Tests;

public class HapWatchdogTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(),
        $"wincue-watch-test-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { if (File.Exists(_log)) File.Delete(_log); } catch (IOException) { }
    }

    private HapWatchdog Make(params (int Id, double Dur)[] durations)
    {
        var map = durations.ToDictionary(d => d.Id, d => d.Dur);
        return new HapWatchdog(id => map.TryGetValue(id, out double d) ? d : 0.0, _log);
    }

    private static ActiveCueInfo Video(int id, double elapsed, double dur) =>
        new(id, id.ToString(), $"cue {id}", CueType.Video, "#fff",
            elapsed, Math.Max(0.0, dur - elapsed), null);

    [Fact]
    public void CadenaSana_NoGritaNada()
    {
        var w = Make((2, 4.0), (3, 4.0));
        // Cue 2 avanza dentro de su duración; luego cambia a cue 3.
        for (int i = 0; i <= 8; i++)
            w.Tick(new[] { Video(2, i * 0.5, 4.0) }, loop: 0);
        for (int i = 0; i <= 8; i++)
            w.Tick(new[] { Video(3, i * 0.5, 4.0) }, loop: 0);
        Assert.Equal(0, w.Summarize());
    }

    [Fact]
    public void RemainingNegativo_SeDetecta()
    {
        // El motor reporta elapsed MÁS ALLÁ de la duración declarada: remaining
        // real negativo (el panel lo clampa a 0; el vigía lo deduce). elapsed
        // 4,5 sobre dur 4 → remaining −0,5 s, pero aún < dur+margen (sin colgado).
        var w = Make((2, 4.0));
        w.Tick(new[] { Video(2, 4.5, 4.0) }, loop: 3);
        int total = w.Summarize();
        Assert.True(total >= 1);
        Assert.Contains("REMAINING NEGATIVO", File.ReadAllText(_log));
    }

    [Fact]
    public void CueColgado_SeDetecta()
    {
        // Cue que sigue sonando MUCHO después de su duración (fin que no llega).
        var w = Make((5, 4.0));
        w.Tick(new[] { Video(5, 7.0, 4.0) }, loop: 2);
        w.Summarize();
        Assert.Contains("CUE COLGADO", File.ReadAllText(_log));
    }

    [Fact]
    public void CadenaEstancada_SeDetecta()
    {
        // Mismo conjunto de activos y CERO progreso durante > 30 ticks: cuelgue
        // del AUTO_CONTINUE. elapsed constante y dentro de la duración (no es
        // ni negativo ni colgado): la única anomalía debe ser el estancamiento.
        var w = Make((2, 60.0));
        for (int i = 0; i < 35; i++)
            w.Tick(new[] { Video(2, 2.0, 60.0) }, loop: 1);
        Assert.Equal(1, w.Summarize());
        Assert.Contains("CADENA ESTANCADA", File.ReadAllText(_log));
    }

    [Fact]
    public void ProgresoNormal_NoEsEstancamiento()
    {
        // El mismo cue activo pero con elapsed que avanza NO es estancamiento.
        var w = Make((2, 60.0));
        for (int i = 0; i < 40; i++)
            w.Tick(new[] { Video(2, i * 0.5, 60.0) }, loop: 1);
        Assert.Equal(0, w.Summarize());
    }

    [Fact]
    public void FugaWorkingSet_MonotonaSeDetecta()
    {
        var w = Make();
        // Tras el warmup, working set estrictamente creciente y > umbral en la
        // ventana: candidato a fuga real (crecimiento sostenido en régimen).
        double ws = 300.0;
        for (int loop = 1; loop <= 22; loop++)
        {
            w.OnLoopClosed(loop, ws, handles: 1000);
            ws += 20.0;   // +20 MB por vuelta → +120 en cada ventana de 6
        }
        w.Summarize();
        Assert.Contains("FUGA CANDIDATA (working set)", File.ReadAllText(_log));
    }

    [Fact]
    public void RampaDeArranque_LuegoRegimen_NoEsFuga()
    {
        // Regresión del FALSO POSITIVO del soak: el working set del motor HAP
        // rampa de forma monótona durante el arranque (pipelines del pool,
        // decode a RAM del audio espejo, JIT) y NO llega a régimen hasta ~10
        // vueltas; luego oscila (sierra por el decode 4K) sin tendencia. El
        // vigía debe ignorar la rampa (warmup) y NO gritar fuga.
        var w = Make();
        // Rampa de arranque monótona (vueltas 1-11): +40 MB por vuelta.
        double ws = 300.0;
        int loop = 1;
        for (; loop <= 11; loop++, ws += 40.0)
            w.OnLoopClosed(loop, ws, handles: 2000 + loop);
        // Régimen: sierra sin tendencia (vueltas 12-30).
        double[] sierra = { 900, 2000, 600, 1200, 1150, 1200, 900, 650, 2020, 610,
                            1190, 1170, 1210, 640, 2020, 600, 1180, 610, 900 };
        foreach (double v in sierra)
            w.OnLoopClosed(loop++, v, handles: 2100 + (loop % 5));
        Assert.Equal(0, w.Summarize());
    }

    [Fact]
    public void MemoriaEstable_NoEsFuga()
    {
        var w = Make();
        // Working set que sube y baja (transitorios de decode 4K): jamás
        // monótono → sin falso positivo de fuga, incluso pasado el warmup.
        double[] pattern =
        {
            300, 1900, 500, 480, 1850, 520, 490, 1900, 510, 500,
            1880, 505, 495, 1910, 515, 500, 1890, 508, 498, 1900,
        };
        for (int i = 0; i < pattern.Length; i++)
            w.OnLoopClosed(i + 1, pattern[i], handles: 1000 + (i % 3));
        Assert.Equal(0, w.Summarize());
    }
}
