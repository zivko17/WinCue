# WinCue — Memoria del proyecto y análisis de viabilidad

**Fecha:** 17 de julio de 2026 · **Rama:** `pruebas-v2` · **Autor del análisis:** asistente de desarrollo del proyecto

> Documento de auditoría: qué es WinCue hoy, qué solidez tiene, qué hueco de
> mercado ocupa y si puede llegar a ser rentable. Las cifras de mercado son
> **estimaciones razonadas sin datos propios** y están marcadas como tales.

---

## Resumen ejecutivo

WinCue es un **reproductor de cues de audio y vídeo para espectáculo en
directo** (modelo QLab + timeline y canvas estilo Watchout) nativo de
**Windows**, escrito en C#/.NET 8. En 5 días de desarrollo intensivo
(2026-07-13 → 17, 72 commits) se ha completado el port desde el prototipo
Python y se ha superado con creces: ~14.900 líneas de producto, ~4.100 de
tests (255 en verde), y un conjunto de capacidades que **ninguna aplicación
Windows ofrece junta a precio de software de escritorio**.

**Veredicto corto:** técnicamente viable ya (utilizable en shows reales
propios hoy); comercialmente, hueco real y mal servido — rentable como
producto de nicho/micro-ISV si se invierte en confianza y distribución;
improbable como negocio principal a corto plazo. El activo más valioso es que
"QLab para Windows" es una demanda recurrente y real; el riesgo más grande no
es técnico: es la **confianza** (nadie arriesga un directo con software sin
reputación) y el **soporte unipersonal**.

---

## 1. El producto hoy (inventario funcional)

**Motor de cues (modelo QLab)** — cadenas auto-continue/follow, grupos
(ENTER/TIMELINE/GO_NEXT/RANDOM), pre/post-wait, triggers por cue (hotkey,
MIDI, timecode, wall-clock), retrigger configurable, concurrencia real,
undo/redo, renumeración, duplicar/copiar/pegar, armado por cue.

**Audio** — multi-bus WASAPI/ASIO con salida por cue, EQ paramétrica de 4
bandas + HPF/LPF + compresor por clip (con medidor GR en vivo), RTA,
normalización LUFS, varispeed 0,25–4×, fades sample-accurate, audición local
por dispositivo aparte, VU con clip-hold.

**Vídeo** — libmpv; modo clásico por HWND y **compositor GPU** (superficie
D3D por cue, opacidad real); crossfades reales en la misma pista y entre
capas; **multi-salida** (hasta 4 monitores) y **modo canvas estilo Watchout**
(las salidas son recortes de un lienzo común, editor visual en Settings, un
solo render con sync perfecta entre monitores); **GO instantáneo** (precarga
en pausa del standby); fades corregidos contra la posición real del motor.

**Timeline** — proyección del show completo, 3–8 pistas elásticas, edición
absoluta (mover un bloque no desplaza al resto), crossfade automático por
capa (y explícito entre capas), cues de control reflejados (STOP trunca,
FADE dibuja su rampa, START crea fantasma), reproducir desde el cursor con
seek real, scrub al PVW, mini-ondas de audio.

**Control externo** — OSC in/out (parser propio), MIDI in con learn, chase
de MIDI Timecode, disparo por reloj de pared.

**Operación** — blackout con fade (tecla B), identificación de salidas,
patrones de test, relocalización de medios, panel de avisos persistente,
backups rotados + autosave + recuperación, monitores PGM/PVW en vivo
redimensionables, escala de fuente para FOH, modo SHOW protegido.

**Distribución** — `publish.ps1` genera un .exe single-file autocontenido
(+ libmpv al lado). Shows y settings 100 % compatibles con el WinCue Python.

## 2. Solidez técnica

| Indicador | Valor |
|---|---|
| Código de producto | ~14.900 líneas C# (38 archivos, 5 proyectos) |
| Código de tests | ~4.100 líneas · **255 tests en verde** |
| Cobertura E2E | El show sintético de referencia se reproduce entero contra el controlador en cada pasada de tests |
| Dependencias | .NET 8 LTS, NAudio (MIT), libmpv (P/Invoke propio), FFmpeg en PATH — sin NuGet exóticos |
| Arquitectura | Capas Core/Audio/Video/Controller/App con contratos testables (motores falsos, reloj virtual) |

**Deuda y riesgos técnicos conocidos:**
- Sesión de estrés pendiente: el compositor crea/destruye pipelines GL+D3D
  por cue; falta demostrar horas de show sin fugas de RAM/VRAM.
- El interop GL↔D3D depende del driver (hay fallback automático a HWND, con
  aviso); NVDEC limita los vídeos simultáneos con decodificación por
  hardware en GeForce antiguas.
- `MainController` concentra mucha lógica (~2.500 líneas): funciona y está
  testeado, pero pide troceado si el proyecto crece.
