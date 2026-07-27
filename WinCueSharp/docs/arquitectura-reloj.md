# El reloj de WinCue

> Un solo reloj para el show. Y una frontera clarísima con los relojes de los
> medios, que no se toca.

## El problema que resuelve

El cliente no va a tener un PC dedicado: WinCue correrá **con otras cosas
reproduciéndose** en la misma máquina. Y hasta ahora en el proyecto convivían
**tres familias de reloj que no se hablaban**:

1. **El planificador** — `IScheduler` / `TimerScheduler`, sobre
   `System.Threading.Timer`. Decidía CUÁNDO se dispara cada cosa: `pre_wait`,
   `post_wait`, `duration`, cadenas `AUTO_CONTINUE`.
2. **La medición** — `MainController` medía con un `Stopwatch` propio
   (`TStartTicks`, el `elapsed`/`remaining` del panel de activos, el reajuste de
   la pausa, los antirrebotes del GO y del PANIC). **Esos números no estaban
   atados a cuándo disparaba el planificador.**
3. **La UI** — catorce `DispatcherTimer` y el bucle de 60 Hz del timeline. Los
   `DispatcherTimer` son de **prioridad baja** en WPF: con la máquina cargada se
   retrasan y ratean.

En un PC ocioso los tres coinciden y todo parece bien. En un PC cargado
divergen: **lo que se ve en pantalla y lo que hace el secuenciador dejan de ser
el mismo tiempo**. Dos de los sustos de directo de esta temporada fueron
exactamente eso, relojes que no se ponían de acuerdo.

## La pieza

`src/WinCue.Controller/ShowClock.cs`.

```csharp
public interface IShowClock
{
    double NowRaw { get; }   // monótono, NUNCA se congela
    double Now    { get; }   // tiempo de SHOW: se congela en la pausa global
    bool IsFrozen { get; }
    void Freeze();
    void Resume();
    event Action? Frozen;    // avisos de transición: el planificador suspende
    event Action? Resumed;   // y re-arma sus plazos vivos
}
```

Tres implementaciones, una idea:

| Clase | Para qué |
|---|---|
| `ShowClockBase` | La contabilidad de la pausa, común y con cerrojo (el planificador lo consulta desde el hilo de sus timers). |
| `ShowClock` | Producción. Base `Stopwatch`: un cambio de hora del sistema o un salto de NTP en mitad del show no mueve ni un milisegundo. |
| `ManualShowClock` | Tiempo virtual: solo avanza cuando alguien lo empuja. Es la costura de determinismo de los tests y del banco de deriva. |

### Por qué dos lecturas

- **`NowRaw`** — eje de los **PLAZOS** y de los **antirrebotes de la botonera**
  (doble GO, doble PANIC). Un antirrebote es un gesto de mano: cuenta en tiempo
  de pared. Si leyera del tiempo de show, un GO justo después de una pausa
  larga se quedaría bloqueado.
- **`Now`** — eje de la **MEDICIÓN** y la **PRESENTACIÓN**: `elapsed`,
  `remaining`, cuentas atrás, reloj grande, cursor del timeline. Al congelarse
  en la pausa, los números se quedan quietos **sin tener que corregir marcas**.

### Quién lo reparte

Lo aporta el planificador (`IScheduler.Clock`) y el controlador lo publica como
`MainController.Clock`. Por eso **la firma del `MainController` no cambió** y no
hubo que tocar ni un test existente: quien inyecta el scheduler inyecta el
reloj. La vista lee el mismo objeto.

## Plazos absolutos: el arreglo de fondo

`System.Threading.Timer` dispara **tarde**: unos pocos ms en un PC ocioso, mucho
más en uno cargado. Antes, cada eslabón de una cadena se programaba «dentro de N
ms» **desde el disparo (ya tardío) del anterior**, así que el retraso se
**acumulaba**.

Ahora `IScheduler.ScheduleAt(deadline, fn)` recibe el instante **absoluto** en el
que la cosa DEBÍA ocurrir, y cada eslabón se ancla **al ideal del anterior**:

```
GO ─┬─ ideal 0,0 s ──► cue 1
    ├─ ideal 0,1 s ──► cue 2      (aunque el cue 1 saliera a 0,025)
    ├─ ideal 0,2 s ──► cue 3      (aunque el cue 2 saliera a 0,127)
    └─ …                          el error no se hereda
```

En el controlador el ancla viaja en `RunningCue`:

- `Anchor` — instante ideal del disparo.
- `ActionAnchor` — ideal del arranque de la acción (`Anchor + pre_wait`).
- `SequenceEnd` — fin ideal **cuando lo marca el reloj y no un motor** (`WAIT`,
  `FADE`, cues instantáneos). En media es `null`: ahí manda el archivo.
- `TStart` — arranque **REAL**, en tiempo de show. Es lo que se mide y se
  enseña.

Un plazo que llega **ya vencido** dispara de inmediato: **recuperar, no
arrastrar**.

### El tope de recuperación

`MainController.MaxRecuperacionSegundos = 2.0`. Comprimir esperas para
recuperar 40 ms es lo correcto; hacerlo para recuperar treinta segundos (la
máquina se fue del todo) **vaciaría media lista de golpe**, que en directo es
mucho peor que ir tarde. Pasado el umbral, la cadena se **reancla al presente** y
sigue desde ahí, en sincronía con la realidad.

## LA FRONTERA CON LOS RELOJES DE MEDIA

**Límite innegociable: el ShowClock NO gobierna la reproducción.** El audio no
se puede esclavizar a un reloj de software — el cristal de la tarjeta decide
cuándo salen las muestras, y el vídeo se disciplina a ese audio. Siguen mandando
en lo suyo, intactos:

| Reloj | Qué manda |
|---|---|
| `AudioEngine.GetElapsedThreadSafe` | Reloj maestro del motor HAP propio. |
| `HapMediaClock` | Extrapolación monotónica de la posición del cue. |
| `MpvSyncPll` / `ObservedTimePosSmooth` | PLL de la ruta híbrida. |

Dónde se ve la frontera en el código:

- **`SnapshotActiveCues`** prefiere el `elapsed` **del motor** cuando lo hay; el
  reloj del show solo cubre el hueco (pre-wait, cues sin motor, motor mudo).
- **El fin de un media** (`StartAction`, casos `Audio` y `Video`) se programa en
  **RELATIVO** desde su arranque **real**, y se corrige contra la posición del
  motor (`VideoEndCheck`, `BeginVideoFadeOut`). Anclarlo al ideal cortaría el
  final de lo que está sonando cuando el arranque llegó tarde.
- **`AUTO_FOLLOW` desde un media** hereda el ahora real, no un ideal: el motor
  es quien sabe cuándo terminó de verdad.

Regla de una línea: **el ShowClock gobierna la SECUENCIA y la PRESENTACIÓN; el
motor gobierna la REPRODUCCIÓN.**

## La UI: qué se migró y qué no

Se tocó **solo lo que muestra tiempo de show**. Los timers de presentación pasan
a ser un simple **refresco**: en cada pasada vuelven a **preguntar la hora** en
lugar de sumar su propio tic, así que si WPF retrasa un tic el valor **salta al
correcto** en vez de quedarse atrás.

