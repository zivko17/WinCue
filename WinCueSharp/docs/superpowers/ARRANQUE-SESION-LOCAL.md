# Arranque de una sesión LOCAL de Claude Code (Windows)

*Escrito 2026-08-24 desde una sesión remota (contenedor Linux) que NO podía
ejecutar ni ver ninguno de los dos productos. Esta sesión sí: corre en la
máquina de Andrés. Aprovéchalo — es la diferencia entre auditar código y
comprobar de verdad.*

*Revisado el mismo día por la primera sesión local que lo usó. Lo que se
corrigió: la receta de captura de pantalla no es infalible (ver su apartado),
apareció un apartado nuevo sobre manejar la interfaz a distancia, y el repaso
del inspector quedó cerrado. Todo lo marcado como leído en fecha viene de
haberlo pagado, no de suponerlo.*

---

## Lo primero, en una frase

**WinCue** (reproductor de cues para directo estilo QLab, C#/.NET 8, UI en
WPF, **en beta pública y vendiéndose**) tiene un hermano: **Secuence**, mismo
motor, UI en **WinUI 3**, para teatro profesional.

**La grafía «Secuence» es deliberada, no una errata. No la corrijas.**

Contexto largo: `docs/superpowers/TRASPASO-SECUENCE.md`. Léelo entero antes
de tocar nada.

---

## Las seis reglas del proyecto

1. **WinCue no cambia de comportamiento ni de aspecto.** Se está vendiendo.
2. **Prohibido copiar código entre productos.** Lo común sube a
   `WinCue.Presentation` con pruebas.
3. En controles pintados a mano: primero se extrae el CÁLCULO, luego se
   pinta, y WinCue pasa a usarlo.
4. **El JSON del show es sagrado**: todo por `CueModel.ToJson/FromJson`.
   Nunca serialización a mano.
5. `settings.json` vive junto al ejecutable → Secuence va **sin empaquetar**
   (`WindowsPackageType=None`), nunca MSIX.
6. Lo que solo se ve en pantalla va a una sección
   `## PENDIENTE — verificación en pantalla (Andrés)`.

---

## Dónde está el trabajo

