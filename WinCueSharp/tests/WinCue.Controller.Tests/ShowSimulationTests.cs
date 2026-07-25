// =============================================================================
// ShowSimulationTests.cs — Shows ENTEROS, de principio a fin, como los opera
// una persona
// =============================================================================
// Los demás tests miran una pieza cada uno: que un crossfade solape, que una
// cadena encadene. Estos miran lo otro — que el conjunto AGUANTE una función
// entera y termine limpio, incluidas las maniobras que en un test corto no
// aparecen nunca: parar en mitad de una cadena y seguir, pausar, entrar en
// pánico y volver, y hacerlo muchas veces seguidas.
//
// LO QUE SE COMPRUEBA SIEMPRE, PASE LO QUE PASE (ver AssertInvariants):
//   · El playhead nunca se sale de la lista.
//   · Nunca hay un cue "sonando" que ya no existe en el modelo.
//   · La vista no ha recibido ningún error.
// Se comprueban DESPUÉS DE CADA ACCIÓN, no solo al final: así el fallo apunta
// a la maniobra que lo rompió y no al montón entero.
//
// FUGAS: un show que termina tiene que dejar el temporizador VACÍO. Un timer
// vivo tras el final es un cue que va a dispararse solo en mitad de la
// siguiente función — el tipo de fallo que en un teatro no se puede depurar.
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class ShowSimulationTests : ControllerTestBase
{
    // ------------------------------------------------------------------
    // Utilidades de simulación
    // ------------------------------------------------------------------

    /// <summary>Invariantes que NINGUNA maniobra puede romper.</summary>
    private void AssertInvariants(string where)
    {
        Assert.True(Controller.Playhead >= 0 && Controller.Playhead <= Model.Count,
            $"{where}: playhead fuera de la lista ({Controller.Playhead}/{Model.Count})");
        foreach (int id in Controller.RunningCueIds)
        {
            // Los ids negativos son internos (audición, espejo de audio de los
            // cues de vídeo): no son filas del modelo.
            if (id < 0)
                continue;
            Assert.True(Model.IndexOfId(id) >= 0,
                $"{where}: suena el cue {id}, que no está en el modelo");
        }
        Assert.Empty(View.Errors);
    }

    /// <summary>Un show sencillo pero COMPLETO: audio, vídeo, una cadena
    /// temporizada y un cue de control. Suficiente para que una función entera
    /// tenga de todo sin depender del JSON de ejemplo.</summary>
    private List<Cue> BuildShow()
    {
        var cues = new List<Cue>
        {
            AddCue(CueType.Audio, "apertura", duration: 5.0),
            AddCue(CueType.Video, "fondo", duration: 10.0),
            AddCue(CueType.Audio, "encadenado", duration: 4.0, setup: c =>
            {
                c.ContinueMode = ContinueMode.AutoContinue;
                c.PostWait = 2.0;
            }),
            AddCue(CueType.Audio, "el que sigue a la cadena", duration: 3.0),
            AddCue(CueType.Video, "cierre", duration: 6.0),
        };
        return cues;
    }

    /// <summary>Reproduce el show entero a base de GO, dejando correr el reloj
    /// entre disparos. Devuelve cuántos GO se dieron.</summary>
    private int RunWholeShow(int msBetweenGos = 12_000)
    {
        int gos = 0;
        // Tope de seguridad: si el playhead dejara de avanzar, esto corta en vez
        // de colgar la suite entera.
        for (int guard = 0; guard < 200 && Controller.Playhead < Model.Count; guard++)
        {
            Controller.OnGoPressed();
            gos++;
            Clock.Advance(msBetweenGos);
            AssertInvariants($"GO #{gos}");
        }
        return gos;
    }

    // ------------------------------------------------------------------
    // La función entera
    // ------------------------------------------------------------------

    [Fact]
    public void ShowEntero_TerminaLimpio_SinCuesVivosNiTimersPendientes()
    {
        BuildShow();

        int gos = RunWholeShow();

        // OJO: hacen falta MENOS GO que cues, porque las cadenas AUTO_CONTINUE
        // avanzan el playhead solas (y los grupos lo saltan entero). Lo que
        // dice que el show terminó es el playhead al final de la lista, no el
        // número de pulsaciones.
        Assert.True(gos > 0);
        Assert.Equal(Model.Count, Controller.Playhead);
        // ...y que de verdad haya sonado el show, no que se haya paseado el
        // playhead por una lista muda.
        Assert.True(Audio.Plays.Count + Video.Plays.Count >= Model.Count,
            $"solo se dispararon {Audio.Plays.Count + Video.Plays.Count} medias "
            + $"para {Model.Count} cues");
        Assert.Empty(Controller.RunningCueIds);
        // UN TIMER VIVO AL FINAL = un cue que se dispara solo en la próxima
        // función. Es la fuga que más caro sale y la que menos se ve.
        Assert.Equal(0, Clock.PendingCount);
    }

    [Fact]
    public void ShowEntero_RepetidoDiezVeces_NoAcumulaNada()
    {
        BuildShow();

        for (int vuelta = 1; vuelta <= 10; vuelta++)
        {
            Controller.OnSetPlayhead(0);
            RunWholeShow();
            Controller.OnStopPressed();

            // SE CUENTA AQUÍ, ANTES DE AVANZAR EL RELOJ, y es la diferencia
            // entre un chequeo de fugas y un adorno: si se deja correr el
            // tiempo primero, un timer fugado se dispara, sale de la lista y el
            // contador vuelve a cero tan campante. Un STOP tiene que dejar el
            // temporizador vacío EN EL ACTO.
            Assert.Empty(Controller.RunningCueIds);
            Assert.True(Clock.PendingCount == 0,
                $"vuelta {vuelta}: el STOP dejó {Clock.PendingCount} timers vivos");

            Clock.Advance(30_000);   // y en el silencio de después no salta nada
            Assert.Empty(Controller.RunningCueIds);
            AssertInvariants($"vuelta {vuelta}");
        }
    }

    [Fact]
    public void StopConUnaCadenaAUnPasoDeDispararse_LaCancelaEnElACTO()
    {
        var cues = BuildShow();

        Controller.OnSetPlayhead(2);       // AUTO_CONTINUE con post_wait de 2 s
        Controller.OnGoPressed();
        Clock.Advance(500);                // el encadenado AÚN no ha saltado
        Assert.True(Clock.PendingCount > 0, "debería haber una cadena esperando");

        Controller.OnStopPressed();

        // Sin avanzar el reloj: el STOP tiene que dejar el temporizador vacío
        // YA. Si el timer sobrevive, el cue encadenado se dispara solo con el
        // telón bajado — y desde fuera parece que la mesa tiene vida propia.
        Assert.Equal(0, Clock.PendingCount);

        Clock.Advance(30_000);
        Assert.Empty(Controller.RunningCueIds);
        Assert.DoesNotContain(Audio.Plays, p => p.CueId == cues[3].Id);
    }

    // ------------------------------------------------------------------
    // Maniobras de directo: parar y seguir
    // ------------------------------------------------------------------

    [Fact]
    public void StopEnMitadDelShow_ElGoSiguienteRepiteElMismoCue_YLaFuncionContinua()
    {
        var cues = BuildShow();

        Controller.OnGoPressed();          // cue 0 al aire
        Controller.OnGoPressed();          // cue 1 al aire
        Clock.Advance(1_000);
        Assert.Equal(2, Controller.Playhead);

        Controller.OnStopPressed();
        AssertInvariants("tras STOP ALL");

        // El STOP devuelve el show al cue que estaba al aire, no al siguiente
        Assert.Equal(1, Controller.Playhead);
        Assert.Empty(Controller.RunningCueIds);

        // Y desde ahí la función SIGUE hasta el final sin saltarse nada
        Controller.OnGoPressed();
        Assert.Contains(Video.Plays, p => p.CueId == cues[1].Id);
        Clock.Advance(12_000);
        RunWholeShow();
        Assert.Equal(Model.Count, Controller.Playhead);
        Assert.Equal(0, Clock.PendingCount);
    }

    [Fact]
    public void PanicEnMitadDelShow_EsUnSalvavidas_NoUnCallejonSinSalida()
    {
        BuildShow();

        Controller.OnGoPressed();
        Controller.OnGoPressed();
        Clock.Advance(500);
        int alAire = Controller.Playhead - 1;

        Controller.OnPanic();
        Clock.Advance(2_000);              // que termine el fundido de pánico

        Assert.Empty(Controller.RunningCueIds);
        // Vuelve al punto del show en vez de quedarse encallado un cue por
        // delante: el pánico se DESHACE dando al GO.
        Assert.Equal(alAire, Controller.Playhead);
        AssertInvariants("tras PANIC");

        Controller.OnGoPressed();
        Assert.NotEmpty(Controller.RunningCueIds);
    }

    [Fact]
    public void FadeAllYLuegoGo_ReanudaEnElMismoPunto()
    {
        BuildShow();

        Controller.OnGoPressed();
        Clock.Advance(500);

        Controller.OnFadeAll();
        Clock.Advance(6_000);              // más que el fundido por defecto

        Assert.Equal(0, Controller.Playhead);
        Controller.OnGoPressed();
        Assert.NotEmpty(Controller.RunningCueIds);
        AssertInvariants("tras FADE ALL + GO");
    }

    [Fact]
    public void SiElOperadorSeMuevePorLaLista_ElStopRespetaSuPosicion()
    {
        BuildShow();

        Controller.OnGoPressed();          // cue 0 al aire, playhead en 1
        Clock.Advance(500);
        Controller.OnSetPlayhead(4);       // se adelanta a preparar el final

        Controller.OnStopPressed();

        // Esa es SU posición: el STOP no la secuestra para volver al cue 0
        Assert.Equal(4, Controller.Playhead);
        AssertInvariants("stop con playhead movido a mano");
    }

    // ------------------------------------------------------------------
    // Pausa a mitad de función
    // ------------------------------------------------------------------

    [Fact]
    public void PausarYReanudar_NoPierdeLaCadenaNiDuplicaDisparos()
    {
        var cues = BuildShow();

        Controller.OnSetPlayhead(2);       // el cue con AUTO_CONTINUE
        Controller.OnGoPressed();
        Clock.Advance(1_000);
        Assert.True(Audio.IsPlaying(cues[2].Id));

        Controller.OnPauseToggle();
        Assert.True(Controller.IsPaused);
        Clock.Advance(500);
        Controller.OnPauseToggle();
        Assert.False(Controller.IsPaused);

        // La cadena sigue viva y dispara UNA sola vez
        Clock.Advance(20_000);
        int disparosDelSiguiente = Audio.Plays.Count(p => p.CueId == cues[3].Id);
        Assert.Equal(1, disparosDelSiguiente);
        AssertInvariants("tras pausa/reanudación");
    }

    // ------------------------------------------------------------------
    // El show de ejemplo REAL, de cabo a rabo
    // ------------------------------------------------------------------

    [Fact]
    public void ShowDeEjemplo_SeOperaEnteroSinRomperNada()
    {
        string src = ShowPruebaE2ETests.FindShowJson();
        string dst = Path.Combine(TempDir, "show-prueba.json");
        File.Copy(src, dst);
        foreach (var cue in CueModel.FromJson(File.ReadAllText(src)))
            if (cue.FilePath.Length > 0)
            {
                string p = Path.Combine(TempDir, cue.FilePath);
                if (!File.Exists(p))
                    File.WriteAllBytes(p, new byte[] { 0 });
            }
        Controller.LoadShow(dst);
        AssertInvariants("recién cargado");

        int gos = RunWholeShow(msBetweenGos: 15_000);

        // Menos GO que cues: el show de ejemplo lleva grupos (un GO salta el
        // grupo entero) y cadenas. Lo que cuenta es llegar al final habiendo
        // disparado de verdad un montón de medias.
        Assert.True(gos > 0);
        Assert.Equal(Model.Count, Controller.Playhead);
        Assert.True(Audio.Plays.Count + Video.Plays.Count >= 15,
            $"el show de ejemplo apenas sonó: {Audio.Plays.Count + Video.Plays.Count} medias");

        // Al terminar, un STOP de seguridad como haría cualquiera al bajar el
        // telón. Se cuenta ANTES de avanzar el reloj: ver la nota de
        // ShowEntero_RepetidoDiezVeces.
        Controller.OnStopPressed();
        Assert.Empty(Controller.RunningCueIds);
        Assert.Equal(0, Clock.PendingCount);

        Clock.Advance(60_000);   // con el telón bajado no puede sonar nada más
        Assert.Empty(Controller.RunningCueIds);
    }

    // ------------------------------------------------------------------
    // Mono con la mesa: maniobras al azar, invariantes fijos
    // ------------------------------------------------------------------

    /// <summary>Aporrea el transporte con una secuencia PSEUDOALEATORIA pero
    /// REPRODUCIBLE (semilla fija: un fallo se repite igual al depurarlo) y
    /// comprueba los invariantes después de cada acción.
    ///
    /// No busca un resultado concreto — busca que ninguna combinación de
    /// maniobras, por absurda que sea, deje el controlador en un estado
    /// imposible. Es lo que hace un operador nervioso en un ensayo.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    public void MonoConLaMesa_NingunaSecuenciaDejaElControladorEnMalEstado(int semilla)
    {
        BuildShow();
        var rnd = new Random(semilla);

        for (int paso = 0; paso < 300; paso++)
        {
            switch (rnd.Next(9))
            {
                case 0: Controller.OnGoPressed(); break;
                case 1: Controller.OnStopPressed(); break;
                case 2: Controller.OnPauseToggle(); break;
                case 3: Controller.OnFadeAll(); break;
                case 4: Controller.OnPanic(); break;
                case 5: Controller.OnPrevPressed(); break;
                case 6: Controller.OnNextPressed(); break;
                case 7: Controller.OnSetPlayhead(rnd.Next(Model.Count)); break;
                case 8: Clock.Advance(rnd.Next(1, 8_000)); break;
            }
            AssertInvariants($"semilla {semilla}, paso {paso}");
        }

        // Y después del vendaval, la mesa tiene que quedar utilizable:
        // parar de verdad para todo y el show se puede volver a empezar.
        Controller.OnStopPressed();
        Clock.Advance(10_000);
        Assert.Empty(Controller.RunningCueIds);

        Controller.OnSetPlayhead(0);
        Controller.OnGoPressed();
        Assert.NotEmpty(Controller.RunningCueIds);
        AssertInvariants("show reiniciado tras el vendaval");
    }

    // ------------------------------------------------------------------
    // Volumen: la mesa y el inspector tienen que contar lo mismo
    // ------------------------------------------------------------------

    [Fact]
    public void VolumenDeUnCue_SobreviveAlGuardarYRecargar_IncluidoElRefuerzo()
    {
        var cues = BuildShow();
        int row = Model.IndexOfId(cues[0].Id);

        // Un refuerzo por encima de la unidad, que antes se recortaba a 1,0
        double reforzado = AudioLevels.FromDb(6.0);
        Controller.OnCueEditCommitted(row,
            new Dictionary<string, object?> { ["volume"] = reforzado });

        string path = Path.Combine(TempDir, "con-refuerzo.json");
        Controller.SaveShow(path);
        var releidos = CueModel.FromJson(File.ReadAllText(path));

        double guardado = releidos[row].Volume;
        Assert.Equal(6.0, AudioLevels.ToDb(guardado), 3);
        // Y la posición del fader para ese volumen es la misma que pintará la
        // escala: si no, la mesa enseñaría un valor y el inspector otro.
        Assert.Equal(AudioLevels.GainToPosition(reforzado),
                     AudioLevels.GainToPosition(guardado), 6);
    }

    [Fact]
    public void VolumenAlAire_LlegaAlMotorConElValorReforzado()
    {
        var cues = BuildShow();
        Controller.OnGoPressed();          // el cue 0 suena
        Clock.Advance(200);

        double reforzado = AudioLevels.FromDb(3.0);
        Controller.OnCueEditCommitted(Model.IndexOfId(cues[0].Id),
            new Dictionary<string, object?> { ["volume"] = reforzado });

        Assert.True(Audio.Volumes.TryGetValue(cues[0].Id, out double aplicado),
            "el volumen en vivo no llegó al motor");
        Assert.Equal(reforzado, aplicado, 4);
        Assert.True(aplicado > 1.0, "el refuerzo se recortó por el camino");
    }
}
