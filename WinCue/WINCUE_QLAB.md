# WinCue → modelo estilo QLab (v0.2)

Reescritura del núcleo de reproducción para acercar WinCue a QLab en
funcionalidad. Resumen de lo que cambió y cómo usarlo.

## Cambio de paradigma: de mono-activo a concurrente

**Antes:** GO cortaba el cue anterior (un solo cue sonando a la vez; el motor de
audio tenía un único stream).

**Ahora (como QLab):**

- **PLAYHEAD / standby**: hay un cue "en espera" (resaltado ámbar). GO lo dispara
  y BAJA el playhead al siguiente **sin cortar** lo que ya suena.
- **Cues concurrentes**: varios cues suenan a la vez. El motor de audio es
  **multi-stream**: dos (o más) audios se mezclan de verdad — se acabó la
  limitación de "un solo stream". Vídeo + audio + varios audios simultáneos.
- La selección de la tabla **es** el playhead: navega con ▲▼ o clic.

## Temporización por cue (pestaña de propiedades: doble clic)

- **Pre-wait**: retardo entre disparar el cue y el comienzo de su acción.
- **Acción / Duración**: longitud de la acción (audio/vídeo/espera/fade).
  Para media, 0 = hasta el final natural del archivo.
- **Post-wait**: segundo temporizador, usado por Auto-continue.
- **Al disparar (continue_mode)**:
  - **No continuar** — GO manual, nada automático.
  - **Auto-continue** — el siguiente cue se dispara tras `pre_wait + post_wait`
    (medido desde el disparo), aunque este cue siga sonando.
  - **Auto-follow** — el siguiente cue se dispara cuando ESTE termina su acción.

Encadenando auto-continue/auto-follow se disparan varios cues desde un solo GO
(equivalente funcional al caso de uso principal de los Group cues de QLab).

## Tipos de cue

| Tipo   | Acción |
|--------|--------|
| AUDIO  | Reproduce audio con volumen, fade in/out (sample-accurate) y loop. |
| VIDEO  | Reproduce vídeo empotrado con volumen y loop; fade = fundido de su volumen. |
| WAIT   | Espera temporizada (para cadenas auto-follow). |
| FADE   | Funde el volumen del **cue objetivo** hacia el valor dado en `duration` s. Si el objetivo es 0 y "stop on done", lo detiene. |
| START  | Dispara el **cue objetivo**. |
| STOP   | Detiene el **cue objetivo** (o TODOS si no hay objetivo), opcionalmente con fade. |
| SCRIPT | Lanza un archivo/comando (.py/.bat/.cmd/.ps1/…) en un proceso aparte. |
| MEMO   | Comentario / separador, sin acción. |

Los cues FADE/START/STOP tienen un selector de **Objetivo** (otro cue de la lista).

## Transporte

| Acción     | Botón       | Tecla | Qué hace |
|------------|-------------|-------|----------|
| GO         | ▶ GO        | Espacio | Dispara el standby y baja el playhead (sin cortar). |
| Stop All   | ■ STOP ALL  | S     | Detiene todos los cues en ejecución y las cadenas pendientes. |
| Panic      | ⚠           | Esc   | Fundido de salida global (~1.5 s) y parada de todo. |
| Pause      | ❚❚ PAUSE    | P     | Pausa / reanuda todos los motores. |
| Standby    | (selección) | ▲ ▼   | Mueve el cue en espera. |
| Saltar a N | —           | 0–9   | Salta el playhead al cue Nº (acumula dígitos, 800 ms). |
| Guardar    | GUARDAR     | Ctrl+S | Guarda el show en JSON (con todos los campos nuevos). |
| Abrir      | ABRIR       | Ctrl+O | Carga un show. |

Panel **EN EJECUCIÓN** (columna izquierda): lista en vivo de los cues sonando,
con barra de progreso y tiempo restante/transcurrido por cue.

## Group cues (grupos anidables, estilo QLab)

Un cue **GROUP** contiene otros cues (hijos), que pueden ser de cualquier tipo,
incluidos otros grupos (anidamiento). Cuatro modos (Settings del cue → «Modo de grupo»):

| Modo | Al pulsar GO sobre el grupo |
|------|------------------------------|
| **Timeline** — todos a la vez | Dispara TODOS los hijos simultáneamente; el playhead salta tras el grupo. |
| **Entrar** | Dispara el 1º hijo y el playhead entra al grupo (organizativo; igual que sin grupo). |
| **1º + salir** | Dispara el 1º hijo; el playhead salta tras el grupo (el resto de hijos solo corren si están encadenados con auto-continue/follow). |
| **Azar** | Dispara un hijo aleatorio (round-robin: cada uno una vez por ronda); playhead tras el grupo. |

