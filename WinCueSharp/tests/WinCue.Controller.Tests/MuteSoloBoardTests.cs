// =============================================================================
// MuteSoloBoardTests.cs — El silencio y el solo de la mesa
// =============================================================================
// Lo que se comprueba aquí no es «el botón se pone rojo», es la parte que de
// verdad se puede hacer mal: que quitar un silencio devuelva EXACTAMENTE la
// ganancia de la que se venía, y que los dos silencios —el del operador y el
// que impone un solo ajeno— no se pisen.
// =============================================================================

namespace WinCue.Controller.Tests;

public class MuteSoloBoardTests
{
    private const int Master = -1;
    private const int Audition = -2;

    /// <summary>Mesa con MASTER, AUDICIÓN y los cues que se le pidan.</summary>
    private static (MuteSoloBoard Board, List<(int Key, int Gain)> Emitted) NewBoard(
        params int[] cueKeys)
    {
        var board = new MuteSoloBoard();
        var emitted = new List<(int, int)>();
        board.GainChanged = (k, g) => emitted.Add((k, g));
        board.Add(Master, 100, canSolo: false);
        board.Add(Audition, 100, canSolo: false);
        foreach (int k in cueKeys)
            board.Add(k);
        emitted.Clear();   // el alta no cuenta como maniobra
        return (board, emitted);
    }

    // ------------------------------------------------------------------
    // Silencio a mano
    // ------------------------------------------------------------------

    [Fact]
    public void Silenciar_PoneCeroYDevuelveLaGananciaExacta()
    {
        var (board, emitted) = NewBoard(1);
        board.FaderMoved(1, 73);          // una ganancia cualquiera, no la unidad

        board.ToggleMute(1);
        Assert.True(board.IsMuted(1));
        Assert.Equal(0, board.GainOf(1));
        Assert.Equal((1, 0), emitted[^1]);

        board.ToggleMute(1);
        Assert.False(board.IsMuted(1));
        // DONDE ESTABA: ni la unidad ni un valor por defecto
        Assert.Equal(73, board.GainOf(1));
        Assert.Equal((1, 73), emitted[^1]);
    }

    [Fact]
    public void SilenciarDosVeces_NoPierdeLaGananciaGuardada()
    {
        var (board, _) = NewBoard(1);
        board.FaderMoved(1, 55);

        board.ToggleMute(1);
        board.ToggleMute(1);
        board.ToggleMute(1);
        board.ToggleMute(1);

        Assert.Equal(55, board.GainOf(1));
    }

    [Fact]
    public void MoverElFaderEstandoMudo_LevantaElSilencio()
    {
        var (board, _) = NewBoard(1);
        board.FaderMoved(1, 80);
        board.ToggleMute(1);
        Assert.True(board.IsMuted(1));

        // El operador sube el fader: mandar tiene que mandar, o estaría
        // subiendo un fader mudo sin entender por qué no suena.
        board.FaderMoved(1, 90);

        Assert.False(board.IsMuted(1));
        Assert.Equal(90, board.GainOf(1));
        // Y ese es ahora el valor al que se vuelve
        board.ToggleMute(1);
        board.ToggleMute(1);
        Assert.Equal(90, board.GainOf(1));
    }

    // ------------------------------------------------------------------
    // Solo
    // ------------------------------------------------------------------

    [Fact]
    public void Solo_EnmudeceALosDemasCues_YDevuelveAlSoltarlo()
    {
        var (board, _) = NewBoard(1, 2, 3);
        board.FaderMoved(1, 100);
        board.FaderMoved(2, 60);
        board.FaderMoved(3, 40);

        board.SetSolo(2, true);
        Assert.True(board.IsMuted(1));
        Assert.False(board.IsMuted(2));
        Assert.True(board.IsMuted(3));

        board.SetSolo(2, false);
        Assert.False(board.IsMuted(1));
        Assert.False(board.IsMuted(3));
        // Cada uno a SU ganancia, no todos a la misma
        Assert.Equal(100, board.GainOf(1));
        Assert.Equal(60, board.GainOf(2));
        Assert.Equal(40, board.GainOf(3));
    }

    [Fact]
    public void VariosSolos_ConvivenYElSilencioSeLevantaConElUltimo()
    {
        var (board, _) = NewBoard(1, 2, 3);

        board.SetSolo(1, true);
        board.SetSolo(2, true);
        Assert.False(board.IsMuted(1));
        Assert.False(board.IsMuted(2));
        Assert.True(board.IsMuted(3));

        board.SetSolo(1, false);       // queda el 2 en solo
        Assert.True(board.IsMuted(3), "con un solo vivo, el resto sigue mudo");
        Assert.True(board.IsMuted(1), "el que soltó el solo pasa a ser 'los demás'");

        board.SetSolo(2, false);       // ya no queda ninguno
        Assert.False(board.IsMuted(1));
        Assert.False(board.IsMuted(3));
    }

