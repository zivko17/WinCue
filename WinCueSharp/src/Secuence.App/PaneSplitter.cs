// PaneSplitter — el separador arrastrable que WinUI 3 no trae.
//
// WPF tiene GridSplitter y WinCue lo usa tal cual (dos: uno entre la columna
// de previos y el centro, otro entre la lista de cues y el panel de
// pestañas). WinUI 3 no lo tiene —está en la lista de ausencias del informe
// del hito 0—, así que aquí se hace a mano. Es código de FRAMEWORK, o sea de
// los que la regla 2 manda reescribir y no compartir; lo que SÍ se comparte
// es dónde para el arrastre: WinCue.Presentation.SplitterDrag, con pruebas.
//
// Trampa que costó descubrir en el resto de la app y que aquí ya viene
// aplicada: el puntero se lee contra `null` (coordenadas de la ventana) y no
// contra el propio control. Contra el control, cada movimiento devolvía una
// posición medida sobre un elemento que ACABA de moverse por ese mismo
// movimiento, y el arrastre se realimentaba.

using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WinCue.Presentation;

namespace Secuence.App;

/// <summary>Franja fina que, arrastrada, cambia el tamaño de un panel. Quién
/// es ese panel y cuánto puede medir lo pone la ventana (ver MainWindow):
/// este control solo traduce el puntero en un tamaño y lo aplica.
///
/// Hereda de Grid y no de Border porque <b>Border está SELLADO en WinUI 3</b>
/// (como CanvasControl). Grid sí se puede heredar, pinta su Background y con
/// él hace hit-test, que es todo lo que necesita una franja de 5 px. Y al no
/// ser un Control, los eventos de puntero se enganchan en vez de
/// sobrescribirse: On_PointerPressed y compañía son virtuales en Control, no
/// en UIElement.</summary>
public class PaneSplitter : Grid
{
    /// <summary>true = separa FILAS (se arrastra arriba y abajo); false =
    /// separa COLUMNAS.</summary>
    public bool Rows { get; set; }

    /// <summary>En filas, si el panel que manda está DEBAJO del separador
    /// (entonces arrastrar hacia arriba lo hace crecer). En columnas, si está
    /// a la DERECHA. El de WinCue entre lista y pestañas es de los de abajo;
    /// el de la columna de previos, de los de la izquierda.</summary>
    public bool GrowsAgainstDrag { get; set; }

    /// <summary>Tamaño actual del panel gobernado.</summary>
    public Func<double>? Current;
    /// <summary>Hueco que se reparten los dos paneles y este separador.</summary>
    public Func<double>? Available;
    /// <summary>Aplicar el tamaño nuevo.</summary>
    public Action<double>? Apply;

    public double MinSize { get; set; }
    public double MaxSize { get; set; } = double.PositiveInfinity;
    public double NeighbourMin { get; set; }

    private bool _dragging;
    private double _startSize;
    private Point _startPoint;

    public PaneSplitter()
    {
        Loaded += (_, _) =>
        {
            // El cursor de arrastre. ProtectedCursor es protected en
            // UIElement: por eso esto es una CLASE y no un Grid suelto con
            // manejadores enganchados desde fuera.
            ProtectedCursor = InputSystemCursor.Create(
                Rows ? InputSystemCursorShape.SizeNorthSouth
                     : InputSystemCursorShape.SizeWestEast);
        };

        PointerPressed += (_, e) =>
        {
            if (Current is null || Apply is null)
                return;
            _startSize = Current();
            _startPoint = e.GetCurrentPoint(null).Position;
            // El arrastre NO depende de que CapturePointer diga que sí: si la
            // captura fallara, el puntero se sale de una franja de 5 px al
            // primer movimiento y el arrastre se moriría a medias.
            _dragging = true;
            CapturePointer(e.Pointer);
            e.Handled = true;
        };

        PointerMoved += (_, e) =>
        {
            if (!_dragging || Current is null || Apply is null)
                return;

            var p = e.GetCurrentPoint(null).Position;
            double moved = Rows ? p.Y - _startPoint.Y : p.X - _startPoint.X;
            // Orientar: el panel de abajo (o el de la derecha) crece cuando el
            // puntero va en sentido CONTRARIO al arrastre.
            double delta = GrowsAgainstDrag ? -moved : moved;

            double available = Available?.Invoke() ?? double.PositiveInfinity;
            double thickness = Rows ? ActualHeight : ActualWidth;
            Apply(SplitterDrag.Resize(_startSize, delta, MinSize, MaxSize,
                                      available, NeighbourMin, thickness));
            e.Handled = true;
        };

        PointerReleased += (_, e) =>
        {
            if (!_dragging)
                return;
            _dragging = false;
            ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        };

        PointerCaptureLost += (_, _) => _dragging = false;
    }
}
