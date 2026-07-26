// =============================================================================
// CueJsonTests.cs — Compatibilidad JSON con la versión Python de WinCue
// =============================================================================
// Estas pruebas garantizan que:
//   1. Un show guardado por WinCue (Python) se abre aquí sin pérdida.
//   2. Un show guardado aquí usa exactamente las mismas claves/valores que
//      escribe Python (snake_case, enums en SCREAMING_SNAKE, etc.).
//   3. La retrocompatibilidad con shows antiguos (solo 5 campos) se mantiene.
// =============================================================================

using WinCue.Core;

namespace WinCue.Core.Tests;

public class CueJsonTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // ------------------------------------------------------------------
    // Round-trip completo
    // ------------------------------------------------------------------

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new Cue
        {
            Id = 42,
            Type = CueType.Audio,
            Name = "Música clímax — Act II (ñ, á, 中文)",
            Duration = 45.5,
            FilePath = @"C:\media\clímax.wav",
            Number = "10.5",
            Color = "purple",
            Notes = "notas del operador",
            Armed = false,
            PreWait = 1.5,
            PostWait = 2.5,
            ContinueMode = ContinueMode.AutoContinue,
            Volume = 0.8,
            FadeIn = 3.0,
            FadeOut = 4.0,
            Loop = true,
            TargetId = 7,
            FadeStopOnDone = false,
            StopOthers = true,
            ParentId = 3,
            GroupMode = GroupMode.GoNext,
            Collapsed = true,
            Flagged = true,
            AutoLoad = true,
            Hotkey = "F5",
            Retrigger = RetriggerMode.HardRestart,
            FadeOthers = true,
            FadeOthersTime = 5.0,
            FadeOthersScope = FadeScope.All,
            Scale = 75.0,
            TrimStart = 2.0,
            TrimEnd = 40.0,
            PlayCount = 3,
            Rate = 1.25,
            PreservePitch = false,
            MidiEnabled = true,
            MidiType = MidiTriggerType.ControlChange,
            MidiChannel = 5,
            MidiNote = 64,
            MidiVelocity = 100,
            WallclockEnabled = true,
            WallclockTime = "20:30:00",
            WallclockDays = 65,
            TimecodeEnabled = true,
            TimecodeValue = "01:02:03:04",
            DuckEnabled = true,
            DuckLevel = -12.0,
            DuckTime = 2.0,
            SecondTriggerOnRelease = true,
            GeoMode = GeoMode.Custom,
            Layer = "5",
            Opacity = 50.0,
            PreserveAspect = false,
            TranslateX = 10.0,
            TranslateY = -20.0,
            ScaleX = 1.5,
            ScaleY = 0.5,
            AnchorX = 0.25,
            AnchorY = -0.25,
            RotateX = 15.0,
            RotateY = 30.0,
            RotateZ = 90.0,
        };

        var model = new CueModel();
        model.AddCue(original);
        string json = model.ToJson();

        var loaded = CueModel.FromJson(json);
        var c = Assert.Single(loaded);

        // Comparación exhaustiva campo a campo
        Assert.Equal(original.Id, c.Id);
        Assert.Equal(original.Type, c.Type);
        Assert.Equal(original.Name, c.Name);
        Assert.Equal(original.Duration, c.Duration);
        Assert.Equal(original.FilePath, c.FilePath);
        Assert.Equal(original.Number, c.Number);
        Assert.Equal(original.Color, c.Color);
        Assert.Equal(original.Notes, c.Notes);
        Assert.Equal(original.Armed, c.Armed);
        Assert.Equal(original.PreWait, c.PreWait);
        Assert.Equal(original.PostWait, c.PostWait);
        Assert.Equal(original.ContinueMode, c.ContinueMode);
        Assert.Equal(original.Volume, c.Volume);
        Assert.Equal(original.FadeIn, c.FadeIn);
        Assert.Equal(original.FadeOut, c.FadeOut);
        Assert.Equal(original.Loop, c.Loop);
        Assert.Equal(original.TargetId, c.TargetId);
        Assert.Equal(original.FadeStopOnDone, c.FadeStopOnDone);
        Assert.Equal(original.StopOthers, c.StopOthers);
        Assert.Equal(original.ParentId, c.ParentId);
        Assert.Equal(original.GroupMode, c.GroupMode);
        Assert.Equal(original.Collapsed, c.Collapsed);
        Assert.Equal(original.Flagged, c.Flagged);
        Assert.Equal(original.AutoLoad, c.AutoLoad);
        Assert.Equal(original.Hotkey, c.Hotkey);
        Assert.Equal(original.Retrigger, c.Retrigger);
        Assert.Equal(original.FadeOthers, c.FadeOthers);
        Assert.Equal(original.FadeOthersTime, c.FadeOthersTime);
        Assert.Equal(original.FadeOthersScope, c.FadeOthersScope);
        Assert.Equal(original.Scale, c.Scale);
        Assert.Equal(original.TrimStart, c.TrimStart);
        Assert.Equal(original.TrimEnd, c.TrimEnd);
        Assert.Equal(original.PlayCount, c.PlayCount);
        Assert.Equal(original.Rate, c.Rate);
        Assert.Equal(original.PreservePitch, c.PreservePitch);
        Assert.Equal(original.MidiEnabled, c.MidiEnabled);
        Assert.Equal(original.MidiType, c.MidiType);
        Assert.Equal(original.MidiChannel, c.MidiChannel);
        Assert.Equal(original.MidiNote, c.MidiNote);
        Assert.Equal(original.MidiVelocity, c.MidiVelocity);
        Assert.Equal(original.WallclockEnabled, c.WallclockEnabled);
        Assert.Equal(original.WallclockTime, c.WallclockTime);
        Assert.Equal(original.WallclockDays, c.WallclockDays);
        Assert.Equal(original.TimecodeEnabled, c.TimecodeEnabled);
        Assert.Equal(original.TimecodeValue, c.TimecodeValue);
        Assert.Equal(original.DuckEnabled, c.DuckEnabled);
        Assert.Equal(original.DuckLevel, c.DuckLevel);
        Assert.Equal(original.DuckTime, c.DuckTime);
        Assert.Equal(original.SecondTriggerOnRelease, c.SecondTriggerOnRelease);
        Assert.Equal(original.GeoMode, c.GeoMode);
        Assert.Equal(original.Layer, c.Layer);
        Assert.Equal(original.Opacity, c.Opacity);
        Assert.Equal(original.PreserveAspect, c.PreserveAspect);
        Assert.Equal(original.TranslateX, c.TranslateX);
        Assert.Equal(original.TranslateY, c.TranslateY);
        Assert.Equal(original.ScaleX, c.ScaleX);
        Assert.Equal(original.ScaleY, c.ScaleY);
        Assert.Equal(original.AnchorX, c.AnchorX);
        Assert.Equal(original.AnchorY, c.AnchorY);
        Assert.Equal(original.RotateX, c.RotateX);
        Assert.Equal(original.RotateY, c.RotateY);
        Assert.Equal(original.RotateZ, c.RotateZ);

        // Runtime: siempre IDLE / sin latencia al cargar
        Assert.Equal(CueState.Idle, c.State);
        Assert.Null(c.LatencyMs);
    }

    // ------------------------------------------------------------------
    // Compatibilidad con shows generados por la versión Python
    // ------------------------------------------------------------------

    [Fact]
    public void PythonShow_DemoShowJson_Loads()
    {
        string json = File.ReadAllText(FixturePath("demo_show.json"));
        var cues = CueModel.FromJson(json);

        Assert.NotEmpty(cues);
        // Cue 1 del demo: MEMO con nombre conocido
        Assert.Equal(1, cues[0].Id);
        Assert.Equal(CueType.Memo, cues[0].Type);
        Assert.Equal("=== DEMO: pulsa GO para avanzar ===", cues[0].Name);
        // Cue 2: WAIT con AUTO_FOLLOW
        Assert.Equal(CueType.Wait, cues[1].Type);
        Assert.Equal(ContinueMode.AutoFollow, cues[1].ContinueMode);
        Assert.Equal(2.0, cues[1].Duration);
        // Cue 3: AUTO_CONTINUE con post_wait 1.5
        Assert.Equal(ContinueMode.AutoContinue, cues[2].ContinueMode);
        Assert.Equal(1.5, cues[2].PostWait);
        // Cue 4: color green
        Assert.Equal("green", cues[3].Color);
    }

    [Fact]
    public void SerializedJson_UsesPythonCompatibleKeysAndValues()
    {
        var model = new CueModel();
        model.AddCue(new Cue
        {
            Id = 1,
            Type = CueType.Audio,
            Name = "test",
            Duration = 1.0,
            ContinueMode = ContinueMode.AutoFollow,
            Retrigger = RetriggerMode.HardRestart,
            GroupMode = GroupMode.GoNext,
            FadeOthersScope = FadeScope.Peers,
            MidiType = MidiTriggerType.NoteOn,
            GeoMode = GeoMode.Full,
        });
        string json = model.ToJson();

        // Wrapper y claves snake_case idénticas a Python
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"cues\":", json);
        Assert.Contains("\"continue_mode\": \"AUTO_FOLLOW\"", json);
        Assert.Contains("\"retrigger\": \"HARD_RESTART\"", json);
        Assert.Contains("\"group_mode\": \"GO_NEXT\"", json);
        Assert.Contains("\"fade_others_scope\": \"PEERS\"", json);
        Assert.Contains("\"midi_type\": \"note_on\"", json);
        Assert.Contains("\"geo_mode\": \"FULL\"", json);
        Assert.Contains("\"type\": \"AUDIO\"", json);
        Assert.Contains("\"fade_stop_on_done\": true", json);
        Assert.Contains("\"target_id\": null", json);
        // Los campos runtime NO se persisten
        Assert.DoesNotContain("\"state\"", json);
        Assert.DoesNotContain("\"latency_ms\"", json);
        Assert.DoesNotContain("\"State\"", json);
    }

    // ------------------------------------------------------------------
    // Retrocompatibilidad y validación
    // ------------------------------------------------------------------

    [Fact]
    public void OldShow_MinimalFields_UsesDefaults()
    {
        // Shows antiguos: solo id/type/name/duration
        const string json = """
            [{"id": 1, "type": "AUDIO", "name": "viejo", "duration": 10.0}]
            """;
        var cues = CueModel.FromJson(json);
        var c = Assert.Single(cues);

        Assert.Equal(1, c.Id);
        Assert.True(c.Armed);
        Assert.Equal(1.0, c.Volume);
        Assert.Equal(ContinueMode.None, c.ContinueMode);
        Assert.Equal(GroupMode.Enter, c.GroupMode);
        Assert.True(c.FadeStopOnDone);
        Assert.Equal(100.0, c.Scale);
        Assert.Equal(1, c.PlayCount);
        Assert.Equal(1.0, c.Rate);
        Assert.True(c.PreservePitch);
        Assert.Equal(127, c.WallclockDays);
        Assert.Equal(-1, c.MidiVelocity);
        Assert.Equal("top", c.Layer);
        Assert.Equal(100.0, c.Opacity);
        Assert.Null(c.TargetId);
        Assert.Null(c.ParentId);
    }

    [Fact]
    public void BareList_WithoutWrapper_IsSupported()
    {
        const string json = """
            [{"id": 1, "type": "MEMO", "name": "a", "duration": 0.0},
             {"id": 2, "type": "WAIT", "name": "b", "duration": 1.0}]
            """;
        var cues = CueModel.FromJson(json);
        Assert.Equal(2, cues.Count);
    }

    [Fact]
    public void DuplicateIds_Throws()
    {
        const string json = """
            [{"id": 1, "type": "MEMO", "name": "a", "duration": 0.0},
             {"id": 1, "type": "MEMO", "name": "b", "duration": 0.0}]
            """;
        var exc = Assert.Throws<FormatException>(() => CueModel.FromJson(json));
        Assert.Contains("ID duplicado", exc.Message);
    }

    [Fact]
    public void InvalidCueType_Throws()
    {
        const string json = """
            [{"id": 1, "type": "NO_EXISTE", "name": "a", "duration": 0.0}]
            """;
        var exc = Assert.Throws<FormatException>(() => CueModel.FromJson(json));
        Assert.Contains("Cue #1", exc.Message);
    }

    [Fact]
    public void InvalidJson_Throws()
    {
        var exc = Assert.Throws<FormatException>(() => CueModel.FromJson("{esto no es json"));
        Assert.Contains("JSON inválido", exc.Message);
    }

    [Fact]
    public void NegativeDuration_Throws()
    {
        const string json = """
            [{"id": 1, "type": "AUDIO", "name": "a", "duration": -5.0}]
            """;
        Assert.Throws<FormatException>(() => CueModel.FromJson(json));
    }

    [Fact]
    public void Normalize_ClampsOutOfRangeValues()
    {
        var c = new Cue
        {
            Id = 1, Type = CueType.Audio, Name = "x", Duration = 1.0,
            PreWait = -3.0, PostWait = -1.0, FadeIn = -2.0, FadeOut = -2.0,
            Volume = 4.2,
        };
        c.Normalize();
        Assert.Equal(0.0, c.PreWait);
        Assert.Equal(0.0, c.PostWait);
        Assert.Equal(0.0, c.FadeIn);
        Assert.Equal(0.0, c.FadeOut);
        // El fader llega a +10 dB: el techo ya no es la unidad
        Assert.Equal(AudioLevels.MaxGain, c.Volume, 4);
    }

    [Fact]
    public void Normalize_KeepsUnityVolumeUntouched()
    {
        // COMPATIBILIDAD: subir el techo a +10 dB no puede cambiar lo que ya
        // significaba un show guardado. 1,0 era 0 dB y lo sigue siendo.
        var c = new Cue { Id = 1, Type = CueType.Audio, Name = "x", Duration = 1.0 };
        c.Normalize();
        Assert.Equal(1.0, c.Volume);
        Assert.Equal(0.0, AudioLevels.ToDb(c.Volume), 6);
    }

    [Fact]
    public void FaderLaw_IsInvertibleAndPlacesUnityWhereTheScaleSaysSo()
    {
        // La escala se rotula con estas mismas posiciones: si la ley y su
        // inversa no cuadran, los números del fader mienten.
        foreach (double db in AudioLevels.ScaleTicks)
        {
            double pos = AudioLevels.DbToPosition(db);
            Assert.InRange(pos, 0.0, 1.0);
            Assert.Equal(db, AudioLevels.ToDb(AudioLevels.PositionToGain(pos)), 3);
        }
        // Monótona: más recorrido, más ganancia
        double prev = -1.0;
        for (int i = 0; i <= 100; i++)
        {
            double g = AudioLevels.PositionToGain(i / 100.0);
            Assert.True(g >= prev, $"la ley baja en pos {i / 100.0}");
            prev = g;
        }
        Assert.Equal(0.0, AudioLevels.PositionToGain(0.0));          // abajo = silencio
        Assert.Equal(AudioLevels.MaxGain, AudioLevels.PositionToGain(1.0), 4);
    }

    [Theory]
    // Lo que de verdad teclea una persona con prisa
    [InlineData("0", 1.0)]
    [InlineData("0 dB", 1.0)]
    [InlineData("+6", 1.9952623)]
    [InlineData("-6", 0.5011872)]
    [InlineData("-6,0 dB", 0.5011872)]      // coma decimal
    [InlineData("-6.0 dB", 0.5011872)]      // punto decimal
    [InlineData("  -6 db  ", 0.5011872)]    // espacios y minúsculas
    [InlineData("−6", 0.5011872)]           // el menos tipográfico de la propia lectura
    [InlineData("-inf", 0.0)]
    [InlineData("−∞", 0.0)]
    [InlineData("-90", 0.0)]                // por debajo del suelo: silencio
    [InlineData("+0,0 dB (100%)", 1.0)]     // el propio formato del inspector
    public void DbEscritosAMano_SeLeenComoLosEscribeUnaPersona(string texto, double esperado)
    {
        double? g = AudioLevels.ParseDb(texto);
        Assert.NotNull(g);
        Assert.Equal(esperado, g!.Value, 5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dB")]
    [InlineData("hola")]
    [InlineData("--3")]
    [InlineData(null)]
    public void DbIlegibles_DevuelvenNull_ParaDejarElValorComoEstaba(string? texto)
    {
        // Null y no 0: un texto que no se entiende NO puede convertirse en
        // silencio, que es el peor valor posible al que caer por error.
        Assert.Null(AudioLevels.ParseDb(texto));
    }

    [Fact]
    public void DbEscritosPorEncimaDelTecho_SeRecortanAlTecho()
    {
        Assert.Equal(AudioLevels.MaxGain, AudioLevels.ParseDb("+40")!.Value, 5);
    }

    [Fact]
    public void LoQueSeEscribe_EsLoQueSeLee_IdaYVuelta()
    {
        // La caja muestra DbLabel y acepta ParseDb: si no fueran inversas, el
        // valor cambiaría solo con entrar y salir del campo sin tocar nada.
        foreach (double db in new[] { 0.0, -5.0, -12.3, 6.0, 10.0 })
        {
            string texto = AudioLevels.DbLabel(AudioLevels.FromDb(db));
            double? vuelta = AudioLevels.ParseDb(texto);
            Assert.NotNull(vuelta);
            Assert.Equal(db, AudioLevels.ToDb(vuelta!.Value), 3);
        }
    }

    [Fact]
    public void LaUnidad_CaeDondeElDobleClicLaBusca()
    {
        // Contrato del DOBLE CLIC del fader: llevarlo a esta posición tiene que
        // dar 0 dB EXACTOS. Si la ley se retoca y la unidad deja de caer en un
        // punto de la tabla, el gesto dejaría el fader «casi» en 0 dB — que en
        // una mesa es lo mismo que dejarlo mal.
        double pos = AudioLevels.GainToPosition(1.0);
        Assert.Equal(0.0, AudioLevels.ToDb(AudioLevels.PositionToGain(pos)), 9);
        // Y a tres cuartos de la subida, que es donde la busca la mano
        Assert.Equal(0.75, pos, 3);
    }
}
