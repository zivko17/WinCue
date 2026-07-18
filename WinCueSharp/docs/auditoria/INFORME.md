# WinCue — Informe ejecutivo de la auditoría técnica

**Fecha:** 2026-07-18 · **Rama:** `pruebas-v2` · **Alcance:** fases 0-5 (inventario, análisis estático, arquitectura, cobertura, estrés, licencias) · **22 commits de auditoría**, suite en verde tras cada uno.

Para el detalle: `00-inventario.md`, `01-analisis-estatico.md`, `02-arquitectura.md`, `03-cobertura.md`, `04-estres.md` y `../legal/`.

---

## 1. Estado de salud del código

| Métrica | Antes | Después |
|---|---|---|
| Tests | 255 en verde | **286 en verde** (+31, todos en ruta crítica) |
| Warnings de compilación | analizadores por defecto, 2 warnings ignorados | **`AnalysisLevel=latest-all` y CERO warnings** |
| Cobertura (código testeable) | desconocida | **77,3 % líneas / 71,4 % ramas**, medible con `scripts/coverage.ps1` |
| Cobertura real del producto | desconocida | **~35 %** — `WinCue.App` (53 % del código) sigue a 0 % |
| `MainController` | 2 383 líneas, 16 responsabilidades | **1 687 líneas + 5 servicios** extraídos con API intacta |
| Fugas de recursos conocidas | 0 conocidas (no medidas) | 4 arregladas (2 wait handles POR CUE de vídeo, renderer huérfano en error, handle de audio, stackalloc OSC) |
| Gobernanza | sin .editorconfig, sin props comunes, sin CI | .editorconfig + Directory.Build.props + tooling (cobertura, estrés, licencias). **CI sigue sin existir** |
| Cumplimiento de licencias | sin inventario | NuGet 100 % MIT verificado; nativas analizadas; notices + Acerca de + empaquetado |

**Lo que está peor de lo que creías — sin anestesia:**

1. **Tu "255 tests en verde" sobrevaloraba la seguridad real.** El 53 % del producto (toda la capa WPF: timeline visual, inspector, ventanas de salida, la fontanería de red UDP/MIDI) no tiene ni un test y ni siquiera aparecía en las métricas. Los tests protegen el motor, no la aplicación que el operador toca.
2. **La `libmpv-2.dll` que distribuyes es casi seguro GPL, no LGPL.** Tu checklist de la memoria decía "usar build LGPL (existe)" como si fuera un trámite; la realidad es que la DLL actual (mpv v0.41.0-524, build de comunidad) es GPL por defecto, y con GPL el enlace dinámico NO te protege: vender WinCue cerrado con ella sería ilegal. Es EL bloqueante.
3. **La pregunta "¿aguanta horas sin fugarse?" sigue sin respuesta.** La auditoría arregló fugas evidentes y montó toda la instrumentación, pero la sesión larga no se ha ejecutado. Hasta que corra 4 h, la fiabilidad en show largo es una hipótesis.

## 2. Riesgos por gravedad

| # | Riesgo | Impacto en un directo real | Esfuerzo | ¿Bloquea distribuir? |
|---:|---|---|---|:---:|
| 1 | **libmpv GPL** | Ninguno en el show; te bloquea el negocio (venta cerrada ilegal) | 4-8 h (conseguir/compilar build LGPL + verificar features con el show de prueba) | **SÍ** |
| 2 | ~~Fugas RAM/VRAM sin medir en shows largos~~ **RESUELTO el mismo día** (auditoría 05): CPU sano, VRAM física estable — la "fuga" era un contador de Windows defectuoso. De propina: pool de pipelines GPU + cumplimiento del contrato D3DImage. Pendiente: verificación visual del refactor + soak de 4 h opcional | — | 4-5 h (ya invertidas) | Ya no |
| 3 | **WinCue.App sin tests (fontanería UDP/MIDI incluida)** | Una regresión de UI o del transporte de red se descubre EN el bolo | Alto (estrategia de test de UI); mitigable por partes | No (riesgo aceptado y documentado) |
| 4 | **Sin CI** | El "todo verde" depende de disciplina manual; una regresión puede entrar sin señal | 1-2 h (GitHub Actions: build+test+warnings-as-errors) | No, pero es el mejor €/hora del proyecto |
| 5 | **Motores reales sin test** (`VideoEngine.PrepareStandby`, `AudioEngine.Fade`…) | El GO instantáneo y los FADE dependen de código solo probado con dobles | 3-6 h (estrategia de test de motor con mpv real) | No |
| 6 | **Marca "WinCue"** (colisión con teleprompter histórico) | Ninguno técnico; rebranding forzoso a posteriori sería caro | 2-3 h de búsqueda EUIPO/USPTO + decisión | SÍ (antes de invertir en marca/web) |
| 7 | Escala absoluta del RTA (−73 dB donde debería ~−6) | Cosmético: el RTA funciona pero sus dB no son dBFS | 1 h | No |
| 8 | Bus factor 1 | Sin cambios: sigues siendo tú solo | — (mitigado: docs de auditoría + servicios extraídos lo hacen traspasable) | No |

