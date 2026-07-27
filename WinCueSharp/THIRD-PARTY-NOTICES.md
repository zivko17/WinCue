# WinCue — Avisos de software de terceros (THIRD-PARTY NOTICES)

WinCue incluye o utiliza los componentes de terceros listados abajo. Los
textos completos de las licencias referenciadas se distribuyen en la carpeta
`licenses/` junto a este archivo (o en las URL indicadas).

> Inventario técnico completo (incluye dependencias transitivas y de test):
> `docs/legal/licencias-nuget.md` en el repositorio del proyecto.

---

## Componentes .NET distribuidos con la aplicación

### NAudio 2.3.0 (y subpaquetes NAudio.Core / .Wasapi / .Asio / .Midi / .WinForms / .WinMM)
- **Licencia:** MIT
- **Copyright:** © Mark Heath & Contributors
- **Proyecto:** https://github.com/naudio/NAudio
- **Texto:** licencia MIT (abajo) · https://licenses.nuget.org/MIT

### Phosphor Icons — `src/WinCue.App/assets/Phosphor.ttf`
- **Versión distribuida:** variante Regular del webfont (`src/regular/Phosphor.ttf`)
- **Licencia:** MIT
- **Copyright:** © 2020-2021 Phosphor Icons
- **Proyecto:** https://github.com/phosphor-icons/web
- **Texto:** licencia MIT (abajo) · https://github.com/phosphor-icons/web/blob/master/LICENSE
- **Enlace:** el `.ttf` se incrusta como `<Resource>` en WinCue.App.dll y se usa
  a través de `assets/WinCue.CompositeFont`. No se ha modificado la fuente.

### Vortice.Windows 3.6.2 (`Vortice.Direct3D11`, `Vortice.Direct3D9`, `Vortice.D3DCompiler` y sus dependencias `Vortice.DXGI`, `Vortice.Mathematics`, `SharpGen.Runtime`)
- **Licencia:** MIT
- **Copyright:** © Amer Koleci and Contributors
- **Proyecto:** https://github.com/amerkoleci/Vortice.Windows
- **Texto:** licencia MIT (abajo) · https://github.com/amerkoleci/Vortice.Windows/blob/main/LICENSE
- **Uso:** envoltorios finos de Direct3D 11 / Direct3D 9Ex / D3DCompiler para el
  pipeline GPU de los motores de vídeo propios (HAP y ProRes). Solo código
  gestionado: las bibliotecas nativas son las del propio Windows.

### FFmpeg.AutoGen 8.1.0
- **Licencia:** MIT
- **Copyright:** © 2025 Ruslan Balanukhin (Rationale One)
- **Proyecto:** https://github.com/Ruslan-B/FFmpeg.AutoGen
- **Texto:** licencia MIT (abajo) · el `LICENSE.txt` va dentro del propio paquete NuGet
- **Uso:** enlaces generados sobre las cabeceras de FFmpeg para el motor ProRes
  propio. **No contiene código nativo**: solo el ensamblado gestionado. Las
  bibliotecas nativas se cargan aparte (ver FFmpeg más abajo).

### Snappier 1.3.1
- **Licencia:** MIT
- **Copyright:** © 2020 Brant Burnett
- **Proyecto:** https://github.com/brantburnett/Snappier
- **Texto:** licencia MIT (abajo) · https://github.com/brantburnett/Snappier/blob/main/LICENSE
- **Uso:** descompresión Snappy de los fotogramas HAP en el motor de vídeo propio.

### Runtime de .NET 8 (publicación autocontenida)
- **Licencia:** MIT
- **Copyright:** © .NET Foundation and Contributors / © Microsoft Corporation
- **Proyecto:** https://github.com/dotnet/runtime
- **Texto:** https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

---

## Bibliotecas nativas

### libmpv (mpv media player) — `libmpv-2.dll`
- **Versión distribuida:** ver propiedades del archivo (actual: v0.41.0)
- **Licencia:** LGPL-2.1-or-later *(requisito de distribución de WinCue: usar
  una build LGPL de libmpv — compilada con la opción `lgpl` de mpv y FFmpeg
  sin componentes GPL; ver docs/legal/checklist-distribucion.md)*
- **Copyright:** © 2000-2026 mpv/MPlayer/mplayer2 projects
- **Proyecto:** https://mpv.io · https://github.com/mpv-player/mpv
- **Enlace:** dinámico (la DLL se carga en tiempo de ejecución por P/Invoke y
  puede sustituirse por cualquier build compatible de libmpv, conforme a la
  sección 6 de la LGPL).
