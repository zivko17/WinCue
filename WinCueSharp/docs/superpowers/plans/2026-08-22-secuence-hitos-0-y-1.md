# Secuence — Hitos 0 y 1: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Averiguar si WinUI 3 es viable en este repo (hito 0) y sacar de
`WinCue.App` el código compartible que Secuence necesitará el día uno, sin
tocar el comportamiento de la beta (hito 1).

**Architecture:** Hito 0 no escribe producto: crea un proyecto WinUI 3
desechable, mide tres cosas y escribe un informe. Hito 1 crea dos proyectos
`net8.0` nuevos (`WinCue.Presentation`, `WinCue.Remote`) y mueve código a
ellos, dejando en WPF solo lo que es de WPF. Cada movimiento va protegido por
pruebas de regresión que fijan los valores actuales.

**Tech Stack:** .NET 8 (SDK 8.0.423 clavado en `global.json`), C#, xunit
2.5.3, WPF (producto 1), WinUI 3 / Windows App SDK + Win2D (producto 2, solo
sondeo en el hito 0), PowerShell 7 para publicación.

**Spec:** `docs/superpowers/specs/2026-08-22-secuence-winui3-design.md`

## Global Constraints

- **El repo git es `C:/Proyectos/ffmpeg-gui`**, un nivel por encima de
  `WinCueSharp/`. `global.json` y `.github/workflows/ci.yml` viven ahí.
- **`global.json` no se toca.** SDK `8.0.423`, `rollForward: latestFeature`,
  `allowPrerelease: false`.
- **El motor sigue `net8.0` puro.** `WinCue.Core`, `.Audio`, `.Controller` y
  `.Video` no aprenden qué es WPF ni WinUI. Los proyectos nuevos de este plan
  (`WinCue.Presentation`, `WinCue.Remote`) también son `net8.0` a secas.
- **El CI compila con `-p:TreatWarningsAsErrors=true` y hoy está a cero.**
  Cualquier proyecto que entre en `WinCueSharp.sln` tiene que mantenerlo a
  cero. `Directory.Build.props` fija `AnalysisLevel=latest-all` y
  `EnforceCodeStyleInBuild=true`.
- **Comando de pruebas del repo:**
  `dotnet test WinCueSharp/WinCueSharp.sln -c Release --filter "Requiere!=libmpv"`
- **Idioma:** comentarios, nombres de prueba y mensajes de commit en
  castellano, siguiendo el estilo de la casa (cabecera de bloque `// ===` con
  el porqué, nombres de prueba tipo `FadeOutMasLargoQueElCue_Avisa`).
- **Nada de este plan puede cambiar el comportamiento observable de WinCue.**
  Es refactor puro sobre un producto que está vendiéndose.
- **Grafía "Secuence"** — deliberada, no es una errata. No la corrijas.

---

# HITO 0 — Pruebas de humo técnicas

No se escribe producto. Se contesta a tres preguntas y se escribe un informe.
Todo lo que se cree aquí es **desechable y va fuera de `src/`**, en
`tools/winui3-lab/`, igual que ya existen `tools/mpv-d3d11-lab/` y
`tools/hap-lab/`.

**Este hito no entra en `WinCueSharp.sln`.** Un proyecto WinUI 3 en la
solución pondría el CI en rojo antes de saber cuánto ruido mete, que es
justamente lo que venimos a medir.

---

### Task 1: Proyecto WinUI 3 desechable y cuenta de analizadores

**Files:**
- Create: `tools/winui3-lab/WinUi3Lab.csproj`
- Create: `tools/winui3-lab/App.xaml`
- Create: `tools/winui3-lab/App.xaml.cs`
- Create: `tools/winui3-lab/MainWindow.xaml`
- Create: `tools/winui3-lab/MainWindow.xaml.cs`
- Create: `tools/winui3-lab/INFORME.md`

**Interfaces:**
- Consumes: nada.
- Produces: `tools/winui3-lab/INFORME.md`, cuyas secciones rellenan las tareas
  2 y 3. Ninguna tarea posterior consume código de aquí.

- [ ] **Step 1: Crear el proyecto**

`tools/winui3-lab/WinUi3Lab.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- ==========================================================================
       WinUi3Lab — banco de pruebas DESECHABLE del hito 0 de Secuence.
       No entra en WinCueSharp.sln a propósito: mide cuánto ruido de
       analizadores mete WinUI 3 ANTES de arriesgar el CI, que hoy está a cero.
       Cuando el hito 0 cierre, esta carpeta se borra o se congela con su
       INFORME.md, como tools/mpv-d3d11-lab.
       ========================================================================== -->

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>WinUi3Lab</RootNamespace>
    <UseWinUI>true</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Platforms>x64</Platforms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.*" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.*" />
  </ItemGroup>

</Project>
```

`tools/winui3-lab/App.xaml`:

```xml
<Application
    x:Class="WinUi3Lab.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

`tools/winui3-lab/App.xaml.cs`:

```csharp
// =============================================================================
// App.xaml.cs — arranque del banco de pruebas del hito 0.
// =============================================================================

using Microsoft.UI.Xaml;

namespace WinUi3Lab;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
```

`tools/winui3-lab/MainWindow.xaml`:

```xml
<Window
    x:Class="WinUi3Lab.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <StackPanel Padding="24" Spacing="12">
        <TextBlock x:Name="Estado" Text="WinUi3Lab" FontSize="20" />
        <Button x:Name="BotonVideo" Content="Probar mpv en HWND hijo" />
    </StackPanel>
</Window>
```

`tools/winui3-lab/MainWindow.xaml.cs`:

```csharp
// =============================================================================
// MainWindow.xaml.cs — ventana del banco de pruebas. La tarea 3 le cuelga el
// HWND hijo con mpv; en la tarea 1 solo tiene que abrirse.
// =============================================================================

using Microsoft.UI.Xaml;

namespace WinUi3Lab;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
```

- [ ] **Step 2: Comprobar que compila y arranca**

```bash
dotnet build tools/winui3-lab/WinUi3Lab.csproj -c Release
```

Esperado: build correcta. Si falla por versión del Windows App SDK, bajar a
la última 1.5.x y **anotar en `INFORME.md` que 1.6 no compila con el SDK
8.0.423**, que es un hallazgo del hito, no un obstáculo a esquivar en
silencio.

Arrancar a mano y confirmar que se ve la ventana:

```bash
dotnet run --project tools/winui3-lab/WinUi3Lab.csproj -c Release
```

- [ ] **Step 3: Medir el ruido de analizadores — la pregunta 2 del hito**

```bash
dotnet build tools/winui3-lab/WinUi3Lab.csproj -c Release -p:TreatWarningsAsErrors=true 2>&1 | grep -E "error (CA|IDE|CS)" | sed -E 's/.*(error [A-Z]+[0-9]+).*/\1/' | sort | uniq -c | sort -rn
```

Esperado: una lista de reglas con su cuenta. **Copiarla entera al informe.**
No arreglar nada todavía: el objetivo es el número.

- [ ] **Step 4: Escribir el informe con el veredicto de analizadores**

`tools/winui3-lab/INFORME.md`, siguiendo el estilo de
`tools/mpv-d3d11-lab/INFORME.md`:

```markdown
# Hito 0 de Secuence — informe del banco de pruebas WinUI 3