## 3. Resuelto en esta auditoría

- **Fugas reales arregladas:** los 2 wait handles del SO que `MpvGpuRenderer` filtraba POR CADA CUE de vídeo; el renderer huérfano si fallaba el cableado de sinks (violaba la regla mpv "render context antes que core" → crash nativo en potencia); el handle WASAPI/ASIO filtrado en reintentos de `EnsureOutput`; el `stackalloc` en bucle de `OscPacket` (desbordamiento de pila latente).
- **Suelo de calidad:** análisis Roslyn al máximo con cero warnings y cubo cosmético silenciado con justificación regla a regla; estilo codificado en `.editorconfig` deducido del código real.
- **Arquitectura:** `MainController` de 2 383 → 1 687 líneas extrayendo `ShowFileService`, `CueListEditor`, `RemoteRouter`, `UndoService` y `CanvasLayout` — API pública intacta, cero cambios en UI/tests, y el núcleo como dueño único de `_running`. El proyecto ya es traspasable a otro desarrollador.
- **+31 tests de ruta crítica** que no existían: fin natural de vídeo y su encadenado, errores de vídeo, pausa/EQ/RTA/GR del provider, el cable OSC completo (incl. el método del fix CA2014, que estaba a 0 %), corrección por deriva de los fades, y el scheduler REAL de producción.
- **Instrumentación permanente:** `scripts/coverage.ps1` (cobertura reproducible), `scripts/stress.ps1` + show de tortura + modo `--stress` (probado en real: detecta vueltas, escribe CSV), inventario de licencias regenerable.
- **Cumplimiento empaquetado:** THIRD-PARTY-NOTICES.md + `licenses/` en el publish, botón ⓘ Acerca de, y el análisis honesto de las nativas.
- Deriva de documentación detectada (CONTEXTO decía 163 tests y fase 6 pendiente cuando había 255 y OSC/MIDI hecho) — los docs de auditoría son ahora la referencia.

## 4. Lo que queda

**Bloqueante para vender:**
1. Build **LGPL de libmpv** (+ `licenses/LGPL-2.1.txt` oficial + origen de la build documentado).
2. **Sesión de estrés de 4 h** ejecutada y analizada (y el parche que salga de ella).
3. Verificación de **marca/nombre** antes de comprometerse públicamente.
4. EULA + firma de código + instalador (checklist en `docs/legal/checklist-distribucion.md`).

**Importante:**
5. **CI** (build + test + `TreatWarningsAsErrors`, que ahora es gratis al estar a cero).
6. Estrategia de test para los motores reales (standby de vídeo, Fade de audio).
7. Actualizar xunit (limpia las 73 transitivas con EULA antigua) y regenerar el inventario.

**Deseable:**
8. Tests de la capa App (empezando por `RemoteInputService`, lo único de App en ruta crítica).
9. Deuda de test menor anotada (RotateBackups, MaxUndo, snapshot corrupto, bordes ParseRegion).
10. Escala del RTA; pausa del timer RTA cuando la pestaña EQ no está visible.
11. `CueRuntime` (paso 6 de la 02) — solo si el proyecto crece.

## 5. Las próximas 20 horas

Mi recomendación, en orden estricto:

| Horas | Qué | Por qué |
|---:|---|---|
| 1-2 | **CI mínimo** (Actions: build + 286 tests + warnings-as-errors) | Es lo único que protege TODO lo demás que hagas a partir de hoy. Cada hora invertida después de esta rinde más. |
| 3-7 | **Sesión de estrés 4 h + análisis** (la infraestructura ya está: `.\scripts\stress.ps1 -Hours 4` y me traes los datos) | Es TU riesgo nº1 operativo y el dato que decide si WinCue puede hacer un bolo largo HOY. Además valida en real los fixes de fugas de la fase 1. |
| 8-15 | **libmpv LGPL**: conseguir la build, verificarla con el show de prueba, documentar origen, texto LGPL | Único bloqueante legal duro. Sin esto no hay producto vendible, por muy sólido que sea el código. Hazlo ya porque puede destapar features que pierdas (y cuanto antes lo sepas, mejor). |
| 16-18 | **Marca**: búsqueda EUIPO/USPTO de "WinCue" + decisión de nombre + dominio | 3 horas que evitan el error más caro de la lista (rebranding con producto publicado). |
| 19-20 | **Usar WinCue en un show real propio** con la instrumentación de estrés activada | Tu propia memoria lo dice: es la mejor auditoría posible y genera el material de demo. Ahora además cada sesión real te deja un CSV de métricas gratis. |

Lo que NO haría todavía: tests de UI masivos (caro, y el riesgo está aceptado), el refactor del núcleo (`CueRuntime` — 1 687 líneas coherentes no lo justifican), ni tocar features nuevas hasta cerrar los puntos 1-3. El producto ya hace de sobra; lo que le falta no es código, es **confianza medida** (estrés), **legalidad** (libmpv) y **red de seguridad** (CI).
