# Arranque de una sesión LOCAL de Claude Code (Windows)

*Escrito 2026-08-24 desde una sesión remota (contenedor Linux) que NO podía
ejecutar ni ver ninguno de los dos productos. Esta sesión sí: corre en la
máquina de Andrés. Aprovéchalo — es la diferencia entre auditar código y
comprobar de verdad.*

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

---

## Lo que toca ahora

Repasar el inspector **elemento por elemento** contra el de WinCue. La
auditoría remota llegó hasta aquí:

- [x] Los 55 campos existen en los dos. No falta ninguno.
- [x] `WireTextCommit` idéntico en los dos.
- [x] Muestras de color: equivalentes.
- [ ] Desplegables (`Cmb`): qué pasa cuando el valor del cue no está entre las
      opciones — WinCue y Secuence pueden diferir.
- [ ] `TargetCombo`: repuebla la lista en cada `SetCue`.
- [ ] Barra de trim (`WaveformCanvas`) y su ida y vuelta con los campos.
- [ ] Rack de EQ y compresor.
- [ ] `FileRow` y el botón de examinar.

Y la lista de paridad de interfaz que sigue pendiente, fuera del inspector:

- **Separador arrastrable** del panel izquierdo (WinCue 200–760; Secuence lo
  tiene clavado en 455).
- **Cabecera del timeline**: rótulo SHOW TIMELINE, números de pista, botones
  de zoom, Fit, casilla «Crossfade on overlap».
- **Dos columnas de la tabla de cues** que faltan: flag y ✕.
- **Hotkeys por cue**: el campo se guarda y WinCue los respeta, pero
  `RebuildHotkeys` de Secuence solo cubre Space y Esc.
- Cajas de grupo y marcas de GO sin pintar en el timeline (los grupos no se
  pueden agarrar).

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
