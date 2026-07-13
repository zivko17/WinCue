// =============================================================================
// GroupAndTargetingTests.cs — Grupos (4 modos) y cues FADE/START/STOP
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class GroupAndTargetingTests : ControllerTestBase
{
    /// <summary>Construye: G[child1, child2, child3], after — devuelve ids.</summary>
    private (Cue Group, Cue C1, Cue C2, Cue C3, Cue After) MakeGroup(GroupMode mode)
    {
        var g = AddCue(CueType.Group, "G", setup: c => c.GroupMode = mode);
        var c1 = AddCue(CueType.Audio, "c1", setup: c => c.ParentId = g.Id);
        var c2 = AddCue(CueType.Audio, "c2", setup: c => c.ParentId = g.Id);
        var c3 = AddCue(CueType.Audio, "c3", setup: c => c.ParentId = g.Id);
        var after = AddCue(CueType.Memo, "after");
        return (g, c1, c2, c3, after);
    }

    [Fact]
    public void GroupEnter_FiresFirstChild_PlayheadEntersGroup()
    {
        var (_, c1, _, _, _) = MakeGroup(GroupMode.Enter);

        Controller.OnGoPressed();

        var play = Assert.Single(Audio.Plays);
        Assert.Equal(c1.Id, play.CueId);
        // El playhead queda en el siguiente hijo (índice 2), no tras el grupo
        Assert.Equal(2, Controller.Playhead);
    }

    [Fact]
    public void GroupTimeline_FiresAllChildren_PlayheadSkipsGroup()
    {
        var (_, c1, c2, c3, _) = MakeGroup(GroupMode.Timeline);

        Controller.OnGoPressed();

        Assert.Equal(3, Audio.Plays.Count);
        Assert.Equal(new[] { c1.Id, c2.Id, c3.Id }, Audio.Plays.Select(p => p.CueId));
        Assert.Equal(4, Controller.Playhead);   // tras el grupo (extent)
    }

    [Fact]
    public void GroupGoNext_FiresOnlyFirstChild_PlayheadSkipsGroup()
    {
        var (_, c1, _, _, _) = MakeGroup(GroupMode.GoNext);

        Controller.OnGoPressed();

        var play = Assert.Single(Audio.Plays);
        Assert.Equal(c1.Id, play.CueId);
        Assert.Equal(4, Controller.Playhead);
    }

    [Fact]
    public void GroupRandom_RoundRobin_FiresEachChildOncePerRound()
    {
        var (g, c1, c2, c3, _) = MakeGroup(GroupMode.Random);
        var childIds = new[] { c1.Id, c2.Id, c3.Id };

        // Ronda completa: 3 disparos (terminando cada hijo entre disparos)
        for (int i = 0; i < 3; i++)
        {
            Controller.TriggerCueId(g.Id);
            int played = Audio.Plays[^1].CueId;
            Audio.EndNaturally(played);
        }

        var firstRound = Audio.Plays.Select(p => p.CueId).ToList();
        Assert.Equal(3, firstRound.Count);
        Assert.Equal(3, firstRound.Distinct().Count());        // sin repetir
        Assert.All(firstRound, id => Assert.Contains(id, childIds));

        // Segunda ronda: vuelve a poder repetir
        Controller.TriggerCueId(g.Id);
        Assert.Equal(4, Audio.Plays.Count);
    }

    [Fact]
    public void GroupRandom_SkipsUnarmedAndRunningChildren()
    {
        var (g, c1, c2, c3, _) = MakeGroup(GroupMode.Random);
        c1.Armed = false;                     // nunca elegible
        Controller.TriggerCueId(c2.Id);       // c2 ya está sonando

        Controller.TriggerCueId(g.Id);

        // Solo c3 era elegible
        Assert.Equal(c3.Id, Audio.Plays[^1].CueId);
    }

    [Fact]
    public void EmptyGroup_Enter_MovesPlayheadPastGroup()
    {
        AddCue(CueType.Group, "vacío", setup: c => c.GroupMode = GroupMode.Enter);
        AddCue(CueType.Memo, "after");

        Controller.OnGoPressed();

        Assert.Empty(Audio.Plays);
        Assert.Equal(1, Controller.Playhead);
    }

    // ------------------------------------------------------------------
    // FADE / START / STOP
    // ------------------------------------------------------------------

    [Fact]
    public void StartCue_TriggersTarget_WithoutMovingPlayhead()
    {
        var target = AddCue(CueType.Audio, "target");
        var start = AddCue(CueType.Start, setup: c => c.TargetId = target.Id);
        AddCue(CueType.Memo);

        Controller.OnSetPlayhead(1);
        Controller.OnGoPressed();   // dispara el START (playhead → 2)

        var play = Assert.Single(Audio.Plays);
        Assert.Equal(target.Id, play.CueId);
        Assert.Equal(2, Controller.Playhead);   // avanzó solo por el GO del START
        Assert.Equal(CueState.Stopped, start.State);   // el START es instantáneo
    }

    [Fact]
    public void StartCue_TargetChain_DoesNotMovePlayhead()
    {
        // El objetivo tiene AUTO_FOLLOW: su cadena corre como sub-secuencia
        var target = AddCue(CueType.Audio, "t",
            setup: c => c.ContinueMode = ContinueMode.AutoFollow);
        var next = AddCue(CueType.Audio, "n");
        var start = AddCue(CueType.Start, setup: c => c.TargetId = target.Id);

        Controller.OnSetPlayhead(2);
        Controller.OnGoPressed();               // START dispara target
        int playheadAfterGo = Controller.Playhead;

        Audio.EndNaturally(target.Id);          // target acaba → sub-cadena sigue

        Assert.Equal(2, Audio.Plays.Count);
        Assert.Equal(next.Id, Audio.Plays[1].CueId);
        Assert.Equal(playheadAfterGo, Controller.Playhead);   // playhead intacto
    }

    [Fact]
    public void FadeCue_FadesAudioTarget()
    {
        var target = AddCue(CueType.Audio);
        var fade = AddCue(CueType.Fade, duration: 3.0, setup: c =>
        {
            c.TargetId = target.Id;
            c.Volume = 0.0;             // fundir a silencio
            c.FadeStopOnDone = true;
        });

        Controller.TriggerCueId(target.Id);
        Controller.TriggerCueId(fade.Id);

        var f = Assert.Single(Audio.Fades);
        Assert.Equal(target.Id, f.CueId);
        Assert.Equal(0.0, f.Target);
        Assert.Equal(3.0, f.Duration);
        Assert.True(f.StopAfter);       // volumen 0 + fade_stop_on_done
    }

    [Fact]
    public void FadeCue_ToNonZero_DoesNotStopAfter()
    {
        var target = AddCue(CueType.Audio);
        var fade = AddCue(CueType.Fade, duration: 2.0, setup: c =>
        {
            c.TargetId = target.Id;
            c.Volume = 0.5;
            c.FadeStopOnDone = true;    // irrelevante si el destino no es 0
        });

        Controller.TriggerCueId(target.Id);
        Controller.TriggerCueId(fade.Id);

        Assert.False(Audio.Fades[0].StopAfter);
    }

    [Fact]
    public void FadeCue_WithoutTarget_FlashesAndDoesNothing()
    {
        var fade = AddCue(CueType.Fade, duration: 1.0);   // sin target
        Controller.TriggerCueId(fade.Id);
        Assert.Empty(Audio.Fades);
        Assert.Contains(View.Statuses, s => s.Contains("FADE sin objetivo"));
    }

    [Fact]
    public void StopCue_StopsTargetWithItsFadeOut()
    {
        var target = AddCue(CueType.Audio);
        var stop = AddCue(CueType.Stop, setup: c =>
        {
            c.TargetId = target.Id;
            c.FadeOut = 1.0;
        });

        Controller.TriggerCueId(target.Id);
        Controller.TriggerCueId(stop.Id);

        var s = Assert.Single(Audio.Stops);
        Assert.Equal(target.Id, s.CueId);
        Assert.Equal(1.0, s.Fade);
    }

    [Fact]
    public void StopCue_WithoutTarget_StopsEverything()
    {
        var a = AddCue(CueType.Audio);
        var b = AddCue(CueType.Audio);
        var stop = AddCue(CueType.Stop);   // sin target → stop all

        Controller.TriggerCueId(a.Id);
        Controller.TriggerCueId(b.Id);
        Controller.TriggerCueId(stop.Id);

        Assert.False(Audio.IsPlaying(a.Id));
        Assert.False(Audio.IsPlaying(b.Id));
        Assert.True(Audio.StopAllCalls > 0);
    }
}