*Escrito <fecha> sobre <commit>. Ver el diseño en
`docs/superpowers/specs/2026-08-22-secuence-winui3-design.md`.*

## Pregunta 2 — ¿cuánto ruido de analizadores mete WinUI 3?

Versión del Windows App SDK que compiló: <versión>
SDK de .NET usado: <salida de `dotnet --version`>

Reglas que saltan con `TreatWarningsAsErrors=true`:

| Regla | Cuenta | ¿Código generado? | Decisión |
|---|---|---|---|
| <p.ej. CA1515> | <n> | sí / no | relajar / arreglar |

**Total: <n> errores de <m> reglas distintas.**

**Veredicto:** <si son pocas y todas de código generado, se relajan por
proyecto con su motivo al lado y el listón del repo no se toca. Si son muchas
y caen en código escrito a mano, hay que hablarlo antes de seguir: bajar el
listón que hoy protege todo el repo es un precio que no estaba en el diseño.>

## Pregunta 1 — forma del paquete

(tarea 2)

## Pregunta 3 — mpv en un HWND hijo

(tarea 3)
```

- [ ] **Step 5: Commit**

```bash
git add WinCueSharp/tools/winui3-lab/
git commit -m "hito 0: banco de pruebas WinUI 3 y cuenta de analizadores"
```

---

### Task 2: Forma del paquete publicado

Contesta la pregunta 1 del hito 0. Recuerda el matiz del spec: **el paquete de
WinCue ya es una carpeta de 24 archivos**, así que esto decide acabado, no
viabilidad.

**Files:**
- Modify: `tools/winui3-lab/INFORME.md` (sección "Pregunta 1")

**Interfaces:**
- Consumes: `tools/winui3-lab/WinUi3Lab.csproj` de la tarea 1.
- Produces: nada de código. Un veredicto escrito que la sección de
  distribución del spec necesita.

- [ ] **Step 1: Intentar la publicación single-file**

```bash
dotnet publish tools/winui3-lab/WinUi3Lab.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o tools/winui3-lab/pub-single
```

Anotar **literalmente** qué pasa: si falla, el mensaje exacto; si "funciona",
si de verdad produce un exe suelto o deja nativas fuera igualmente.

- [ ] **Step 2: Publicar sin single-file, como referencia**

```bash
dotnet publish tools/winui3-lab/WinUi3Lab.csproj -c Release -r win-x64 --self-contained true -o tools/winui3-lab/pub-carpeta
```

- [ ] **Step 3: Contar y medir las dos salidas**

```bash
for d in pub-single pub-carpeta; do echo -n "$d: "; find "tools/winui3-lab/$d" -type f 2>/dev/null | wc -l; du -sh "tools/winui3-lab/$d" 2>/dev/null; done
```

Referencia para comparar: el paquete actual de WinCue son **24 archivos y
324 MB**.

- [ ] **Step 4: Comprobar que el publicado arranca en limpio**

Ejecutar el `.exe` de `pub-carpeta` con doble clic. Tiene que abrir la ventana
sin .NET instalado en la ruta y **sin pedir el Windows App SDK**: eso es lo
que verifica de verdad `WindowsAppSDKSelfContained` + `WindowsPackageType=None`.

Prueba adicional que importa más que el recuento de archivos, porque es la que
decide si `settings.json` puede seguir viviendo junto al ejecutable:

```bash
echo '{"prueba":1}' > tools/winui3-lab/pub-carpeta/settings.json
```

Confirmar que el proceso puede leer y **reescribir** ese archivo desde su
propia carpeta.

- [ ] **Step 5: Escribir el veredicto y limpiar**

Rellenar la sección "Pregunta 1" del informe con la tabla de recuentos, el
mensaje de error del single-file si lo hubo, y el veredicto sobre
`settings.json`. Después borrar las carpetas publicadas, que no se versionan:

```bash
rm -rf tools/winui3-lab/pub-single tools/winui3-lab/pub-carpeta
```

- [ ] **Step 6: Commit**

```bash
git add WinCueSharp/tools/winui3-lab/INFORME.md
git commit -m "hito 0: forma del paquete de un WinUI 3 desempaquetado"
```

---

### Task 3: mpv en un HWND hijo de un AppWindow

La pregunta que de verdad puede parar el proyecto: si mpv no pinta ahí, la
ruta 1 del spec no existe y Secuence dependería de la Fase 2b para tener
vídeo.

**Files:**
- Create: `tools/winui3-lab/HwndHijo.cs`
- Modify: `tools/winui3-lab/MainWindow.xaml.cs`
- Modify: `tools/winui3-lab/INFORME.md` (sección "Pregunta 3")

**Interfaces:**
- Consumes: `MainWindow` de la tarea 1.
- Produces: `WinUi3Lab.HwndHijo.Crear(nint padre, int x, int y, int w, int h)
  → nint`. Es el patrón que la tarea equivalente del hito 2 reutilizará; no lo
  consume ninguna tarea de este plan.

- [ ] **Step 1: Escribir el creador de ventana hija**

`tools/winui3-lab/HwndHijo.cs`. Es el mismo patrón que
`src/WinCue.App/SwapChainHost.cs:40`, sin la parte de `HwndHost` (que es de
WPF y en WinUI 3 no existe):

```csharp
// =============================================================================
// HwndHijo.cs — ventana hija Win32 bajo el HWND de un AppWindow de WinUI 3.
// =============================================================================
// En WPF esto lo daba HwndHost. WinUI 3 no tiene equivalente, así que la
// ventana hija se crea a mano — que es lo que HwndHost hacía por dentro
// (ver src/WinCue.App/SwapChainHost.cs).
//
// Sobre esta ventana se le pasa el --wid a mpv. Igual que en WPF, una ventana
// hija Win32 se pinta SIEMPRE encima del contenido XAML: el airspace sigue
// existiendo por esta ruta. Solo desaparece con SwapChainPanel (Fase 2b).
// =============================================================================

using System.Runtime.InteropServices;

namespace WinUi3Lab;

internal static class HwndHijo
{
    private const int WsChild = unchecked((int)0x40000000);
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int exStyle, string clase, string? titulo, int estilo,
        int x, int y, int ancho, int alto,
        nint padre, nint menu, nint instancia, nint param);

    /// <summary>Crea la ventana hija y devuelve su HWND. 0 = fallo.</summary>
    internal static nint Crear(nint padre, int x, int y, int ancho, int alto)
    {
        nint h = CreateWindowExW(
            0, "static", null,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            x, y, ancho, alto, padre, 0, 0, 0);

        if (h == 0)
            throw new InvalidOperationException(
                $"CreateWindowExW falló: {Marshal.GetLastWin32Error()}");

        return h;
    }
}
```

- [ ] **Step 2: Colgarlo del botón y lanzar mpv**

En `MainWindow.xaml.cs`, sustituir el cuerpo por:

```csharp
// =============================================================================
// MainWindow.xaml.cs — ventana del banco de pruebas.
// El botón crea un HWND hijo y le pasa el --wid a mpv.exe. Se usa el EJECUTABLE
// y no libmpv a propósito: lo que se prueba aquí es que un HWND hijo de un
// AppWindow acepta vídeo, no el P/Invoke, que ya está probado en WinCue.Video.
// =============================================================================

