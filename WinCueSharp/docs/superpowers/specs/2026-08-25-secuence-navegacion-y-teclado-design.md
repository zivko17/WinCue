# Diseño — Secuence: navegación del playhead y teclado

*Escrito 2026-08-25, después de que Andrés probara Secuence por primera vez con
el compositor y el canvas dentro, y de tres auditorías paralelas (teclado,
monitores e interfaz) que devolvieron 114 hallazgos en Secuence y 8 en WinCue.*

Esta es **la primera de cinco tandas**. Las otras cuatro —red de seguridad,
saber qué pasa en directo, la salida que hace cosas que nadie pidió, y timeline
e inspector— tienen su propio ciclo. Aquí no entran.

---

## El problema

De la auditoría de interfaz, el hallazgo que ella misma llama el peor de todos:

> **En Secuence no hay ninguna forma de mover el playhead.** `OnSetPlayhead`,
> `OnPrevPressed` y `OnNextPressed` no aparecen ni una vez en `Secuence.App`,
> aunque los tres son públicos en `MainController`.

Consecuencia: el show solo se reproduce **desde el principio, hacia delante, un
GO cada vez**. No se puede saltar un cue, ni volver atrás tras un fallo, ni
colocar el playhead en el Acto II después del descanso. *Un reproductor de cues
en el que el operador no elige el siguiente cue no es utilizable en una
función.*

Y de la auditoría de teclado: **de los 23 atajos de WinCue, en Secuence
funcionan 3**. Faltan 18, y **siete de ellos están anunciados en el propio menú
contextual de Secuence**, que los enseña sin que hagan nada.

## Lo que ya está hecho, y por qué esta tanda es más pequeña de lo que parece

**El motor está completo.** `MainController` ya tiene `OnSetPlayhead(row)`,
`OnPrevPressed()`, `OnNextPressed()`, `OnJumpToCue(n)` —los dos de flecha son
`±1` con tope— y `Undo`/`Redo`/`CanUndo`/`CanRedo`. Nada de esto hay que
diseñarlo: hay que **llamarlo**.

Y el despachador de teclado existe desde el lote de urgencias del mismo día:
`PreviewKeyDown` sobre el `Grid` raíz, con guardián de escritura, contador de
diálogos, y los hotkeys por cue en la burbuja. Esta tanda **amplía** ese
despachador; no lo reescribe.

## Reglas que gobiernan este diseño

Las del proyecto siguen en pie. Las tres que más mandan aquí:

1. **WinCue no cambia de comportamiento ni de aspecto.** Está vendiéndose. Esta
   tanda **no toca `src/WinCue.App/` ni el motor compartido**.
2. **Prohibido copiar código entre productos.** Aquí no aplica porque no se
   comparte nada nuevo — ver la decisión sobre dónde vive el mapa.
3. Lo que solo se ve en pantalla va a un epígrafe
   `## PENDIENTE — verificación en pantalla (Andrés)`.

## Decisiones tomadas, y quién las tomó

| Decisión | Elegido | Por qué |
|---|---|---|
| ¿Selección = playhead? | **NO, separados** | Elección de Andrés. Permite mirar el cue 47 en mitad del acto sin que el GO deje de disparar el 12 |
| ¿Qué hacen `↑`/`↓`? | **Mueven el playhead** | Elección de Andrés. En función las manos están en el teclado: retroceder un cue tras un fallo es una tecla |
| ¿Atajos configurables? | **Todos clavados, de momento** | Elección de Andrés. Acorta la tanda; `keyboard_*` queda para más adelante |
| ¿Dónde vive el mapa de teclas? | **Dentro de `Secuence.App`** | Elección de Andrés, contra mi recomendación. Ver el coste, abajo |
| `Ctrl+F` | **Fuera** | En Secuence no hay buscar cues. No se cablea lo que no existe |

### El coste de la última, escrito para que nadie lo descubra tarde

Yo recomendé subir el mapa y las precedencias a `WinCue.Presentation` con
pruebas, consumido de momento solo por Secuence. Andrés eligió tenerlo dentro
de `Secuence.App`. **Ningún proyecto de pruebas referencia `Secuence.App`**, así
que la consecuencia es concreta y hay que asumirla: **todo lo de esta tanda se
verifica midiendo, no con pruebas automáticas**, y nada de ello corre en el CI.
El método está en la sección 4 y ya ha funcionado hoy; pero es trabajo manual
que habrá que repetir cada vez que alguien toque el despachador.

