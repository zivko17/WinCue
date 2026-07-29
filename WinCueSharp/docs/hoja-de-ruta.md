# WinCue — Hoja de ruta

*Documento vivo. Norte fijado 2026-07-23. Última pasada de realidad: 2026-07-28.*

## El norte

**Ser "el QLab de Windows".** No perseguir la tabla entera de media servers:
clavarse en el hueco real y mal servido (QLab no existe en Windows; SCS es
anticuado, Multiplay está muerto, PVP es otra liga de precio). Un reproductor
de cues audio/vídeo para directo, Windows, con tres diferenciales afilados:

1. **Motor de vídeo nativo** (ruta propia para HAP y ProRes, sin depender de mpv
   para los códecs estrella del sector).
2. **Mesa de audio por clip** (EQ + compresor + limitador + VU real — a la altura
   de vMix, por encima de QLab en esto), y ya para TODO vídeo, no solo HAP.
3. **Timeline + control de directo** (OSC/MIDI/timecode, PANIC, crash-safety).

Mercado que paga 400-1000 € por licencia: teatro, sala mediana, corporate AV,
iglesias, bandas, freelance técnico. La regla del sector: **no ganan los que
tienen más features, ganan los que NO FALLAN.** Todo el plan sirve a eso.

## El cuello de botella real

Ya no es escribir código. El límite es **el tiempo de Andrés para validar + las
decisiones de negocio que una IA no puede tomar.** El plan lo asume: separa lo
que ejecuta Claude de lo que solo puede hacer/decidir Andrés.

---

## FASE 0 — CERRADA (2026-07-27)

- [x] Fix del bug de relojes HAP (cuentas negativas, encadenado colgado,
      crossfades pegados) + garantía de fin. Un solo reloj para todo el show
      (`docs/arquitectura-reloj.md`).
- [x] Soak con el motor nuevo dentro: **watchdog limpio** — 0 remaining
      negativos, 0 cues colgados, 0 cadenas estancadas, 0 fugas candidatas,
      memoria en sierra acotada sin tendencia (`tools/hap-lab/INFORME.md`).
- [x] `audio_device_name` al arrancar (el arranque lo ignoraba; ya se aplica,
      con ASIO contemplado y tests).
- [ ] **[Andrés] Verificación visual** — la única casilla de F0 que sigue
      abierta, y lleva acumulando desde entonces. Ver el bloque de abajo.

## LA DEUDA QUE MÁS PESA — verificación visual de Andrés

No es una fase, es un tapón. Nada se da por hecho hasta que se ve en pantalla, y
hay ya varias sagas enteras validadas solo por tests:

- Cue HAP end-to-end: VU en Mixer, EQ en vivo, fundido de opacidad, PANIC,
  encadenado con solape. RTA/EQ y panel Mixer.
- Motor ProRes: interruptor de Ajustes, fallback a mpv, show de tortura mixto.
- Audio híbrido de vídeos mpv (EQ/compresor/VU sobre H.264 y demás).
- Refactor del compositor (pool de pipelines GPU): crossfades en la misma pista,
  cruce entre capas, modo canvas, salida 2, GO instantáneo del standby.
- Localización: repasar EN y ES buscando cadenas sueltas.
- **La libmpv LGPL nueva en pantalla**: que `hwdec` (d3d11va) siga entrando —es
  lo único que las pruebas automáticas no pueden ver— y un show real por la ruta
  mpv. La DLL ya está en `libmpv-lgpl/` y la app la coge sola.
- La beta `beta-20260728` en otros PCs — **sobre todo en una máquina con gráfica
  integrada**, que es lo menos probado de todo.

## FASE 1 — Desbloquear la venta (crítica, EN CURSO)
*Meta: quitar TODO bloqueante legal/empaquetado para que una beta PUEDA
distribuirse y cobrarse.*

### Hecho

- [x] **FFmpeg LGPL resuelto y blindado** (motor ProRes): build `lgpl-shared` de
      BtbN, procedencia con SHA256 por DLL en `ffmpeg-lgpl/ffmpeg-BUILD.txt`, y
      un **guardián en `publish.ps1`** que verifica las DLL ya copiadas al
      paquete y aborta borrándolas si el veredicto no es LGPL.
- [x] THIRD-PARTY-NOTICES completo: Vortice, Snappier, FFmpeg.AutoGen y las
      bibliotecas de FFmpeg (LGPLv3) con procedencia y oferta de fuente.
      `licenses/` con LGPL-2.1, LGPL-3.0 y GPL-3.0.
