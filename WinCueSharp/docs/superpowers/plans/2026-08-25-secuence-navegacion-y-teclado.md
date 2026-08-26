# Secuence — Navegación del playhead y teclado: plan de implementación

> **Para agentes:** SUB-SKILL OBLIGATORIA: usa `superpowers:subagent-driven-development`
> (recomendada) o `superpowers:executing-plans` para ejecutar este plan tarea a
> tarea. Los pasos usan casillas (`- [ ]`) para el seguimiento.

**Goal:** Que el operador pueda **elegir el siguiente cue** —hoy no puede— y que
el teclado de Secuence haga lo que su propio menú promete.

**Architecture:** El motor ya está completo (`OnSetPlayhead`, `OnPrevPressed`,
`OnNextPressed`, `Undo`/`Redo`) y la vista ya recibe `SetPlayhead(int)`: esta
tanda **cablea**, no diseña comportamiento nuevo. Se amplía el despachador de
teclado que dejó el lote de urgencias del mismo día, se separa visualmente el
playhead de la selección, y se traen los `KeyboardAccelerator` sueltos al
despachador para que respeten el contador de diálogos.

**Tech Stack:** C# / .NET 8 (SDK clavado en 8.0.423), WinUI 3 (Windows App SDK,
desempaquetado). Pruebas puras en `WinCue.Presentation` para la decision; UI
Automation **por patrones** para el cableado.

**Spec:** `docs/superpowers/specs/2026-08-25-secuence-navegacion-y-teclado-design.md`
— léelo entero antes de la primera tarea. Trae las decisiones de Andrés y el
orden de precedencia verificado en el código de WinCue.

## Global Constraints

- **Rama:** `claude/secuence-hitos-0-1-1occ8p`. Raíz git: `C:\Proyectos\ffmpeg-gui`
  — **no** `WinCueSharp\`. Los `git add` llevan el prefijo `WinCueSharp/`.
- **Esta tanda NO toca `src/WinCue.App/` ni el motor compartido.** WinCue está
  en beta pública y vendiéndose. Si crees que necesitas tocarlo, **para y
  pregunta**.
- **La grafía «Secuence» es deliberada.** No la corrijas nunca.
- **Antes de compilar:** `Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue`.
  **NO mates `WinCue.App`**: puede estar abierto porque lo está usando Andrés.
- **Compila SIEMPRE con `dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true`**,
  nunca el `.csproj` a secas: escribe en `bin\Release\...` mientras el exe que se
  lanza vive en `bin\x64\Release\...`, y eso ya hizo que una medición saliera
  **verde con un fallo puesto**. Imprime la fecha del `Secuence.dll` en cada
  medición.
- **0 avisos, 0 errores.** Sin supresiones nuevas.
- `dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"` → **1.229
  en verde**. Flakes conocidos que NO son tuyos:
  `MpvPropertyTests.FadeVolume_ReachesTarget` y `WinCue.Audio.Tests` bajo carga
  en paralelo.
- **Esas 1.229 pruebas NO son evidencia de esta tanda.** Ningún proyecto de
  pruebas referencia `Secuence.App`: son no-regresión del motor compartido. Lo
  que sostiene cada tarea son **mediciones con control negativo**.
- Guardián de paleta: `pwsh -File WinCueSharp\scripts\verificar-paleta.ps1` OK.
  **Ni un color literal**: todo por `Theme`, que deriva de `Palette` con
  correspondencia POR NOMBRE.
- **Todo texto de interfaz bilingüe con `Loc.T("es", "en")`.**
- Commits en ASCII, prefijo convencional. **NO hagas `git push`.**
- Comentarios en castellano explicando el PORQUÉ. **Y que no mientan**: en la
  tanda anterior hubo que arreglar tres comentarios falsos y se coló uno nuevo
  por el camino.

### El método de medición — CAMBIADO EL 2026-08-26, léelo entero

**NO SE PUEDE SIMULAR UNA TECLA EN ESTA MÁQUINA.** No es una preferencia:

- `keybd_event` / `SendInput` van **al que tenga el foco**, y en esta máquina hay
  una ventana de escritorio remoto conectada a **equipo de audio de un local
  real**. Un clic sintético por coordenadas ya se perdió ahí una vez. Prohibido.
- `PostMessage` a un HWND concreto sí es seguro por construcción, pero al HWND
  de nivel superior **no llega**: WinUI 3 pone el XAML en una isla. Y el intento
  contra el HWND del bridge lo **bloquea el clasificador de seguridad** de la
  sesión. **Ese bloqueo no se rodea.**

Por eso existe la Task 3: la **decisión** de qué hace cada tecla se prueba como
función pura, sin ventana y sin pulsar nada, y eso sí corre en el CI.

**Para el cableado, lo único permitido es UI Automation POR PATRONES**, acotado
al PID de Secuence: `SelectionItemPattern.Select()`, `InvokePattern.Invoke()`.
Apunta a un elemento de un proceso concreto y **no puede aterrizar en otra
aplicación aunque el foco cambie**. Cero coordenadas de pantalla.

Las capturas, **recortadas a la ventana de Secuence** (`GetWindowRect` sobre su
HWND). Si al recortar ves que otra ventana la tapa, **para y dilo** en vez de
medir píxeles de otra aplicación.

**Y si un paso te pide de verdad entrada sintética: para y pregunta.** No lo
improvises y no falsees la medición.

**Control negativo siempre que midas**: rompe el arreglo a propósito, comprueba
que la medición lo caza, y revierte. Esta sesión ha tenido **tres verdes por el
motivo equivocado**, y los tres los destapó ese control y ninguna otra cosa.

---

## Estructura de ficheros

**Se modifican** (ninguno se crea):

| Fichero | Cambio |
|---|---|
| `src/WinCue.Presentation/DespachadorTeclado.cs` | **NUEVO** — la decision de que hace cada tecla, pura y probada |
| `tests/WinCue.Presentation.Tests/DespachadorTecladoTests.cs` | **NUEVO** — sus pruebas |
| `src/Secuence.App/CueRow.cs:105-127` | El playhead deja de teñir el fondo; expone la marca de borde |
| `src/Secuence.App/MainWindow.xaml` | La plantilla de fila pinta la marca; fuera los cuatro `KeyboardAccelerator` |
| `src/Secuence.App/MainWindow.xaml.cs` | Doble clic, despachador ampliado, buffer de ir-a-cue, aviso de colisión |
| `docs/superpowers/TRASPASO-SECUENCE.md`, `ARRANQUE-SESION-LOCAL.md` | Estado y matriz de verificación |