**Uso:**
- **Agrupar**: selecciona varios cues (clic + Shift/Ctrl) y pulsa **Ctrl+G** (o clic derecho → «Agrupar»).
- **Colapsar/expandir**: clic en el triángulo **▼/▶** a la izquierda del número del grupo, o clic derecho → «Contraer/Expandir».
- **Desagrupar**: clic derecho sobre el grupo → «Desagrupar» (los hijos suben un nivel).
- **Cambiar el modo**: doble clic en el grupo → «Modo de grupo».
- Los hijos se muestran **indentados** bajo el grupo (en negrita, con su modo entre paréntesis).

*Pendiente (fase 2):* el editor visual de **Timeline** de QLab (arrastrar/trim/slip/
escalar cada hijo en una línea de tiempo). El comportamiento de reproducción de los
4 modos ya está completo; falta solo esa herramienta de edición visual.

## Inspector acoplado (estilo QLab)

Panel con pestañas **bajo la lista** que edita **en vivo** el cue seleccionado
(la selección = el standby). Se muestra/oculta con **Ctrl+I**.

**Pestaña Básicos:** Nº, Tipo, Color, Armado, ⚑ Flag, Auto-load, Nombre,
Pre-wait / Acción / Post-wait, Continue, Objetivo (cues FADE/START/STOP),
Modo de grupo (GROUP), Volumen / Fade in / Fade out / Loop (media),
«Cortar los demás», Notas. Todo se aplica al instante al cambiarlo.

**Pestaña Triggers:**
- **Hotkey** — asigna una tecla que dispara el cue **esté o no en standby**
  (útil para efectos). Botón ✕ para borrarla.
- **Al re-disparar** (second trigger, cuando el cue ya suena): No hacer nada ·
  Panic (fade+stop) · Parar · Parada dura · Parada dura + reiniciar.
- **Fade & stop otros** — al dispararse, funde y para otros cues en el ámbito
  elegido (Hermanos / Toda la lista / Todo) en el tiempo dado.

*Fase 2:* triggers **MIDI / Timecode / Wall-clock**, **Duck/Boost** de audio,
inspectores **secundarios** y **pop-out** de ventana.

El antiguo diálogo modal (doble clic) sigue disponible como alternativa.

## Menú contextual (clic derecho en un cue)

Disparar desde aquí · Fijar como standby · Detener este cue · Editar propiedades ·
Agrupar selección (Ctrl+G) · Contraer/Expandir grupo · Desagrupar ·
Insertar cue (de cualquier tipo, incluido Grupo) · Borrar.

## Propiedades extra por cue

Número (editable, estilo QLab), Color (tira de color en la columna Nº), Notas,
Armado (los cues desarmados se muestran atenuados), Loop.

## Persistencia

`to_dict/from_dict` guardan y cargan todos los campos nuevos. Los shows antiguos
(solo id/tipo/nombre/duración/archivo) siguen cargando: el resto toma valores por
defecto (retrocompatible).

## Limitaciones conocidas / futuro

- **Group cues**: implementados (4 modos, anidamiento, colapsar, agrupar/desagrupar).
  Falta el **editor visual de Timeline** (arrastrar/trim/slip de hijos) — fase 2.
- **Pause** detiene los motores pero los temporizadores de cadena siguen corriendo
  (un auto-follow podría dispararse durante la pausa). Aceptable en v0.2.
- **Fade de vídeo** = fundido de su volumen (mpv empotrado no expone opacidad/
  geometría fácilmente). El fade de audio sí es sample-accurate.
- MIDI/OSC/network cues, cue carts: fuera de alcance por ahora.
- Empaquetado PyInstaller a .exe: pendiente (incluir libmpv-2.dll + bin/ffmpeg).

## Arquitectura tocada

- `cue_model.py` — Cue con campos QLab + helpers en CueModel.
- `audio_engine.py` — **reescrito** multi-stream con envolvente de fade en el
  callback RT (fade-in/out, fade externo, loop, stop con fade, pause/resume).
- `video_engine.py` — volumen/fade/pause/stop_all/loop (multi-player ya existía).
- `cue_controller.py` — **reescrito**: playhead + cues concurrentes + pre/post-wait
  + auto-continue/auto-follow + transport + ejecución de FADE/START/STOP/SCRIPT.
- `main_window.py` — columnas QLab, panel de activos, transporte, menú contextual,
  selección=playhead, barra de estado reactivada.
- `cue_edit_dialog.py` — inspector completo con visibilidad según tipo.
