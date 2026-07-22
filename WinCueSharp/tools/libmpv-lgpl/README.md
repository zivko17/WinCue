# Compilar libmpv LGPL para WinCue (ruta GitHub Actions)

Objetivo: obtener una `libmpv-2.dll` **LGPL** (compilada `-Dgpl=false`, FFmpeg
sin `--enable-gpl`) para poder distribuir WinCue con licencia propietaria.
La compila **la CI gratuita de GitHub** — tú no montas nada local.

> Contexto: la DLL actual es **GPL confirmado** (probado con `mpv-configuration`).
> No existen builds LGPL prebuilt para Windows. Ver `docs/legal/checklist-distribucion.md`.
> WinCue solo usa el núcleo LGPL (reproducción, render API, hwdec, keep-open,
> geometría) → funcionalmente el cambio es seguro; solo se pierden encoders
> GPL (x264/x265/xvid) y dvd-nav, que WinCue NO usa.

---

## Pasos

### 1. Forkea el repo del toolchain
En GitHub: **Fork** de `https://github.com/shinchiro/mpv-winbuild-cmake`
a tu cuenta (zivko17). Trabajarás sobre tu fork.

### 2. Edita `packages/ffmpeg.cmake` — quita los componentes GPL
**BORRA** estas 7 líneas de la lista de configure (todas requieren GPL y son
encoders/DVD que WinCue no usa):

```
--enable-gpl
--enable-libdvdnav
--enable-libdvdread
--enable-libx264
--enable-libx265
--enable-libxvid
--enable-libzvbi
```

Deja el resto igual. (`--enable-version3` se queda: da LGPLv3, compatible con
enlace propietario. Los decoders H.264/HEVC/AV1 son nativos de FFmpeg, no
GPL → la reproducción sigue funcionando; solo se pierde CODIFICAR, que no se
usa.)

### 3. Edita `packages/mpv.cmake` — desactiva GPL en mpv
**a)** En la lista de opciones `meson setup`, añade una línea nueva:
```
-Dgpl=false
```
(ponla justo después de `-Dlibmpv=true`).

**b)** Cambia:
```
-Ddvdnav=enabled     →     -Ddvdnav=disabled
```
(dvdnav exige GPL; sin este cambio meson falla con `-Dgpl=false`.)

**c)** *(opcional, ahorra tiempo de build)* en el bloque `DEPENDS` de arriba,
borra las líneas `libdvdnav` y `libdvdread`.

### 4. Añade la subida de la DLL como artifact
En `.github/workflows/mpv_gcc.yml`, justo **después** del step
`- name: Packaging mpv`, añade este step (respeta la indentación de 6 espacios):

```yaml
      - name: Subir libmpv LGPL como artifact
        uses: actions/upload-artifact@v4
        with:
          name: libmpv-lgpl-${{ matrix.bit }}
          path: release_${{ matrix.bit }}/*.7z
          retention-days: 7
```

(Así no necesitas configurar Sourceforge ni releases: la CI te deja el .7z
descargable desde la propia ejecución.)

### 5. Ejecuta el workflow
En tu fork → pestaña **Actions** → workflow **"mpv gcc"** → botón
**Run workflow** (deja los inputs por defecto) → **Run**.
Tarda ~1,5-2 h (compila toolchain + FFmpeg + mpv). La primera vez va entera;
las siguientes reusan caché.

### 6. Descarga y verifica
Al terminar: abre la ejecución → sección **Artifacts** → baja
`libmpv-lgpl-x86_64` → descomprime el `.7z` → dentro está `libmpv-2.dll`.

**Verifica que es LGPL de verdad** (5 s) con el probe de al lado:
```powershell
# Copia la DLL nueva a una carpeta y apunta el probe a ella:
$env:WINCUE_LIBMPV = "C:\ruta\a\la\nueva\libmpv-2.dll"
dotnet run --project tools\libmpv-lgpl\verificar-licencia
```
Debe imprimir **`VEREDICTO: LGPL (gpl=false)`**. Si dice GPL o indeterminado,
algo no se aplicó — revisa los pasos 2-3.

### 7. Instálala en WinCue
Sustituye `..\WinCue\libmpv-2.dll` (la que `publish.ps1` copia) por la nueva.
Relanza WinCue con el show de prueba y confirma que todo reproduce igual
(los formatos que usas siguen soportados). Guarda el origen de la build
(URL de la ejecución de Actions + commit de tu fork) en
`licenses/libmpv-BUILD.txt` — la LGPL obliga a poder ofrecer el fuente de
LA build distribuida.

---

## Qué se pierde (nada que WinCue use)

| Se pierde (GPL) | ¿WinCue lo usa? |
|---|---|
| Encoders x264 / x265 / xvid | No (WinCue solo reproduce) |
| DVD-nav / DVD-read | No |
| Teletexto (zvbi) | No |
| Filtros de vídeo GPL de FFmpeg | No |

Decodificación H.264/HEVC/AV1/VP9, hwdec (dxva2/d3d11va), imágenes, audio,
render API OpenGL, geometría, keep-open → **todo intacto** (núcleo LGPL).