**Orden y dependencias:** 1 → 2 → 3 → 4 → 5 → 6 → 7, en serie. La 3 es la que se anadio el 26 (el despachador puro con pruebas) y las 4-7 son las que antes eran 3-6. La 2 depende de que
la 1 haya separado los dos estados visualmente (si no, no se puede *ver* que
funciona), y la 4 depende del despachador que amplía la 2.

---

### Task 1: El playhead deja de disputarle el fondo a la selección

**Files:**
- Modify: `src/Secuence.App/CueRow.cs:105-127`
- Modify: `src/Secuence.App/MainWindow.xaml` (plantilla de fila de `CueList`)

**Interfaces:**
- Consumes: `Theme` y `WinCue.Presentation.Palette` (correspondencia por nombre;
  el guardián lo exige). `CueRow` ya es `INotifyPropertyChanged` y ya tiene
  `IsPlayhead` con su `PropertyChanged`.
- Produces: `CueRow.PlayheadBarBrush` (`SolidColorBrush`) y
  `CueRow.NumberWeight` (`FontWeight`), los dos notificados cuando cambia
  `IsPlayhead`. **`CueRow.RowBrush` desaparece.**

**El porqué, que es lo que no se puede perder:** hoy `RowBrush` tiñe el **fondo**
de la fila cuando `IsPlayhead`, y el resaltado de selección del `ListView`
también es **fondo**. Mientras seleccionar movía el playhead daba igual porque
siempre coincidían. Desde la tarea 2 **pueden no coincidir**, y dos tintes de
fondo en la misma fila no se distinguen. Se separan por eje: **el playhead pasa
al borde izquierdo**, la selección se queda con el fondo.

- [ ] **Step 1: Cambiar lo que expone `CueRow`**

En `src/Secuence.App/CueRow.cs`, sustituir el bloque del playhead:

```csharp
    // ── Playhead (SetPlayhead/RefreshRow/ReloadTable de IShowView) ────────
    private bool _isPlayhead;

    /// <summary>true = GO disparará este cue a continuación. Lo fija
    /// MainWindow.SetPlayhead recorriendo TODAS las filas (una sola vez
    /// true, el resto false) — igual que CueRowViewModel.IsPlayhead en WPF.</summary>
    public bool IsPlayhead
    {
        get => _isPlayhead;
        set
        {
            if (_isPlayhead == value)
                return;
            _isPlayhead = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlayhead)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayheadBarBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NumberWeight)));
        }
    }

    /// <summary>Marca del playhead: una barra en el BORDE IZQUIERDO de la fila.
    /// No es cosmética la elección del eje. Antes esto teñía el FONDO
    /// (RowBrush), y el resaltado de selección del ListView también es fondo:
    /// mientras seleccionar movía el playhead los dos estados coincidían
    /// siempre y la colisión no se veía. Desde que son independientes —mirar un
    /// cue ya no cambia lo que disparará el GO— hay que poder leer los dos a la
    /// vez en la misma fila, y por eso uno usa borde y el otro fondo.</summary>
    public SolidColorBrush PlayheadBarBrush =>
        _isPlayhead ? Theme.PlayheadRowBrush : Theme.TransparentBrush;

    /// <summary>El número del cue del playhead va en negrita: segunda pista,
    /// para quien mire la tabla de lejos o no distinga bien el color.</summary>
    public FontWeight NumberWeight =>
        _isPlayhead ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
```

Borrar `RowBrush` por completo. Si algo más lo consume, el compilador lo dirá.

- [ ] **Step 2: Pintar la marca en la plantilla**

En `src/Secuence.App/MainWindow.xaml`, en la plantilla de fila de `CueList`:
quitar el binding del fondo a `RowBrush` y añadir como **primer** elemento de la
fila la barra, antes de la primera celda:

```xml
<Rectangle Width="3"
           VerticalAlignment="Stretch"
           Fill="{x:Bind PlayheadBarBrush, Mode=OneWay}" />
```

Y en el `TextBlock` que pinta el número del cue:

```xml
FontWeight="{x:Bind NumberWeight, Mode=OneWay}"
```

Respeta la rejilla de columnas que ya tenga la plantilla: la barra no debe
desplazar el contenido de las filas, así que va **dentro** del margen que ya
existe, no añadiendo uno nuevo.

`Mode=OneWay` es obligatorio: sin él `x:Bind` es de una sola vez y la fila no se
repinta al moverse el playhead.

- [ ] **Step 3: Compilar y pasar la suite**

```powershell
Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
pwsh -File scripts\verificar-paleta.ps1
```

- [ ] **Step 4: Medir que los dos estados se leen a la vez**

Lanza la app con el show de prueba, y **con el ratón** selecciona una fila
distinta de la del playhead (que al arrancar es la 0). Captura el recorte de la
tabla y compruébalo por píxel: la fila del playhead tiene la barra del color de
`Palette.PlayheadRow` en su borde izquierdo, y la fila seleccionada tiene el
fondo del resaltado. **Las dos a la vez, en filas distintas.**

Control negativo: devuelve `PlayheadBarBrush` a `Theme.TransparentBrush` siempre,
comprueba que la barra desaparece de la captura, y revierte.

