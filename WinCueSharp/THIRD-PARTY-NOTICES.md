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

### FFmpeg / ffprobe (herramienta externa — NO distribuida)
- WinCue invoca `ffmpeg`/`ffprobe` como **procesos externos** si están en el
  PATH del sistema (decodificación de formatos no nativos, duraciones,
  formas de onda). **No se enlazan ni se distribuyen con WinCue**: el usuario
  los instala por su cuenta y se rigen por la licencia de la build que
  instale (https://ffmpeg.org/legal.html).
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
https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt).
