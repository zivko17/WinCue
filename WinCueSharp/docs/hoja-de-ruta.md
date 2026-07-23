# WinCue — Hoja de ruta

*Documento vivo. Norte fijado 2026-07-23.*

## El norte

**Ser "el QLab de Windows".** No perseguir la tabla entera de media servers:
clavarse en el hueco real y mal servido (QLab no existe en Windows; SCS es
anticuado, Multiplay está muerto, PVP es otra liga de precio). Un reproductor
de cues audio/vídeo para directo, Windows, con tres diferenciales afilados:

1. **Motor HAP nativo** (ruta propia, sin depender de mpv para el códec estrella).
2. **Mesa de audio por clip** (EQ + compresor + limitador + VU real — a la altura
   de vMix, por encima de QLab en esto).
3. **Timeline + control de directo** (OSC/MIDI/timecode, PANIC, crash-safety).

Mercado que paga 400-1000 € por licencia: teatro, sala mediana, corporate AV,
iglesias, bandas, freelance técnico. La regla del sector: **no ganan los que
tienen más features, ganan los que NO FALLAN.** Todo el plan sirve a eso.

## El cuello de botella real

Ya no es escribir código (hoy hemos parido un motor de media en un día). El
límite ahora es **el tiempo de Andrés para validar + las decisiones de negocio
que una IA no puede tomar.** Las fases avanzan cada vez más limitadas por eso,
no por velocidad de desarrollo. El plan lo asume: separa lo que ejecuta Claude
de lo que solo puede hacer/decidir Andrés.

---

## FASE 0 — Cerrar el sprint de hoy (esta semana)
*Meta: todo lo construido hoy, verificado y estable.*

- [Claude · EN CURSO] Fix del bug de relojes HAP (fin natural que no llega →
  cuentas negativas, encadenado colgado, crossfades pegados) + garantía de fin.
- [Andrés] **Verificación visual** — la prueba de fuego: cue HAP end-to-end
  (VU en Mixer, EQ en vivo, fundido de opacidad, PANIC, encadenado con solape),
  RTA/EQ, panel Mixer. Nada está "hecho" hasta que lo veas.
- [Claude] Soak de 4 h con el motor nuevo dentro → analizar contadores/gcdumps.
- [Claude] Cerrar la sesión paralela de `audio_device_name` al arrancar.

## FASE 1 — Desbloquear la venta (crítico, en paralelo)
*Meta: quitar TODO bloqueante legal/empaquetado para que una beta PUEDA salir.
Nada de esto es opcional para cobrar. Mucho depende de decisiones de Andrés.*

- **libmpv LGPL** — el bloqueante histórico (CI del fork, 8 iteraciones rojas).
  DECISIÓN ABIERTA (ver abajo). Cuanto más cubra el motor propio, menos pesa,
  pero mpv seguirá siendo el fallback universal → hay que resolverlo igual.
- THIRD-PARTY-NOTICES: añadir **Vortice** (nuevo por el motor HAP) + texto LGPL.
- **EULA + modelo de licencia/activación** (¿licencia por máquina? ¿prueba con
  marca de agua? ¿cómo se protege?) — DECISIÓN ABIERTA.
- Firma de código / mitigar SmartScreen (el "Ejecutar de todas formas" no basta
  para vender con seriedad; valorar certificado).
- publish.ps1 final + ZIP + show demo incluido.
- Repo privado en GitHub + CI (Actions: build + test + warnings-as-errors).

## FASE 2 — Madurez del motor y del producto (1-3 meses)
*Meta: la app es de fiar y el motor propio es un diferencial de verdad.*

- **Motor HAP paso 4**: streaming del audio (vídeos largos sin comerse la RAM),
  modo canvas multi-salida, varispeed, validación en máquinas solo-iGPU.
- **Fase híbrida de audio**: rack de audio (EQ/comp/VU) para TODO vídeo, no solo
  HAP — mpv mudo + pista por AudioEngine + PLL. Reutiliza el reloj maestro y el
  stream espejo que el paso 3 ya construyó. Extiende el diferencial nº2 a ProRes,
  H.264, todo.
- Fase 4 de assets: panel de Media con progreso (estados de conversión visibles).
- Localización ES/EN completa (cazar literales que quedan).
- Presets de EQ/compresor. TreatWarningsAsErrors (gratis, build ya a cero).
- Pulir lo que salga del soak y de los primeros shows reales.

## FASE 3 — Beta pública → 1.0 (3-6 meses)
*Meta: usuarios reales, bucle de feedback, primeras ventas.*

- Beta a 5-10 técnicos reales (teatro, corporate, banda, iglesia).
- Bucle feedback → fixes. Telemetría de crashes (con consentimiento).
- Más soak, edge cases de archivos reales (la cola larga que mpv ya domina y
  nuestro motor aún no).
- Web + precio + canal de distribución.
- Multiviewer de salidas.
- **1.0.**

## FASE 4 — Crecimiento / nivel 2 (6-18 meses, según tracción)
*"Watchout de bolsillo". Solo si el nivel 1 tracciona.*

- ProRes con decode propio (siguiente códec del motor).
- NDI, timecode chase robusto, quizá 10-bit.
- Lo que pidan los usuarios que YA pagan (no lo que imaginemos).

---

## Decisiones abiertas (de Andrés)

1. **Timing de la beta.** ¿Preparar la beta YA sobre la ruta mpv (estaba lista,
   commit 8ea5a4d2) con el motor HAP como opción experimental opt-in, y madurarlo
   en campo? ¿O esperar a que el motor esté sólido para que sea el buque insignia?
   *Recomendación Claude:* sacar la beta pronto sobre mpv, motor propio como
   flag — arrancar el bucle de feedback/credibilidad cuanto antes; no dejar que
   el motor (que hoy aún tiene bugs) retrase el producto que ya funcionaba.

2. **libmpv LGPL.** La build propia lleva 8 CI rojas. Opciones: seguir peleándola
   / buscar una build LGPL de terceros ya hecha / replantear la estrategia legal.
   *Recomendación Claude:* dedicar UNA sesión a evaluar builds LGPL existentes
   antes de reintentar la nuestra; es tiempo mejor invertido que la 9ª iteración.

3. **Modelo de negocio.** Precio, tipo de licencia, protección/activación, prueba.
   No condiciona el código a corto plazo, pero sí el empaquetado de la Fase 1.

## Métrica de éxito por fase
- F0: un show HAP encadenado corre 30 min sin una sola cuenta negativa ni cuelgue.
- F1: existe un ZIP que un desconocido puede instalar y arrancar, legalmente limpio.
- F2: 4 h de soak sin fuga ni crash; ProRes con VU por cue.
- F3: 5 técnicos usándolo en bolos reales y volviendo a usarlo.
- F4: la primera factura.