using System.Diagnostics;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WinUi3Lab;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        BotonVideo.Click += ProbarVideo;
    }

    private void ProbarVideo(object sender, RoutedEventArgs e)
    {
        nint padre = WindowNative.GetWindowHandle(this);
        nint hijo = HwndHijo.Crear(padre, 0, 80, 640, 360);

        string media = Environment.GetEnvironmentVariable("LAB_MEDIA")
            ?? throw new InvalidOperationException("Define LAB_MEDIA con la ruta a un vídeo.");

        var p = Process.Start(new ProcessStartInfo("mpv.exe",
            $"--wid={hijo} --loop --no-terminal \"{media}\"")
        { UseShellExecute = false });

        Estado.Text = p is null
            ? "mpv.exe no arrancó — ¿está en el PATH?"
            : $"mpv en HWND {hijo}. Padre {padre}.";
    }
}
```

- [ ] **Step 3: Ejecutar y mirar la pantalla**

```bash
LAB_MEDIA=tests/show-prueba/video-A-colores.mp4 dotnet run --project tools/winui3-lab/WinUi3Lab.csproj -c Release
```

**Ojo en clon limpio:** la media de prueba **no está versionada** (lo dice el
propio `ci.yml`). Si `tests/show-prueba/` está vacío, vale cualquier vídeo del
disco — lo que se prueba es el HWND, no el archivo.

Pulsar el botón. Esperado: **se ve el vídeo dentro de la ventana**.

Anotar además, porque es lo que el spec identifica como riesgo número 1:
- ¿Tapa el vídeo al `TextBlock` de arriba? (airspace: debería, y es lo esperado)
- Arrastrar la ventana entre dos pantallas con **DPI distinto**: ¿se
  redimensiona el hijo o se queda descolocado?

- [ ] **Step 4: Escribir el veredicto**

Rellenar la sección "Pregunta 3" del informe: si se vio vídeo, qué pasó con el
airspace y qué pasó al cambiar de DPI. **Si no se vio vídeo, esto es un
hallazgo bloqueante y hay que pararse aquí y hablarlo**, no seguir al hito 1.

- [ ] **Step 5: Commit**

```bash
git add WinCueSharp/tools/winui3-lab/
git commit -m "hito 0: mpv en un HWND hijo de un AppWindow de WinUI 3"
```

---

### Task 4: THIRD-PARTY-NOTICES de Secuence y cierre del hito

**Files:**
- Create: `docs/legal/notices-secuence.md`
- Modify: `tools/winui3-lab/INFORME.md` (resumen final)

**Interfaces:**
- Consumes: la lista de paquetes que resultó compilar en la tarea 1.
- Produces: `docs/legal/notices-secuence.md`, que el `publish-secuence.ps1` del
  hito 2 copiará al paquete.

- [ ] **Step 1: Listar lo que Secuence añade sobre WinCue**

```bash
dotnet list tools/winui3-lab/WinUi3Lab.csproj package --include-transitive
```

- [ ] **Step 2: Escribir el fichero de avisos**

`docs/legal/notices-secuence.md`, con el mismo formato que
`THIRD-PARTY-NOTICES.md` (procedencia + licencia + oferta de fuente donde
aplique). Como mínimo:

- `Microsoft.WindowsAppSDK` — MIT
- `Microsoft.Windows.SDK.BuildTools` — comprobar la licencia real en el
  paquete, **no darla por MIT sin mirarla**
- `Microsoft.Graphics.Win2D` — MIT (aún no referenciado; entra en el hito 3)

Y una nota al principio dejando claro que este fichero es el **superconjunto**:
Secuence distribuye además todo lo de `THIRD-PARTY-NOTICES.md`, porque lleva
las mismas nativas (libmpv LGPL, FFmpeg LGPLv3).

- [ ] **Step 3: Escribir el resumen del hito 0**

Al principio del `INFORME.md`, una sección de cinco líneas: las tres preguntas
con su respuesta de una frase, y **una recomendación explícita de seguir o
parar**.

- [ ] **Step 4: Commit**

```bash
git add WinCueSharp/docs/legal/notices-secuence.md WinCueSharp/tools/winui3-lab/INFORME.md
git commit -m "hito 0: avisos de terceros de Secuence y cierre del informe"
```

---

> ## ⛔ Puerta del hito 0
>
> **Parar aquí y enseñar el informe a Andrés antes de empezar el hito 1.**
>
> El hito 1 toca el producto que se está vendiendo. No se toca si el hito 0
> dijo que hay que replantear algo.

---

# HITO 1 — Sacar de la UI lo que no es UI

## Corrección de alcance respecto al spec

Al leer los archivos uno a uno, tres cosas del spec no se sostienen:

| Spec decía | Realidad | Qué se hace |
|---|---|---|
| `LocExtension` aporta 53 líneas de cadenas | `Loc` **ya vive en `src/WinCue.Core/Loc.cs`**. `LocExtension` es solo el `MarkupExtension` de WPF | **Nada.** Ya está compartido |
| `RemoteInputService` → `WinCue.Controller` | Usa `NAudio.Midi`, y **Controller no referencia NAudio** (solo `WinCue.Core`). Meter un paquete de dispositivos en la capa de los 377 tests deterministas es un error | Proyecto nuevo **`WinCue.Remote`** |
| `CueRowViewModel` (283) se comparte | 28 usos de tipos WPF; expone `Brush` para binding | **Se aplaza al hito 2.** Secuence va a rediseñar la tabla de cues; compartirlo ahora es adivinar |
| `StressRunner` (145) → `tools/` | Usa `DispatcherTimer`. No lo necesita nadie hasta que Secuence tenga UI que estresar | **Se aplaza.** YAGNI |

Alcance real del hito 1: **~450 líneas a `WinCue.Presentation`**, 270 a
`WinCue.Remote`, y el guardián de licencias a un script compartido.

---

### Task 5: Proyecto WinCue.Presentation con UiMetrics dentro

`UiMetrics` va primero porque es el único que se mueve **tal cual**: 16
`const double`, cero tipos de WPF. Sirve para levantar el proyecto y su
proyecto de pruebas con un movimiento que no puede salir mal.

**Files:**
- Create: `src/WinCue.Presentation/WinCue.Presentation.csproj`
- Create: `src/WinCue.Presentation/UiMetrics.cs` (movido)
- Delete: `src/WinCue.App/UiMetrics.cs`
- Create: `tests/WinCue.Presentation.Tests/WinCue.Presentation.Tests.csproj`
- Create: `tests/WinCue.Presentation.Tests/UiMetricsTests.cs`
- Modify: `src/WinCue.App/WinCue.App.csproj` (añadir `ProjectReference`)
- Modify: `WinCueSharp.sln`

**Interfaces:**
- Consumes: nada.
- Produces: `namespace WinCue.Presentation` con
  `public static class UiMetrics` y sus 16 `public const double`
  (`FaderWidth`, `FaderHeight`, `TrimBarHeight`, `StripTitleLineHeight`,
  `FaderScaleWidth`, `EqCanvasHeight`, `StripExtrasHeight`, `DbReadoutWidth`,
  `DbReadoutWideWidth`, …). Las tareas 6 y 7 añaden clases a este mismo
  proyecto.

- [ ] **Step 1: Escribir la prueba que falla**

`tests/WinCue.Presentation.Tests/UiMetricsTests.cs`:

```csharp
// =============================================================================
// UiMetricsTests.cs — red del traslado de UiMetrics a la capa compartida.
// =============================================================================
// No prueban lógica: no la hay. Fijan los VALORES que la interfaz de WinCue
// tiene hoy, para que el traslado no cambie ni un píxel. Si alguien afina una
// medida más adelante, tendrá que tocar aquí a propósito — que es el punto.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class UiMetricsTests
{
    [Fact]
    public void LosFadersMidenLoMismoQueAntesDelTraslado()
    {
        Assert.Equal(26, UiMetrics.FaderWidth);
        Assert.Equal(170, UiMetrics.FaderHeight);
        Assert.Equal(30, UiMetrics.FaderScaleWidth);
    }

    [Fact]
    public void LasMedidasFijasSiguenSiendoLasDeLaAuditoriaDeInterfaz()
    {
        Assert.Equal(72, UiMetrics.TrimBarHeight);
        Assert.Equal(18, UiMetrics.StripTitleLineHeight);
        Assert.Equal(270, UiMetrics.EqCanvasHeight);
        Assert.Equal(16, UiMetrics.StripExtrasHeight);
    }

    [Fact]
    public void LaLecturaEnDbConservaSuAnchoFijo()
    {
        // Fijos a propósito: el texto cambia de ancho al usarlo («+0,0 dB» →
        // «+12,0 dB») y sin ancho reservado se meneaba media columna.
        Assert.Equal(74, UiMetrics.DbReadoutWidth);
        Assert.Equal(124, UiMetrics.DbReadoutWideWidth);
    }
}
```

- [ ] **Step 2: Ejecutar la prueba para verla fallar**

```bash
dotnet test tests/WinCue.Presentation.Tests -c Release
```

Esperado: FALLA — el proyecto no existe todavía.

- [ ] **Step 3: Crear los dos proyectos**

`src/WinCue.Presentation/WinCue.Presentation.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- ==========================================================================
       WinCue.Presentation — lo que la interfaz necesita y NO es de ningún
       framework de interfaz: medidas, valores de color, glifos de icono.
       net8.0 a secas, como el motor: lo consumen WinCue.App (WPF) y, cuando
       exista, Secuence.App (WinUI 3). Si algo de aquí necesita un tipo de WPF
       o de WinUI, es que no es de aquí.
       ========================================================================== -->

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\WinCue.Core\WinCue.Core.csproj" />
  </ItemGroup>