- Windows-only por diseño (WPF) — es también su posicionamiento.
- **Bus factor 1**: una sola persona conoce y mantiene el proyecto.

## 3. Mercado y competencia

El estándar de facto del sector (teatro, salas, eventos) es **QLab, que solo
existe en macOS**. La petición "QLab para Windows" es recurrente desde hace
años en foros técnicos. Lo que hay en Windows:

| Competidor | Precio aprox.* | Fortaleza | Debilidad frente a WinCue |
|---|---|---|---|
| Show Cue System (SCS) | ~65–400 US$ | Veterano, fiable en audio | Vídeo básico, sin compositor/canvas, UI anticuada |
| Multiplay | Gratis | Sencillo | Audio-céntrico, desarrollo casi parado |
| CasparCG | Gratis | Potente en broadcast | Otro paradigma (playout), curva enorme, sin cue-list teatral |
| Resolume | ~300–800 € | Referencia VJ | No es cue-list de teatro; audio pobre |
| Watchout / disguise | Miles de € | Canvas multi-display pro | Precio y hardware de otra liga |
| vMix / OBS | 0–1.200 US$ | Streaming | No es show control |

\* Precios orientativos de memoria, a verificar antes de posicionar.

**El hueco de WinCue:** cue-list QLab **+** timeline editable **+** canvas
multi-salida Watchout **+** procesado de audio por clip, en una sola
aplicación Windows de escritorio. Esa combinación no la ofrece nadie en ese
rango de precio.

**Público objetivo:** teatro amateur y semiprofesional, salas pequeñas y
medianas, iglesias/worship, eventos corporativos, educación (escuelas de
técnicas escénicas), técnicos freelance con PC.

## 4. ¿Puede ser rentable?

Modelos posibles (no excluyentes):
1. **Licencia perpetua** 149–299 € (referencia SCS/QLab v4) con año de
   actualizaciones.
2. **Freemium**: audio completo gratis (tracción y confianza) + licencia de
   pago para vídeo/compositor/canvas/multi-salida — es el patrón QLab.
3. **Alquiler por evento/semana** para producciones puntuales.
4. Servicios (instalaciones, soporte, formación) como complemento.

**Escenarios orientativos** (estimaciones sin datos propios, solo para
dimensionar):

| Escenario | Ventas/año | Ingreso bruto (199 €) | Lectura |
|---|---|---|---|
| Tracción inicial | 30–80 | 6.000–16.000 € | Hobby rentable; cubre costes y algo más |
| Nicho consolidado (2–3 años) | 200–500 | 40.000–100.000 € | Micro-ISV viable a tiempo parcial/total |
| Techo realista del nicho | ~1.000+ | 200.000 €+ | Requiere reputación, soporte y comunidad; nivel SCS tras décadas |

**La barrera real no es el producto, es la confianza y la distribución**: en
directo nadie adopta software sin historial. El camino probado del sector:
regalar mucho al principio (o freemium generoso), demos en vídeo, shows
reales documentados, presencia en foros técnicos y 1–2 años de reputación.

## 5. Qué falta para poder venderlo (checklist)

**Legal/licencias (bloqueante antes de distribuir):**
- [ ] libmpv: usar build **LGPL** (existe) — el enlace ya es dinámico ✓;
      FFmpeg: build LGPL. NAudio es MIT ✓. Documentar atribuciones.
- [ ] Nombre/marca: verificar colisiones de "WinCue" y registrar dominio.
- [ ] EULA + política de datos (aunque no haya telemetría).

**Producto comercial (~1–2 meses de trabajo no-features):**
- [ ] Instalador firmado (certificado code-signing) y canal de updates.
- [ ] Crash-reporting opt-in.
- [ ] Manual de usuario + 4–6 vídeos demo (el canvas y los crossfades venden solos en vídeo).
- [ ] Web con descarga demo + licenciamiento/activación.
- [ ] Beta con 5–10 técnicos reales y shows documentados.

**Solidez pendiente:** sesión de estrés (RAM/VRAM en shows largos), LTC por
audio si el público objetivo lo pide.

## 6. Veredicto

- **Viabilidad técnica: alta.** El producto ya hace hoy, con tests, lo que
  promete; sirve de herramienta propia aunque nunca se venda (ese valor ya
  está cobrado).
- **Viabilidad comercial: real pero de nicho.** El hueco existe y está mal
  servido; con freemium + confianza construida, un ingreso complementario de
  micro-ISV es plausible en 1–2 años. Como negocio principal, solo tras
  reputación demostrada.
- **Recomendación:** (1) usarlo en shows reales propios — es la mejor
  auditoría posible y genera el material de demo; (2) decidir el modelo
  (freemium generoso es el que mejor casa con la barrera de confianza);
  (3) resolver el checklist legal **antes** de distribuir el primer .exe.