Lo que **sí** nos llevamos de aquella recomendación, porque no dependía de
dónde viva el código, es el orden de precedencia escrito y explícito.

---

## Sección 1 — El modelo: qué es el playhead y qué lo mueve

**Selección y playhead pasan a ser dos cosas distintas.** La selección es lo
que el operador está mirando: puebla el inspector y el timeline, como hoy. El
playhead es lo que disparará el próximo GO.

Lo mueven **cinco gestos, y ninguno más**:

| Gesto | Qué hace |
|---|---|
| `↑` / `←` | Playhead al cue anterior (`OnPrevPressed`) |
| `↓` / `→` | Playhead al cue siguiente (`OnNextPressed`) |
| `Enter` con el foco en la tabla | Playhead a la fila seleccionada (`OnSetPlayhead`) |
| Doble clic en una fila | Playhead a esa fila (`OnSetPlayhead`) |
| Dígitos + `Enter` | Playhead al cue con ese NÚMERO (`OnSetPlayhead`) |

**Ojo con el último**: no es `OnJumpToCue(n)`, que salta por posición. WinCue
casa el texto tecleado contra `DisplayNumber` —que es `"1.1"`, no un índice— y
llama a `OnSetPlayhead(fila)`. Es lo correcto: el operador teclea el número que
ve en la tabla. `OnJumpToCue` se queda sin usar.

Más el GO, que lo avanza el motor como ya hace hoy.

**Un clic simple NO mueve el playhead.** Ese es el cambio.

### Dos cosas que WinCue no necesita y aquí sí

**La tabla tiene que marcar dos filas distintas.** El playhead se pinta como
una **barra vertical en el borde izquierdo de la fila**, del color de standby de
la paleta, con el número del cue en negrita; la selección sigue siendo el
resaltado de fondo de siempre. Ejes distintos —borde contra fondo— para que se
lean a la vez cuando coinciden, que es el caso normal.

**Cuando el playhead se mueve, la tabla se desplaza para enseñarlo.** Si no,
retroceder dos cues con las flechas lo deja fuera de pantalla. La selección
**no** se mueve con él.

### Lo que no cambia

El inspector sigue enseñando lo **seleccionado**. En modo SHOW esto se comporta
igual que en EDIT: se descartó explícitamente la variante de frenar el
movimiento durante la función.

---

## Sección 2 — El despachador: un orden, escrito una vez

Hoy hay **tres** sitios que ven teclas: el túnel (transporte), la burbuja
(hotkeys por cue) y cuatro `KeyboardAccelerator` en el XAML. Los aceleradores
**se traen al despachador**: son el único cabo que quedó fuera del modelo nuevo,
y por eso hoy `Ctrl+O` y `Ctrl+S` siguen llegando con Ajustes abierto.

El orden, de arriba abajo:

| | Capa | Contenido |
|---|---|---|
| 0 | **Diálogo abierto** | No pasa nada. Ya existe (contador de diálogos) |
| 1 | **Campo de escritura** | Sale entero. Excepción: el selector de fuente del PGM |
| 2 | **Con `Ctrl`/`Alt`** | `Ctrl+O/S/Shift+S/G`, `Ctrl+P/C/V/D/Z/Y` |
| 3 | **Transporte** | GO (`Espacio`), STOP ALL (`Escape`), DETENER (`S`), ANTERIOR/SIGUIENTE (flechas), PAUSA (`P`) |
| 4 | **Seguridad** | `B` — blackout |
| 5 | **Hotkeys por cue** | |
| 6 | **Ir a cue** | Dígitos, `.`, `Retroceso`, `Enter` |

### El orden real de WinCue, verificado — y en qué divergimos a propósito

Leído en `WinCue.App/MainWindow.xaml.cs:2084-2180`, porque de memoria se cuenta
mal. WinCue resuelve así:

1. Guardián de escritura (`TextBox`/`ComboBox`/`ComboBoxItem`); `Escape` ahí solo
   devuelve el foco a la tabla.
2. **Solo si NO hay modificadores**: GO → DETENER → ANTERIOR → **`B`** →
   hotkeys por cue → buffer de ir-a-cue.
