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
| ¿La build actual es LGPL o GPL? | **GPL — CONFIRMADO** (2026-07-18, vía `mpv-configuration` de la propia DLL): build de `shinchiro/mpv-winbuild-cmake`, sin `-Dgpl=false`, con componentes que exigen GPL activados (dvdnav). Además la GPL obliga a entregar el CÓDIGO FUENTE del conjunto → incompatible con licencia propietaria de código cerrado. **No existen builds LGPL prebuilt para Windows** (shinchiro issue #586 sin respuesta desde ene-2024) → hay que COMPILARLA. Nota positiva: WinCue solo usa el núcleo LGPL (reproducción, render API, hwdec, keep-open, geometría), así que el cambio es funcionalmente seguro. |

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

### FFmpeg / ffprobe — ✅ sin riesgo tal como está hoy

| Comprobación | Resultado |
|---|---|
| ¿Cómo se usa? | **Subproceso** (`Process.Start("ffmpeg"/"ffprobe")`) en `AudioDecoder`, `MediaImporter`, `WaveformControl`. Nunca enlazado. |
| ¿Se distribuye? | **No** — `publish.ps1` no lo incluye; se exige en el PATH del usuario |
| Riesgo | Invocar un binario GPL como proceso separado **no contamina** a WinCue (programas separados). Mientras NO se incluya ffmpeg.exe en el paquete, no hay obligación. |
| ⚠️ Si algún día se empaqueta | usar una build **LGPL compartida** (o cumplir la licencia de la build elegida: aviso + fuente). Decidirlo ANTES de meterlo en el instalador. |

## 2. Hecho en esta fase (en el repo)

- [x] Inventario NuGet completo con transitivas (`licencias-nuget.md/.json`) — producto 100 % MIT, sin copyleft ni paquetes sin licencia.
- [x] `THIRD-PARTY-NOTICES.md` con atribuciones (NAudio, .NET runtime, libmpv, FFmpeg como herramienta externa).
- [x] `publish.ps1` copia THIRD-PARTY-NOTICES.md y `licenses/` junto al exe (avisa si falta el texto LGPL).
- [x] "Acerca de" en la app (botón ⓘ) que muestra versión y abre el archivo de atribuciones.

## 3. Pendiente FUERA del código (lo tuyo)

**Bloqueante:**
- [ ] **libmpv LGPL** (punto 1 de arriba) — sin esto no hay distribución cerrada legal.
- [ ] Descargar el texto oficial LGPL-2.1 → `licenses/LGPL-2.1.txt`
      (https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt) — el publish avisa si falta.

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
