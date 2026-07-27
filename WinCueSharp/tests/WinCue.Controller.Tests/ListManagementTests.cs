// =============================================================================
// ListManagementTests.cs — Añadir/insertar/agrupar/editar/guardar/cargar
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class ListManagementTests : ControllerTestBase
{
    [Fact]
    public void AddNewCue_AppendsAndMovesPlayhead()
    {
        Controller.AddNewCue(CueType.Audio);
        Controller.AddNewCue(CueType.Wait);

        Assert.Equal(2, Model.Count);
        Assert.Equal(CueType.Wait, Model.Cues[1].Type);
        Assert.Equal(3.0, Model.Cues[1].Duration);      // WAIT por defecto 3 s
        Assert.Equal(1, Controller.Playhead);
        Assert.Equal(2, View.Appended.Count);
    }

    [Fact]
    public void InsertCue_InheritsParentOfReference()
    {
        var g = AddCue(CueType.Group);
        AddCue(CueType.Audio, setup: c => c.ParentId = g.Id);

        Controller.OnInsertCue(CueType.Wait, row: 1);   // bajo el hijo

        Assert.Equal(3, Model.Count);
        Assert.Equal(CueType.Wait, Model.Cues[2].Type);
        Assert.Equal(g.Id, Model.Cues[2].ParentId);      // hereda el grupo
    }

    [Fact]
    public void GroupSelected_WrapsCuesInNewGroup()
    {
        var a = AddCue(CueType.Audio, "a");
        var b = AddCue(CueType.Audio, "b");
        AddCue(CueType.Memo, "fuera");

        Controller.OnGroupSelected(new[] { 0, 1 });

        Assert.Equal(4, Model.Count);
        var group = Model.Cues[0];
        Assert.Equal(CueType.Group, group.Type);
        Assert.Equal(group.Id, a.ParentId);
        Assert.Equal(group.Id, b.ParentId);
        Assert.Null(Model.Cues[3].ParentId);            // "fuera" intacto
        Assert.Equal(new[] { group.Id, a.Id, b.Id }, Model.Cues.Take(3).Select(c => c.Id));
    }

    [Fact]
    public void Ungroup_PromotesChildrenAndRemovesGroup()
    {
        var a = AddCue(CueType.Audio, "a");
        AddCue(CueType.Audio, "b");
        Controller.OnGroupSelected(new[] { 0, 1 });
        int groupRow = 0;

        Controller.OnUngroup(groupRow);

        Assert.Equal(2, Model.Count);
        Assert.Null(a.ParentId);
        Assert.DoesNotContain(Model.Cues, c => c.Type == CueType.Group);
    }

    [Fact]
    public void DeleteCue_StopsItIfRunning_AndRemoves()
    {
        var a = AddCue(CueType.Audio, "a");
        AddCue(CueType.Memo, "b");
        Controller.TriggerCueId(a.Id);

        Controller.OnDeleteCue(0);

        Assert.Equal(1, Model.Count);
        Assert.Equal("b", Model.Cues[0].Name);
        Assert.False(Audio.IsPlaying(a.Id));
        Assert.Empty(Controller.RunningCueIds);
    }

    [Fact]
    public void ToggleCollapse_FlipsFlagAndAppliesVisibility()
    {
        var g = AddCue(CueType.Group);
        Controller.OnToggleCollapse(0);
        Assert.True(g.Collapsed);
        Assert.Equal(1, View.GroupVisibilityApplies);
        Controller.OnToggleCollapse(0);
        Assert.False(g.Collapsed);
    }

    [Fact]
    public void AddCuesFromFiles_CreatesTypedCues()
    {
        string wav = Path.Combine(TempDir, "tema.wav");
        string mp4 = Path.Combine(TempDir, "clip.mp4");
        string bad = Path.Combine(TempDir, "raro.xyz");
        File.WriteAllText(wav, "x");
        File.WriteAllText(mp4, "x");
        File.WriteAllText(bad, "x");

        Controller.AddCuesFromFiles(-1, new[] { wav, mp4, bad });

        Assert.Equal(2, Model.Count);   // el .xyz se descarta
        Assert.Equal(CueType.Audio, Model.Cues[0].Type);
        Assert.Equal("tema", Model.Cues[0].Name);
        Assert.Equal(CueType.Video, Model.Cues[1].Type);
    }

    // ------------------------------------------------------------------
    // Edición desde el inspector
    // ------------------------------------------------------------------

    [Fact]
    public void EditCommitted_AppliesTypedFields()
    {
        var a = AddCue(CueType.Audio);

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        {
            ["name"] = "Nuevo nombre",
            ["volume"] = 0.6,
            ["continue_mode"] = "AUTO_FOLLOW",
            ["retrigger"] = "HARD_RESTART",
            ["loop"] = true,
            ["play_count"] = 5,
            ["pre_wait"] = 1.25,
        });

        Assert.Equal("Nuevo nombre", a.Name);
        Assert.Equal(0.6, a.Volume);
        Assert.Equal(ContinueMode.AutoFollow, a.ContinueMode);
        Assert.Equal(RetriggerMode.HardRestart, a.Retrigger);
        Assert.True(a.Loop);
        Assert.Equal(5, a.PlayCount);
        Assert.Equal(1.25, a.PreWait);
    }

    [Fact]
    public void EditCommitted_TargetId_EmptyMeansNull()
    {
        var a = AddCue(CueType.Fade, setup: c => c.TargetId = 7);
        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        {
            ["target_id"] = -1,
        });
        Assert.Null(a.TargetId);
    }

    [Fact]
    public void EditCommitted_VolumeWhilePlaying_AppliesLive()
    {
        var a = AddCue(CueType.Audio, duration: 10.0);
        Controller.OnGoPressed();

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        {
            ["volume"] = 0.3,
        });

        Assert.Equal(0.3, Audio.Volumes[a.Id]);
    }

    [Fact]
    public void EditCommitted_GeometryWhilePlaying_AppliesLive()
    {
        var v = AddCue(CueType.Video);
        Controller.OnGoPressed();

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        {
            ["geo_mode"] = "CUSTOM",
            ["translate_x"] = 100.0,
            ["rotate_z"] = 90.0,
        });

        var (cueId, geo, _) = Assert.Single(Video.GeometryApplied);
        Assert.Equal(v.Id, cueId);
        Assert.Equal("CUSTOM", geo.Mode);
        Assert.Equal(100.0, geo.TranslateX);
        Assert.Equal(90.0, geo.RotateZ);
        Assert.Equal(1920.0, geo.SurfW);   // canvas de la vista
    }

    [Fact]
    public void EditCommitted_FileChange_InvalidatesAudioCache()
    {
        var a = AddCue(CueType.Audio);
        a.LatencyMs = 12.0;

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        {
            ["type"] = "AUDIO",
            ["file_path"] = @"C:\otro\archivo.wav",
        });

        Assert.Null(a.LatencyMs);
        Assert.Contains(a.Id, Audio.Invalidated);
    }

    [Fact]
    public void EditCommitted_FileDropWithoutType_UsesMediaImporter()
    {
        string mp4 = Path.Combine(TempDir, "video.mp4");
        File.WriteAllText(mp4, "x");
        var a = AddCue(CueType.Audio);

        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        {
            ["file_path"] = mp4,   // sin "type" → detección automática
        });

        Assert.Equal(CueType.Video, a.Type);
        Assert.Equal(Path.GetFullPath(mp4), a.FilePath);
    }

    [Fact]
    public void EditCommitted_Hotkey_RebuildsHotkeys()
    {
        AddCue(CueType.Audio);
        Controller.OnCueEditCommitted(0, new Dictionary<string, object?>
        {
            ["hotkey"] = "F5",
        });
        Assert.Equal(1, View.HotkeyRebuilds);
    }

    // ------------------------------------------------------------------
    // Guardar / cargar
    // ------------------------------------------------------------------

    [Fact]
    public void SaveAndLoadShow_RoundTrips()
    {
        AddCue(CueType.Audio, "uno", setup: c => c.ContinueMode = ContinueMode.AutoFollow);
        AddCue(CueType.Wait, "dos", duration: 2.0);
        string path = Path.Combine(TempDir, "show.json");

        Controller.SaveShow(path);
        Assert.True(File.Exists(path));

        // Cargar en un modelo limpio (mismo controlador)
        Model.ReplaceAll(new List<Cue>());
        Controller.LoadShow(path);

        Assert.Equal(2, Model.Count);
        Assert.Equal("uno", Model.Cues[0].Name);
        Assert.Equal(ContinueMode.AutoFollow, Model.Cues[0].ContinueMode);
        Assert.Equal(0, Controller.Playhead);
        Assert.True(View.ReloadCount > 0);
    }

    [Fact]
    public void SaveShow_AddsJsonExtension()
    {
        AddCue(CueType.Memo);
        string path = Path.Combine(TempDir, "sinext");
        Controller.SaveShow(path);
        Assert.True(File.Exists(path + ".json"));
    }

    [Fact]
    public void LoadShow_InvalidJson_ShowsErrorAndKeepsModel()
    {
        AddCue(CueType.Memo, "intacto");
        string path = Path.Combine(TempDir, "roto.json");
        File.WriteAllText(path, "{esto no es json");

        Controller.LoadShow(path);

        Assert.Single(View.Errors);
        Assert.Equal(1, Model.Count);
        Assert.Equal("intacto", Model.Cues[0].Name);
    }

    [Fact]
    public void LoadShow_StopsEverythingFirst()
    {
        var a = AddCue(CueType.Audio, "sonando");
        Controller.TriggerCueId(a.Id);
        string path = Path.Combine(TempDir, "show.json");
        Controller.SaveShow(path);

        Controller.LoadShow(path);

        Assert.False(Audio.IsPlaying(a.Id));
        Assert.Empty(Controller.RunningCueIds);
    }
}
