# Auditoría WinCue — Checklist de distribución (legal y cumplimiento)

**Fecha:** 2026-07-18 · **Estado:** los NuGet están limpios (ver `licencias-nuget.md`); el bloqueante es la build de libmpv.

---

## 1. Las DLL nativas — análisis hecho desde el repo

### libmpv (`libmpv-2.dll`) — 🔴 EL BLOQUEANTE

| Comprobación | Resultado |
|---|---|
| ¿Dónde se carga? | `src/WinCue.Video/MpvNative.cs` y `MpvRenderNative.cs` (`[LibraryImport("libmpv-2.dll")]` + `DllImportResolver` propio que busca la DLL junto al exe / subiendo directorios / env `WINCUE_LIBMPV`) |
| ¿Qué binario se distribuye? | `publish.ps1` copia `..\WinCue\libmpv-2.dll` (121 MB) junto al exe |
| ¿Enlace dinámico? | **SÍ** ✓ — P/Invoke con carga en tiempo de ejecución; la DLL es sustituible por el usuario (cumple el mecanismo de la LGPL §6, que exige que el usuario pueda reemplazar la biblioteca) |
| ¿La build actual es LGPL o GPL? | **GPL — CONFIRMADO** (2026-07-18, vía `mpv-configuration` de la propia DLL): build de `shinchiro/mpv-winbuild-cmake`, sin `-Dgpl=false`, con componentes que exigen GPL activados (dvdnav). Además la GPL obliga a entregar el CÓDIGO FUENTE del conjunto → incompatible con licencia propietaria de código cerrado. **CORRECCIÓN 2026-07-28: sí existe build LGPL prebuilt para Windows.** Lo que aquí se afirmaba ("no existen") se apoyaba solo en el issue #586 de shinchiro (abierto ene-2024, sigue sin respuesta), pero `zhongfly/mpv-winbuild` publica a diario `mpv-dev-lgpl-x86_64-*.7z` desde CI pública — LGPLv2.1+ con FFmpeg LGPLv3 estático. Ver el plan de verificación en `docs/hoja-de-ruta.md` (F1.1). Nota positiva: WinCue solo usa el núcleo LGPL (reproducción, render API, hwdec, keep-open, geometría), así que el cambio es funcionalmente seguro. |

**Consecuencia:** con una libmpv GPL, distribuir WinCue propietario **obliga a
licenciar WinCue bajo GPL** (el enlace dinámico NO exime del GPL; solo la
LGPL permite enlazar sin contaminar). **No se puede vender WinCue cerrado con
esta DLL.**

**Qué debes comprobar/hacer tú (en orden):**
1. **Confirmar la procedencia** de la DLL actual: ¿de dónde se descargó?
   (sourceforge de shinchiro / github zhongfly / mpv-winbuild). Si es una de
   esas → GPL, confirmado.
2. **Conseguir una build LGPL de libmpv**: compilar mpv con la opción de
   build `lgpl` (meson `-Dgpl=false`), enlazando un FFmpeg compilado SIN
   `--enable-gpl` ni componentes GPL (sin x264/x265/postproc…). Proyectos
   como mpv-winbuild-cmake permiten esta variante. Se pierden algunas
   features GPL de mpv — verificar con el show de prueba que todo lo que
   WinCue usa sigue funcionando (decodificación por hardware, keep-open,
   render API: sí están en el núcleo LGPL).
3. **Guardar el origen de la build** (URL + commit + opciones) en
   `licenses/libmpv-BUILD.txt` — la LGPL obliga a poder ofrecer el código
   fuente de LA BUILD distribuida (no el de WinCue).
4. Verificación rápida de cualquier DLL candidata: sus propiedades de
   archivo (versión) + `mpv_get_property(handle, "mpv-configuration")` o
   buscar en el binario la cadena `-Dgpl=false`/`lgpl`. Si la publican los
   autores con changelog, basta su declaración de licencia.

### FFmpeg — dos usos DISTINTOS, no confundirlos

**(a) `ffmpeg.exe` / `ffprobe.exe` del PATH — ✅ sin riesgo, como siempre**

| Comprobación | Resultado |
|---|---|
| ¿Cómo se usa? | **Subproceso** (`Process.Start("ffmpeg"/"ffprobe")`) en `AudioDecoder`, `MediaImporter`, `HapConvertService`, `WaveformControl`. Nunca enlazado. |
| ¿Se distribuye? | **No** — `publish.ps1` no incluye ejecutables; se exigen en el PATH del usuario |
| Riesgo | Invocar un binario GPL como proceso separado **no contamina** (programas separados). |

**(b) `avcodec-62.dll` / `avutil-60.dll` / `swresample-6.dll` — 🟢 resuelto (2026-07-27, motor ProRes propio)**

