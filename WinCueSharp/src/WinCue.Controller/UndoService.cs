// =============================================================================
// UndoService.cs — Undo/redo por snapshots JSON (extraído de MainController,
// paso 4 del plan de docs/auditoria/02-arquitectura.md)
// =============================================================================
// Snapshots baratos (el show es texto pequeño) con coalescencia de ráfagas:
// un slider o un arrastre commitean decenas de veces por segundo — solo la
// PRIMERA instantánea del gesto va a la pila. Restaurar NO detiene la
// reproducción: los motores van por id y las instancias en ejecución
// conservan copia de lo que necesitan.
//
// El flag HasUnsavedChanges NO vive aquí: pertenece al ciclo de edición del
// controlador (lo limpia guardar/cargar), que envuelve Push().
//
// Hilos: solo el hilo de UI (como el controlador).
// =============================================================================

using System.Diagnostics;
using WinCue.Core;

namespace WinCue.Controller;

public sealed class UndoService
{
    private const int MaxUndo = 100;

    private readonly CueModel _model;
    private readonly IShowView _view;
    private readonly Func<int> _getPlayhead;     // el snapshot guarda el playhead
    private readonly Action<int> _setPlayhead;

    private readonly List<(string Json, int Playhead)> _undoStack = new();
    private readonly List<(string Json, int Playhead)> _redoStack = new();
    private long _lastPushTicks;

    public UndoService(
        CueModel model,
        IShowView view,
        Func<int> getPlayhead,
        Action<int> setPlayhead)
    {
        _model = model;
        _view = view;
        _getPlayhead = getPlayhead;
        _setPlayhead = setPlayhead;
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Apunta el estado actual en la pila de deshacer (llamar ANTES
    /// de mutar el modelo). Toda edición invalida la pila de rehacer, incluso
    /// las coalescidas.</summary>
    public void Push()
    {
        _redoStack.Clear();
        // Coalescencia de ráfagas: un slider o un arrastre commitean decenas de
        // veces por segundo — solo la PRIMERA instantánea del gesto va al undo.
        if (_lastPushTicks != 0 &&
            Stopwatch.GetElapsedTime(_lastPushTicks).TotalMilliseconds < 800)
            return;
        _lastPushTicks = Stopwatch.GetTimestamp();
        _undoStack.Add((_model.ToJson(), _getPlayhead()));
        if (_undoStack.Count > MaxUndo)
            _undoStack.RemoveAt(0);
    }

    public void Undo()
    {
        if (_undoStack.Count == 0)
            return;
        _redoStack.Add((_model.ToJson(), _getPlayhead()));
        var (json, playhead) = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreSnapshot(json, playhead);
        _view.FlashStatus(Loc.T("↶ Deshacer", "↶ Undo"));
    }

    public void Redo()
    {
        if (_redoStack.Count == 0)
            return;
        _undoStack.Add((_model.ToJson(), _getPlayhead()));
        var (json, playhead) = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreSnapshot(json, playhead);
        _view.FlashStatus(Loc.T("↷ Rehacer", "↷ Redo"));
    }

    private void RestoreSnapshot(string json, int playhead)
    {
        // No detiene la reproducción: los motores van por id, y las instancias
        // en _running conservan copia de los datos que necesitan.
        try
        {
            _model.ReplaceAll(CueModel.FromJson(json));
        }
        catch (FormatException)
        {
            return;   // snapshot corrupto: no tocar el modelo
        }
        _view.ReloadTable();
        _setPlayhead(playhead);
    }
}