- [ ] **Step 5: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/Secuence.App/CueRow.cs WinCueSharp/src/Secuence.App/MainWindow.xaml
git commit -m "feat(secuence): el playhead se marca en el borde, no en el fondo"
```

- [ ] **Step 6: Anotar la verificación en pantalla**

> Con una fila seleccionada y el playhead en otra, que se distingan **de un
> vistazo** y que las dos se lean cuando coinciden.

---

### Task 2: Los cinco gestos que mueven el playhead

**Files:**
- Modify: `src/Secuence.App/MainWindow.xaml.cs` (`OnPreviewKeyDownGlobal`, la
  tabla `CueList`)

**Interfaces:**
- Consumes: `_controller.OnPrevPressed()`, `_controller.OnNextPressed()`,
  `_controller.OnSetPlayhead(int row)` — los tres públicos en `MainController`
  (`:914`, `:920`, `:937`). `_model.IndexOfId(int cueId)` para pasar de `CueRow`
  a fila. `MainWindow.SetPlayhead(int)` ya existe (`:2154`) y lo llama el motor.
- Produces: nada nuevo hacia fuera.

**El modelo, que es la decisión de fondo de la tanda:** seleccionar una fila con
un clic simple **NO** mueve el playhead. Lo mueven `↑`/`←`, `↓`/`→`, `Enter` con
el foco en la tabla, el doble clic, y el GO (que ya lo mueve el motor).

- [ ] **Step 1: Comprobar que la selección NO mueve el playhead**

`OnCueSelectionChanged` (`:494`) hoy solo puebla inspector y timeline, y **no**
llama a `OnSetPlayhead`. Verifícalo con `grep` antes de tocar nada: si alguien lo
añadió, hay que quitarlo. Es la mitad del encargo.

- [ ] **Step 2: Doble clic**

Enganchar `DoubleTapped` en la fila de `CueList` y llamar a
`_controller.OnSetPlayhead(fila)`, resolviendo la fila con
`_model.IndexOfId(row.CueId)`. Si el índice sale `-1`, no hacer nada.

- [ ] **Step 3: Flechas y Enter en el despachador**

En `OnPreviewKeyDownGlobal`, dentro de la capa de **transporte** (por encima de
los hotkeys por cue — ver el spec para el porqué), añadir:

```csharp
            case VirtualKey.Up:
            case VirtualKey.Left:
                _controller.OnPrevPressed();
                e.Handled = true;
                return;
            case VirtualKey.Down:
            case VirtualKey.Right:
                _controller.OnNextPressed();
                e.Handled = true;
                return;
```

Y `Enter`, **solo cuando el foco está en la tabla de cues** — fuera de ahí sigue
siendo del control, que es lo que mantiene viva la ruta `Tab`+`Enter`:

```csharp
            case VirtualKey.Enter when FocusManager.GetFocusedElement(Content.XamlRoot) is ListView lv
                                       && ReferenceEquals(lv, CueList):
                if (CueList.SelectedItem is CueRow fila)
                {
                    int idx = _model.IndexOfId(fila.CueId);
                    if (idx >= 0)
                        _controller.OnSetPlayhead(idx);
                }
                e.Handled = true;
                return;
```

- [ ] **Step 4: Que la tabla siga al playhead**

En `MainWindow.SetPlayhead(int index)` (`:2154`), después del bucle que fija
`IsPlayhead`, desplazar la lista para que la fila quede a la vista
(`CueList.ScrollIntoView(...)`), **sin tocar la selección**. Sin esto,
retroceder dos cues con las flechas lo deja fuera de pantalla y el operador lo
pierde.

- [ ] **Step 5: Compilar y pasar la suite**

```powershell
Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
```

- [ ] **Step 6: Medir los cinco gestos**

Con el show de prueba, leyendo el playhead de la barra de estado o del estado
real de las filas:

| Gesto | Esperado |
|---|---|
| Clic simple en otra fila | playhead **NO se mueve**; la selección sí |
| `↓` | playhead +1 |
| `↑` | playhead −1 |
| `Enter` con el foco en la tabla | playhead → fila seleccionada |
| Doble clic en una fila | playhead → esa fila |
| `↑` con el playhead en 0 | se queda en 0, no da error |
| `Tab` hasta un botón + `Enter` | **activa el botón**, no mueve el playhead |

La primera y la última son las que definen la tanda: si alguna falla, no está
hecha.

Control negativo: haz que `OnCueSelectionChanged` llame a `OnSetPlayhead`,
comprueba que la primera fila de la tabla se pone en rojo, y revierte.

- [ ] **Step 7: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/Secuence.App/MainWindow.xaml.cs
git commit -m "feat(secuence): el operador ya elige el siguiente cue"
```

---

### Task 3: La decisión de qué hace cada tecla, pura y con pruebas

> **AÑADIDA EL 2026-08-26, y las tareas 4-7 son las que antes eran 3-6.**
> Andrés había elegido tener el mapa dentro de `Secuence.App`. El precio que se
> le presentó era «se verifica midiendo». El precio real resultó ser **«no se
> puede verificar sin él delante pulsando teclas»**: `keybd_event` va al que
> tenga el foco y en esa máquina hay un escritorio remoto a equipo de audio de
> un local; `PostMessage` al HWND de nivel superior no llega porque WinUI 3 pone
> el XAML en una isla; y el intento contra el HWND del bridge lo bloqueó el
> clasificador de seguridad, que no se rodea. Con esa premisa nueva cambió la
> decisión.

**Files:**
- Create: `src/WinCue.Presentation/DespachadorTeclado.cs`
- Test: `tests/WinCue.Presentation.Tests/DespachadorTecladoTests.cs`

**Interfaces:**
- Consumes: nada. **Función pura, sin dependencias de framework** —
  `WinCue.Presentation` es `net8.0` a secas y si algo de aquí necesitara
  `VirtualKey` o `Key`, estaría en el sitio equivocado.
- Produces:

```csharp
namespace WinCue.Presentation;

/// <summary>Qué hay que hacer con una tecla. `Nada` significa
/// «no es mía»: el llamante NO debe marcarla como tratada.</summary>
public enum AccionTecla
{
    Nada,
    Go, StopAll, StopSeleccionado, PausaGlobal, Blackout,
    PlayheadAnterior, PlayheadSiguiente, PlayheadASeleccion,
    DispararCue,
    BufferDigito, BufferBorrar, BufferSaltar,
    Abrir, Guardar, GuardarComo, Agrupar,
    Deshacer, Rehacer, Copiar, Pegar, Duplicar, PausarCue, Eliminar, Renombrar,
}

/// <summary>La decisión. `CueId` solo para DispararCue; `Caracter` solo
/// para BufferDigito.</summary>
public readonly record struct DecisionTecla(
    AccionTecla Accion, int CueId = 0, char Caracter = '\0');

/// <summary>Lo que el despachador necesita saber del mundo, y que no puede
/// averiguar por sí mismo sin dejar de ser puro.</summary>
public readonly record struct EstadoTeclado(
    bool DialogoAbierto,
    bool FocoEnCampoDeEscritura,
    bool FocoEnTablaDeCues,
    bool HayDigitosEnBuffer,
    bool ModoShow);

public static class DespachadorTeclado
{
    /// <param name="vk">Código de tecla virtual de Windows. En WinUI 3 es
    /// <c>(int)e.Key</c> directamente; en WPF, <c>KeyInterop.VirtualKeyFromKey</c>.
    /// Se usa el número y no un enum propio para que los dos productos puedan
    /// llamar sin traducir.</param>
    /// <param name="hotkeysPorCue">vk → id de cue, del show cargado.</param>
    public static DecisionTecla Resolver(
        int vk, bool ctrl, bool shift, bool alt,
        in EstadoTeclado estado,
        IReadOnlyDictionary<int, int> hotkeysPorCue);
}
```

