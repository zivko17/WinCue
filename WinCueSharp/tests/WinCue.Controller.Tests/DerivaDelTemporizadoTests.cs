// =============================================================================
// DerivaDelTemporizadoTests.cs — Lo que el PC cargado le hacía al show
// =============================================================================
//
// EL CASO REAL: el cliente no tiene un PC dedicado. Con la máquina cargada,
// System.Threading.Timer despierta TARDE, y si cada eslabón de una cadena se
// programaba "dentro de N ms" desde el disparo (ya tardío) del anterior, el
// retraso se acumulaba: una cadena larga derivaba segundos.
//
// Aquí el PC cargado se SIMULA de forma determinista: VirtualScheduler
// .RetrasoDeDisparo hace que TODOS los timers despierten N ms después de su
// vencimiento. Con plazos absolutos ese retraso es CONSTANTE (cada disparo
// llega tarde una vez) en vez de ACUMULATIVO.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class DerivaDelTemporizadoTests : ControllerTestBase
{
    /// <summary>El reloj del controlador y el del planificador son EL MISMO
    /// objeto. Es la premisa de todo lo demás: si fueran dos, lo que se mide y
    /// lo que se dispara podrían separarse otra vez.</summary>
    [Fact]
    public void ElControladorYElPlanificadorCompartenReloj()
    {
        Assert.Same(Clock.Clock, Controller.Clock);
    }

    // ------------------------------------------------------------------
    // El mecanismo, al desnudo: relativo vs absoluto
    // ------------------------------------------------------------------

    /// <summary>Veinte plazos de 100 ms encadenados con timers que despiertan
    /// 25 ms tarde. Encadenados en RELATIVO llegan al final con 500 ms de
    /// deriva; en ABSOLUTO, con los 25 ms del último salto y ni uno más.</summary>
    [Fact]
    public void PlazosRelativos_AcumulanElRetraso_LosAbsolutosNo()
    {
        const int saltos = 20;
        Clock.RetrasoDeDisparo = 25;
        var handles = new List<IDisposable>();

        try
        {
            // ── Encadenado RELATIVO (lo que se hacía antes) ────────────
            double finRelativo = -1;
            int n = 0;
            void SiguienteRelativo()
            {
                if (++n >= saltos) { finRelativo = Clock.Clock.NowRaw; return; }
                handles.Add(Clock.Schedule(100, SiguienteRelativo));
            }
            handles.Add(Clock.Schedule(100, SiguienteRelativo));
            Clock.Advance(10_000);

            // ── Encadenado ABSOLUTO (lo que se hace ahora) ─────────────
            double t0 = Clock.Clock.NowRaw;
            double finAbsoluto = -1;
            int m = 0;
            void SiguienteAbsoluto()
            {
                if (++m >= saltos) { finAbsoluto = Clock.Clock.NowRaw; return; }
                handles.Add(Clock.ScheduleAt(t0 + (m + 1) * 0.1, SiguienteAbsoluto));
            }
            handles.Add(Clock.ScheduleAt(t0 + 0.1, SiguienteAbsoluto));
            Clock.Advance(10_000);

            // Relativo: 20 × (100 + 25) = 2500 ms → 500 ms de deriva
            Assert.Equal(2.500, finRelativo, 3);
            // Absoluto: 2000 ms nominales + los 25 ms del ÚLTIMO salto
            Assert.Equal(t0 + 2.025, finAbsoluto, 3);
        }
        finally
        {
            foreach (var h in handles)
                h.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Lo mismo, pero con un show de verdad
    // ------------------------------------------------------------------

    /// <summary>Sesenta cues encadenados con AUTO_CONTINUE cada 100 ms y
    /// timers que despiertan 25 ms tarde. Sin plazos absolutos, el último
    /// cue saldría a los 7,4 s (1,5 s tarde): con ellos sale a los 5,925 s —
    /// los 5,9 s nominales más el retraso del último disparo.</summary>
    [Fact]
    public void CadenaLarga_ConDisparosTardios_NoAcumulaDeriva()
    {
        const int cues = 60;
        Clock.RetrasoDeDisparo = 25;
        for (int i = 0; i < cues; i++)
            AddCue(CueType.Audio, setup: c =>
            {
                c.ContinueMode = ContinueMode.AutoContinue;
                c.PostWait = 0.1;
            });

        Controller.OnGoPressed();

        // A los 5,9 s nominales han salido todos menos el último (que espera
        // los 25 ms de su propio disparo tardío, no los 1.475 de la deriva).
        Clock.Advance(5_900);
        Assert.Equal(cues - 1, Audio.Plays.Count);

        Clock.Advance(50);
        Assert.Equal(cues, Audio.Plays.Count);
    }

    /// <summary>Lo mismo con cues WAIT encadenados por AUTO_FOLLOW: el fin de
    /// una espera lo marca el RELOJ, así que su eslabón siguiente cuelga del
    /// instante ideal. (En un media el fin lo marca el motor y ahí NO se
    /// finge: manda el archivo que está sonando.)</summary>
    [Fact]
    public void CadenaDeEsperas_ConDisparosTardios_NoAcumulaDeriva()
    {
        const int esperas = 20;
        Clock.RetrasoDeDisparo = 30;
        for (int i = 0; i < esperas; i++)
            AddCue(CueType.Wait, duration: 0.25,
                setup: c => c.ContinueMode = ContinueMode.AutoFollow);
        var final = AddCue(CueType.Audio);

        Controller.OnGoPressed();

        // 20 × 250 ms = 5 s nominales. Con deriva serían 5 s + 20 × 30 ms.
        Clock.Advance(5_000);
        Assert.Empty(Audio.Plays);          // falta el retraso del último salto
        Clock.Advance(60);
        var play = Assert.Single(Audio.Plays);
        Assert.Equal(final.Id, play.CueId);
    }

    /// <summary>Un plazo que llega YA VENCIDO (parón largo de la máquina) se
    /// dispara de inmediato y la cadena se recupera: no arrastra el hueco.</summary>
    [Fact]
    public void UnParonLargo_SeRecupera_NoSeArrastra()
    {
        Clock.RetrasoDeDisparo = 900;       // parón brutal: casi un segundo
        for (int i = 0; i < 5; i++)
            AddCue(CueType.Audio, setup: c =>
            {
                c.ContinueMode = ContinueMode.AutoContinue;
                c.PostWait = 0.2;
            });

        Controller.OnGoPressed();
        Clock.Advance(2_000);

        // Los 5 salieron: los plazos vencidos durante el parón dispararon en
        // cuanto el reloj volvió, sin encadenar un segundo de retraso cada uno.
        Assert.Equal(5, Audio.Plays.Count);
    }

    /// <summary>Pero un parón MUY grande no se recupera comprimiendo esperas:
    /// pasado MaxRecuperacionSegundos la cadena se reancla al presente. Si no,
    /// una máquina que se va treinta segundos volvería vaciando media lista de
    /// golpe — en directo, mucho peor que ir tarde.</summary>
    [Fact]
    public void UnParonEnorme_ReanclaLaCadena_NoVaciaLaLista()
    {
        AddCue(CueType.Audio, setup: c =>
        {
            c.ContinueMode = ContinueMode.AutoContinue;
            c.PostWait = 0.5;
        });
        for (int i = 0; i < 4; i++)
            AddCue(CueType.Audio, setup: c =>
            {
                c.ContinueMode = ContinueMode.AutoContinue;
                c.PostWait = 2.0;      // > MaxRecuperacionSegundos... casi
            });

        Clock.RetrasoDeDisparo = 30_000;   // la máquina desaparece 30 s
        Controller.OnGoPressed();
        Clock.Advance(31_000);

        // El primer eslabón sale (el GO es de mano) y el segundo también,
        // pero la cadena NO se desploma entera: los plazos siguientes vuelven
        // a colgar del presente y siguen esperando sus 2 s.
        Assert.Equal(2, Audio.Plays.Count);

        Clock.Advance(32_000);
        Assert.Equal(3, Audio.Plays.Count);
    }

    // ------------------------------------------------------------------
    // Auto-corrección visible: lo que se ve es tiempo REAL
    // ------------------------------------------------------------------

    /// <summary>Si el disparo llega tarde, el remaining mostrado cuenta desde
    /// que la acción arrancó DE VERDAD, no desde su instante teórico. Si
    /// fingiera el ideal, la cuenta atrás llegaría a cero con el cue aún
    /// sonando — que es exactamente el susto de un directo.</summary>
    [Fact]
    public void RemainingMostrado_TrasUnDisparoTardio_CuentaTiempoReal()
    {
        Clock.RetrasoDeDisparo = 300;
        AddCue(CueType.Audio, duration: 10.0, setup: c => c.PreWait = 1.0);

        Controller.OnGoPressed();
        Clock.Advance(1_000);
        Assert.Empty(Audio.Plays);          // el pre_wait despertó tarde
        Clock.Advance(300);
        Assert.Single(Audio.Plays);         // arrancó de verdad en t = 1,3 s

        Clock.Advance(2_000);               // dos segundos DE VERDAD sonando
        var fila = Assert.Single(Controller.SnapshotActiveCues(out double? primario));

        Assert.Equal(2.0, fila.Elapsed, 2);       // real, no 2,3 (el ideal)
        Assert.Equal(8.0, fila.Remaining!.Value, 2);
        Assert.Equal(8.0, primario!.Value, 2);
    }

    /// <summary>Mientras dura el pre_wait la vista enseña cuánto falta, y esa
    /// cuenta también se mide contra el reloj del show.</summary>
    [Fact]
    public void DuranteElPreWait_LaCuentaAtrasEsDelRelojDelShow()
    {
        AddCue(CueType.Audio, duration: 5.0, setup: c => c.PreWait = 4.0);

        Controller.OnGoPressed();
        Clock.Advance(1_500);

        var fila = Assert.Single(Controller.SnapshotActiveCues(out _));
        Assert.Equal(2.5, fila.PreWaitRemaining!.Value, 2);
    }

    // ------------------------------------------------------------------
    // Pausa
    // ------------------------------------------------------------------

    /// <summary>La pausa congela las cuentas y reanudar no salta: es la misma
    /// semántica de antes (TStart += dt), pero conseguida congelando EL RELOJ
    /// en vez de corrigiendo la marca de cada cue.</summary>
    [Fact]
    public void Pausa_CongelaLaCuenta_YReanudarNoSalta()
    {
        AddCue(CueType.Audio, duration: 30.0);

        Controller.OnGoPressed();
        Clock.Advance(2_000);
        Assert.Equal(2.0, Elapsed(), 2);

        Controller.OnPauseToggle();
        Clock.Advance(9_000);               // nueve segundos parado
        Assert.Equal(2.0, Elapsed(), 2);    // la cuenta, quieta

        Controller.OnPauseToggle();
        Assert.Equal(2.0, Elapsed(), 2);    // reanudar no da un salto
        Clock.Advance(1_000);
        Assert.Equal(3.0, Elapsed(), 2);    // y sigue desde donde estaba
    }

    /// <summary>Un cue cuyo pre_wait vence DURANTE la pausa global no puede
    /// quedarse con la marca en el futuro. Antes se le corregía TStart hacia
    /// adelante como a todos y salía con el elapsed en negativo.</summary>
    [Fact]
    public void CueQueArrancaDuranteLaPausa_NoDaElapsedNegativo()
    {
        AddCue(CueType.Audio, duration: 30.0);                       // ancla
        AddCue(CueType.Audio, duration: 30.0, setup: c => c.PreWait = 3.0);

        Controller.OnGoPressed();           // el ancla, para poder pausar
        Controller.OnGoPressed();           // el del pre_wait largo
        Clock.Advance(1_000);

        Controller.OnPauseToggle();
        Clock.Advance(5_000);               // el pre_wait vence estando en pausa
        Controller.OnPauseToggle();

        foreach (var fila in Controller.SnapshotActiveCues(out _))
        {
            Assert.True(fila.Elapsed >= 0.0,
                $"elapsed negativo en {fila.Number}: {fila.Elapsed}");
            Assert.True(fila.Remaining is null or <= 30.0);
        }
    }

    /// <summary>El antirrebote del GO cuenta en tiempo de PARED: es un gesto
    /// de mano. Con el show en pausa el reloj de show se congela, y si el
    /// antirrebote leyera de ahí, un GO tras la pausa quedaría bloqueado.</summary>
    [Fact]
    public void ElAntirreboteDelGo_NoSeCongelaConLaPausa()
    {
        Settings.Set("go_min_interval_ms", 200);
        AddCue(CueType.Audio, duration: 30.0);
        AddCue(CueType.Audio, duration: 30.0);

        Controller.OnGoPressed();
        Controller.OnPauseToggle();
        Clock.Advance(5_000);               // cinco segundos de pausa
        Controller.OnPauseToggle();

        Controller.OnGoPressed();           // debe pasar: han pasado 5 s de pared
        Assert.Equal(2, Audio.Plays.Count);
    }

    private double Elapsed() =>
        Controller.SnapshotActiveCues(out _)[0].Elapsed;
}