</Project>
```

`tests/WinCue.Presentation.Tests/WinCue.Presentation.Tests.csproj` — copiar
`tests/WinCue.Core.Tests/WinCue.Core.Tests.csproj` y cambiar solo la
`ProjectReference` para que apunte a `..\..\src\WinCue.Presentation\WinCue.Presentation.csproj`.
Mantener las mismas versiones de paquete: xunit 2.5.3,
Microsoft.NET.Test.Sdk 17.8.0, xunit.runner.visualstudio 2.5.3,
coverlet.collector 6.0.0.

- [ ] **Step 4: Mover UiMetrics.cs**

```bash
git mv src/WinCue.App/UiMetrics.cs src/WinCue.Presentation/UiMetrics.cs
```

Cambiar **solo** la línea del namespace:

```csharp
namespace WinCue.Presentation;
```

No tocar ni un comentario ni un valor. El `git mv` deja el historial intacto,
que importa en un archivo cuyos comentarios explican decisiones de diseño.

- [ ] **Step 5: Referenciar desde WinCue.App y arreglar los usos**

En `src/WinCue.App/WinCue.App.csproj`, añadir dentro del `ItemGroup` de
proyectos:

```xml
<ProjectReference Include="..\WinCue.Presentation\WinCue.Presentation.csproj" />
```

Compilar y añadir `using WinCue.Presentation;` a los archivos que se quejen:

```bash
dotnet build WinCueSharp.sln -c Release 2>&1 | grep -E "error CS0103|error CS0246" | sort -u
```

- [ ] **Step 6: Meter los dos proyectos en la solución**

```bash
dotnet sln WinCueSharp.sln add src/WinCue.Presentation/WinCue.Presentation.csproj tests/WinCue.Presentation.Tests/WinCue.Presentation.Tests.csproj
```

- [ ] **Step 7: Ejecutar todo y verlo verde**

```bash
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --no-build --filter "Requiere!=libmpv"
```

Esperado: build a cero warnings y **todo verde**. La suite crece en 3 pruebas.
No fijes un número absoluto como criterio: 694 es la cuenta de atributos
`[Fact]`/`[Theory]`, y el runner informa de los casos de `[Theory]` expandidos
(unos 753). Lo que se comprueba es *ninguna roja* y *tres más que antes*.

- [ ] **Step 8: Arrancar WinCue y mirarla**

```bash
dotnet run --project src/WinCue.App -c Release -- --smoke
```

Esperado: abre y se cierra sola a los 3 s sin excepción. Después, abrirla a
mano sin `--smoke`, cargar un show de `tests/show-prueba/` y confirmar que
**los faders y las gráficas se ven igual que antes**. Es un cambio de medidas:
si algo se movió, se ve.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor: UiMetrics sale a WinCue.Presentation, la capa que compartiran los dos productos"
```

---

### Task 6: Palette — los valores de color salen de Theme

`Theme.cs` tiene **94 colores** declarados como
`public static readonly Color Go = Hex(0x2ecc71);`. El `Color` es
`System.Windows.Media.Color`, o sea WPF. Lo que se comparte es el **número**;
el `Color`, los `Brush` congelados, `Veil()` y `Install()` se quedan donde
están.

Después de esta tarea `Theme` sigue exponiendo exactamente la misma API — nadie
que la use se entera. Eso es lo que la hace segura.

**Files:**
- Create: `src/WinCue.Presentation/Palette.cs`
- Modify: `src/WinCue.App/Theme.cs`
- Create: `tests/WinCue.Presentation.Tests/PaletteTests.cs`

**Interfaces:**
- Consumes: `WinCue.Presentation` de la tarea 5.
- Produces: `public static class Palette` en `namespace WinCue.Presentation`,
  con un `public const uint <Nombre> = 0xRRGGBB;` por cada uno de los 94
  colores, con los mismos nombres que `Theme`. Los derivados de `Theme`
  (`TypeFade = Warn`) se mantienen como alias en `Palette`, no se duplica el
  número.

