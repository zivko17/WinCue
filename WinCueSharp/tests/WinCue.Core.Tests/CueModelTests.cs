// =============================================================================
// CueModelTests.cs — Colección, jerarquía de grupos y mutación
// =============================================================================

using WinCue.Core;

namespace WinCue.Core.Tests;

public class CueModelTests
{
    private static Cue MakeCue(int id, CueType type = CueType.Memo, int? parentId = null) =>
        new() { Id = id, Type = type, Name = $"cue {id}", Duration = 0.0, ParentId = parentId };

    /// <summary>Modelo con jerarquía: 1, G2[3, G4[5]], 6</summary>
    private static CueModel MakeHierarchy()
    {
        var m = new CueModel();
        m.AddCue(MakeCue(1));
        m.AddCue(MakeCue(2, CueType.Group));
        m.AddCue(MakeCue(3, parentId: 2));
        m.AddCue(MakeCue(4, CueType.Group, parentId: 2));
        m.AddCue(MakeCue(5, parentId: 4));
        m.AddCue(MakeCue(6));
        return m;
    }

    [Fact]
    public void GetById_And_IndexOfId()
    {
        var m = MakeHierarchy();
        Assert.Equal("cue 3", m.GetById(3)!.Name);
        Assert.Null(m.GetById(99));
        Assert.Equal(2, m.IndexOfId(3));
        Assert.Equal(-1, m.IndexOfId(99));
    }

    [Fact]
    public void NextId_IsMaxPlusOne()
    {
        var m = MakeHierarchy();
        Assert.Equal(7, m.NextId());
        Assert.Equal(1, new CueModel().NextId());
    }

    [Fact]
    public void NextNumber_UsesNumericTokens()
    {
        var m = new CueModel();
        m.AddCue(new Cue { Id = 1, Type = CueType.Memo, Name = "a", Number = "5" });
        m.AddCue(new Cue { Id = 2, Type = CueType.Memo, Name = "b", Number = "no-num" });
        Assert.Equal("6", m.NextNumber());
    }

    [Fact]
    public void Depth_CountsNesting()
    {
        var m = MakeHierarchy();
        Assert.Equal(0, m.Depth(m.GetById(1)!));
        Assert.Equal(1, m.Depth(m.GetById(3)!));
        Assert.Equal(2, m.Depth(m.GetById(5)!));
    }

    [Fact]
    public void IsDescendant_AnyLevel()
    {
        var m = MakeHierarchy();
        Assert.True(m.IsDescendant(m.GetById(3)!, 2));
        Assert.True(m.IsDescendant(m.GetById(5)!, 2));   // nieto
        Assert.True(m.IsDescendant(m.GetById(5)!, 4));
        Assert.False(m.IsDescendant(m.GetById(6)!, 2));
        Assert.False(m.IsDescendant(m.GetById(1)!, 2));
    }

    [Fact]
    public void DirectChildren_OnlyImmediate()
    {
        var m = MakeHierarchy();
        var children = m.DirectChildren(2);
        Assert.Equal(new[] { 3, 4 }, children.Select(c => c.Id));
        Assert.Equal(new[] { 2, 3 }, m.DirectChildIndices(2));
    }

    [Fact]
    public void Descendants_AllLevels()
    {
        var m = MakeHierarchy();
        Assert.Equal(new[] { 3, 4, 5 }, m.Descendants(2).Select(c => c.Id));
    }

    [Fact]
    public void GroupExtent_FirstRowAfterGroup()
    {
        var m = MakeHierarchy();
        Assert.Equal(5, m.GroupExtent(1));  // G2 en fila 1 → tras 5 (fila 4) viene fila 5
        Assert.Equal(5, m.GroupExtent(3));  // G4 en fila 3 → tras 5 (fila 4)
    }

    [Fact]
    public void IsAncestorCollapsed_DetectsCollapsedParents()
    {
        var m = MakeHierarchy();
        Assert.False(m.IsAncestorCollapsed(m.GetById(5)!));
        m.GetById(2)!.Collapsed = true;
        Assert.True(m.IsAncestorCollapsed(m.GetById(3)!));
        Assert.True(m.IsAncestorCollapsed(m.GetById(5)!));   // nieto también oculto
        Assert.False(m.IsAncestorCollapsed(m.GetById(6)!));
    }

    [Fact]
    public void MoveCue_Reorders()
    {
        var m = MakeHierarchy();
        m.MoveCue(0, 2);
        Assert.Equal(new[] { 2, 3, 1, 4, 5, 6 }, m.Cues.Select(c => c.Id));
        // Fuera de rango: no-op
        m.MoveCue(-1, 2);
        m.MoveCue(0, 99);
        Assert.Equal(6, m.Count);
    }

    [Fact]
    public void InsertRemoveUpdate_Basics()
    {
        var m = new CueModel();
        m.AddCue(MakeCue(1));
        m.InsertCue(0, MakeCue(2));
        Assert.Equal(new[] { 2, 1 }, m.Cues.Select(c => c.Id));
        m.InsertCue(99, MakeCue(3));  // clamp al final
        Assert.Equal(3, m.Cues[^1].Id);
        m.RemoveCue(0);
        Assert.Equal(new[] { 1, 3 }, m.Cues.Select(c => c.Id));
        m.RemoveCue(99);  // no-op
        Assert.Equal(2, m.Count);
        m.UpdateCue(0, MakeCue(9));
        Assert.Equal(9, m.Cues[0].Id);
    }

    [Fact]
    public void ReplaceAll_KeepsInstanceValid()
    {
        var m = MakeHierarchy();
        var reference = m;   // referencia externa
        m.ReplaceAll(new[] { MakeCue(100) });
        Assert.Equal(1, reference.Count);
        Assert.Equal(100, reference.Cues[0].Id);
    }

    [Fact]
    public void SetState_And_SetLatency()
    {
        var m = MakeHierarchy();
        m.SetState(0, CueState.Playing);
        Assert.Equal(CueState.Playing, m.Cues[0].State);
        m.SetLatency(0, 12.5);
        Assert.Equal(12.5, m.Cues[0].LatencyMs);
        // Fuera de rango: no-op
        m.SetState(99, CueState.Error);
        m.SetLatency(99, 1.0);
    }
}
