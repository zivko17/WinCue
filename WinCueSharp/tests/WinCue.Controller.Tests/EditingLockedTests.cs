// =============================================================================
// EditingLockedTests.cs — Candado de edición del modo SHOW (MainController.
// EditingLocked / CueListEditor.RejectIfLocked)
// =============================================================================
// Cubre EXACTAMENTE las cuatro operaciones que WinCue.App bloquea con TODAS
// sus entradas a la vez (toolbar + tecla Supr): añadir, insertar, borrar e
// importar. Copiar/pegar/duplicar/agrupar quedan fuera a propósito — en
// WinCue.App esas solo se bloquean desde el menú contextual, no desde sus
// atajos de teclado, así que un candado aquí cambiaría su comportamiento
// (ver el comentario de CueListEditor._isLocked).
// =============================================================================

using WinCue.Core;

namespace WinCue.Controller.Tests;

public class EditingLockedTests : ControllerTestBase
{
    [Fact]
    public void EditingLocked_DefaultsToFalse()
    {
        Assert.False(Controller.EditingLocked);
    }

    [Fact]
    public void SetEditingLocked_RoundTrips()
    {
        Controller.SetEditingLocked(true);
        Assert.True(Controller.EditingLocked);
        Controller.SetEditingLocked(false);
        Assert.False(Controller.EditingLocked);
    }

    [Fact]
    public void Locked_AddNewCue_IsRejected()
    {
        Controller.SetEditingLocked(true);
        Controller.AddNewCue(CueType.Audio);
        Assert.Equal(0, Model.Count);
        Assert.Contains(View.Statuses, s => s.Contains("SHOW"));
    }

    [Fact]
    public void Locked_OnInsertCue_IsRejected()
    {
        AddCue(CueType.Audio);
        Controller.SetEditingLocked(true);
        Controller.OnInsertCue(CueType.Wait, row: 0);
        Assert.Equal(1, Model.Count);
    }

    [Fact]
    public void Locked_OnDeleteCue_IsRejected()
    {
        AddCue(CueType.Audio);
        Controller.SetEditingLocked(true);
        Controller.OnDeleteCue(0);
        Assert.Equal(1, Model.Count);
    }

    [Fact]
    public void Locked_AddCuesFromFiles_IsRejected()
    {
        string wav = Path.Combine(TempDir, "tema.wav");
        File.WriteAllText(wav, "x");
        Controller.SetEditingLocked(true);

        Controller.AddCuesFromFiles(-1, new[] { wav });

        Assert.Equal(0, Model.Count);
    }

    [Fact]
    public void Unlocked_AfterRelock_OperationsWorkAgain()
    {
        Controller.SetEditingLocked(true);
        Controller.AddNewCue(CueType.Audio);
        Assert.Equal(0, Model.Count);

        Controller.SetEditingLocked(false);
        Controller.AddNewCue(CueType.Audio);
        Assert.Equal(1, Model.Count);
    }
}