- [ ] **Step 1: Escribir la prueba que falla**

`tests/WinCue.Presentation.Tests/PaletteTests.cs`:

```csharp
// =============================================================================
// PaletteTests.cs — red del traslado de los valores de color.
// =============================================================================
// Theme.cs avisa en su cabecera: «este fichero preserva los valores EXACTOS
// que había, sin cambiar un píxel». El traslado a Palette tiene que preservar
// esa promesa, y un hex mal tecleado no lo ve nadie hasta que un cue sale del
// color equivocado en un escenario. Estas pruebas son ese ojo.
// =============================================================================

using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class PaletteTests
{
    [Fact]
    public void LosColoresDeTransporteSonLosDeSiempre()
    {
        Assert.Equal(0x2ecc71u, Palette.Go);
        Assert.Equal(0x1e7e34u, Palette.GoButton);
        Assert.Equal(0x27865au, Palette.GoDeep);
        Assert.Equal(0xc0392bu, Palette.Pgm);
        Assert.Equal(0x7e1e1eu, Palette.PanicButton);
    }

    [Fact]
    public void LosAvisosYElOroSonLosDeSiempre()
    {
        Assert.Equal(0xe8c000u, Palette.Standby);
        Assert.Equal(0xe8a200u, Palette.Warn);
        Assert.Equal(0xe87a00u, Palette.WarnDeep);
        Assert.Equal(0xc9a227u, Palette.Gold);
        Assert.Equal(0xc9a84au, Palette.GoldSoft);
        Assert.Equal(0xff2020u, Palette.Clip);
        Assert.Equal(0xff8a7au, Palette.Danger);
        Assert.Equal(0x2a6fb0u, Palette.Blue);
    }

    [Fact]
    public void LosColoresPorTipoDeCueMantienenSusAlias()
    {
        Assert.Equal(0x4a86b8u, Palette.TypeAudio);
        Assert.Equal(0x9b59b6u, Palette.TypeVideo);
        Assert.Equal(0x888888u, Palette.TypeWait);

        // Derivados, no duplicados: si alguien cambia Warn, TypeFade cambia.
        Assert.Equal(Palette.Warn, Palette.TypeFade);
        Assert.Equal(Palette.Go, Palette.TypeStart);
        Assert.Equal(Palette.Pgm, Palette.TypeStop);
    }

    [Fact]
    public void EstanLos94ColoresQueTeniaTheme()
    {
        // Cuenta exacta. Si el traslado se dejó uno por el camino, el
        // compilador no dice nada — pero WinCue pinta un gris donde no toca.
        int n = typeof(Palette)
            .GetFields(System.Reflection.BindingFlags.Public |
                       System.Reflection.BindingFlags.Static)
            .Count(f => f.IsLiteral && f.FieldType == typeof(uint));

        Assert.Equal(94, n);
    }
}
```

- [ ] **Step 2: Ejecutar la prueba para verla fallar**

```bash
dotnet test tests/WinCue.Presentation.Tests -c Release --filter "FullyQualifiedName~PaletteTests"
```

Esperado: FALLA con `CS0246: no se encuentra el tipo 'Palette'`.

- [ ] **Step 3: Escribir Palette.cs**

`src/WinCue.Presentation/Palette.cs`. Trasladar los **94** valores desde
`src/WinCue.App/Theme.cs`, **conservando el orden, los grupos y los
comentarios** (incluidas las marcas «≈» de los grises casi duplicados, que
según la cabecera de `Theme` están ahí para que colapsarlos sea una decisión
consciente).

La transformación es mecánica y **solo tiene dos formas**. No inventes una
tercera:

| En `Theme.cs` | En `Palette.cs` |
|---|---|
| `public static readonly Color Go = Hex(0x2ecc71);` | `public const uint Go = 0x2ecc71;` |
| `public static readonly Color TypeFade = Warn;` | `public const uint TypeFade = Warn;` |

Es decir: literal → literal, alias → alias. **Ningún alias se resuelve a su
número** — si `TypeFade = Warn` se copiara como `0xe8a200`, cambiar `Warn` en
el futuro dejaría de arrastrar a `TypeFade`, que es justo el tipo de deriva
que `Theme` nació para impedir. La cuarta prueba de la tarea lo vigila.

Cabecera:

```csharp
// =============================================================================
// Palette.cs — los VALORES de la paleta de WinCue, sin framework.
// =============================================================================
// Salieron de Theme.cs cuando apareció el segundo producto: el número
// 0x2ecc71 es el mismo en WPF y en WinUI 3, pero System.Windows.Media.Color
// solo existe en WPF. Aquí vive el número; cada interfaz lo convierte en lo
// que sepa pintar (Theme.cs en WPF).
//
// REGLA: aquí NO entra ningún tipo de interfaz. Ni Color, ni Brush, ni nada
// que venga de un framework. Si hace falta uno, el sitio es Theme.
//
// Se preservan los valores EXACTOS y los grises casi duplicados marcados «≈»,
// por el mismo motivo que los preservaba Theme: colapsarlos debe ser una
// decisión consciente y no el efecto colateral de una mudanza.
// =============================================================================

namespace WinCue.Presentation;

/// <summary>Valores de color de WinCue, en 0xRRGGBB.</summary>
public static class Palette
{
    // ── Transporte ───────────────────────────────────────────────────
    /// <summary>Verde de GO.</summary>
    public const uint Go = 0x2ecc71;
    ...
}
```

- [ ] **Step 4: Reescribir Theme.cs para que derive de Palette**

En `src/WinCue.App/Theme.cs`, cada declaración pasa de literal a derivada:

```csharp
// Antes:  public static readonly Color Go = Hex(0x2ecc71);
// Ahora:
public static readonly Color Go = Hex(Palette.Go);
```

**Lo que NO cambia**, y es la clave de que esto sea seguro: los nombres, el
tipo (`Color`), `Hex`, `Alpha`, `Solid`, `Veil`, `CueTypeBrushes`,
`BrushForCueType` e `Install`. `Install()` publica los recursos reflexionando
sobre los campos públicos estáticos de `Theme`, y esos campos siguen ahí
exactamente igual — así que el XAML con `{DynamicResource Go}` no se entera de
nada.

Añadir arriba `using WinCue.Presentation;` y actualizar la cabecera de
`Theme.cs` para que diga de dónde vienen ahora los números.

- [ ] **Step 5: Ejecutar las pruebas**

```bash
dotnet test tests/WinCue.Presentation.Tests -c Release --filter "FullyQualifiedName~PaletteTests"
```

Esperado: PASAN las 4.

- [ ] **Step 6: Compilar y pasar la suite entera**

```bash
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --no-build --filter "Requiere!=libmpv"
```

- [ ] **Step 7: Mirarla en pantalla — esto es color, se ve**

```bash
dotnet run --project src/WinCue.App -c Release
```