**EL ORDEN, que es lo que esta tarea existe para poder probar.** Verificado en
el código de WinCue (`WinCue.App/MainWindow.xaml.cs:2084-2180`), con la
divergencia deliberada del transporte por encima de los hotkeys — el porqué
está en el spec:

1. `DialogoAbierto` → **`Nada`**, siempre. Ni una tecla pasa.
2. `FocoEnCampoDeEscritura` → **`Nada`**, salvo que el llamante ya haya
   decidido exceptuar ese control (el selector del PGM). El despachador no
   conoce controles: recibe el booleano ya resuelto.
3. Con `ctrl` → archivo y edición. Las de edición (`Copiar`, `Pegar`,
   `Duplicar`, `Eliminar`, `Renombrar`) devuelven **`Nada` si `ModoShow`**.
4. **Transporte**: `Go` (espacio), `StopAll` (escape), `StopSeleccionado` (`S`),
   `PlayheadAnterior` (`↑`/`←`), `PlayheadSiguiente` (`↓`/`→`), `PausaGlobal`
   (`P`).
5. **Seguridad**: `Blackout` (`B`). **Antes de los hotkeys**, como WinCue.
6. **Hotkeys por cue** → `DispararCue` con su id.
7. **Buffer de ir a cue**: dígitos y `.` → `BufferDigito`; y **solo si
   `HayDigitosEnBuffer`**, `Retroceso` → `BufferBorrar` y `Enter` →
   `BufferSaltar`.
8. **`Enter` con el buffer vacío**: `PlayheadASeleccion` **solo si
   `FocoEnTablaDeCues`**; si no, **`Nada`** — de eso depende que `Tab`+`Enter`
   siga activando botones.

- [ ] **Step 1: Escribir las pruebas que fallan**

Crear `tests/WinCue.Presentation.Tests/DespachadorTecladoTests.cs`. Como mínimo
estas, que son las reglas que han costado sangre en este proyecto:

