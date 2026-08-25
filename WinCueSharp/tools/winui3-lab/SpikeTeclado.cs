// =============================================================================
// SpikeTeclado.cs — spike de 20 minutos que pide auditoria-teclado.md §7.
// =============================================================================
// LA PREGUNTA, Y POR QUÉ HAY QUE MEDIRLA ANTES DE CONSTRUIR NADA
//
// El arreglo del teclado de Secuence (Opción 1 del informe) descansa entero en
// una afirmación de la documentación de Microsoft que el propio informe marca
// como NO comprobada: que el «PreviewKeyDown» de un ANCESTRO se dispara ANTES
// del `OnKeyDown` interno del control que tiene el foco, y que marcar
// `Handled` ahí le quita la tecla a ese control.
//
// Si eso fuera falso, el despachador único no serviría de nada: la barra
// seguiría pulsando el botón enfocado y el arreglo tendría que ser otro
// (subclassing del HWND, Opción 5). Por eso se mide aquí, en un laboratorio
// aislado, y no dentro del producto.
//
// CÓMO SE MIDE (sin que nadie tenga que mirar la pantalla)
//
// Un `Grid` raíz con un `Button`, un `TextBox` y un `ListView` dentro. El Grid
// engancha PreviewKeyDown; el Button apunta su Click y su KeyDown. Se coloca
// el foco por código, se inyecta la tecla con `keybd_event` (el mismo método
// que usó la auditoría) y se anota el ORDEN REAL de los eventos.
//
// Cuatro casos, y los dos primeros son el control A/B que decide el arreglo:
//   A · foco en el Button, el preview NO marca Handled → ¿llega el preview
//       antes que el Click? (orden)
//   B · foco en el Button, el preview SÍ marca Handled → ¿desaparece el Click?
//       (autoridad: esto es lo que de verdad hace falta)
//   C · foco en el TextBox, Espacio, el preview marca Handled → ¿se escribe
//       igual el espacio? (mide el daño colateral de NO tener guardián)
//   D · foco en el TextBox, Espacio, el preview SALE SIN TOCAR NADA (que es
//       lo que hará el guardián de escritura del arreglo real) → el espacio
//       tiene que escribirse y no debe dispararse nada.
//
// Se ejecuta con LAB_SPIKE_TECLADO=1 y deja el resultado en el archivo que
// diga LAB_SPIKE_LOG (o spike-teclado.log junto al ejecutable). La ventana se
// cierra sola al terminar: es una medición, no una demo.
// =============================================================================

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace WinUi3Lab;

internal sealed class SpikeTecladoWindow : Window
{
    private const byte VkSpace = 0x20;
    private const uint KeyeventfKeyup = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    private readonly StringBuilder _log = new();
    private readonly List<string> _eventos = new();

    private readonly Grid _root = new();
    private readonly Button _boton = new() { Content = "BOTON", Width = 200, Height = 40 };
    private readonly TextBox _campo = new() { Text = "abc", Width = 200 };
    private readonly ListView _lista = new() { Width = 200, Height = 80 };

    /// <summary>Qué debe hacer el preview del ancestro en el caso en curso.</summary>
    private bool _previewMarcaHandled;

    /// <summary>El preview se calla del todo (simula el guardián de escritura).</summary>
    private bool _previewSeCalla;

    public SpikeTecladoWindow()
    {
        var pila = new StackPanel { Spacing = 8, Padding = new Thickness(16) };
        _lista.Items.Add("uno");
        _lista.Items.Add("dos");
        pila.Children.Add(_boton);
        pila.Children.Add(_campo);
        pila.Children.Add(_lista);
        _root.Children.Add(pila);
        Content = _root;

        // EL ENGANCHE QUE SE ESTÁ MIDIENDO: preview en el ANCESTRO, con
        // handledEventsToo para verlo incluso si alguien ya lo marcó.
        _root.AddHandler(UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewRaiz), handledEventsToo: true);
        _root.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(OnKeyDownRaiz), handledEventsToo: true);

        _boton.Click += (_, _) => _eventos.Add("BOTON.Click");
        _boton.KeyDown += (_, e) => _eventos.Add($"BOTON.KeyDown({e.Key},handled={e.Handled})");
        _campo.KeyDown += (_, e) => _eventos.Add($"CAMPO.KeyDown({e.Key},handled={e.Handled})");

        Activated += OnActivatedUnaVez;
    }

    private void OnPreviewRaiz(object sender, KeyRoutedEventArgs e)
    {
        if (_previewSeCalla)
        {
            _eventos.Add($"RAIZ.PreviewKeyDown({e.Key}) -> SE CALLA");
            return;
        }
        _eventos.Add($"RAIZ.PreviewKeyDown({e.Key},handled_entrada={e.Handled})");
        if (_previewMarcaHandled)
            e.Handled = true;
    }

    private void OnKeyDownRaiz(object sender, KeyRoutedEventArgs e) =>
        _eventos.Add($"RAIZ.KeyDown({e.Key},handled={e.Handled})");

    private bool _arrancado;

    private async void OnActivatedUnaVez(object sender, WindowActivatedEventArgs args)
    {
        if (_arrancado || args.WindowActivationState == WindowActivationState.Deactivated)
            return;
        _arrancado = true;
        Activated -= OnActivatedUnaVez;

        SetForegroundWindow(WindowNative.GetWindowHandle(this));
        await Task.Delay(700);

        await CasoAsync("A · foco BOTON, Espacio, preview NO marca Handled",
            enfocar: _boton, marcaHandled: false, seCalla: false);
        await CasoAsync("B · foco BOTON, Espacio, preview SI marca Handled",
            enfocar: _boton, marcaHandled: true, seCalla: false);
        await CasoAsync("C · foco CAMPO, Espacio, preview SI marca Handled",
            enfocar: _campo, marcaHandled: true, seCalla: false);
        await CasoAsync("D · foco CAMPO, Espacio, preview SE CALLA (guardian)",
            enfocar: _campo, marcaHandled: false, seCalla: true);
        await CasoAsync("E · foco LISTA, Espacio, preview SI marca Handled",
            enfocar: _lista, marcaHandled: true, seCalla: false);

        string ruta = Environment.GetEnvironmentVariable("LAB_SPIKE_LOG")
            ?? Path.Combine(AppContext.BaseDirectory, "spike-teclado.log");
        File.WriteAllText(ruta, _log.ToString());
        Close();
    }

    private async Task CasoAsync(string titulo, Control enfocar, bool marcaHandled, bool seCalla)
    {
        _previewMarcaHandled = marcaHandled;
        _previewSeCalla = seCalla;
        _eventos.Clear();

        if (enfocar == _campo)
            _campo.Text = "";
        enfocar.Focus(FocusState.Programmatic);
        await Task.Delay(250);

        keybd_event(VkSpace, 0, 0, 0);
        keybd_event(VkSpace, 0, KeyeventfKeyup, 0);
        await Task.Delay(450);

        _log.AppendLine(titulo);
        _log.AppendLine($"   foco real = {FocusManager.GetFocusedElement(Content.XamlRoot)?.GetType().Name ?? "(null)"}");
        foreach (string ev in _eventos)
            _log.AppendLine($"   {ev}");
        if (enfocar == _campo)
            _log.AppendLine($"   CAMPO.Text = «{_campo.Text}» (largo {_campo.Text.Length})");
        _log.AppendLine();
    }
}