Cargar un show de `tests/show-prueba/` con cues de varios tipos y comprobar:
el verde de GO, el rojo de PANIC, el ámbar de standby, y **los colores por
tipo de cue en la tabla y en el timeline** — que según la cabecera de `Theme`
ya se desincronizaron una vez en el pasado, y son justo lo que un traslado de
94 valores puede volver a romper.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor: los 94 valores de color salen a Palette; Theme sigue dando la misma API"
```

---

### Task 7: Icons — los glifos salen; la fuente compuesta se queda

`Icons.cs` son **59 `const string`** con puntos de código de Phosphor —datos
puros— más un `FontFamily` que apunta a la fuente compuesta de WPF y un
comprobador de carga. Se parte por ahí.

**Files:**
- Create: `src/WinCue.Presentation/Icons.cs`
- Create: `src/WinCue.App/IconFont.cs`
- Delete: `src/WinCue.App/Icons.cs`
- Create: `tests/WinCue.Presentation.Tests/IconsTests.cs`
- Modify: los archivos de `src/WinCue.App` que usen `Icons.Font`

**Interfaces:**
- Consumes: `WinCue.Presentation` de la tarea 5.
- Produces:
  - `WinCue.Presentation.Icons` — los 59 `public const string`, el diccionario
    `ByName` por reflexión y `public static string Get(string name)`, que
    **lanza `ArgumentException`** si el nombre no existe (contrato actual: un
    icono mal escrito en XAML debe cantar al abrir la ventana).
  - `WinCue.App.IconFont` — `public static readonly FontFamily Font` y el
    comprobador de carga que hoy vive en `Icons`.

- [ ] **Step 1: Escribir la prueba que falla**

`tests/WinCue.Presentation.Tests/IconsTests.cs`. Las pruebas son
**estructurales a propósito**: los glifos son caracteres del área de uso
privado y transcribirlos a mano en una prueba es justo el error que se busca
evitar.

```csharp
// =============================================================================
// IconsTests.cs — red del traslado del catálogo de iconos.
// =============================================================================
// No se comparan glifos literales: son caracteres del PUA y teclearlos en la
// prueba reproduciría el error que se quiere cazar. Se comprueban PROPIEDADES
// que un traslado chapucero rompe — que estén los 59, que ninguno se haya
// quedado vacío, que no haya dos con el mismo glifo (copiar-pegar) y que Get()
// alcance a todos.
// =============================================================================

using System.Reflection;
using WinCue.Presentation;

namespace WinCue.Presentation.Tests;