```csharp
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class DespachadorTecladoTests
{
    private const int VkSpace = 0x20, VkEscape = 0x1B, VkEnter = 0x0D,
                      VkUp = 0x26, VkDown = 0x28, VkLeft = 0x25, VkRight = 0x27,
                      VkB = 0x42, VkS = 0x53, VkP = 0x50, VkZ = 0x5A,
                      VkDelete = 0x2E, Vk1 = 0x31, VkBack = 0x08;

    private static readonly IReadOnlyDictionary<int, int> SinHotkeys =
        new Dictionary<int, int>();

    private static EstadoTeclado Normal => new(
        DialogoAbierto: false, FocoEnCampoDeEscritura: false,
        FocoEnTablaDeCues: false, HayDigitosEnBuffer: false, ModoShow: false);

    [Fact]
    public void ConDialogoAbierto_NoPasaNiUnaTecla()
    {
        var estado = Normal with { DialogoAbierto = true };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkSpace, false, false, false, estado, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkEscape, false, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void EscribiendoEnUnCampo_ElEspacioNoDispara()
    {
        // La mitad peor del fallo original: un TextBox no marca el espacio
        // como tratado, asi que escribir un espacio en el nombre de un cue
        // disparaba el GO del show.
        var estado = Normal with { FocoEnCampoDeEscritura = true };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkSpace, false, false, false, estado, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkEscape, false, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void Blackout_GanaAlHotkeyDeCue()
    {
        // Sin esto, un show con un cue de hotkey B hace blackout en WinCue y
        // saca ese cue A SALA en Secuence, con el mismo archivo.
        var hotkeys = new Dictionary<int, int> { [VkB] = 42 };
        var d = DespachadorTeclado.Resolver(VkB, false, false, false, Normal, hotkeys);
        Assert.Equal(AccionTecla.Blackout, d.Accion);
    }

    [Fact]
    public void ElTransporte_GanaAlHotkeyDeCue()
    {
        // Divergencia deliberada con WinCue: si un show puede asignar la
        // flecha abajo como hotkey, le roba al operador la navegacion del
        // playhead, que es el gesto principal de esta tanda.
        var hotkeys = new Dictionary<int, int> { [VkDown] = 7, [VkP] = 8, [VkS] = 9 };
        Assert.Equal(AccionTecla.PlayheadSiguiente,
            DespachadorTeclado.Resolver(VkDown, false, false, false, Normal, hotkeys).Accion);
        Assert.Equal(AccionTecla.PausaGlobal,
            DespachadorTeclado.Resolver(VkP, false, false, false, Normal, hotkeys).Accion);
        Assert.Equal(AccionTecla.StopSeleccionado,
            DespachadorTeclado.Resolver(VkS, false, false, false, Normal, hotkeys).Accion);
    }

    [Fact]
    public void UnHotkeyQueNoEsDeTransporte_SiDispara()
    {
        var hotkeys = new Dictionary<int, int> { [0x51] = 13 };   // Q
        var d = DespachadorTeclado.Resolver(0x51, false, false, false, Normal, hotkeys);
        Assert.Equal(AccionTecla.DispararCue, d.Accion);
        Assert.Equal(13, d.CueId);
    }

    [Fact]
    public void ElBuffer_VaDESPUESDeLosHotkeys()
    {
        // Un cue cuya hotkey sea un digito dispara, y ese digito nunca llega
        // al buffer. Es el orden de WinCue, verificado en su codigo.
        var hotkeys = new Dictionary<int, int> { [Vk1] = 5 };
        var d = DespachadorTeclado.Resolver(Vk1, false, false, false, Normal, hotkeys);
        Assert.Equal(AccionTecla.DispararCue, d.Accion);
        Assert.Equal(5, d.CueId);
    }

    [Fact]
    public void SinHotkeyEnEseDigito_AlimentaElBuffer()
    {
        var d = DespachadorTeclado.Resolver(Vk1, false, false, false, Normal, SinHotkeys);
        Assert.Equal(AccionTecla.BufferDigito, d.Accion);
        Assert.Equal('1', d.Caracter);
    }

    [Fact]
    public void ConBufferVacio_RetrocesoYEnterNoSonDelBuffer()
    {
        Assert.NotEqual(AccionTecla.BufferBorrar,
            DespachadorTeclado.Resolver(VkBack, false, false, false, Normal, SinHotkeys).Accion);
        Assert.NotEqual(AccionTecla.BufferSaltar,
            DespachadorTeclado.Resolver(VkEnter, false, false, false, Normal, SinHotkeys).Accion);
    }

    [Fact]
    public void ConDigitosEnElBuffer_EnterSalta()
    {
        var estado = Normal with { HayDigitosEnBuffer = true, FocoEnTablaDeCues = true };
        Assert.Equal(AccionTecla.BufferSaltar,
            DespachadorTeclado.Resolver(VkEnter, false, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void EnterEnLaTabla_SinBuffer_LlevaElPlayheadALaSeleccion()
    {
        var estado = Normal with { FocoEnTablaDeCues = true };
        Assert.Equal(AccionTecla.PlayheadASeleccion,
            DespachadorTeclado.Resolver(VkEnter, false, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void EnterFueraDeLaTabla_NO_EsDelDespachador()
    {
        // De esto depende que Tab+Enter siga activando botones, que es la
        // concesion de accesibilidad que paga que Espacio ya no active el
        // control con foco.
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkEnter, false, false, false, Normal, SinHotkeys).Accion);
    }

    [Fact]
    public void EnModoShow_LasDeEdicionNoHacenNada()
    {
        var estado = Normal with { ModoShow = true };
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(VkDelete, false, false, false, estado, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.Nada,
            DespachadorTeclado.Resolver(0x43, ctrl: true, false, false, estado, SinHotkeys).Accion);  // Ctrl+C
    }

    [Fact]
    public void EnModoShow_ElTransporteSIFunciona()
    {
        var estado = Normal with { ModoShow = true };
        Assert.Equal(AccionTecla.Go,
            DespachadorTeclado.Resolver(VkSpace, false, false, false, estado, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.Blackout,
            DespachadorTeclado.Resolver(VkB, false, false, false, estado, SinHotkeys).Accion);
    }

    [Fact]
    public void CtrlZ_NoDisparaUnCueCuyaHotkeySeaZ()
    {
        var hotkeys = new Dictionary<int, int> { [VkZ] = 3 };
        var d = DespachadorTeclado.Resolver(VkZ, ctrl: true, false, false, Normal, hotkeys);
        Assert.Equal(AccionTecla.Deshacer, d.Accion);
    }

    [Fact]
    public void LasCuatroFlechas_MuevenElPlayhead()
    {
        Assert.Equal(AccionTecla.PlayheadAnterior,
            DespachadorTeclado.Resolver(VkUp, false, false, false, Normal, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.PlayheadAnterior,
            DespachadorTeclado.Resolver(VkLeft, false, false, false, Normal, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.PlayheadSiguiente,
            DespachadorTeclado.Resolver(VkDown, false, false, false, Normal, SinHotkeys).Accion);
        Assert.Equal(AccionTecla.PlayheadSiguiente,
            DespachadorTeclado.Resolver(VkRight, false, false, false, Normal, SinHotkeys).Accion);
    }
}
```

- [ ] **Step 2: Verlas fallar**

```powershell
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet test tests\WinCue.Presentation.Tests\WinCue.Presentation.Tests.csproj -c Release --filter "FullyQualifiedName~DespachadorTecladoTests"
```

Esperado: **no compila** — `DespachadorTeclado` no existe. Es el fallo correcto
en C#; no busques un rojo de aserción.

- [ ] **Step 3: Escribir `DespachadorTeclado`**

Con el orden de arriba, tal cual, y una cabecera que explique **por qué existe
este fichero**: porque la decisión de qué hace cada tecla es lo único de todo
el teclado que se puede probar sin una ventana, sin GPU y sin que nadie pulse
nada — y en esta máquina resultó que pulsar no se puede.

Documenta dentro las tres reglas que no son obvias y que se leyeron del código
de WinCue: `B` antes de los hotkeys, el buffer después de los hotkeys, y que
los hotkeys solo se miran **sin modificadores**.

- [ ] **Step 4: Verlas pasar**

```powershell
dotnet test tests\WinCue.Presentation.Tests\WinCue.Presentation.Tests.csproj -c Release --filter "FullyQualifiedName~DespachadorTecladoTests"
```

Esperado: **15 en verde**.

- [ ] **Step 5: Suite entera y commit**

```powershell
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
```

`MpvPropertyTests.FadeVolume_ReachesTarget` puede salir rojo por temporización:
es el flake conocido y pasa en solitario.

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/WinCue.Presentation/DespachadorTeclado.cs \
        WinCueSharp/tests/WinCue.Presentation.Tests/DespachadorTecladoTests.cs
git commit -m "feat(presentation): la decision de que hace cada tecla, pura y con pruebas"
```

---

### Task 4: «Ir a cue» tecleando su número

**Files:**
- Modify: `src/Secuence.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `_controller.OnSetPlayhead(int row)`, `_model.Cues[i].DisplayNumber`
  (string), `FlashStatus(string)`, `Icons.Bar`.
- Produces: un `bool TratarBufferIrACue(KeyRoutedEventArgs e)` privado, que
  devuelve `true` si consumió la tecla.

**Lo que hay que copiar del comportamiento de WinCue, leído en
`WinCue.App/MainWindow.xaml.cs:2204-2260`:**

