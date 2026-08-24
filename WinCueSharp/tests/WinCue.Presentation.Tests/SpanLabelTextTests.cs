using WinCue.Core;
using WinCue.Presentation;
using Xunit;

namespace WinCue.Presentation.Tests;

public class SpanLabelTextTests
{
    static TimelineSpan Span(string number, string name, GroupMode mode)
    {
        var group = new Cue
        {
            Id = 1,
            Number = number,
            Name = name,
            Type = CueType.Group,
            GroupMode = mode,
        };
        return new TimelineSpan { Group = group, Start = 0, End = 5, MinLayer = 0, MaxLayer = 1 };
    }

    [Fact]
    public void ConIcono_EsLoQuePintaWinCue()
    {
        string text = TimelineLayout.SpanLabelText(Span("9", "Grupo", GroupMode.Enter));
        Assert.StartsWith(Icons.Group, text);
        Assert.Contains("9 · Grupo · ENTER", text);
    }

    [Fact]
    public void SinIcono_MismaEtiquetaPeroSinGlifo()
    {
        // Es lo que necesita quien pinta con Win2D: DirectWrite no resuelve la
        // fuente de iconos y el glifo saldria como un cuadradito.
        string text = TimelineLayout.SpanLabelText(Span("9", "Grupo", GroupMode.Enter),
            withIcon: false);
        Assert.Equal("9 · Grupo · ENTER", text);
        Assert.DoesNotContain(Icons.Group, text);
    }

    [Fact]
    public void ElModoVaEnMAYUSCULAS()
    {
        Assert.EndsWith("GONEXT",
            TimelineLayout.SpanLabelText(Span("2", "X", GroupMode.GoNext), withIcon: false));
    }

    [Fact]
    public void PorDefectoLLevaElIcono_ParaQueWinCueNoCambie()
    {
        var span = Span("3", "Y", GroupMode.Enter);
        Assert.Equal(TimelineLayout.SpanLabelText(span, withIcon: true),
                     TimelineLayout.SpanLabelText(span));
    }
}
