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
