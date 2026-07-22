// =============================================================================
// EditingAndSafetyTests.cs — Undo/redo, copiar/pegar, mover, pausa por cue,
// anti doble-GO, rutas relativas, media faltante y autosave
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class EditingAndSafetyTests : ControllerTestBase
{
    // ------------------------------------------------------------------
    // Undo / Redo
    // ------------------------------------------------------------------

    [Fact]
    public void Undo_RestoresDeletedCue()
    {
        AddCue(CueType.Audio, "importante");
        Controller.OnDeleteCue(0);
        Assert.Equal(0, Model.Count);

        Controller.Undo();
        Assert.Equal(1, Model.Count);
        Assert.Equal("importante", Model.Cues[0].Name);
    }

    [Fact]
    public void Undo_Redo_FieldEdit()
    {
        var a = AddCue(CueType.Audio, "original");
        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["name"] = "cambiado" });
        Assert.Equal("cambiado", Model.Cues[0].Name);

        Controller.Undo();
        Assert.Equal("original", Model.Cues[0].Name);
        Controller.Redo();
        Assert.Equal("cambiado", Model.Cues[0].Name);
    }

    [Fact]
    public void CueEdit_AplicaPreWaitYTimelineLayer()
    {
        // El timeline commitea estos dos campos al soltar un bloque arrastrado
        AddCue(CueType.Audio, "bloque");
        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["pre_wait"] = 4.2, ["timeline_layer"] = 3 });

        Assert.Equal(4.2, Model.Cues[0].PreWait);
        Assert.Equal(3, Model.Cues[0].TimelineLayer);
    }

    [Fact]
    public void SaveShow_RotatesBackupsOnOverwrite()
    {
        AddCue(CueType.Memo, "v1");
        string path = Path.Combine(TempDir, "show.json");

        Controller.SaveShow(path);
        Assert.False(File.Exists($"{path}.bak1"));   // primera vez: sin backup

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        { ["name"] = "v2" });
        Controller.SaveShow(path);
        Assert.True(File.Exists($"{path}.bak1"));
        Assert.Contains("v1", File.ReadAllText($"{path}.bak1"));
        Assert.Contains("v2", File.ReadAllText(path));
    }

    [Fact]
    public void Undo_AfterLoadShow_RestoresPreviousShow()
    {
        AddCue(CueType.Memo, "antiguo");
        string path = Path.Combine(TempDir, "otro.json");
        var other = new CueModel();
        other.AddCue(new Cue { Id = 1, Type = CueType.Memo, Name = "nuevo" });
        File.WriteAllText(path, other.ToJson());

        Controller.LoadShow(path);
        Assert.Equal("nuevo", Model.Cues[0].Name);

        Controller.Undo();
        Assert.Equal("antiguo", Model.Cues[0].Name);
    }

    [Fact]
    public void Undo_EmptyStack_IsNoOp()
    {
        Assert.False(Controller.CanUndo);
        Controller.Undo();   // no lanza
        Controller.Redo();
    }

    // ------------------------------------------------------------------
    // Copiar / pegar / duplicar / mover
    // ------------------------------------------------------------------

    [Fact]
    public void CopyPaste_CreatesNewIdsAndPreservesData()
    {
        var a = AddCue(CueType.Audio, "orig", setup: c => { c.Volume = 0.6; c.Loop = true; });
        AddCue(CueType.Memo, "after");

        Controller.CopyCues(new[] { 0 });
        Controller.PasteCues(1);

        Assert.Equal(3, Model.Count);
        var pasted = Model.Cues[2];
        Assert.NotEqual(a.Id, pasted.Id);
        Assert.Equal("orig", pasted.Name);
        Assert.Equal(0.6, pasted.Volume);
        Assert.True(pasted.Loop);
        Assert.Equal(CueState.Idle, pasted.State);
    }

    [Fact]
    public void CopyPaste_Group_RemapsChildParentIds()
    {
        var g = AddCue(CueType.Group, "G");
        AddCue(CueType.Audio, "hijo", setup: c => c.ParentId = g.Id);

        Controller.CopyCues(new[] { 0 });   // el grupo arrastra a su hijo
        Controller.PasteCues(1);

        Assert.Equal(4, Model.Count);
        var newGroup = Model.Cues[2];
        var newChild = Model.Cues[3];
        Assert.Equal(CueType.Group, newGroup.Type);
        Assert.Equal(newGroup.Id, newChild.ParentId);   // jerarquía remapeada
        Assert.NotEqual(g.Id, newGroup.Id);
    }

    [Fact]
    public void Duplicate_InsertsCopyBelow()
    {
        AddCue(CueType.Audio, "uno");
        AddCue(CueType.Memo, "dos");

        Controller.DuplicateCues(new[] { 0 });

        Assert.Equal(3, Model.Count);
        Assert.Equal("uno", Model.Cues[1].Name);   // copia justo debajo
        Assert.Equal("dos", Model.Cues[2].Name);
    }

    [Fact]
    public void MoveCue_ReordersAndIsUndoable()
    {
        AddCue(CueType.Memo, "a");
        AddCue(CueType.Memo, "b");
        AddCue(CueType.Memo, "c");

        Controller.OnMoveCue(0, 2);
        Assert.Equal(new[] { "b", "c", "a" }, Model.Cues.Select(c => c.Name));

        Controller.Undo();
        Assert.Equal(new[] { "a", "b", "c" }, Model.Cues.Select(c => c.Name));
    }

    [Fact]
    public void Renumber_AssignsSequentialNumbersWithUndo()
    {
        AddCue(CueType.Memo, "a", setup: c => c.Number = "7");
        AddCue(CueType.Memo, "b", setup: c => c.Number = "2.5");
        AddCue(CueType.Memo, "c", setup: c => c.Number = "");

        Controller.RenumberCues();
        Assert.Equal(new[] { "1", "2", "3" }, Model.Cues.Select(c => c.Number));

        Controller.Undo();
        Assert.Equal("7", Model.Cues[0].Number);
    }

    [Fact]
    public void Tick_DuringPreWait_ReportsPreWaitRemaining()
    {
        AddCue(CueType.Wait, "espera", duration: 5.0, setup: c => c.PreWait = 2.0);

        Controller.OnGoPressed();
        Controller.Tick();
        Assert.Single(View.LastActiveCues);
        Assert.NotNull(View.LastActiveCues[0].PreWaitRemaining);
        Assert.InRange(View.LastActiveCues[0].PreWaitRemaining!.Value, 1.0, 2.0);

        Clock.Advance(2100);   // pre_wait consumido: empieza la acción
        Controller.Tick();
        Assert.Null(View.LastActiveCues[0].PreWaitRemaining);
    }

    [Fact]
    public void MoveCue_GroupMovesAsBlockWithChildren()
    {
        AddCue(CueType.Memo, "a");
        var g = AddCue(CueType.Group, "G");
        AddCue(CueType.Audio, "h1", setup: c => c.ParentId = g.Id);
        AddCue(CueType.Audio, "h2", setup: c => c.ParentId = g.Id);
        AddCue(CueType.Memo, "b");

        Controller.OnMoveCue(1, 4);   // arrastrar el GRUPO más allá de "b"

        Assert.Equal(new[] { "a", "b", "G", "h1", "h2" },
            Model.Cues.Select(c => c.Name));
        Assert.Equal(g.Id, Model.Cues[3].ParentId);   // los hijos siguen dentro
        Assert.Equal(g.Id, Model.Cues[4].ParentId);
        Assert.Null(Model.Cues[2].ParentId);          // el grupo sigue en la raíz
    }

    [Fact]
    public void MoveCue_IntoGroupRegion_AdoptsGroupParent()
    {
        var g = AddCue(CueType.Group, "G");
        AddCue(CueType.Audio, "h1", setup: c => c.ParentId = g.Id);
        AddCue(CueType.Audio, "h2", setup: c => c.ParentId = g.Id);
        AddCue(CueType.Memo, "suelto");

        Controller.OnMoveCue(3, 2);   // soltar "suelto" entre h1 y h2

        Assert.Equal(new[] { "G", "h1", "suelto", "h2" },
            Model.Cues.Select(c => c.Name));
        Assert.Equal(g.Id, Model.Cues[2].ParentId);   // entra en el grupo
    }

    [Fact]
    public void MoveCue_OntoGroupRow_EntersGroupAsFirstChild()
    {
        var g = AddCue(CueType.Group, "G");
        AddCue(CueType.Audio, "h1", setup: c => c.ParentId = g.Id);
        AddCue(CueType.Video, "video");

        Controller.OnMoveCue(2, 0);   // soltar "video" SOBRE la fila del grupo

        Assert.Equal(new[] { "G", "video", "h1" },
            Model.Cues.Select(c => c.Name));
        Assert.Equal(g.Id, Model.Cues[1].ParentId);   // entra como primer hijo
    }

    [Fact]
    public void MoveCue_FromAboveOntoGroupRow_AlsoEntersGroup()
    {
        AddCue(CueType.Video, "video");
        var g = AddCue(CueType.Group, "G");
        AddCue(CueType.Audio, "h1", setup: c => c.ParentId = g.Id);

        Controller.OnMoveCue(0, 1);   // arrastrar desde arriba sobre el grupo

        Assert.Equal(new[] { "G", "video", "h1" },
            Model.Cues.Select(c => c.Name));
        Assert.Equal(g.Id, Model.Cues[1].ParentId);
    }

    [Fact]
    public void MoveCue_OutOfGroup_ClearsParent()
    {
        var g = AddCue(CueType.Group, "G");
        AddCue(CueType.Audio, "h1", setup: c => c.ParentId = g.Id);
        AddCue(CueType.Audio, "h2", setup: c => c.ParentId = g.Id);
        AddCue(CueType.Memo, "fin");

        Controller.OnMoveCue(1, 3);   // sacar h1 más allá de "fin"

        Assert.Equal(new[] { "G", "h2", "fin", "h1" },
            Model.Cues.Select(c => c.Name));
        Assert.Null(Model.Cues[3].ParentId);          // ya no pertenece al grupo
    }

    // ------------------------------------------------------------------
    // Pausa por cue / stop por id
    // ------------------------------------------------------------------

    [Fact]
    public void PauseCueToggle_PausesOnlyThatCue()
    {
        var a = AddCue(CueType.Audio, duration: 10.0);
        var b = AddCue(CueType.Audio);
        Controller.TriggerCueId(a.Id);
        Controller.TriggerCueId(b.Id);

        Controller.OnPauseCueToggle(0);
        Assert.Contains(a.Id, Audio.PausedCues);
        Assert.DoesNotContain(b.Id, Audio.PausedCues);

        Controller.OnPauseCueToggle(0);
        Assert.DoesNotContain(a.Id, Audio.PausedCues);
    }

    [Fact]
    public void PauseCue_FreezesDurationTimer_ResumesRemaining()
    {
        var a = AddCue(CueType.Audio, duration: 2.0);
        Controller.TriggerCueId(a.Id);

        Clock.Advance(1_000);
        Controller.OnPauseCueToggle(0);   // pausa: el timer de duración muere
        Clock.Advance(5_000);
        Assert.True(Audio.IsPlaying(a.Id), "la pausa no debe dejar que el timer corte");

        Audio.Elapsed[a.Id] = 1.0;        // el motor congeló la posición en 1 s
        Controller.OnPauseCueToggle(0);   // reanudar → reprograma el resto (1 s)
        Clock.Advance(999);
        Assert.True(Audio.IsPlaying(a.Id));
        Clock.Advance(1);
        Assert.False(Audio.IsPlaying(a.Id));
    }

    [Fact]
    public void StopCueById_StopsRunningCue()
    {
        var a = AddCue(CueType.Audio);
        Controller.TriggerCueId(a.Id);
        Controller.StopCueById(a.Id);
        Assert.False(Audio.IsPlaying(a.Id));
        Assert.Empty(Controller.RunningCueIds);
    }

    // ------------------------------------------------------------------
    // Anti doble-GO
    // ------------------------------------------------------------------

    [Fact]
    public void DoubleGo_WithinInterval_IsIgnored()
    {
        Settings.Set("go_min_interval_ms", 5_000);   // intervalo enorme para el test
        AddCue(CueType.Audio);
        AddCue(CueType.Audio);

        Controller.OnGoPressed();
        Controller.OnGoPressed();   // doble-tap inmediato → ignorado

        Assert.Single(Audio.Plays);
        Assert.Equal(1, Controller.Playhead);
    }

    // ------------------------------------------------------------------
    // Rutas relativas / media faltante / autosave / log
    // ------------------------------------------------------------------

    [Fact]
    public void SaveShow_RelativizesMediaNextToJson_AndLoadResolves()
    {
        string media = Path.Combine(TempDir, "musica.wav");
        File.WriteAllText(media, "x");
        var a = AddCue(CueType.Audio, "tema", setup: c => c.FilePath = media);
        string show = Path.Combine(TempDir, "show.json");

        Controller.SaveShow(show);

        // En disco la ruta es relativa; en memoria sigue absoluta
        Assert.Contains("\"file_path\": \"musica.wav\"", File.ReadAllText(show));
        Assert.Equal(media, a.FilePath);

        Model.ReplaceAll(new List<Cue>());
        Controller.LoadShow(show);
        Assert.Equal(Path.GetFullPath(media), Model.Cues[0].FilePath);
        Assert.NotEqual(CueState.Error, Model.Cues[0].State);
    }

    [Fact]
    public void ExportPortable_CopiesMediaRelative_WithoutMutatingWorkingShow()
    {
        // Media en una carpeta APARTE (no junto al show de trabajo)
        string srcDir = Path.Combine(TempDir, "origen");
        Directory.CreateDirectory(srcDir);
        string media = Path.Combine(srcDir, "clip.wav");
        File.WriteAllText(media, "data");
        var a = AddCue(CueType.Audio, "clip", setup: c => c.FilePath = media);

        // Exportar a otra carpeta
        string outDir = Path.Combine(TempDir, "portable");
        string target = Path.Combine(outDir, "show.json");
        var (copied, missing) = Controller.ExportPortableShow(target);

        Assert.Equal(1, copied);
        Assert.Equal(0, missing);
        // El medio se copió a «media» junto al show exportado
        Assert.True(File.Exists(Path.Combine(outDir, "media", "clip.wav")));
        // El show de TRABAJO no se altera (sigue apuntando al original)
        Assert.Equal(media, a.FilePath);
        Assert.Equal(media, Model.Cues[0].FilePath);

        // Cargar el show exportado resuelve la ruta relativa al medio copiado
        Model.ReplaceAll(new List<Cue>());
        Controller.LoadShow(target);
        Assert.Equal(Path.GetFullPath(Path.Combine(outDir, "media", "clip.wav")),
                     Model.Cues[0].FilePath);
        Assert.NotEqual(CueState.Error, Model.Cues[0].State);
    }

    [Fact]
    public void LoadShow_MarksMissingMediaAsError()
    {
        var m = new CueModel();
        m.AddCue(new Cue
        {
            Id = 1, Type = CueType.Audio, Name = "perdido",
            FilePath = Path.Combine(TempDir, "no_existe.wav"),
        });
        m.AddCue(new Cue { Id = 2, Type = CueType.Memo, Name = "ok" });
        string show = Path.Combine(TempDir, "show.json");
        File.WriteAllText(show, m.ToJson());

        Controller.LoadShow(show);

        Assert.Equal(CueState.Error, Model.Cues[0].State);
        Assert.Equal(CueState.Idle, Model.Cues[1].State);
        Assert.Contains(View.Statuses, s => s.Contains("NO encontrados"));
    }

    [Fact]
    public void Autosave_WritesSilently_AndSkipsEmptyShow()
    {
        string path = Path.Combine(TempDir, "autosave.json");
        Assert.False(Controller.Autosave(path));   // show vacío → no escribe
        Assert.False(File.Exists(path));

        AddCue(CueType.Memo, "algo");
        Assert.True(Controller.Autosave(path));
        Assert.True(File.Exists(path));
        Assert.Empty(View.Statuses);   // silencioso: sin flashes
    }

    // ------------------------------------------------------------------
    // Salida por cue / audición / fade-all / wall-clock
    // ------------------------------------------------------------------

    [Fact]
    public void AudioOutput_PerCue_ReachesEngine()
    {
        AddCue(CueType.Audio, setup: c => c.AudioOutput = "Monitores");
        Controller.OnGoPressed();
        Assert.Equal("Monitores", Audio.Plays[0].P.OutputDevice);
    }

    [Fact]
    public void Audition_PlaysOnAuditionDevice_AndToggles()
    {
        Settings.Set("audition_device_name", "Cascos");
        var a = AddCue(CueType.Audio, "tema", setup: c => c.TrimStart = 2.0);

        Controller.AuditionToggle(0);
        var play = Assert.Single(Audio.Plays);
        Assert.Equal(MainController.AuditionCueId, play.CueId);
        Assert.Equal("Cascos", play.P.OutputDevice);
        Assert.Equal(2.0, play.P.TrimStart);
        Assert.Empty(Controller.RunningCueIds);   // no entra en el flujo del show

        Controller.AuditionToggle(0);              // segundo toque = parar
        Assert.False(Audio.IsPlaying(MainController.AuditionCueId));
    }

    [Fact]
    public void FadeAll_UsesConfiguredSeconds()
    {
        Settings.Set("fade_all_seconds", 3.0);
        var a = AddCue(CueType.Audio);
        Controller.TriggerCueId(a.Id);

        Controller.OnFadeAll();

        var stop = Assert.Single(Audio.Stops);
        Assert.Equal(3.0, stop.Fade);
    }

    [Fact]
    public void Wallclock_FiresCueAtConfiguredTime()
    {
        var target = new DateTime(2026, 7, 14, 20, 30, 0);   // martes
        var cue = AddCue(CueType.Audio, "apertura", setup: c =>
        {
            c.WallclockEnabled = true;
            c.WallclockTime = "20:30:00";
            c.WallclockDays = 127;   // todos los días
        });

        Controller.NowProvider = () => target.AddSeconds(-1);
        Controller.Tick();                       // 20:29:59 → nada
        Assert.Empty(Audio.Plays);

        Controller.NowProvider = () => target;
        Controller.Tick();                       // 20:30:00 → dispara
        Assert.Single(Audio.Plays);

        Controller.Tick();                       // mismo segundo → no repite
        Assert.Single(Audio.Plays);
    }

    [Fact]
    public void Wallclock_RespectsDayMask()
    {
        var tuesday = new DateTime(2026, 7, 14, 10, 0, 0);   // martes = bit 1
        AddCue(CueType.Audio, setup: c =>
        {
            c.WallclockEnabled = true;
            c.WallclockTime = "10:00:00";
            c.WallclockDays = 1;     // SOLO lunes (bit 0)
        });

        Controller.NowProvider = () => tuesday;
        Controller.Tick();
        Assert.Empty(Audio.Plays);   // martes no está en la máscara
    }

    [Fact]
    public void ShowLog_EmitsTransportEvents()
    {
        var log = new List<string>();
        Controller.ShowLog += log.Add;
        AddCue(CueType.Audio, "tema");

        Controller.OnGoPressed();
        Controller.OnStopPressed();
        Controller.OnPanic();

        Assert.Contains(log, l => l.StartsWith("GO") && l.Contains("tema"));
        Assert.Contains("STOP ALL", log);
        Assert.Contains("PANIC", log);
    }
}