public class IconsTests
{
    private static Dictionary<string, string> Todos() =>
        typeof(Icons)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!,
                          StringComparer.Ordinal);

    [Fact]
    public void EstanLos59IconosQueTeniaLaInterfazDeWpf()
    {
        Assert.Equal(59, Todos().Count);
    }

    [Fact]
    public void NingunIconoSeQuedoVacioEnLaMudanza()
    {
        foreach (var (nombre, glifo) in Todos())
            Assert.False(string.IsNullOrEmpty(glifo), $"El icono «{nombre}» se quedó vacío.");
    }

    [Fact]
    public void NoHayDosIconosConElMismoGlifo()
    {
        // Un copiar-pegar mal rematado deja dos nombres apuntando al mismo
        // dibujo, y eso no lo ve nadie hasta que un botón sale con el icono
        // de otro.
        var repetidos = Todos()
            .GroupBy(p => p.Value)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" = ", g.Select(p => p.Key)))
            .ToList();

        Assert.True(repetidos.Count == 0,
            "Iconos que comparten glifo: " + string.Join("; ", repetidos));
    }

    [Fact]
    public void GetAlcanzaATodosLosIconosPorNombre()
    {
        foreach (var (nombre, glifo) in Todos())
            Assert.Equal(glifo, Icons.Get(nombre));
    }

    [Fact]
    public void GetCantaSiElNombreNoExiste()
    {
        // Contrato heredado: un icono mal escrito en XAML debe fallar al abrir
        // la ventana, no salir en blanco.
        var ex = Assert.Throws<ArgumentException>(() => Icons.Get("NoExisteEsteIcono"));
        Assert.Contains("NoExisteEsteIcono", ex.Message, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Ejecutar la prueba para verla fallar**

```bash
dotnet test tests/WinCue.Presentation.Tests -c Release --filter "FullyQualifiedName~IconsTests"
```

Esperado: FALLA con `CS0246: no se encuentra el tipo 'Icons'`.

- [ ] **Step 3: Mover el archivo y quitarle la parte de WPF**

```bash
git mv src/WinCue.App/Icons.cs src/WinCue.Presentation/Icons.cs
```

En el archivo movido:
- `namespace WinCue.Presentation;`
- Quitar `using System.Windows;` y `using System.Windows.Media;`
- **Quitar** el campo `Font` y el comprobador de carga de la fuente.
- **Dejar** los 59 `const string` intactos, el diccionario `ByName` (la
  reflexión es sobre `typeof(Icons)`, que ahora es la clase de aquí, así que
  sigue funcionando sin cambios) y `Get`.
- Actualizar la cabecera: la explicación de por qué es una fuente y no un
  control **se queda** (sigue siendo el porqué del diseño), pero la parte de
  la fuente compuesta y el `pack://` se muda al archivo nuevo con un puntero
  cruzado.

- [ ] **Step 4: Escribir IconFont.cs en la app de WPF**

`src/WinCue.App/IconFont.cs`:

```csharp
// =============================================================================
// IconFont.cs — la FUENTE con la que WPF dibuja los glifos de WinCue.Presentation.
// =============================================================================
// Los nombres y los puntos de código viven en WinCue.Presentation.Icons, que
// no sabe de frameworks. Esto es la mitad que sí: el FontFamily de WPF.
//
// OJO — LA FUENTE NO SE APLICA SUELTA. Phosphor mapea también 'a'..'z', ' ' y
// '-' (los usa como entrada de sus ligaduras), así que ponerla como FontFamily
// de un control convertiría toda la minúscula de la interfaz en iconos. Por eso
// apunta a la fuente COMPUESTA WinCue.CompositeFont, que enruta por rango: el
// PUA a Phosphor y el resto a Segoe UI.
// =============================================================================

using System.Windows.Media;

namespace WinCue.App;

/// <summary>La fuente compuesta con la que se pintan los iconos en WPF.</summary>
public static class IconFont
{
    /// <summary>Tipografía de los glifos. Se aplica como fuente por defecto en
    /// App.OnStartup y a mano en los FormattedText que pintan iconos.</summary>
    public static readonly FontFamily Font =
        new(new Uri("pack://application:,,,/"), "./assets/#WinCue UI");
}
```

Mover ahí también, **sin cambiarle una línea**, el comprobador de carga que
hoy vive en `Icons` y que llama `App.OnStartup`.

- [ ] **Step 5: Arreglar los usos en WinCue.App**

```bash
grep -rn "Icons\.Font\|Icons\.Comprobar" src/WinCue.App --include=*.cs --include=*.xaml
```

Cambiar cada `Icons.Font` por `IconFont.Font`. Los `Icons.Open`,
`Icons.Get(...)` y `{local:Loc Icon=…}` **no se tocan**: siguen resolviendo,
ahora contra la clase de `WinCue.Presentation` (hace falta
`using WinCue.Presentation;` en los `.cs`, y en XAML comprobar que el
`xmlns:local` sigue alcanzando `Icons` — si no, añadir un `xmlns` al nuevo
namespace).

- [ ] **Step 6: Ejecutar las pruebas**

```bash
dotnet test tests/WinCue.Presentation.Tests -c Release --filter "FullyQualifiedName~IconsTests"
```

Esperado: PASAN las 5.

- [ ] **Step 7: Compilar y pasar la suite entera**

```bash
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --no-build --filter "Requiere!=libmpv"
```

- [ ] **Step 8: Mirarla en pantalla**

```bash
dotnet run --project src/WinCue.App -c Release
```

Los iconos están en 185 sitios. Recorrer la barra superior, las pestañas del
inspector y las 5 pestañas de Settings: **ni un cuadrito vacío ni una letra
donde debería haber un dibujo**. Un cuadrito significa que la fuente no cargó;
una letra suelta, que la fuente compuesta se aplicó mal.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor: los 59 glifos salen a WinCue.Presentation; la fuente compuesta se queda en WPF"
```

---

### Task 8: WinCue.Remote — el disparo por OSC y MIDI sale de la capa de UI

`RemoteInputService` escucha sockets OSC y dispositivos MIDI y remite al
`MainController`. No tiene un solo tipo de WPF, así que **no es interfaz**: que
viva en `WinCue.App` es un accidente que Secuence heredaría copiándolo.

Va a un proyecto propio y no a `WinCue.Controller` porque necesita
`NAudio.Midi`, y Controller —los 377 tests deterministas con motores falsos y
reloj virtual— no debe aprender qué es un dispositivo.

**Files:**
- Create: `src/WinCue.Remote/WinCue.Remote.csproj`
- Create: `src/WinCue.Remote/RemoteInputService.cs` (movido)
- Delete: `src/WinCue.App/RemoteInputService.cs`
- Modify: `src/WinCue.App/WinCue.App.csproj`
- Modify: `src/WinCue.App/MainWindow.xaml.cs` (el `using`)
- Modify: `WinCueSharp.sln`

**Interfaces:**
- Consumes: `WinCue.Controller` y `WinCue.Core` (ya referenciados por el
  archivo hoy).
- Produces: `WinCue.Remote.RemoteInputService`, con la **misma superficie
  pública que hoy**: `IDisposable`, el método `Apply()` recargable en caliente
  y el evento `Status`. Ni un miembro cambia de nombre ni de firma.

- [ ] **Step 1: Anotar la superficie pública actual, para no cambiarla**

```bash
grep -n "public \|internal " src/WinCue.App/RemoteInputService.cs
```

Copiar esa lista al mensaje del commit final. Es un traslado, no un rediseño:
si algo de esa lista cambia, se ha ido de alcance.

- [ ] **Step 2: Comprobar que no hay pruebas que se rompan**

```bash
grep -rn "RemoteInputService" tests/ --include=*.cs
```

Esperado: **ninguna**. El ruteo sí está probado
(`tests/WinCue.Controller.Tests/RemoteControlTests.cs`), pero contra
`MainController`, no contra este servicio — que es exactamente por qué el
traslado es seguro y por qué la parte de escucha merece salir aparte.

Si aparece alguna referencia, **parar y replantear la tarea**: dejaría de ser
un traslado.

- [ ] **Step 3: Crear el proyecto**

`src/WinCue.Remote/WinCue.Remote.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- ==========================================================================
       WinCue.Remote — disparo remoto: escucha OSC (UDP) y MIDI IN y remite al
       controlador. Vive aparte por dos motivos:

       · No es interfaz. Estaba en WinCue.App por accidente y copiarlo al
         segundo producto habría sido la primera grieta entre los dos.
       · No puede ir en WinCue.Controller. Necesita NAudio.Midi, y Controller
         es la capa de los 377 tests deterministas con motores falsos y reloj
         virtual: meterle un paquete de dispositivos la ensucia.
       ========================================================================== -->

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.3.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\WinCue.Core\WinCue.Core.csproj" />
    <ProjectReference Include="..\WinCue.Controller\WinCue.Controller.csproj" />
  </ItemGroup>

</Project>
```

La versión de NAudio es **2.3.0**, la misma que `WinCue.Audio`. Dos versiones
distintas del mismo paquete en una solución es una tarde perdida.

- [ ] **Step 4: Mover el archivo**

```bash
git mv src/WinCue.App/RemoteInputService.cs src/WinCue.Remote/RemoteInputService.cs
```

Cambiar **solo** el namespace:

```csharp
namespace WinCue.Remote;
```

El comentario de cabecera dice *"remite cada mensaje al hilo de UI"*.
Comprobar cómo lo hace: si es por evento, no hay nada que tocar. **Si usa
`Dispatcher` de WPF, parar** — eso sería un tipo de WPF que el grep no vio y
la tarea cambia de forma.

- [ ] **Step 5: Referenciar desde WinCue.App**

En `src/WinCue.App/WinCue.App.csproj`:

```xml
<ProjectReference Include="..\WinCue.Remote\WinCue.Remote.csproj" />
```

Y `using WinCue.Remote;` donde se cree el servicio (`MainWindow.xaml.cs`).

- [ ] **Step 6: Meterlo en la solución y compilar**

```bash
dotnet sln WinCueSharp.sln add src/WinCue.Remote/WinCue.Remote.csproj
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test WinCueSharp.sln -c Release --no-build --filter "Requiere!=libmpv"
```

- [ ] **Step 7: Probarlo con hardware de verdad — aquí no vale el smoke**

Esto no lo cubre ninguna prueba automática. Con WinCue abierta:

1. Settings → Red/Control: configurar OSC y usar el **test de ping OSC** que ya
   trae la ventana. Debe responder.
2. Enchufar un controlador MIDI, usar **MIDI Learn** sobre un cue y confirmar
   que el botón lo dispara.
3. Cambiar el puerto OSC en Settings y **guardar con la app abierta**:
   `Apply()` es recargable en caliente y tiene que coger la configuración
   nueva sin reiniciar.
4. Provocar un error a propósito (puerto OSC ya ocupado por otra cosa) y
   confirmar que **sale en la barra de estado y no tira la app** — el contrato
   de la cabecera es *"nada de aquí puede tirar el show"*.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor: el disparo por OSC y MIDI sale a WinCue.Remote"
```

---

### Task 9: El guardián de licencias sale a un script compartido

La regla 4 del spec. Hoy el guardián vive dentro de `publish.ps1`
(líneas ~116-140) y `publish-secuence.ps1` va a nacer de mirarlo. Si alguien
recorta al copiar, el fallo no es un bug: es distribuir software cerrado con
DLL GPL.

**Files:**
- Create: `scripts/verificar-licencias.ps1`
- Modify: `publish.ps1`

**Interfaces:**
- Consumes: `tools/verificar-ffmpeg/verificar-ffmpeg.csproj` (el verificador,
  ya existe).
- Produces: `scripts/verificar-licencias.ps1`, con parámetros
  `-CarpetaFfmpegBin <ruta>` y `-RaizRepo <ruta>`. **Lanza** si el veredicto no
  es LGPL, después de borrar las DLL. `publish-secuence.ps1` (hito 2) lo
  invocará igual.

- [ ] **Step 1: Anotar el comportamiento actual, que es el contrato**

Leer `publish.ps1` líneas 116-140 y escribir los cuatro comportamientos que
hay que conservar **exactamente**:

1. Verifica el **contenido del paquete**, no el árbol de desarrollo.
2. Pasa la carpeta por `$env:WINCUE_FFMPEG_DIR`, **nunca como argumento** — el
   comentario del script explica por qué: `dotnet run` reenvía a la aplicación
   lo que no entiende y el verificador acababa mirando otra carpeta, con lo que
   el guardián podía dar por bueno un paquete sin haberlo mirado.
3. Si `$LASTEXITCODE -ne 0`: **borra** `ffmpeg-lgpl\bin` del paquete y
   **lanza**.
4. Restaura `$env:WINCUE_FFMPEG_DIR = $null` en un `finally`.

- [ ] **Step 2: Escribir el script**

`scripts/verificar-licencias.ps1`:

```powershell
# =============================================================================
# verificar-licencias.ps1 — el guardián que impide publicar una build GPL
# =============================================================================
# Salió de publish.ps1 al aparecer el segundo producto. El motivo es de fondo:
# publish-secuence.ps1 iba a nacer de copiar publish.ps1, y el recorte más
# probable de un copia-pega es justo esta parte, que es la lenta. Un guardián
# que se puede olvidar no es un guardián.
#
# Verifica el CONTENIDO DEL PAQUETE (la carpeta recién copiada), no el árbol de
# desarrollo: es lo que de verdad se le entrega a un cliente.
#
# CONTRATO: si el veredicto no es LGPL, BORRA las DLL del paquete y LANZA. El
# script que lo llama no tiene que comprobar nada; si vuelve, el paquete vale.
# =============================================================================

[CmdletBinding()]
param(
    # Carpeta bin del paquete con las DLL de FFmpeg a verificar.
    [Parameter(Mandatory)][string]$CarpetaFfmpegBin,
    # Raíz de WinCueSharp, para encontrar el verificador.
    [Parameter(Mandatory)][string]$RaizRepo
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $CarpetaFfmpegBin)) {
    Write-Warning ("No hay $CarpetaFfmpegBin: el paquete sale SIN el motor " +
        "ProRes propio. Nada que verificar.")
    return
}

Write-Host "`nVerificando la licencia de las DLL del PAQUETE..." -ForegroundColor Cyan

$verificador = Join-Path $RaizRepo 'tools\verificar-ffmpeg\verificar-ffmpeg.csproj'

# La carpeta va por variable de entorno y NO como argumento: `dotnet run` no
# conoce `--nologo` y reenvía a la aplicación todo lo que no entiende, así que
# el verificador acababa mirando una carpeta equivocada y el guardián podía dar
# por bueno un paquete sin haberlo mirado. Con la variable no hay ambigüedad —
# y FFmpegLoader la trata de forma ESTRICTA: si esa carpeta no tiene avcodec,
# falla en vez de buscar en otro sitio.
$env:WINCUE_FFMPEG_DIR = $CarpetaFfmpegBin
try { & dotnet run --project $verificador -c Release } finally { $env:WINCUE_FFMPEG_DIR = $null }

if ($LASTEXITCODE -ne 0) {
    Remove-Item $CarpetaFfmpegBin -Recurse -Force
    throw ("LICENCIA RECHAZADA: las DLL de FFmpeg del paquete no son LGPL. " +
           "Se han borrado del paquete. NO se puede distribuir software cerrado " +
           "con ellas. Consigue una build 'lgpl-shared' con " +
           "tools\ffmpeg-lgpl\obtener-ffmpeg-lgpl.ps1.")
}

Write-Host "Licencia de FFmpeg verificada: LGPL." -ForegroundColor Green
```

- [ ] **Step 3: Sustituir el bloque en publish.ps1**

Reemplazar el bloque de la línea ~116 a la ~140 por la llamada, conservando el
manejo de `-SinVerificarLicencias`, que se queda en `publish.ps1` porque es una
opción de ese script y no del guardián:

```powershell
# ── SMOKE DE LICENCIAS ───────────────────────────────────────────────────
# El guardián vive en scripts\verificar-licencias.ps1 para que
# publish-secuence.ps1 use EL MISMO y no una copia recortada.
if ($SinVerificarLicencias) {
    Write-Warning "Verificación de licencias SALTADA (-SinVerificarLicencias). NO distribuyas este paquete."
} else {
    & (Join-Path $root 'scripts\verificar-licencias.ps1') `
        -CarpetaFfmpegBin (Join-Path $ffDst 'bin') `
        -RaizRepo $root
}
```

- [ ] **Step 4: Verificar que sigue aprobando un paquete bueno**

```bash
pwsh -File publish.ps1
```

Esperado: `Licencia de FFmpeg verificada: LGPL.` y el paquete completo, con los
mismos **24 archivos** de antes.

- [ ] **Step 5: Verificar que sigue rechazando uno malo — el que importa**

Un guardián que nunca se ha visto rechazar no se sabe si funciona:

```bash
cp publish/ffmpeg-lgpl/bin/avcodec-*.dll /tmp/avcodec-bueno.dll
pwsh -Command "Set-Content -Path (Get-ChildItem publish/ffmpeg-lgpl/bin/avcodec-*.dll)[0].FullName -Value 'no soy una dll' -Encoding Ascii"
pwsh -File publish.ps1
```

Esperado: **lanza**, el mensaje dice `LICENCIA RECHAZADA`, y
`publish/ffmpeg-lgpl/bin` **ya no existe**. Después restaurar con una
publicación limpia.

- [ ] **Step 6: Commit**

```bash
git add scripts/verificar-licencias.ps1 publish.ps1
git commit -m "refactor(publicacion): el guardian de licencias sale a un script que los dos productos comparten"
```

---

> ## ⛔ Puerta del hito 1
>
> Antes de darlo por cerrado, la puerta que fija el spec:
>
> - [ ] `dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true` a cero
> - [ ] Suite entera verde (694 + las nuevas de las tareas 5-7)
> - [ ] CI verde en la rama
> - [ ] **WinCue abre un show real y hace GO**, con OSC y MIDI funcionando
> - [ ] Los colores, los iconos y las medidas se ven igual que antes
> - [ ] `pwsh -File publish.ps1` produce los mismos 24 archivos y el guardián
>       aprueba
>
> Si todo pasa, WinCue ha quedado **mejor** que antes aunque Secuence no se
> escriba nunca: es el hito que se paga solo.