| Timer | Qué se hizo | Por qué |
|---|---|---|
| Reloj del show (500 ms, `MainWindow`) | **Migrado** | Marca y lectura en `Clock.Now`. Además ahora se congela con la pausa, igual que el countdown — antes seguía corriendo y decía otra cosa. |
| Cursor del timeline (60 Hz, `CompositionTarget.Rendering`) | **Migrado** | La extrapolación entre ticks usa `Clock.Now`. Con el show en pausa el reloj se congela y el cursor deja de deslizar solo. |
| Cuentas atrás por cue / countdown primario | **Migrado (por la vía del controlador)** | Los números salen de `SnapshotActiveCues`, que ya mide con el ShowClock. La vista solo formatea. |
| Tick de 100 ms (`App.xaml.cs`) | **Sin cambios** | No mide: solo pide al controlador que publique su instantánea. Si se retrasa, la instantánea siguiente ya trae el valor correcto. |
| Monitor PGM (500 ms) | **Sin cambios** | Enseña la posición **del motor** de vídeo: es reloj de MEDIA. |
| Transporte del monitor PVW (120 ms) | **Sin cambios** | Ídem: posición del media, y además no toca la sala. |
| Parpadeo de STOP/PANIC (450 ms), decay del VU (50 ms), RTA (80 ms), GR (150 ms), estado (4 s), autoguardado (60 s), identificar salida (3 s) | **Sin cambios** | Cosméticos o de mantenimiento. No miden tiempo de show: convertirlos sería ruido y riesgo sin ganancia. |
| `FadeAllRunning` (`MainWindow`) | **Sin cambios** | Mide lo que dura un fundido **de los motores** para apagar el parpadeo. Es tiempo de media. |
| `UndoService` (antirrebote de 800 ms) | **Sin cambios** | Gesto de edición, no de show. |

## La pausa global congela también los PLAZOS

Antes, la pausa paraba los motores y congelaba el tiempo de show, pero **los
timers seguían corriendo**. Un cue con `duration` fija al que le pillaba una
pausa larga **se cortaba antes de tiempo**: su timer de fin llegaba a cero con
el motor parado.

Ahora el reloj **avisa** de cada transición y el planificador reacciona:

```csharp
event Action? Frozen;    // el planificador suspende sus plazos vivos
event Action? Resumed;   // y los re-arma con lo que les faltaba
```

`TimerScheduler` se suscribe en su constructor. Al congelar guarda por cada
plazo vivo `Restante = Deadline − NowRaw` y lo desarma (`Timer.Change(Infinite)`);
al reanudar recalcula `Deadline = NowRaw + Restante` y lo vuelve a armar. El eje
de los plazos (`NowRaw`) **no** se congela, así que el plazo se **desplaza** con
la pausa en vez de dejarse contar.

Detalles que importan:

- **Se congela TODO lo que es tiempo de show**: los plazos absolutos de la
  secuencia (`ScheduleAt`) y los relativos que cuelgan de un motor (fin de un
  media, reintentos corregidos contra su posición). Con el motor parado, su
  reloj tampoco avanza.
- **La frontera no se mueve.** El fin de un media se sigue anclando a su
  arranque **REAL** y corrigiendo contra la posición del motor
  (`VideoEndCheck`, `BeginVideoFadeOut`). Lo único que cambia es que ese plazo
  deja de correr mientras el show está en pausa.
- **Un plazo creado DURANTE la pausa nace suspendido**: un cue disparado con el
  show parado no gasta su `pre_wait` con el sonido detenido.
- **La única excepción es `Schedule(0)`**: un plazo de cero no es un plazo, es
  un despacho al hilo del controlador (`HapConvertService.Post`). Congelarlo
  dejaría colgado hasta la reanudación un mensaje de servicio que no tiene nada
  que ver con el show.
- **El borde se decide bajo cerrojo.** Un plazo que vence justo al pausar o
  dispara ahora, o se suspende y sale al reanudar — nunca las dos cosas. El
  cerrojo del `TimerScheduler` es lo que impide el doble disparo entre el hilo
  del timer y el de la botonera.
- **`STOP ALL` en pausa** descongela el reloj, y con él vuelven a correr los
  plazos: si se quedaran suspendidos, el resto de la función no terminaría ni
  un cue.
- La pausa de UN cue (`OnPauseCueToggle`) sigue como estaba: tira sus timers y,
  al reanudar, reprograma lo que le queda **según el motor**. Las dos pausas se
  combinan sin descontar dos veces.

## Lo que NO hace (a propósito)

- **No toca los relojes de media.** Ver la frontera, arriba.
- **No hay timecode externo.** Este trabajo va de **coherencia interna**; el
  MTC/wall-clock de `RemoteRouter` sigue como estaba.

## La medida

Banco: `tests/WinCue.Controller.Tests/DerivaBajoCargaBench.cs`. Cadena de 100
cues encadenados con `AUTO_CONTINUE` cada 50 ms (**nominal 4,950 s**), con el
planificador **de producción** y los callbacks bombeados por **un solo hilo**
(el papel del dispatcher de WPF), mientras N hilos queman CPU. Está apagado por
defecto:

```powershell
$env:WINCUE_BENCH_DERIVA = "1"
$env:WINCUE_BENCH_CARGA  = "24"     # hilos quemando CPU
dotnet test tests/WinCue.Controller.Tests -c Release --filter DerivaBajoCargaBench
```

El fichero **no usa nada del ShowClock**, para que compile igual en el commit
base y la comparación sea honesta.

Máquina de 24 núcleos, dos repeticiones por punto:

| Carga (hilos) | ANTES | DESPUÉS |
|---|---|---|
| 0 (ocioso) | +50 / +36 ms · 0,7–1,0 % | **−2 / −2 ms · 0,04 %** |
| 24 (= núcleos) | +1.567 / +1.505 ms · 30–32 % | **+20 / +3 ms · 0,06–0,4 %** |
| 48 (2× núcleos) | +6.948 / +7.405 ms · 140–150 % | **+66 / +42 ms · 0,8–1,3 %** |
| 72 (3× núcleos) | +14.323 / +12.553 ms · 254–289 % | **+2.596 / +3.082 ms · 52–62 %** |

Con la máquina **completamente** ahogada (72 hilos sobre 24 núcleos) la deriva
no desaparece —si el hilo de bombeo no llega a ejecutarse, ningún reloj lo
arregla— pero pasa de **×3,9 el tiempo nominal a ×1,6**.

### Y con la app de verdad

Soak real (`--stress tests\stress\show-estres.json`, medias generadas con
`tools\generar-show-prueba.ps1`, `WINCUE_STRESS_WATCH=1`, GPU y audio reales),
con 24 procesos quemando CPU en paralelo:

| | segundos por vuelta |
|---|---|
| ANTES, con carga | **57,33 s** |
| DESPUÉS, con carga | **46,67 s** |
| Cualquiera de los dos, ocioso | 47,0 s |

Es decir: **con el PC cargado el show antes se iba +10 s por vuelta (≈ +22 %) y
ahora mantiene su cadencia de reposo**. Watchdog: `TOTAL ANOMALÍAS 0 → SOAK
LIMPIO ✔` (0 cuentas negativas, 0 cues colgados, 0 cadenas estancadas).

## Los tests

- `ShowClockTests.cs` — el reloj en aislamiento: monotonía, la pausa congela el
  show pero no la pared, reanudar no salta ni retrocede, `Freeze`/`Resume`
  idempotentes, y los avisos de transición se emiten **una sola vez** y con el
  reloj **ya** en su nuevo estado.
- `PausaCongelaLosPlazosTests.cs` — la pausa global contra los plazos, con el
  reloj virtual: la duración fija sobrevive a una pausa de un minuto (y también
  con el PC cargado), varias pausas no se comen el resto, un plazo al borde de
  vencer ni se pierde ni dispara dos veces, `pre_wait` / cadena `AUTO_CONTINUE`
  / cue `WAIT` no avanzan en pausa, un cue disparado en pausa nace congelado,
  `STOP ALL` devuelve los plazos a la vida, la pausa individual dentro de la
  global no descuenta dos veces, y el despacho de 0 ms **no** se congela.
- `TimerSchedulerTests.cs` — lo mismo en el planificador **de producción**
  (timers reales): suspender un plazo vivo, nacer congelado, el despacho de
  0 ms, y que cancelar un plazo suspendido no lo resucite al reanudar.
- `DerivaDelTemporizadoTests.cs` — el temporizado con **el PC cargado
  simulado** de forma determinista (`VirtualScheduler.RetrasoDeDisparo`):
  relativo vs absoluto al desnudo, cadena de 60 cues sin deriva, cadena de
  esperas sin deriva, recuperación de un parón, tope de recuperación, el
  `remaining` cuenta tiempo **real** tras un disparo tardío, la pausa no salta,
  un cue que arranca **durante** la pausa no da `elapsed` negativo, y el
  antirrebote del GO no se congela con la pausa.