- [x] `publish.ps1` final + ZIP + show demo + LEEME-BETA.
- [x] Repo privado en GitHub (`zivko17/WinCue`, rama `pruebas-v2`).
- [x] Borrador de **EULA de evaluación** (`docs/legal/EULA-EVALUACION.md`).

### 1. libmpv LGPL — ✅ RESUELTO (2026-07-28)

WinCue distribuye ya la build **`mpv-dev-lgpl`** de `zhongfly/mpv-winbuild`,
verificada en cuatro frentes (integridad SHA256, autodeclaración `-Dgpl=false`
de la propia DLL, la receta `compile-lgpl-libmpv.patch` leída línea a línea, y
la desaparición en el binario de todas las cadenas de identidad de x264/x265/
xvid/rubberband/dvdnav/dvdread/zvbi/avisynth que sí lleva la GPL antigua).
Paridad funcional comprobada con los medios de `tests/`: H.264 y HAP decodifican
con idénticos códec, dimensiones y audio.

Lo que quedó montado:
- `tools/libmpv-lgpl/obtener-libmpv-lgpl.ps1` — release **fijada** (no "latest"),
  aborta si el SHA256 no cuadra, deja `libmpv-lgpl/libmpv-BUILD.txt` con
  procedencia, SHA256 y **commit de la receta** (sin él no se puede cumplir la
  oferta de fuente de la LGPL).
- `publish.ps1` copia esa DLL —ya no la GPL de `..\WinCue`— y **la verifica
  dentro del paquete**, borrándola y abortando si no es LGPL. Probado en los dos
  sentidos: acepta la LGPL y rechaza la GPL.
- `MpvNative` busca `libmpv-lgpl/` **antes** que `WinCue/`, para que en
  desarrollo se pruebe contra la misma DLL que recibe el cliente.

**Dos cosas honestas:** (a) el LTO borra `avcodec_configuration` del binario, así
que la ausencia de `--enable-gpl` en el FFmpeg estático de dentro se infiere de
la receta y de la ausencia de componentes, no se lee directamente; (b) **hwdec
(d3d11va) sigue sin verificar** — necesita salida de vídeo real. Ver el bloque de
verificación visual.

<details>
<summary>Cómo se llegó aquí (el camino, por si hay que rehacerlo)</summary>

**Dato nuevo del 2026-07-28 que invalida lo que decía este repo:** la auditoría
del 2026-07-18 afirmaba que *"no existen builds LGPL prebuilt para Windows"*.
**Es falso hoy.** Esa conclusión se apoyaba solo en el issue #586 de
`shinchiro/mpv-winbuild-cmake` (abierto el 2024-01-04, sigue sin respuesta) —
pero shinchiro no es el único que compila mpv para Windows.

**`zhongfly/mpv-winbuild` publica una variante LGPL, a diario, desde su CI
pública de GitHub Actions:**

- Assets `mpv-dev-lgpl-x86_64-<fecha>-git-<sha>.7z` (~27,7 MB), más `aarch64` y
  `x86_64-v3`. Presentes en todas las releases recientes (última vista:
  `2026-07-27-48e6c35c0e`).
- Lo que declara el proyecto: *"libmpv under LGPLv2.1+ license, which disables
  LGPLv2.1+ incompatible packages and statically links to ffmpeg under LGPLv3"*.
- Descargo del autor: *"I'm not a lawyer and can't guarantee that I've disabled
  all LGPL-incompatible packages, use at your own risk."*

Es **exactamente el mismo patrón que ya resolvió FFmpeg**: coger una CI pública
reproducible, fijar una build concreta, verificarla con herramienta propia y
guardar la procedencia. No hay que inventar nada: hay que repetirlo.

**Plan (una sesión, en vez de una novena CI):**

1. Descargar `mpv-dev-lgpl-x86_64` de una **release fija** (no "latest"), y
   dejar un `tools/libmpv-lgpl/obtener-libmpv-lgpl.ps1` calcado del de FFmpeg.
2. `WINCUE_LIBMPV=<ruta>` + `dotnet run --project tools/libmpv-lgpl/verificar-licencia`
   → exigir **`VEREDICTO: LGPL (gpl=false)`**.
3. **Segunda fuente independiente**, como en FFmpeg: la línea de configure del
   FFmpeg embebido no debe traer `--enable-gpl` ni `--enable-nonfree`.
4. `licenses/libmpv-BUILD.txt` con URL exacta de la release, commit de mpv,
   SHA256 del .7z y de la DLL.