3. Bloque `Ctrl`: `Z`, `Y`, `C`, `V`, `D`, `P`, `F`.
4. Un `switch` final con las teclas sueltas: **`P` (pausa global), las flechas,
   `Supr`, `F2`**.

Tres detalles que es fácil suponer al revés:

- **`B` no es la capa más alta**: va después del transporte configurable. Pero
  **sí va antes de los hotkeys por cue**, con el comentario literal de WinCue
  *«control de seguridad: gana a los hotkeys»*.
- **El buffer de «ir a cue» va DESPUÉS de los hotkeys.** Un cue cuya hotkey sea
  un dígito dispara, y ese dígito nunca llega al buffer.
- **El bloque de modificadores va después de los hotkeys**, pero eso no crea
  conflicto porque el bloque de hotkeys está encerrado en un `if (Modifiers ==
  None)`. `Ctrl+Z` no dispara un cue cuya hotkey sea `Z`.

**Y aquí divergimos, a propósito y por una razón concreta:** en WinCue las
flechas y `P` viven en el `switch` final, o sea **por debajo de los hotkeys por
cue**. Eso significa que un show que asigne `↓` como hotkey de un cue **le roba
la navegación del playhead al operador**, en silencio.

En WinCue eso importa poco, porque allí las flechas son un extra. En Secuence
las flechas **son** el gesto principal de esta tanda: si un show puede
apagarlas, la tanda no sirve. Así que en Secuence **todo el transporte —GO,
DETENER, ANTERIOR/SIGUIENTE y PAUSA— va por encima de los hotkeys por cue**, y
los hotkeys quedan por debajo.

Coste de la divergencia, dicho claro: **un cue cuya hotkey sea una flecha o `P`
no se podrá disparar con el teclado en Secuence**, y sí en WinCue. Es el mismo
tipo de trueque que WinCue ya hizo con `B`, y se prefiere porque un control de
transporte que un archivo de show puede desactivar no es un control de
transporte.

### Por qué la capa de seguridad es una corrección y no un añadido

Hoy Secuence no tiene esa capa. Como el JSON del show es el mismo en los dos
productos, **un show con un cue cuya hotkey sea `B` hace blackout en WinCue y
saca ese cue a sala en Secuence**. La misma tecla, el mismo show, dos cosas
opuestas. Añadir «la tecla B» no lo arregla: lo arregla el orden.

### `Enter`, que es el caso delicado

`Enter` se dejó fuera del despachador a propósito en el lote de urgencias, para
que `Tab`+`Enter` siguiera activando botones — es la única ruta accesible por
teclado que quedaba, y es la concesión que paga que `Espacio` ya no active el
control con foco.

Se resuelve **por foco**: el despachador atiende `Enter` únicamente cuando el
foco está en la tabla de cues, o cuando hay dígitos acumulados en el buffer. Con
el foco en un botón, `Enter` sigue siendo del botón.

---

## Sección 3 — Qué entra y qué no

**Entran los 17 cuya acción ya existe** en Secuence o en el controlador:

| Tecla | Acción | De dónde sale |
|---|---|---|
| `S` | Detener el cue seleccionado | `OnStopSelected` |
| `↑` `←` `↓` `→` | Playhead anterior / siguiente | `OnPrevPressed` / `OnNextPressed` |
| `Enter` | Playhead a la selección | `OnSetPlayhead` |
| dígitos + `Enter` | Ir a cue | `OnJumpToCue` |
| `B` | Blackout | El que ya usa el menú de salidas |
| `P` | Pausa global | `OnPauseToggle` |
| `Supr` | Eliminar | Menú contextual |
| `F2` | Renombrar | Menú contextual |
| `Ctrl+P` | Pausar/reanudar cue | Menú contextual |
| `Ctrl+C` `Ctrl+V` `Ctrl+D` | Copiar / pegar / duplicar | Menú contextual |
| `Ctrl+Z` `Ctrl+Y` | Deshacer / rehacer | `Undo` / `Redo` |

Con esto **el menú contextual deja de mentir**: los siete atajos que anuncia
pasan a existir.

Las de edición (`Supr`, `F2`, `Ctrl+C/V/D`) **respetan el candado de modo
SHOW**, igual que WinCue: durante la función no se añade, ni se inserta, ni se
borra, ni se importa.

**Queda fuera `Ctrl+F`**, y se dice aquí para que no parezca un olvido: en
Secuence no existe buscar cues (hallazgo S-11 de la auditoría). No se cablea lo
que no hay. Entra con la tanda que traiga la búsqueda.

