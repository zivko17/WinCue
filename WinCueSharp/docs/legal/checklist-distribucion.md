# Auditoría WinCue — Checklist de distribución (legal y cumplimiento)

**Fecha:** 2026-07-18 · **Estado:** los NuGet están limpios (ver `licencias-nuget.md`); el bloqueante es la build de libmpv.

---

## 1. Las DLL nativas — análisis hecho desde el repo

### libmpv (`libmpv-2.dll`) — 🟢 RESUELTO (2026-07-28)

> **Estado actual:** WinCue distribuye la build **`mpv-dev-lgpl`** de
> `zhongfly/mpv-winbuild`, verificada LGPL y con guardián en `publish.ps1` que
> aborta la publicación si alguna vez se colara una GPL. Procedencia y SHA256 en
> `libmpv-lgpl/libmpv-BUILD.txt`; se obtiene con
> `tools/libmpv-lgpl/obtener-libmpv-lgpl.ps1`. **Qué se comprobó**, en cuatro
> frentes independientes:
>
> 1. **Integridad**: SHA256 del `.7z` igual al publicado por GitHub.
> 2. **La DLL se autodeclara**: `mpv-configuration` trae `-Dgpl=false`
>    (+ `dvdnav=disabled`, `rubberband=disabled`, `openal=disabled`).
> 3. **La receta**: `compile-lgpl-libmpv.patch` quita `--enable-gpl` del
>    `configure` de FFmpeg y elimina x264, x265, zvbi, dvdnav, dvdread,
>    rubberband, avisynth y davs2; mantiene `--enable-version3` (→ el FFmpeg
>    estático de dentro es **LGPLv3**, no v2.1). También desactiva LZO en
>    libarchive.
> 4. **El binario corrobora la receta**: las cadenas de identidad `libx264`,
>    `libx265`, `libxvid`, `librubberband`, `libdvdnav`, `libdvdread`, `libzvbi`
>    y `AviSynth` están en la DLL GPL antigua y **no** en la nueva; la línea de
>    meson del binario coincide flag por flag con el parche.
>
> **Límite honesto de la prueba:** el LTO elimina `avcodec_configuration` y
> `avutil_license` del binario, así que la ausencia de `--enable-gpl` en el
> FFmpeg de dentro **no se puede leer directamente**: se infiere de la receta más
> la ausencia de componentes. Es sólido, pero es una inferencia.
>
> **Paridad funcional comprobada** con los medios de `tests/`: H.264 (1080p30,
> 1080p60, 720p+PCM) y HAP decodifican con los mismos códec, dimensiones y audio
> que con la build GPL. **hwdec (d3d11va) sigue sin verificar**: requiere salida
> de vídeo real, o sea la app en pantalla.

<details>
<summary>Análisis original (2026-07-18), cuando era el bloqueante</summary>

| Comprobación | Resultado |
|---|---|
| ¿Dónde se carga? | `src/WinCue.Video/MpvNative.cs` y `MpvRenderNative.cs` (`[LibraryImport("libmpv-2.dll")]` + `DllImportResolver` propio que busca la DLL junto al exe / subiendo directorios / env `WINCUE_LIBMPV`) |
| ¿Qué binario se distribuye? | `publish.ps1` copia `..\WinCue\libmpv-2.dll` (121 MB) junto al exe |
| ¿Enlace dinámico? | **SÍ** ✓ — P/Invoke con carga en tiempo de ejecución; la DLL es sustituible por el usuario (cumple el mecanismo de la LGPL §6, que exige que el usuario pueda reemplazar la biblioteca) |
| ¿La build actual es LGPL o GPL? | **GPL — CONFIRMADO** (2026-07-18, vía `mpv-configuration` de la propia DLL): build de `shinchiro/mpv-winbuild-cmake`, sin `-Dgpl=false`, con componentes que exigen GPL activados (dvdnav). Además la GPL obliga a entregar el CÓDIGO FUENTE del conjunto → incompatible con licencia propietaria de código cerrado. *(Aquí se afirmaba además que "no existen builds LGPL prebuilt para Windows", apoyándose solo en el issue #586 de shinchiro. Era falso: ver el bloque de arriba.)* Nota positiva: WinCue solo usa el núcleo LGPL (reproducción, render API, hwdec, keep-open, geometría), así que el cambio es funcionalmente seguro. |

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

</details>

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
- [x] **libmpv LGPL — RESUELTO (2026-07-28)**, ver punto 1. Con esto **ya no
      queda ningún bloqueante legal de código**: las dos nativas que WinCue
      enlaza (FFmpeg del motor ProRes y libmpv) son LGPL verificadas, y las dos
      tienen guardián en `publish.ps1`. Lo que falta para vender es de negocio
      (EULA comercial, modelo de licencia, firma de código, marca).
- [ ] **Verificación en pantalla de Andrés con la libmpv LGPL**: hwdec
      (d3d11va) y un show real. Es lo único de libmpv que los tests no cubren.
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

## 4. Añadido por Secuence sobre lo de arriba (hito 0)

Ver `notices-secuence.md` para el detalle completo (qué se verificó, cómo y
con qué límites); aquí solo las decisiones pendientes, que son de
negocio/legal, no de código.

- [ ] **`NOTICE.txt` embebidos en paquetes de terceros que Secuence
      redistribuye** — el de `Microsoft.WindowsAppSDK` (incluye
      Newtonsoft.Json 13.0.1 MIT con obligación de *pass-through*, una
      oferta escrita de código fuente de Microsoft, y una cláusula de
      referencia a la LGPL) y el propio de `Microsoft.Web.WebView2` (sin
      revisar el contenido todavía). Decidir si basta con copiarlos íntegros
      a `licenses/` (como ya se hace con los `license.txt`) o si hace falta
      algo más explícito en el aviso propio de Secuence.
- [ ] **`WinRT.Runtime.dll` / `Microsoft.Windows.SDK.NET.dll`** — vienen del
      runtime pack `Microsoft.Windows.SDK.NET.Ref`, invisible a `dotnet list
      package --include-transitive` (por eso no se había detectado antes).
      Se distribuyen de hecho en la carpeta de salida y no tienen licencia
      verificada — ver `notices-secuence.md`. Verificar antes de que el
      hito 2 publique nada.