5. **Extender el guardián de `publish.ps1` a libmpv**: hoy solo vigila FFmpeg.
   Que aborte la publicación igual que con las DLL de ProRes.
6. Prueba funcional con show real por la ruta mpv: hwdec, keep-open, render API,
   geometría, y el PLL del audio híbrido.
7. Si pasa todo → archivar el fork y sus 8 CI rojas.

**Riesgos que hay que asumir con los ojos abiertos:**

- El FFmpeg va **estático dentro** de la DLL: la obligación de ofrecer el fuente
  exacto viaja con ella. Hay que archivar la receta (commit del repo de zhongfly),
  no solo el enlace de descarga.
- Son builds de **git master**, no de release etiquetada de mpv. Fijar una y no
  ir detrás de la nightly.
- El descargo del autor no nos exime de nada: lo que vale es nuestra
  verificación. Por eso van dos fuentes independientes + guardián en publish.
- **Plan B** si la verificación falla: reintentar la CI propia, que ya tiene el
  fork con los 3 cambios aplicados (`tools/libmpv-lgpl/README.md`).

</details>

**Nota sobre la beta ya repartida:** `beta-20260728` se empaquetó con la
`libmpv-2.dll` **GPL** de siempre. Entregarla a quien la prueba es distribución,
y con GPL eso arrastra obligación de fuente. Asumible en un círculo cerrado de
pruebas, pero **el próximo paquete debe rehacerse** con la LGPL — `publish.ps1`
ya no deja empaquetar de otra forma.

### 2. CI en GitHub Actions — NO EXISTE

Confirmado 2026-07-28: **no hay `.github/` en el repo.** Ningún workflow. Lo que
hoy protege la calidad es la disciplina manual (build → test → smoke), que
funciona pero no sobrevive a un despiste ni a un segundo par de manos.

Mínimo que hay que montar:
- `build + dotnet test` (la suite completa) en cada push a `pruebas-v2`. Sin
  cifra escrita a mano: cada vez que se anota, caduca en el siguiente commit.
- Smoke (`--smoke`, exit 0).
- Después: gate de `TreatWarningsAsErrors` y `nuget-license` en cada cambio de
  dependencias.

### 3. Decisiones de Andrés (no son código)

- [ ] **Firma de código / SmartScreen.** Certificado OV (~200-400 €/año) o EV.
      Sin firma, SmartScreen espanta a cualquier comprador. Para la beta basta
      el truco del "Ejecutar de todas formas"; para vender, no.
- [ ] **Modelo de licencia y activación.** ¿Por máquina? ¿Prueba con marca de
      agua? ¿Cómo se protege? Condiciona el empaquetado, no el motor.
- [ ] **EULA comercial** (la de evaluación ya está en borrador) + política de
      privacidad para la web.
- [ ] **Colisión de marca "WinCue"**: EUIPO/TMview y USPTO, clases 9/42 (existe
      un teleprompter histórico con ese nombre). Decidir antes de dominio y web.

### 4. Antes del primer .exe público

- [ ] Revisar `CA5392` (rutas de búsqueda de DLL, auditoría 01) — endurecer la
      carga de libmpv para el escenario instalador (DLL planting).
- [ ] Instalador (MSIX/Inno/WiX) que incluya THIRD-PARTY-NOTICES.md + `licenses/`.
- [ ] Canal de actualizaciones y aviso de versión.

## FASE 2 — Madurez del motor y del producto (1-3 meses)

- [x] **Fase híbrida de audio: HECHA.** El rack (EQ/comp/VU/limitador) gobierna
      el audio de TODO vídeo, no solo HAP: mpv mudo (`aid=no`) + pista por
      AudioEngine + PLL. Kill-switch `WINCUE_VIDEO_AUDIO=mpv`. Extiende el
      diferencial nº2 a ProRes, H.264, todo.
- [x] Localización ES/EN de la UI operativa (queda el repaso visual, arriba).
- [ ] **Streaming del audio espejo** (paso 4): hoy el espejo decodifica el
      archivo ENTERO a RAM (~23 MB/min), y por eso el híbrido tiene un tope de
      duración (`video_audio_max_minutes`, 20 min). Sin esto, los vídeos largos
      caen a la ruta mpv clásica.
- [ ] Modo canvas multi-salida, varispeed, **validación en máquinas solo-iGPU**.
- [ ] ProRes: prefetch de disco real y medida en solo-iGPU.
- [ ] Fase 4 de assets: panel de Media con progreso (conversión visible).
- [ ] Presets de EQ/compresor.
- [x] `TreatWarningsAsErrors` — **resuelto por el CI** (2026-07-29): se fuerza
      allí, no en `Directory.Build.props`, para no interrumpir el trabajo local.