Rama **`claude/secuence-hitos-0-1-1occ8p`** (no `secuence/hitos-0-1`, que se
quedó atrás). Raíz git: `C:\Proyectos\ffmpeg-gui` — **no** `WinCueSharp\`.

---

## El encargo vivo

> «Quiero que la interfaz de Secuence sea exactamente igual que la de WinCue
> en cuanto a proporciones de los botones y de todos los componentes. Color y
> textura me da igual.»
>
> «Quiero que las pestañas sean igual que estas, el orden de los elementos;
> después colores y formas ya lo veremos.»
>
> «Vete probando y arreglándolo. Quiero que lo hagas tú.»

**La referencia es `src/WinCue.App/InspectorPanel.cs`.** Cuando dudes de cómo
debe comportarse algo, la respuesta está ahí, no en tu criterio.

---

## Ya hecho en esta rama (no lo repitas)

Las cinco pestañas del inspector con el ORDEN de WinCue:

- **Básicos** — Armado y Flag en la misma fila.
- **Disparadores** — Hotkey → Segundo disparo → la nota, en ese orden.
- **Tiempo y bucles** — sin el fader de volumen (vive en Audio).
- **Audio** — dos columnas: strip de niveles (VOL, lectura en dB, escala +
  fader + VU estéreo, MUTE/CUE) │ GANANCIA/EQ + COMPRESOR. El strip comparte
  el MISMO `MuteSoloBoard` que la mesa, en los dos sentidos.
- **Geometría** — MODO (radios) │ COMPOSITOR + SALIDA │ lienzo. Con
  deslizadores, botones Centrar/Restablecer, opacidad y salida por cue.

Y tres fallos reales arreglados:

1. `Commit` desreferenciaba `_cue` después de invocar `CommitField` sin volver
   a comprobar que siguiera habiendo cue → `NullReferenceException` que tiraba
   la ventana. Llevaba ahí desde que existe el inspector.
2. Los radios de Geometría comiteaban solos: WinUI relanza `Checked` al
   realizar la pestaña y al reagrupar por `GroupName`, y ese eco entraba como
   una pulsación. Ahora solo se commitea un cambio REAL de modo.
3. **Modo SHOW dejaba el inspector entero en gris.** WinCue no hace eso: su
   candado de SHOW solo rechaza AÑADIR / INSERTAR / BORRAR / IMPORTAR (la
   lista). Editar campos con la función montada está permitido a propósito.

### Y de la sesión LOCAL del 2026-08-24 (`6497e4f2`, `7f86bacc`)

- El inspector cuadrado con el de WinCue: los `MinWidth` del tema anulados,
  el rack de audio a cinco columnas, los anchos de la columna DESTINO, la
  barra de trim sin el doble inset.
- `TargetCombo` ya no enseña el objetivo del cue anterior.
- La caída de `Commit` que seguía viva en `MarkersChanged`.
- La cabecera «BARRA DE TRIM» se pintaba **dos veces** — no se veía porque el
  panel de pestañas se quedaba corto y la segunda caía fuera de lo visible.
  Al hacer arrastrable el separador, apareció.
- Los **dos separadores arrastrables**.

**Moraleja que se repite:** subir el separador destapó un fallo que llevaba
ahí desde siempre. Cuando algo deja de estar recortado, vuelve a mirarlo
entero.

---

## Lo que toca ahora

El repaso del inspector **elemento por elemento** está CERRADO
(commits `6497e4f2` y `7f86bacc`, CI en verde):

- [x] Los 55 campos existen en los dos. No falta ninguno.
- [x] `WireTextCommit` idéntico en los dos.
- [x] Muestras de color: equivalentes.
- [x] Desplegables (`Cmb`) con el valor fuera de las opciones: **idénticos**,
      los dos dejan puesta la selección anterior. No divergen.
- [x] `TargetCombo`: **divergía y era un fallo de verdad.** Le faltaba el
      `SelectedIndex = 0` con el que WinCue cae a «(sin objetivo)» — borras el
      cue al que apuntaba un FADE y el desplegable enseñaba el objetivo del
      cue ANTERIOR. Además ancho 230→260 y etiqueta 185→210.
- [x] Barra de trim: doble inset quitado, `MinHeight = 0` puesto, y **la misma
      caída que se arregló en `Commit` seguía viva en `MarkersChanged`** (el
      guardián mira si hay cue ANTES de los dos commits). Sigue **sin onda en
      cues de VÍDEO**: solo decodifica audio, y en un vídeo pone «— no media —».
- [x] Rack de EQ y compresor: **reestructurado a las cinco columnas de
      WinCue**. Era una columna vertical que dejaba ~1000 px de la derecha en
      negro y mandaba el compresor detrás del scroll.
- [x] `FileRow`: caja 220→300 y etiqueta a 210.

**El hallazgo que más lejos llegó** no era del inspector sino del tema: los
`MinWidth` por defecto de `TextBox` (64) y `CheckBox` (120) descuadraban
*todos* los anchos del panel. Ver la lista de trampas del traspaso.

Y la lista de paridad de interfaz que sigue pendiente, fuera del inspector:

- [x] ~~**Separadores arrastrables**~~ — HECHO, los dos (panel izquierdo
  455/200/760 y panel de pestañas 330/140/900), con `PaneSplitter` +
  `SplitterDrag`. El alto del panel se guarda (`bottom_pane_height`) y el
  ancho de la columna no, igual que en WinCue.
- **Cabecera del timeline**: rótulo SHOW TIMELINE, números de pista, botones
  de zoom, Fit, casilla «Crossfade on overlap».
- **Dos columnas de la tabla de cues** que faltan: flag y ✕.
- **Hotkeys por cue**: el campo se guarda y WinCue los respeta, pero
  `RebuildHotkeys` de Secuence solo cubre Space y Esc.
- Cajas de grupo y marcas de GO sin pintar en el timeline (los grupos no se
  pueden agarrar).
- Barra de trim para cues de VÍDEO (arriba).

---

## Cómo compilar y lanzar

```powershell
cd C:\Proyectos\ffmpeg-gui\WinCueSharp
Stop-Process -Name Secuence -Force -ErrorAction SilentlyContinue
dotnet build src\Secuence.App\Secuence.App.csproj -c Debug
```

**El `Stop-Process` no es opcional.** Con Secuence abierto, el build revienta
con doce errores MSB3021/MSB3027 de ficheros bloqueados, que no tienen nada
que ver con el código. Ya pasó una vez y costó un rato.

**Y es de `Secuence`, SOLO de `Secuence`.** No mates `WinCue.App`: compilar
Secuence no lo necesita, y **puede estar abierto porque lo está usando
Andrés**. Matarlo a la brava se lleva por delante su sesión y al volver a
abrir sale el diálogo de recuperación de autoguardado — que además **no debes
contestar tú**, porque «No» tira lo único que queda de lo que hubiera sin
guardar. Pasó el 2026-08-24. Si necesitas WinCue para comparar, ábrelo
aparte; y si ya estaba abierto, mejor aún.

Lanzar:

```powershell
Start-Process (Get-ChildItem "src\Secuence.App\bin" -Recurse -Filter "Secuence.exe" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
```

Los dos productos a la vez, para comparar:

```powershell
dotnet build WinCueSharp.sln -c Release -p:TreatWarningsAsErrors=true
```

---

## Cuando algo se caiga

`logs/crash-*.log`, junto al ejecutable:

```powershell
Get-ChildItem src\Secuence.App\bin -Recurse -Filter "crash-*.log" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content
```

Cuelga de los DOS enganches (`AppDomain.UnhandledException` y
`Application.UnhandledException` de WinUI 3). El segundo es el que ve las
excepciones del hilo de interfaz, que son las que tira esta app.

---

## Comparar las dos ventanas MIRÁNDOLAS

Esto es lo que la sesión remota no podía hacer. Captura la pantalla a PNG y
**léelo con la herramienta Read**, que admite imágenes:

```powershell
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [Drawing.Point]::Empty, $bmp.Size)
$bmp.Save("$env:TEMP\pantalla.png", [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
```

Luego `Read` sobre `%TEMP%\pantalla.png`. Con las dos ventanas lado a lado se
comparan proporciones y orden sin que Andrés tenga que describirlos.

### Si la captura falla — leído el 2026-08-24, costó media hora

`CopyFromScreen` puede reventar con **«Controlador no válido»**. No es que el
shell no tenga escritorio: es que **una pantalla estaba apagada** y la sesión
sin componer. Señal que lo confirma en un segundo: `VirtualScreen` devuelve
`1920x1080` (un solo monitor) en vez de `1920x1680`. Antes de dar por perdida
la captura, comprueba eso y **pídele a Andrés que encienda la segunda
pantalla**; con las dos encendidas funciona a la primera.

```powershell
[System.Windows.Forms.Screen]::AllScreens |
  ForEach-Object { "$($_.DeviceName) $($_.Bounds.Width)x$($_.Bounds.Height)" }
```

**`PrintWindow` NO sirve de recambio.** Es la salida obvia —pedirle a la
ventana que se pinte sola, sin tocar el escritorio— y **devuelve el área de
cliente en NEGRO** en los dos productos: WinUI 3 compone por
DirectComposition y WPF por su propio DirectX, así que solo sale la barra de
título, que la dibuja el DWM. Da `true` y guarda un PNG perfectamente
negro — o sea que **parece que ha funcionado**. No lo persigas.

Si aun así no hay manera, la salida es pedirle a Andrés que ejecute la
captura él con el prefijo `!` en el prompt: la imagen entra en la
conversación y `Read` la puede leer igual.

---

## Manejar la interfaz a distancia (clics y arrastres)

Se puede pilotar Secuence sin manos: sirve para abrir una pestaña concreta y
capturarla, o para probar un arrastre. Tres cosas que hay que saber antes:

**1. `SetCursorPos` mueve el cursor pero NO inyecta eventos de puntero.** Los
clics sueltos funcionan; los **arrastres no**: llega el `PointerPressed`, la
captura se hace, llega *un* `PointerMoved` en el punto del press y ahí se
acaba. El delta sale 0 y **parece que el control está roto cuando no lo
está** — con el separador nuevo se fue un buen rato en bajar por press →
captura → movimiento con marcas de color, y los tres funcionaban. Usa
`mouse_event` **absoluto**:

```
MOUSEEVENTF_MOVE (0x0001) | MOUSEEVENTF_ABSOLUTE (0x8000) | MOUSEEVENTF_VIRTUALDESK (0x4000)
dx = (x - VirtualScreen.X) * 65535 / (VirtualScreen.Width  - 1)
dy = (y - VirtualScreen.Y) * 65535 / (VirtualScreen.Height - 1)
```

**2. `SetForegroundWindow` a secas NO trae la ventana al frente** si el que
llama no es ya el proceso en primer plano. Pasó, y los clics se los comió
**la ventana de WinCue de Andrés** que estaba encima. Hay que
`AttachThreadInput` al hilo de la ventana que tiene el foco, y **comprobar
con `GetForegroundWindow` que el título empieza por «Secuence» antes de
pinchar nada**. Sin esa comprobación estás pinchando a ciegas en el producto
que se está vendiendo.

**3. Las coordenadas cambian entre capturas.** Si arrastras el separador, las
pestañas se mueven. Vuelve a capturar y vuelve a mirar; no reutilices las
posiciones de la captura anterior.

---

## Trampas de WinUI 3 que ya se pagaron

- `ContentDialog` se **ajusta a su contenido** por defecto: por eso el cuadro
  de Ajustes cambiaba de tamaño según la pestaña.
- `Control.VerticalContentAlignment` es **Top** y `HorizontalContentAlignment`
  es **Left**: un `ContentControl` NO estira su contenido.
- `SizeChangedEventArgs` solo trae `NewSize`/`PreviousSize`. No hay
  `WidthChanged` como en WPF → hace falta guardián de re-entrada a mano.
- Un glifo dentro de un `Header` de texto plano sale como cuadradito: la
  fuente es del control entero. Hay que partirlo en dos `TextBlock`
  (ver `IconLabel`).
- `CanvasControl` (Win2D) está **sellada**: se compone, no se hereda.
- `Colors` es **`Microsoft.UI.Colors`**, no el de `Windows.UI`. Son dos tipos
  distintos con el mismo nombre y el segundo no trae las paradas.
- `RadioButton` relanza `Checked` por su cuenta al realizarse y al reagrupar
  por `GroupName`. Un guardián de «estoy cargando» no basta: hay que
  comprobar que el valor cambia de verdad.
- **`Border` está SELLADO**, como `CanvasControl`. Para un control propio,
  hereda de `Grid`: se puede heredar, pinta su `Background` y con él hace
  hit-test. Ojo, no es un `Control`: los eventos de puntero **se enganchan**,
  no se sobrescriben (`OnPointerPressed` es virtual en `Control`, no en
  `UIElement`).
- **Los mínimos del tema se comen los anchos que pides:** `TextBox` trae
  `MinWidth = 64` y `CheckBox` `MinWidth = 120`. En WPF no existe ninguno de
  los dos. Cuando un `Width` «no se aplica», **sospecha de un mínimo del tema
  antes que de tu código** — es la misma familia que el `MinHeight` de la
  barra de trim.
- El `Padding` por defecto también es otro: (6,4) en WinUI contra (4,2) en
  WPF, y con el de WinUI las filas salen más altas que las de WinCue.

La lista larga y con el porqué de cada una está en
`TRASPASO-SECUENCE.md`; esta es solo la punta.

---

## Herramientas de este repo

- **graphify** ya está enganchado en `.claude/settings.json`. Para preguntas
  sobre el código: `graphify query "<pregunta>"`, `graphify path "<A>" "<B>"`,
  `graphify explain "<concepto>"`. Después de tocar código:
  `graphify update .`. Si `graphify-out/` no existe (está en `.gitignore`),
  indéxalo primero.
- **superpowers**: el diseño aprobado y el plan están en
  `docs/superpowers/specs/` y `docs/superpowers/plans/`.

---

## El CI

`.github/workflows/ci.yml`, en la RAÍZ del repo (no en `WinCueSharp/`).
Runner **windows-latest**, obligatorio: `Secuence.App` es WinUI 3 y no compila
fuera de Windows. Escucha `pruebas-v2`, `main`, `secuence/**` y `claude/**`.

Detalle sin resolver: la entrada `main` no corresponde a ninguna rama real —
el remoto tiene `master`. Señalado, no tocado.

---

## Un cabo suelto

`RelocateMediaTests.RelocateMedia_RecuperaPorNombreDeArchivo` falla **solo en
Linux**: `Path.GetFileName(@"D:\vieja\intro.wav")` devuelve la cadena entera
porque `\` no es separador fuera de Windows. En Windows y en el CI pasa. Se
ofreció arreglarlo (cambio solo de prueba, cero riesgo para el motor) y quedó
sin respuesta. Aquí, en Windows, no molesta.
