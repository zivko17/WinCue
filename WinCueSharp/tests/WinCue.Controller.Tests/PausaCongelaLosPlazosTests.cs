// =============================================================================
// PausaCongelaLosPlazosTests.cs — La pausa global para TAMBIÉN los relojes
// =============================================================================
//
// EL CASO REAL: la pausa global paraba los motores y congelaba el reloj del
// show, pero NO los plazos ya programados — los timers seguían corriendo con
// el sonido detenido. Consecuencia en directo: un cue con duration fija al que
// le pillaba una pausa larga se cortaba antes de tiempo, porque su timer de
// fin llegaba a cero mientras el motor estaba parado.
//
// Ahora el planificador escucha el Freeze/Resume del reloj: al congelar guarda
// lo que le falta a cada plazo vivo y lo desarma; al reanudar lo vuelve a armar
// con ese resto. Aquí se comprueba con el reloj VIRTUAL (determinista) y, en
// varios casos, con el PC cargado simulado (VirtualScheduler.RetrasoDeDisparo).
//
// LA FRONTERA NO SE MUEVE: el fin de un media se sigue anclando a su arranque
// REAL y corrigiendo contra la posición del motor. Lo único que cambia es que
// ese plazo deja de contar mientras el show está en pausa — que es justo lo
// coherente, porque el motor tampoco avanza.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class PausaCongelaLosPlazosTests : ControllerTestBase
{
    // ------------------------------------------------------------------
    // El caso que dolía: la duración fija
    // ------------------------------------------------------------------

    /// <summary>EL REGRESO: 10 s de duración, pausa de un minuto a los 3 s. El
    /// cue tiene que seguir vivo al reanudar y durar sus 7 s restantes. Antes
    /// el timer de fin se comía la pausa entera y el cue salía cortado en
    /// cuanto se reanudaba.</summary>
    [Fact]
    public void DuracionFija_ConUnaPausaLarga_NoSeCortaAntesDeTiempo()
    {
        AddCue(CueType.Audio, duration: 10.0);

        Controller.OnGoPressed();
        Clock.Advance(3_000);

        Controller.OnPauseToggle();
        Clock.Advance(60_000);              // un minuto entero parado
        Assert.Empty(Audio.Stops);          // el fin NO ha vencido en la pausa
        Assert.Single(Controller.RunningCueIds);

        Controller.OnPauseToggle();
        Clock.Advance(6_900);               // le quedaban 7 s
        Assert.Empty(Audio.Stops);
        Clock.Advance(200);
        Assert.Single(Audio.Stops);
    }

    /// <summary>Y con el PC cargado (cada timer despierta 40 ms tarde) sigue
    /// igual: el retraso del disparo se conserva a través de la pausa, no se
    /// suma otra vez ni se pierde el resto.</summary>
    [Fact]
    public void DuracionFija_ConElPcCargado_TambienSeCongela()
    {
        Clock.RetrasoDeDisparo = 40;
        AddCue(CueType.Audio, duration: 5.0);

        Controller.OnGoPressed();
        Clock.Advance(1_000);

        Controller.OnPauseToggle();
        Clock.Advance(20_000);
        Controller.OnPauseToggle();

        Clock.Advance(3_990);               // 4 s de resto + los 40 ms de tara
        Assert.Empty(Audio.Stops);
        Clock.Advance(100);
        Assert.Single(Audio.Stops);
    }

    /// <summary>Tres pausas seguidas: cada una descuenta lo suyo y ninguna se
    /// come el resto. El cue acaba a los 10 s DE SHOW, no de pared.</summary>
    [Fact]
    public void VariasPausas_NoSeComenElResto()
    {
        AddCue(CueType.Audio, duration: 10.0);

        Controller.OnGoPressed();
        Clock.Advance(2_000);

        for (int i = 0; i < 3; i++)
        {
            Controller.OnPauseToggle();
            Clock.Advance(5_000);           // 15 s de pausa en total
            Controller.OnPauseToggle();
            Clock.Advance(2_000);           // 8 s de show en total
        }

        Assert.Empty(Audio.Stops);
        Clock.Advance(1_900);               // 9,9 s de show
        Assert.Empty(Audio.Stops);
        Clock.Advance(200);
        Assert.Single(Audio.Stops);
    }

    /// <summary>Un plazo al que le pilla la pausa a un milisegundo de vencer no
    /// se pierde ni dispara dos veces: se suspende y sale al reanudar. Es el
    /// borde donde el timer y la pausa se cruzan.</summary>
    [Fact]
    public void UnPlazoAlBordeDeVencer_NiSePierdeNiDisparaDosVeces()
    {
        AddCue(CueType.Audio, duration: 5.0);

        Controller.OnGoPressed();
        Clock.Advance(4_999);
        Controller.OnPauseToggle();
        Clock.Advance(50_000);
        Assert.Empty(Audio.Stops);

        Controller.OnPauseToggle();
        Clock.Advance(1);
        Assert.Single(Audio.Stops);         // una vez, y solo una

        Clock.Advance(10_000);
        Assert.Single(Audio.Stops);
    }

    // ------------------------------------------------------------------
    // Lo mismo para la SECUENCIA
    // ------------------------------------------------------------------

    /// <summary>El pre_wait tampoco se gasta en pausa: si se gastara, el cue
    /// saldría al aire con los motores parados.</summary>
    [Fact]
    public void ElPreWait_NoSeGastaDuranteLaPausa()
    {
        AddCue(CueType.Audio, duration: 30.0);                       // ancla
        AddCue(CueType.Audio, duration: 30.0, setup: c => c.PreWait = 5.0);

        Controller.OnGoPressed();           // el ancla, para poder pausar
        Controller.OnGoPressed();           // el del pre_wait
        Clock.Advance(1_000);

        Controller.OnPauseToggle();
        Clock.Advance(30_000);
        Assert.Single(Audio.Plays);         // el segundo sigue esperando

        Controller.OnPauseToggle();
        Clock.Advance(3_900);               // le quedaban 4 s de pre_wait
        Assert.Single(Audio.Plays);
        Clock.Advance(200);
        Assert.Equal(2, Audio.Plays.Count);
    }

    /// <summary>Una cadena AUTO_CONTINUE no avanza en pausa: el post_wait del
    /// eslabón vivo se congela con todo lo demás.</summary>
    [Fact]
    public void LaCadenaAutoContinue_NoAvanzaDuranteLaPausa()
    {
        for (int i = 0; i < 3; i++)
            AddCue(CueType.Audio, setup: c =>
            {
                c.ContinueMode = ContinueMode.AutoContinue;
                c.PostWait = 2.0;
            });

        Controller.OnGoPressed();
        Clock.Advance(500);
        Assert.Single(Audio.Plays);

        Controller.OnPauseToggle();
        Clock.Advance(20_000);
        Assert.Single(Audio.Plays);         // la cadena, quieta

        Controller.OnPauseToggle();
        Clock.Advance(1_400);               // al post_wait le quedaba 1,5 s
        Assert.Single(Audio.Plays);
        Clock.Advance(200);
        Assert.Equal(2, Audio.Plays.Count);
    }

    /// <summary>Un cue WAIT es secuencia pura (plazo ABSOLUTO): también se
    /// congela, y su AUTO_FOLLOW sale cuando toca.</summary>
    [Fact]
    public void UnCueWait_NoCorreDuranteLaPausa()
    {
        AddCue(CueType.Wait, duration: 4.0,
            setup: c => c.ContinueMode = ContinueMode.AutoFollow);
        AddCue(CueType.Audio);

        Controller.OnGoPressed();
        Clock.Advance(1_000);

        Controller.OnPauseToggle();
        Clock.Advance(30_000);
        Assert.Empty(Audio.Plays);

        Controller.OnPauseToggle();
        Clock.Advance(2_900);               // a la espera le quedaban 3 s
        Assert.Empty(Audio.Plays);
        Clock.Advance(200);
        Assert.Single(Audio.Plays);
    }

    // ------------------------------------------------------------------
    // Bordes de la pausa
    // ------------------------------------------------------------------

    /// <summary>Un cue disparado DURANTE la pausa nace con sus plazos ya
    /// congelados: sería absurdo que su pre_wait corriera con los motores
    /// parados.</summary>
    [Fact]
    public void UnCueDisparadoEnPausa_NaceConSusPlazosCongelados()
    {
        AddCue(CueType.Audio, duration: 30.0);                       // ancla
        AddCue(CueType.Audio, duration: 30.0, setup: c => c.PreWait = 2.0);

        Controller.OnGoPressed();           // el ancla
        Clock.Advance(500);
        Controller.OnPauseToggle();         // pausa ANTES de disparar el segundo

        Controller.OnGoPressed();           // GO con el show en pausa
        Clock.Advance(30_000);
        Assert.Single(Audio.Plays);         // su pre_wait no ha corrido

        Controller.OnPauseToggle();
        Clock.Advance(1_900);
        Assert.Single(Audio.Plays);
        Clock.Advance(200);
        Assert.Equal(2, Audio.Plays.Count);
    }

    /// <summary>STOP ALL estando en pausa descongela el reloj — y con él tienen
    /// que volver a correr los plazos. Si se quedaran suspendidos, el resto de
    /// la función no terminaría ni un cue.</summary>
    [Fact]
    public void StopAll_EnPausa_DevuelveLosPlazosALaVida()
    {
        AddCue(CueType.Audio, duration: 10.0);

        Controller.OnGoPressed();
        Clock.Advance(1_000);
        Controller.OnPauseToggle();
        Controller.OnStopPressed();         // para todo y deshace la pausa

        Controller.OnGoPressed();           // el show sigue: nuevo disparo
        Clock.Advance(9_900);
        Assert.Single(Controller.RunningCueIds);
        Clock.Advance(200);
        Assert.Empty(Controller.RunningCueIds);
    }

    /// <summary>La pausa de UN cue y la global se combinan sin descontar dos
    /// veces: al reanudar el cue se reprograma lo que le queda según el motor,
    /// y si además el show está en pausa ese resto tampoco corre.</summary>
    [Fact]
    public void PausaDeUnCue_DentroDeLaPausaGlobal_NoDescuentaDosVeces()
    {
        var cue = AddCue(CueType.Audio, duration: 10.0);

        Controller.OnGoPressed();
        Clock.Advance(4_000);
        Audio.Elapsed[cue.Id] = 4.0;        // el motor ha reproducido 4 s

        Controller.OnPauseCueToggle(0);     // pausa individual: tira su timer
        Controller.OnPauseToggle();         // y encima, pausa global
        Clock.Advance(30_000);

        Controller.OnPauseCueToggle(0);     // reanudar el cue: quedan 6 s
        Clock.Advance(30_000);
        Assert.Empty(Audio.Stops);          // pero el show sigue en pausa

        Controller.OnPauseToggle();
        Clock.Advance(5_900);
        Assert.Empty(Audio.Stops);
        Clock.Advance(200);
        Assert.Single(Audio.Stops);
    }

    // ------------------------------------------------------------------
    // La excepción: los despachos
    // ------------------------------------------------------------------

    /// <summary>Un plazo de 0 ms no es un plazo, es un despacho al hilo del
    /// controlador (HapConvertService.Post). La pausa congela plazos, no
    /// despachos: si no, un mensaje de servicio se quedaría colgado hasta que
    /// alguien reanudara el show.</summary>
    [Fact]
    public void UnDespachoDeCeroMs_NoLoCongelaLaPausa()
    {
        bool corrio = false;
        Clock.Clock.Freeze();

        using var handle = Clock.Schedule(0, () => corrio = true);
        Clock.Advance(1);

        Assert.True(corrio, "el despacho se quedó colgado en la pausa");
    }

    /// <summary>Y el contraste: un plazo de verdad programado en pausa espera
    /// a la reanudación.</summary>
    [Fact]
    public void UnPlazoDeVerdad_ProgramadoEnPausa_EsperaALaReanudacion()
    {
        bool corrio = false;
        Clock.Clock.Freeze();

        using var handle = Clock.Schedule(10, () => corrio = true);
        Clock.Advance(10_000);
        Assert.False(corrio);

        Clock.Clock.Resume();
        Clock.Advance(11);
        Assert.True(corrio);
    }
}