    [Fact]
    public void ElSoloNoDevuelveALoSilenciadoAMano()
    {
        var (board, _) = NewBoard(1, 2);
        board.FaderMoved(1, 88);
        board.ToggleMute(1);           // el operador lo calla A PROPÓSITO

        board.SetSolo(2, true);
        board.SetSolo(2, false);       // y suelta el solo

        // Son dos silencios distintos: soltar el solo no puede deshacer una
        // decisión del operador.
        Assert.True(board.IsMuted(1), "el silencio del operador se respetó");
        Assert.True(board.IsMutedByHand(1));
        Assert.Equal(88, board.LevelOf(1));
    }

    [Fact]
    public void PulsarMEnUnStripEnmudecidoPorElSoloAjeno_NoLoVuelveAudible()
    {
        var (board, _) = NewBoard(1, 2);
        board.SetSolo(1, true);
        Assert.True(board.IsMuted(2));
        Assert.True(board.IsMutedBySolo(2));

        // Con un único flag de silencio, esto lo DESTAPABA: la M lo «quitaba»
        // y el cue se colaba por encima del solo de otro.
        board.ToggleMute(2);

        Assert.True(board.IsMuted(2), "sigue callado: el solo de otro manda");
        Assert.True(board.IsMutedByHand(2), "y además ahora tiene el suyo");
        Assert.Equal(0, board.GainOf(2));

        // Al soltar el solo NO vuelve: le queda su propio silencio puesto
        board.SetSolo(1, false);
        Assert.True(board.IsMuted(2));
        board.ToggleMute(2);
        Assert.False(board.IsMuted(2));
    }

    [Fact]
    public void MoverElFaderConUnSoloAjenoPuesto_NoRompeElSolo()
    {
        var (board, _) = NewBoard(1, 2);
        board.SetSolo(1, true);

        // Devuelve lo que debe SONAR (0), no lo que marca el control: si quien
        // llama mandara al motor el valor del fader, el solo se desharía solo.
        int suena = board.FaderMoved(2, 90);

        Assert.Equal(0, suena);
        Assert.True(board.IsMuted(2));
        Assert.Equal(90, board.LevelOf(2));

        // Y al soltar el solo aparece con la ganancia que se dejó puesta
        board.SetSolo(1, false);
        Assert.Equal(90, board.GainOf(2));
    }

    [Fact]
    public void UnCueQueEmpiezaASonarConUnSoloPuesto_NaceMudo()
    {
        var (board, _) = NewBoard(1);
        board.SetSolo(1, true);

        board.Add(2, 100);             // entra un cue nuevo con el solo puesto

        Assert.True(board.IsMuted(2),
            "si naciera sonando, el solo duraría hasta el siguiente GO");

        board.SetSolo(1, false);
        Assert.False(board.IsMuted(2));
    }

    [Fact]
    public void SiTerminaElUnicoCueEnSolo_LosDemasVuelvenSolos()
    {
        var (board, _) = NewBoard(1, 2);
        board.SetSolo(1, true);
        Assert.True(board.IsMuted(2));

        board.Remove(1);               // su cue termina y el strip desaparece

        // Si el solo se fuera con el strip sin recalcular, la mesa se quedaría
        // muda sin que nadie hubiera tocado nada.
        Assert.False(board.IsMuted(2));
        Assert.False(board.AnySolo);
    }

    // ------------------------------------------------------------------
    // MASTER y AUDICIÓN
    // ------------------------------------------------------------------

    [Fact]
    public void MasterYAudicion_SeSilencianPeroNoSeSolean()
    {
        var (board, _) = NewBoard(1);

        board.ToggleMute(Master);
        Assert.True(board.IsMuted(Master));
        board.ToggleMute(Master);
        Assert.False(board.IsMuted(Master));

        board.SetSolo(Master, true);
        Assert.False(board.IsSoloed(Master), "el MASTER no tiene contra qué solear");
        Assert.False(board.AnySolo);
    }