- **Código fuente:** el código fuente completo de la build de libmpv
  distribuida está disponible en https://github.com/mpv-player/mpv (y el de
  la build concreta, en el origen indicado en `licenses/libmpv-BUILD.txt`).
- **Texto:** `licenses/LGPL-2.1.txt`

libmpv incorpora a su vez FFmpeg (© the FFmpeg developers, LGPL-2.1+ en la
configuración requerida) y otras bibliotecas; sus avisos se incluyen en el
paquete fuente de la build.

### FFmpeg (bibliotecas) — `ffmpeg-lgpl\bin\avcodec-62.dll`, `avutil-60.dll`, `swresample-6.dll`
- **Versión distribuida:** rama n8.1, build *lgpl-shared* de
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) (CI pública de
  GitHub Actions). La URL exacta, la fecha y el **SHA256 del paquete y de cada
  DLL** están en `ffmpeg-lgpl/ffmpeg-BUILD.txt`, que se distribuye junto a ellas.
- **Licencia:** LGPL-3.0-or-later *(la build se compila con `--enable-version3`
  y **sin** `--enable-gpl` ni `--enable-nonfree`; se verifica en cada
  publicación — ver abajo)*
- **Copyright:** © the FFmpeg developers
- **Proyecto:** https://ffmpeg.org · https://git.ffmpeg.org/ffmpeg.git
- **Uso:** decodificación de ProRes **en proceso** para el motor de vídeo propio
  de WinCue. Se distribuyen únicamente las tres bibliotecas que WinCue enlaza;
  `avformat`, `avfilter`, `avdevice` y `swscale` **no** se incluyen porque no se
  usan (el demuxer de QuickTime es propio y la conversión de color va en un
  shader).
- **Enlace:** **dinámico**. Las DLL viven en una carpeta `ffmpeg-lgpl\bin` junto
  al ejecutable, se cargan en tiempo de ejecución y **pueden sustituirse** por
  cualquier build compatible de FFmpeg (variable de entorno `WINCUE_FFMPEG_DIR`
  para apuntar a otra carpeta). La carpeta no se firma ni se sella, conforme al
  mecanismo de la sección 4 de la LGPLv3.
- **Código fuente:** el de FFmpeg está en https://git.ffmpeg.org/ffmpeg.git; el
  de **esta build concreta** (tarball + recetas de compilación del mismo commit
  de BtbN/FFmpeg-Builds) se ofrece según los datos de `ffmpeg-BUILD.txt`.
- **Verificación:** `publish.ps1` ejecuta `tools/verificar-ffmpeg` sobre las DLL
  del paquete y **aborta la publicación** si el veredicto no es LGPL. Cruza dos
  fuentes independientes: la línea de `configure` de `avcodec_configuration()` y
  la licencia declarada por cada biblioteca (`avutil_license()`,
  `avcodec_license()`).
- **Texto:** `licenses/LGPL-3.0.txt` **y** `licenses/GPL-3.0.txt` (la LGPLv3 se
  define como permisos adicionales sobre la GPLv3: se distribuyen los dos).

### FFmpeg / ffprobe (ejecutables — herramienta externa, NO distribuida)
- WinCue invoca `ffmpeg`/`ffprobe` como **procesos externos** si están en el
  PATH del sistema (conversión a HAP, sondeo de códecs, duraciones, formas de
  onda). **Esos ejecutables no se enlazan ni se distribuyen con WinCue**: el
  usuario los instala por su cuenta y se rigen por la licencia de la build que
  instale (https://ffmpeg.org/legal.html). Es un caso distinto del de las
  bibliotecas de arriba, que sí se distribuyen y sí se enlazan.
- FFmpeg es una marca de Fabrice Bellard, promotor del proyecto FFmpeg.

---

## Texto de la licencia MIT

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## GNU Lesser General Public License 2.1

El texto completo y oficial de la LGPL-2.1 se distribuye como
`licenses/LGPL-2.1.txt` junto a la aplicación (fuente oficial:
https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt). Aplica a libmpv.

## GNU Lesser General Public License 3.0 (y GPL 3.0)

Los textos completos y oficiales se distribuyen como `licenses/LGPL-3.0.txt` y
`licenses/GPL-3.0.txt` junto a la aplicación (fuentes oficiales:
https://www.gnu.org/licenses/lgpl-3.0.txt y
https://www.gnu.org/licenses/gpl-3.0.txt). Aplican a las bibliotecas de FFmpeg
que distribuye WinCue: la build lleva `--enable-version3`, y la LGPLv3 se define
como un conjunto de permisos adicionales sobre la GPLv3, de modo que ambos
textos forman parte de la licencia.