- Se acumulan **dígitos** (fila superior y teclado numérico) y el **punto** —
  hace falta porque los cues se numeran `"1.1"`.
- El buffer **caduca a los 4 s** sin teclear.
- `Retroceso` borra el último carácter, y `Enter` salta — pero **solo si el
  buffer no está vacío**; si lo está, los dos caen al siguiente que los quiera
  (y `Enter` vacío es «playhead a la selección», de la tarea 2).
- Al saltar, se casa el texto contra **`DisplayNumber`**, no contra la posición:
  el operador teclea el número que ve. **No uses `OnJumpToCue`**, que salta por
  índice.
- Si no hay ningún cue con ese número, se dice en la barra de estado y no se
  mueve nada.

**Dónde va en el orden:** **después** de los hotkeys por cue, igual que WinCue.
O sea que un cue cuya hotkey sea un dígito dispara, y ese dígito nunca llega al
buffer. Es deliberado y hay que escribirlo en el comentario.

**`Escape` NO forma parte del buffer**: sigue siendo STOP ALL siempre. Un
control de seguridad no puede quedarse sin su tecla porque alguien tecleara un
número hace tres segundos.

- [ ] **Step 1: Escribir `TratarBufferIrACue`**

El esqueleto, con las decisiones ya tomadas dentro:

```csharp
    private string _bufferIrACue = "";
    private long _bufferIrACueTicks;

    /// <summary>«Ir a cue» al estilo QLab: se teclea el NÚMERO del cue y Enter.
    /// Devuelve true si consumió la tecla.
    ///
    /// Va DESPUÉS de los hotkeys por cue, igual que WinCue: un cue cuya hotkey
    /// sea un dígito dispara, y ese dígito nunca llega aquí. Deliberado.
    ///
    /// Y Escape NO entra: sigue siendo STOP ALL siempre. Un control de
    /// seguridad no se queda sin su tecla porque alguien tecleara un número
    /// hace tres segundos; para eso está la caducidad.</summary>
    private bool TratarBufferIrACue(KeyRoutedEventArgs e)
    {
        if (_bufferIrACue.Length > 0 &&
            Stopwatch.GetElapsedTime(_bufferIrACueTicks).TotalSeconds > 4)
            _bufferIrACue = "";

        char? ch = e.Key switch
        {
            >= VirtualKey.Number0 and <= VirtualKey.Number9
                => (char)('0' + (e.Key - VirtualKey.Number0)),
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9
                => (char)('0' + (e.Key - VirtualKey.NumberPad0)),
            (VirtualKey)190 or (VirtualKey)110 => '.',   // OemPeriod / Decimal
            _ => null,
        };

        if (ch is char c)
        {
            _bufferIrACue += c;
            _bufferIrACueTicks = Stopwatch.GetTimestamp();
            FlashStatus(Loc.T($"Ir a cue: {_bufferIrACue}{Icons.Bar}  (Enter salta · Retroceso borra)",
                              $"Go to cue: {_bufferIrACue}{Icons.Bar}  (Enter jumps · Backspace deletes)"));
            e.Handled = true;
            return true;
        }

        // Con el buffer vacío, Retroceso y Enter son de quien venga detrás
        // (Enter vacío = «playhead a la selección», ver la tarea 2).
        if (_bufferIrACue.Length == 0)
            return false;

        switch (e.Key)
        {
            case VirtualKey.Back:
                _bufferIrACue = _bufferIrACue[..^1];
                FlashStatus(_bufferIrACue.Length > 0
                    ? Loc.T($"Ir a cue: {_bufferIrACue}{Icons.Bar}",
                            $"Go to cue: {_bufferIrACue}{Icons.Bar}")
                    : "");
                e.Handled = true;
                return true;

            case VirtualKey.Enter:
                string objetivo = _bufferIrACue;
                _bufferIrACue = "";
                int fila = -1;
                for (int i = 0; i < _model.Count; i++)
                    if (_model.Cues[i].DisplayNumber == objetivo)
                    {
                        fila = i;
                        break;
                    }
                if (fila >= 0)
                {
                    _controller.OnSetPlayhead(fila);
                    FlashStatus(Loc.T($"Playhead → cue {objetivo}", $"Playhead → cue {objetivo}"));
                }
                else
                {
                    FlashStatus(Loc.T($"No hay ningún cue con número «{objetivo}»",
                                      $"There is no cue numbered «{objetivo}»"));
                }
                e.Handled = true;
                return true;
        }
        return false;
    }
```

**Comprueba los códigos de `VirtualKey` del punto** antes de darlos por buenos:
están puestos por número porque `VirtualKey` no trae constantes con nombre para
ellos, y un número equivocado no falla al compilar — se queda mudo, que es la
misma familia de fallo que el índice de vtbl que ya nos costó una vuelta.

- [ ] **Step 2: Enchufarlo al despachador**

Llamarlo **después** de los hotkeys por cue. Si devuelve `true`, la tecla está
consumida.

- [ ] **Step 3: Compilar y pasar la suite**

```powershell
Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
```

- [ ] **Step 4: Medir**

| Caso | Esperado |
|---|---|
| Teclear `5` `.` `1` + `Enter` | playhead al cue **5.1**; la barra lo dice |
| Teclear `9` `9` + `Enter` | «No hay ningún cue con número 99»; playhead quieto |
| Teclear `2`, esperar 5 s, teclear `1` + `Enter` | va al cue **1**, no al 21 |
| Teclear `1`, `Retroceso`, `Enter` | buffer vacío → `Enter` lleva el playhead a la selección |
| `Escape` con `1` en el buffer | **STOP ALL**, no cancelar el buffer |

Control negativo: quita la caducidad de 4 s, comprueba que el tercer caso se va
al cue 21, y revierte.