    [Fact]
    public void ElSoloDeUnCue_NoTocaAlMasterNiALaAudicion()
    {
        var (board, _) = NewBoard(1, 2);

        board.SetSolo(1, true);

        // Enmudecer el MASTER dejaría la sala muda: el solo es entre cues.
        Assert.False(board.IsMuted(Master));
        Assert.False(board.IsMuted(Audition));
        Assert.True(board.IsMuted(2));
    }

    [Fact]
    public void CargarElVolumenGuardado_NoLevantaUnSilencioPuesto()
    {
        var (board, _) = NewBoard();
        board.ToggleMute(Master);
        Assert.True(board.IsMuted(Master));

        // Refresco desde ajustes/modelo: no es una maniobra del operador
        board.SetGainSilently(Master, 85);

        Assert.True(board.IsMuted(Master), "un refresco no puede desilenciar");
        Assert.Equal(0, board.GainOf(Master));
        board.ToggleMute(Master);
        Assert.Equal(85, board.GainOf(Master));
    }

    // ------------------------------------------------------------------
    // Lo que sale hacia el motor
    // ------------------------------------------------------------------

    [Fact]
    public void MoverElFader_NoEmiteNada_LoOtroSi()
    {
        var (board, emitted) = NewBoard(1);

        // Mover el fader ya lo sabe quien lo movió: emitir aquí sería un eco.
        board.FaderMoved(1, 70);
        Assert.Empty(emitted);

        board.ToggleMute(1);
        Assert.Equal((1, 0), Assert.Single(emitted));
    }

    [Fact]
    public void ClaveDesconocida_NoRevienta()
    {
        var (board, emitted) = NewBoard(1);

        board.ToggleMute(999);
        board.SetSolo(999, true);
        board.FaderMoved(999, 50);
        board.SetGainSilently(999, 50);
        board.Remove(999);

        Assert.Empty(emitted);
        Assert.False(board.Knows(999));
    }

    // ------------------------------------------------------------------
    // Aporreo: los invariantes se sostengan hagas lo que hagas
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(99)]
    public void MonoConLosBotones_NuncaSeQuedaTodoMudoSinMotivo(int semilla)
    {
        var (board, _) = NewBoard(1, 2, 3, 4);
        var rnd = new Random(semilla);
        int[] cues = { 1, 2, 3, 4 };

        for (int paso = 0; paso < 500; paso++)
        {
            int k = cues[rnd.Next(cues.Length)];
            switch (rnd.Next(4))
            {
                case 0: board.ToggleMute(k); break;
                case 1: board.SetSolo(k, !board.IsSoloed(k)); break;
                case 2: board.FaderMoved(k, rnd.Next(0, 316)); break;
                case 3: board.ToggleMute(rnd.Next(2) == 0 ? Master : Audition); break;
            }

            foreach (int c in cues)
            {
                // INVARIANTE 1: un strip en solo NUNCA lo enmudece el solo —
                // sería un solo que se calla a sí mismo. Silenciarlo A MANO sí
                // se permite: es una decisión del operador, rara pero suya.
                if (board.IsSoloed(c) && board.IsMutedBySolo(c))
                    Assert.Fail($"semilla {semilla}, paso {paso}: "
                        + $"el cue {c} está en solo y el solo lo ha enmudecido");
                // INVARIANTE 2: sin ningún solo puesto no puede quedar nadie
                // enmudecido POR el solo.
                if (!board.AnySolo && board.IsMutedBySolo(c))
                    Assert.Fail($"semilla {semilla}, paso {paso}: "
                        + $"el cue {c} sigue mudo por un solo que ya no existe");
                // INVARIANTE 3: lo que suena es 0 o la ganancia del fader,
                // nunca un valor inventado.
                Assert.True(board.GainOf(c) == 0 || board.GainOf(c) == board.LevelOf(c),
                    $"semilla {semilla}, paso {paso}: el cue {c} suena a "
                    + $"{board.GainOf(c)} con el fader en {board.LevelOf(c)}");
            }
        }

        // Y se puede volver a un estado utilizable soltando todo. EN DOS
        // PASADAS, y no es un capricho del test: mientras quede UN solo vivo,
        // pulsar M en un strip que ese solo tiene callado le añade su propio
        // silencio (que es justo lo que debe hacer). Así que primero se sueltan
        // TODOS los solos y solo después se quitan los silencios de mano.
        foreach (int c in cues)
            board.SetSolo(c, false);
        Assert.False(board.AnySolo);
        foreach (int c in cues)
            if (board.IsMutedByHand(c))
                board.ToggleMute(c);

        foreach (int c in cues)
            Assert.False(board.IsMuted(c), $"el cue {c} se quedó mudo");
    }
}