- [ ] **Deuda que aparecerá al subir de .NET 8.** El primer CI compiló sin
      querer con el SDK de .NET 10 (no había `global.json`; `dotnet` coge el más
      alto instalado) y, como `AnalysisLevel` es `latest-all`, saltaron reglas
      que aquí no existen: **~60 `CA1515`** (clases de test públicas que podrían
      ser internas — xUnit v2 las quiere públicas, así que probablemente toque
      silenciar la regla en los proyectos de test, no cambiar el código) y **2
      `CS8618`** en `src/WinCue.Video/Gpu/RenderGraph.cs:39-40` (`Renderer` y
      `Pool` no anulables sin inicializar) — este último **es un agujero real**
      del compositor, aunque hoy no lo dispare ningún SDK que usemos. Ya está
      fijado el SDK en `global.json`, así que no molesta; pero el día que se
      suba de major, esto es lo que hay esperando.
- [ ] Pulir lo que salga del soak y de los primeros shows reales.

## FASE 2b — Compositor D3D11 (escrito, sin conectar)

`src/WinCue.Video/Gpu/` está committeado y **no lo llama nadie**. Activarlo es
reconectar toda la salida de vídeo. Su propio plan (`docs/plan-compositor.md`)
exige antes paridad de canvas/geometría/PANIC/standby/handover, genlock, soak de
4 h y verificación visual. **Camino obligado: F2 primero** (ruta nueva tras flag,
la vieja por defecto), F3 después. Nunca directo a F3.

## FASE 3 — Beta pública → 1.0 (3-6 meses)

- [~] **Beta arrancada**: `beta-20260728` publicada en Releases (ZIP 142 MB),
      pendiente de pruebas en otros PCs. Siguiente paso: 5-10 técnicos reales
      (teatro, corporate, banda, iglesia) — **con la libmpv LGPL dentro**.
- [ ] Bucle feedback → fixes. Telemetría de crashes (con consentimiento).
- [ ] Más soak, edge cases de archivos reales (la cola larga que mpv domina y
      nuestro motor aún no).
- [ ] Web + precio + canal de distribución.
- [ ] Multiviewer de salidas.
- [ ] **1.0.**

## FASE 4 — Crecimiento / nivel 2 (6-18 meses, según tracción)
*"Watchout de bolsillo". Solo si el nivel 1 tracciona.*

- ~~ProRes con decode propio.~~ **ADELANTADO Y HECHO** (2026-07-27): el
  decodificador de FFmpeg va en proceso alimentando nuestra tubería, con
  interruptor en Ajustes y kill-switch `WINCUE_PRORES_ENGINE=0`. Se adelantó
  porque el laboratorio del paso 1 lo dejó todo medido y la elegibilidad
  reutiliza la de HAP casi entera. Ver `docs/arquitectura-motor-video.md` y el
  paso 2 de `tools/prores-lab/INFORME.md`.
- NDI, timecode chase robusto, quizá 10-bit.
- Lo que pidan los usuarios que YA pagan (no lo que imaginemos).

---

## Decisiones abiertas (de Andrés)

1. ~~**Timing de la beta.**~~ **RESUELTA**: la beta salió el 2026-07-28 sobre la
   ruta mpv, con el motor propio activo para HAP y ProRes. Se arrancó el bucle de
   feedback sin esperar a que el motor fuera el buque insignia.

2. ~~**libmpv LGPL.**~~ **RESUELTA** el 2026-07-28 con la build prebuilt de
   zhongfly, verificada y con guardián. El fork con las 8 CI rojas queda
   archivado como plan B (`tools/libmpv-lgpl/README.md`).

3. **Modelo de negocio.** Precio, tipo de licencia, protección/activación,
   prueba. No condiciona el código a corto plazo, pero sí el empaquetado de F1.

4. **Firma de código.** Certificado OV o EV, y cuándo pagarlo.

## Métrica de éxito por fase
- ~~F0: un show HAP encadenado corre 30 min sin una sola cuenta negativa ni cuelgue.~~ ✅
- F1: existe un ZIP que un desconocido puede instalar y arrancar, **legalmente
  limpio** (= sin una sola DLL GPL dentro) y firmado.
- F2: 4 h de soak sin fuga ni crash; vídeo largo con VU por cue (streaming del
  espejo); validado en una máquina solo-iGPU.
- F3: 5 técnicos usándolo en bolos reales y volviendo a usarlo.
- F4: la primera factura.