| Comprobación | Resultado |
|---|---|
| ¿Dónde se carga? | `src/WinCue.Video/ProRes/FFmpegLoader.cs` — fija `ffmpeg.RootPath` a una carpeta explícita y fuerza la resolución. **NO cae al PATH del sistema** (donde suele haber un ffmpeg GPL). |
| ¿Qué binario se distribuye? | Build **`lgpl-shared`** de la rama n8.1 de [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), CI pública y receta reproducible. `publish.ps1` la copia a `ffmpeg-lgpl\bin` junto al exe. Solo las **tres** bibliotecas que se enlazan: `avformat`, `avfilter`, `avdevice` y `swscale` (68 MB) no se incluyen porque no se usan. |
| ¿Enlace dinámico? | **SÍ** ✓ — carga en tiempo de ejecución desde una carpeta abierta y sustituible (`WINCUE_FFMPEG_DIR` permite apuntar a otra). No se firma ni se sella: cumple la sección 4 de la LGPLv3. |
| ¿Licencia? | **LGPL-3.0-or-later — VERIFICADO** (`--enable-version3`, sin `--enable-gpl` ni `--enable-nonfree`). |
| ¿Cómo se verifica? | `tools/verificar-ffmpeg` cruza **dos fuentes independientes**: la línea de `configure` (`avcodec_configuration()`) y la licencia declarada por cada biblioteca (`avutil_license()`, `avcodec_license()`). Lo ejecuta **`publish.ps1` sobre las DLL YA COPIADAS AL PAQUETE**, y si el veredicto no es LGPL **borra las DLL y aborta la publicación**. Es el guardián que impide que un error humano meta una build GPL en un instalador. |
| ¿Procedencia? | `ffmpeg-lgpl/ffmpeg-BUILD.txt` (versionado; los binarios no) con URL, fecha, SHA256 del zip y SHA256 de cada DLL — la LGPL obliga a poder ofrecer el fuente EXACTO de la build distribuida. |
| Textos de licencia | `licenses/LGPL-3.0.txt` **y** `licenses/GPL-3.0.txt` (la LGPLv3 son permisos adicionales sobre la GPLv3: van los dos). |

**Nota de riesgo NO legal sino operativo:** este decodificador va **en proceso**.
Un fallo de memoria dentro del código nativo se lleva la aplicación (con
`ffmpeg.exe` por subproceso, no podía). Mitigaciones: validación del contenedor
antes de tocar nada nativo (`MovDemuxer.Validate`), lista cerrada de formatos de
píxel, fallback por cue a mpv, **interruptor en Ajustes → Vídeo** y kill-switch
`WINCUE_PRORES_ENGINE=0`.

## 2. Hecho en esta fase (en el repo)

- [x] Inventario NuGet completo con transitivas (`licencias-nuget.md/.json`) — producto 100 % MIT, sin copyleft ni paquetes sin licencia.
- [x] `THIRD-PARTY-NOTICES.md` con atribuciones (NAudio, Phosphor, .NET runtime, libmpv, FFmpeg como herramienta externa).
- [x] `publish.ps1` copia THIRD-PARTY-NOTICES.md y `licenses/` junto al exe (avisa si falta el texto LGPL).
- [x] "Acerca de" en la app (botón ⓘ) que muestra versión y abre el archivo de atribuciones.

### Añadido con el motor ProRes propio (2026-07-27)

- [x] `THIRD-PARTY-NOTICES.md` completado con lo que faltaba y lo nuevo:
      **Vortice.Windows** (MIT, entró con HAP y no estaba), **Snappier** (MIT,
      ídem), **FFmpeg.AutoGen** (MIT) y las **bibliotecas de FFmpeg** (LGPLv3)
      con procedencia, mecanismo de sustitución y oferta de fuente.
- [x] `licenses/LGPL-3.0.txt` y `licenses/GPL-3.0.txt` descargados de gnu.org.
- [x] `publish.ps1` copia `ffmpeg-lgpl\bin\*.dll` + `ffmpeg-BUILD.txt` junto al exe.
- [x] **Verificación de licencia en el smoke de publicación**: `publish.ps1`
      ejecuta `tools/verificar-ffmpeg` sobre las DLL del paquete y aborta —
      borrándolas— si no son LGPL. `-SinVerificarLicencias` existe para
      diagnóstico y avisa a gritos de que ese paquete no se distribuye.
- [x] `tools/ffmpeg-lgpl/obtener-ffmpeg-lgpl.ps1` reconstruye la carpeta desde
      cero y deja la procedencia con SHA256 por DLL.

## 3. Pendiente FUERA del código (lo tuyo)

**Bloqueante:**
- [ ] **libmpv LGPL** (punto 1 de arriba) — sin esto no hay distribución cerrada legal.
      *(Las bibliotecas de FFmpeg del motor ProRes ya están resueltas y
      verificadas automáticamente; el bloqueante que queda es solo libmpv.)*
- [x] Textos oficiales de licencia en `licenses/`: LGPL-2.1 (libmpv),
      LGPL-3.0 + GPL-3.0 (FFmpeg).

**Marca y producto:**
- [ ] **Colisiones del nombre "WinCue"**: búsqueda en EUIPO/TMview (UE) y USPTO (EE. UU.) por "WinCue" en clases 9/42; búsqueda general (hay al menos software histórico de teleprompter llamado WinCue — verificar si sigue en uso comercial). Decidir: mantener nombre, o renombrar ANTES de crear marca/web.
- [ ] Registrar dominio (y las variantes obvias) una vez decidido el nombre.
- [ ] **EULA** propia (licencia de uso, limitación de responsabilidad — crítico en software de espectáculos en directo) + política de privacidad (aunque no haya telemetría, la web la necesitará).

**Instalador y confianza:**
- [ ] **Certificado de firma de código** (OV ~200-400 €/año o EV) y firmar exe + instalador — sin firma, SmartScreen espanta a cualquier comprador.
- [ ] Instalador (MSIX/Inno/WiX) que incluya THIRD-PARTY-NOTICES.md + licenses/ (el paso de publish ya los deja preparados).
- [ ] Canal de actualizaciones y aviso de versión.

**Antes del primer .exe público:**
- [ ] Revisar `CA5392` (rutas de búsqueda de DLL) anotado en la auditoría 01 — endurecer la carga de libmpv para el escenario instalador (DLL planting).
- [ ] Repetir `nuget-license` en cada actualización de dependencias (candidato a paso de CI).