**Y queda fuera hacer los atajos configurables** (`keyboard_go/stop/prev`, que
WinCue sí lee). Decisión de Andrés para acortar esta tanda.

---

## Sección 4 — Cómo se verifica

**Sin pruebas automáticas, y hay que decirlo sin adornos.** Ningún proyecto de
pruebas referencia `Secuence.App`, así que la suite de 1.229 seguirá en verde
diga lo que diga este código: es no-regresión del motor compartido, **no
evidencia de nada de esta tanda**.

Lo que sostiene esta tanda son mediciones, con el método que ya funcionó hoy:
UI Automation para colocar el foco y `keybd_event` para las teclas, leyendo el
resultado del estado real de la aplicación (playhead, selección, luma de la
salida, texto de la barra de estado).

**Con control negativo en cada caso**: romper el arreglo a propósito, ver que la
medición lo caza, y revertir. Esta sesión ha tenido **tres verdes por el motivo
equivocado** —un fence que no fenceaba, una prueba de espejo que no cazaba lo
que decía cazar, y un arrastre que no ejercitaba el camino roto—, y los tres los
destapó ese control y ninguna otra cosa.

### La matriz mínima

Cada tecla contra cada foco: **tabla de cues, botón, campo de texto, selector
del PGM, y diálogo abierto**.

### Los controles de no-regresión, que aquí son la mitad del trabajo

Estos ya han mordido una vez cada uno en el mismo día:

- `Escape` cierra el diálogo **sin parar el show**.
- `Tab`+`Enter` **activa un botón** (es el argumento entero de la concesión de
  accesibilidad).
- `Espacio` sobre un combo del **inspector** abre el desplegable; sobre el
  selector del **PGM** da GO.
- `Ctrl+O/S/Shift+S/G` siguen llegando — y ahora, además, **dejan de llegar con
  un diálogo abierto**, que es el agujero que esta tanda cierra.

### Y el control que define esta tanda

**Seleccionar una fila con un clic simple NO mueve el playhead.** Medido:
playhead antes, clic en otra fila, playhead después. Si se mueve, la tanda no
está hecha.

### `## PENDIENTE — verificación en pantalla (Andrés)`

- Que la marca del playhead **se distinga de la selección** de un vistazo, y que
  las dos se lean cuando coinciden.
- Que al mover el playhead con las flechas la tabla **se desplace** y no lo
  pierda de vista.
- Que retroceder tras un fallo y volver a dar GO haga lo que espera.
- Que los siete atajos del menú contextual hagan ya lo que el menú promete.

---

## Riesgos, por orden

1. **La separación selección/playhead es un cambio de modelo, no un añadido.**
   Es la primera divergencia deliberada de fondo respecto a WinCue. No contamina
   los shows —el playhead no se guarda en el JSON—, pero sí obliga a Andrés a
   cambiar un hábito si venía de WinCue.
2. **Sin pruebas automáticas**, cada cambio futuro del despachador exige repetir
   la matriz a mano. Es el precio de la decisión de dónde vive el mapa, y la
   forma de pagarlo menos veces es dejar la matriz escrita y reutilizable.
3. **Traer los `KeyboardAccelerator` al despachador toca teclas que hoy
   funcionan.** `Ctrl+S` guardando es de las que más duele romper. Va con sus
   propios controles de no-regresión.
4. **`Enter` resuelto por foco** es la parte más fácil de romper sin darse
   cuenta: cualquier cambio futuro en quién tiene el foco cambia lo que hace la
   tecla.
5. **Poner el transporte por encima de los hotkeys** deja sin disparar por
   teclado a un cue cuya hotkey sea una flecha o `P`. Es deliberado y está
   argumentado arriba, pero si un show real de Andrés usa esas teclas como
   hotkey, se notará — y el aviso tiene que salir por la barra de estado al
   cargar ese show, no descubrirse en una función.

## Lo que esto desbloquea (y no entra aquí)

Con el playhead móvil y el despachador completo, la tanda de la **red de
seguridad** (deshacer con su interfaz, confirmaciones, avisos al cerrar) tiene
dónde engancharse; y la de **saber qué pasa** puede enseñar el cue del playhead
frente al que suena, que ahora mismo no se pueden distinguir porque son lo
mismo.