- [ ] **Step 5: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/Secuence.App/MainWindow.xaml.cs
git commit -m "feat(secuence): ir a cue tecleando su numero"
```

---

### Task 5: Los atajos que faltan, y los aceleradores al despachador

**Files:**
- Modify: `src/Secuence.App/MainWindow.xaml.cs` (despachador, `AddAccelerator`)
- Modify: `src/Secuence.App/MainWindow.xaml` (quitar los cuatro
  `KeyboardAccelerator`)

**Interfaces:**
- Consumes: las acciones que **ya existen** en el menú contextual
  (`BuildCueMenu`, `:1262-1356`) y en el menú de salidas (`:1651`), más
  `_controller.Undo()`, `_controller.Redo()`, `_controller.OnStopSelected(int)`,
  `_controller.OnPauseToggle()`, `_controller.OnPauseCueToggle(int)`.
- Produces: nada nuevo hacia fuera.

**Las teclas, por capa** (el orden completo está en el spec):

| Capa | Teclas |
|---|---|
| Con `Ctrl` | `O`, `S`, `Shift+S`, `G` (los cuatro que hoy son aceleradores) + `P`, `C`, `V`, `D`, `Z`, `Y` |
| Transporte | `Espacio`, `Escape`, `S`, flechas, `P` |
| Seguridad | `B` |
| Hotkeys por cue | (ya está) |
| Ir a cue | (tarea 3) |

**Dos cosas que no se pueden hacer mal:**

1. **`B` va por encima de los hotkeys por cue.** No es una preferencia: hoy, un
   show con un cue cuya hotkey sea `B` **hace blackout en WinCue y saca ese cue
   a sala en Secuence**, con el mismo archivo. WinCue lo tiene así con el
   comentario literal «control de seguridad: gana a los hotkeys».
2. **Las de edición respetan el candado de modo SHOW** (`Supr`, `F2`,
   `Ctrl+C/V/D`), igual que WinCue: durante la función no se añade, ni se
   inserta, ni se borra, ni se importa. Reutiliza el candado que ya usa el menú
   contextual; no inventes otro.

**Por qué se traen los aceleradores:** los cuatro `KeyboardAccelerator` de XAML
**no miran el contador de diálogos**, así que hoy `Ctrl+O` y `Ctrl+S` siguen
llegando con Ajustes abierto. Es el único cabo que quedó fuera del modelo nuevo.

**`Ctrl+F` NO entra**: en Secuence no existe buscar cues. No se cablea lo que no
hay.

- [ ] **Step 1: Añadir las teclas al despachador**, en las capas de la tabla.

La forma, con las dos trampas ya resueltas dentro:

```csharp
        // ── Con Ctrl ────────────────────────────────────────────────────
        // Van ANTES que todo lo demás para que ninguna tecla suelta se las
        // coma. Y aquí, no como KeyboardAccelerator del XAML: los
        // aceleradores NO miran el contador de diálogos, y por eso hasta hoy
        // Ctrl+O y Ctrl+S seguían llegando con Ajustes abierto.
        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.Z: _controller.Undo(); e.Handled = true; return;
                case VirtualKey.Y: _controller.Redo(); e.Handled = true; return;
                // … C, V, D, P y los cuatro de archivo, cada uno a la MISMA
                // acción que ya invoca su entrada del menú contextual: no
                // dupliques la lógica, llama a lo que el menú llama.
            }
        }

        // ── Transporte ──────────────────────────────────────────────────
        // Por ENCIMA de los hotkeys por cue, a diferencia de WinCue. El
        // porqué, en el spec: si un show puede asignar la flecha abajo como
        // hotkey, le roba al operador la navegación del playhead, que es el
        // gesto principal de esta tanda.
        switch (e.Key)
        {
            case VirtualKey.S: _controller.OnStopSelected(CueList.SelectedIndex); e.Handled = true; return;
            case VirtualKey.P: _controller.OnPauseToggle(); e.Handled = true; return;
            // … Espacio, Escape y las flechas, ya puestos en tareas anteriores
        }

        // ── Seguridad ───────────────────────────────────────────────────
        // B gana a los hotkeys por cue, igual que en WinCue («control de
        // seguridad: gana a los hotkeys»). No es preferencia: sin esto, un
        // show con un cue de hotkey B hace blackout en WinCue y saca ese cue
        // A SALA en Secuence, con el mismo archivo.
        if (e.Key == VirtualKey.B)
        {
            AlternarBlackout();
            e.Handled = true;
            return;
        }
```

Las de edición (`Supr`, `F2`, `Ctrl+C/V/D`) van detrás del **candado de modo
SHOW que ya usa el menú contextual**. Reutilízalo; no inventes otro.

- [ ] **Step 2: Quitar los cuatro `KeyboardAccelerator`** del XAML y de
`AddAccelerator` (`:246-251`), ya cubiertos por la capa de `Ctrl`.

- [ ] **Step 3: Compilar y pasar la suite**

```powershell
Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
pwsh -File scripts\verificar-paleta.ps1
```

- [ ] **Step 4: Medir cada tecla**

Una fila por tecla: `S`, `B`, `P`, `Supr`, `F2`, `Ctrl+P/C/V/D/Z/Y`, y los
cuatro de archivo. Y **los controles de no-regresión, que aquí son la mitad del
trabajo** porque cada uno ya ha mordido una vez:

| Control | Esperado |
|---|---|
| `Escape` con Ajustes abierto | cierra el diálogo, **no** para el show |
| `Ctrl+O`/`Ctrl+S` con Ajustes abierto | **no llegan** (esto es lo que arregla la tarea) |
| `Ctrl+S` sin diálogo | guarda: el asterisco de modificado desaparece |
| `Tab` + `Enter` sobre un botón | activa el botón |
| `Espacio` sobre un combo del **inspector** | abre el desplegable |
| `Espacio` sobre el selector del **PGM** | da GO |
| `Supr` en modo SHOW | **no borra** |
| Cue con hotkey `B` + pulsar `B` | **blackout**, no dispara el cue |

La última es la que arregla el conflicto entre productos: prepárate un show de
prueba con un cue cuya hotkey sea `B`.

Control negativo: pon `B` **después** de los hotkeys, comprueba que el cue se
dispara en vez del blackout, y revierte.

- [ ] **Step 5: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/Secuence.App/MainWindow.xaml.cs WinCueSharp/src/Secuence.App/MainWindow.xaml
git commit -m "feat(secuence): los 17 atajos que faltaban, y el menu deja de mentir"
```

---

### Task 6: Avisar cuando un show usa teclas de transporte como hotkey

**Files:**
- Modify: `src/Secuence.App/MainWindow.xaml.cs` (donde se reconstruye el mapa de
  hotkeys al cargar un show)

**Interfaces:**
- Consumes: el mapa de hotkeys por cue que ya se construye al cargar;
  `FlashStatus(string)`.
- Produces: nada nuevo.

**El porqué:** esta tanda pone el transporte **por encima** de los hotkeys por
cue, a diferencia de WinCue, donde las flechas y `P` van por debajo. La razón
está en el spec: si un show puede asignar `↓` como hotkey, le roba al operador
la navegación del playhead. El precio es que **ese cue no se podrá disparar con
el teclado en Secuence**, y sí en WinCue.

Ese precio es aceptable; que se descubra en una función, no. Al cargar un show,
si algún cue tiene como hotkey una tecla de transporte (`Espacio`, `Escape`, las
cuatro flechas, `S`, `P`, `B`), **se avisa en la barra de estado** diciendo qué
cues son y que esa tecla la manda el transporte.

- [ ] **Step 1: Detectar y avisar**

Donde se reconstruye el mapa de hotkeys al cargar un show, después de tenerlo:

```csharp
    /// <summary>Teclas que manda el TRANSPORTE, y que por tanto un hotkey por
    /// cue no puede robar en Secuence (a diferencia de WinCue, donde las
    /// flechas y P van por debajo de los hotkeys — ver el spec). El precio de
    /// esa decisión es que un cue con una de estas teclas no se dispara con el
    /// teclado; que ese precio se pague en una FUNCIÓN, en cambio, no es
    /// aceptable: por eso se avisa al cargar.</summary>
    private static readonly VirtualKey[] TeclasDeTransporte =
    {
        VirtualKey.Space, VirtualKey.Escape,
        VirtualKey.Up, VirtualKey.Down, VirtualKey.Left, VirtualKey.Right,
        VirtualKey.S, VirtualKey.P, VirtualKey.B,
    };

    private void AvisarHotkeysPisadas()
    {
        var pisados = new List<string>();
        foreach (var (tecla, cueId) in _hotkeysPorCue)
            if (Array.IndexOf(TeclasDeTransporte, tecla) >= 0)
            {
                int fila = _model.IndexOfId(cueId);
                if (fila >= 0)
                    pisados.Add(_model.Cues[fila].DisplayNumber);
            }

        if (pisados.Count == 0)
            return;

        string lista = string.Join(", ", pisados);
        FlashStatus(Loc.T(
            $"Aviso: los cues {lista} tienen como atajo una tecla de transporte; el transporte manda y no se dispararán con el teclado.",
            $"Warning: cues {lista} use a transport key as their hotkey; transport wins, so they will not fire from the keyboard."));
    }
```

Ajusta el nombre de `_hotkeysPorCue` al que ya use el fichero — no crees un
segundo mapa.

- [ ] **Step 2: Compilar y pasar la suite**

```powershell
Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --filter "Requiere!=libmpv"
```

- [ ] **Step 3: Medir**

Carga el show con el cue de hotkey `B` de la tarea 4: la barra de estado tiene
que nombrarlo al cargar. Carga el show de prueba normal: **ni un aviso**.

- [ ] **Step 4: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/src/Secuence.App/MainWindow.xaml.cs
git commit -m "fix(secuence): avisar si un show usa teclas de transporte como hotkey"
```

---

### Task 7: Documentación al día

**Files:**
- Modify: `docs/superpowers/TRASPASO-SECUENCE.md`
- Modify: `docs/superpowers/ARRANQUE-SESION-LOCAL.md`

- [ ] **Step 1: Poner al día el traspaso**

- En «Lo que Secuence hace ya»: el playhead móvil, los 17 atajos, ir a cue.
- **La divergencia con WinCue**, con su porqué y su precio: selección ≠
  playhead, y el transporte por encima de los hotkeys.
- La lista de lo que **sigue** faltando de esta zona: `Ctrl+F` (no hay buscar) y
  los atajos configurables (`keyboard_go/stop/prev`).
- Y que **quedan cuatro tandas** de las cinco en que se decompuso la auditoría.

- [ ] **Step 2: La matriz de verificación, al arranque local**

`ARRANQUE-SESION-LOCAL.md` tiene que quedarse con **la matriz de teclado
completa, escrita y reutilizable**: cada tecla contra cada foco. Sin pruebas
automáticas, esa matriz es lo único que evita repetir el trabajo de razonarla
cada vez que alguien toque el despachador. Es el entregable más valioso de esta
tarea.

- [ ] **Step 3: Commit**

```bash
cd /c/Proyectos/ffmpeg-gui
git add WinCueSharp/docs/superpowers/TRASPASO-SECUENCE.md \
        WinCueSharp/docs/superpowers/ARRANQUE-SESION-LOCAL.md
git commit -m "docs(secuence): estado al dia tras la navegacion y el teclado"
```

---

## Dónde se puede parar

- **Tras la 3** — la decision de que hace cada tecla ya esta cubierta por pruebas en el CI.
- **Tras la 2** — el operador ya puede elegir el siguiente cue. Es el 80 % del
  valor de la tanda; todo lo demás es comodidad.
- **Tras la 5** — el menú contextual deja de mentir y el conflicto de `B` entre
  productos desaparece.
- **Tras la 6** — nadie se lleva una sorpresa en una función.

## Riesgos

1. **Traer los aceleradores al despachador toca teclas que hoy funcionan.**
   `Ctrl+S` guardando es de las que más duele romper. Por eso tiene su propio
   control de no-regresión.
2. **`Enter` resuelto por foco** es lo más fácil de romper sin darse cuenta:
   cualquier cambio futuro en quién tiene el foco cambia lo que hace la tecla.
3. **El CABLEADO sigue sin pruebas automáticas** (la decisión ya no). La matriz de la tarea 6 es la mitigación, y por
   eso no es documentación de adorno.

**Desconfía del plan antes que del ejecutor.** En este proyecto los defectos han
estado en el plan mucho más que en la ejecución — y mientras escribía este,
comprobar el código en vez de fiarme de la memoria corrigió **cuatro**
afirmaciones mías: el orden de `B`, dónde va el buffer, que las flechas de
WinCue están por debajo de los hotkeys, y que el salto casa por `DisplayNumber`
y no por índice.
