# Secuence — Avisos de software de terceros (THIRD-PARTY NOTICES)

> **Este fichero es un superconjunto, no un reemplazo.** Secuence comparte el
> mismo motor que WinCue (`WinCue.Core`, el motor de vídeo, etc.), así que
> distribuye además **todo lo que ya recoge
> [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md)** de WinCue: NAudio,
> Phosphor Icons, Vortice.Windows, FFmpeg.AutoGen, Snappier, el runtime de
> .NET 8, y las dos bibliotecas nativas con copyleft débil que ya llevan
> guardián de licencia en `publish.ps1` — **libmpv (LGPL-2.1)** y **FFmpeg
> (LGPLv3)**, con FFmpeg también enlazado estáticamente dentro de la propia
> `libmpv-2.dll`. `publish-secuence.ps1` (hito 2) tiene que copiar **los dos**
> ficheros de avisos junto al ejecutable, no solo este.
>
> Lo que sigue abajo es **exclusivamente lo que Secuence añade** sobre WinCue,
> por venir con el `.csproj` de WinUI 3 (`tools/winui3-lab/WinUi3Lab.csproj`,
> banco de pruebas del hito 0 — la lista real de paquetes que entrará en el
> proyecto de producto de Secuence).
>
> **Cómo se verificó cada licencia de abajo:** no se ha dado ninguna por MIT
> "porque la mayoría de paquetes de Microsoft lo son". Se ha mirado el
> `.nuspec` y el fichero de licencia real dentro de cada `.nupkg` en
> `C:\Users\andre\.nuget\packages\`. El resultado, para quien lea esto sin
> memoria del proceso: **una de las dos preguntas del encargo tenía trampa** —
> `Microsoft.WindowsAppSDK` **no es MIT** como daba por hecho el borrador
> inicial del encargo. Ver el detalle abajo.

---

## Componentes que Secuence añade sobre WinCue

### Microsoft.WindowsAppSDK 1.6.250602001

- **Licencia real, verificada en el paquete: NO es MIT.**
  El `.nuspec` declara `<license type="file">license.txt</license>`, y ese
  `license.txt` (90 líneas, dentro del propio `.nupkg`) es un documento
  titulado **"MICROSOFT SOFTWARE LICENSE TERMS — MICROSOFT WINDOWS APP SDK"**:
  una EULA propietaria de Microsoft, con arbitraje vinculante, límite de
  responsabilidad de 5 USD y cláusulas de uso de datos — no el texto MIT.
- **Matiz que no hay que perder:** el **código fuente** del proyecto
  (https://github.com/microsoft/windowsappsdk) sí es MIT. Pero lo que
  Secuence va a distribuir no es ese código fuente: es el **paquete NuGet
  compilado**, y ese paquete — DLL nativas y gestionadas incluidas
  (`Microsoft.UI.Xaml.dll`, `Microsoft.WindowsAppRuntime.Bootstrap.dll`,
  `DWriteCore.dll`, etc., verificadas una por una en la carpeta de salida de
  `tools/winui3-lab/bin/Release/net8.0-windows10.0.19041.0/win-x64/`) — se
  rige por esta EULA, no por la licencia del repositorio de GitHub.
- **Copyright:** © Microsoft Corporation. Todos los derechos reservados.
- **Proyecto:** https://github.com/microsoft/windowsappsdk (fuente, MIT) ·
  paquete NuGet: https://www.nuget.org/packages/Microsoft.WindowsAppSDK
- **Uso en Secuence:** el framework de UI completo (WinUI 3, `AppWindow`,
  ventanas, controles) — es la base de todo el hito 1 en adelante.
- **Distribución:** self-contained (`WindowsAppSDKSelfContained=true`); las
  DLL nativas y gestionadas del SDK viajan en la carpeta del `.exe`, igual que
  las de WinCue.
- **Texto:** el `license.txt` real, íntegro y sin editar, tal como viene
  dentro del `.nupkg`, se copia (no se retranscribe a mano, para no
  introducir errores de transcripción en un documento legal) a
  `licenses/MICROSOFT-WINDOWSAPPSDK-LICENSE.txt` junto al ejecutable.
  **Pendiente para `publish-secuence.ps1` (hito 2):** automatizar esa copia
  desde `$env:USERPROFILE\.nuget\packages\microsoft.windowsappsdk\<version>\license.txt`.
- **NOTICE.txt propio del paquete:** el `.nupkg` de WindowsAppSDK incluye
  además un `NOTICE.txt` de atribuciones de terceros que trae embebido
  (p. ej. Newtonsoft.Json 13.0.1, MIT). Es la lista de terceros **dentro**
  del propio SDK de Microsoft, no de código de Secuence — no genera una
  obligación adicional para Secuence, pero se deja documentado por si hiciera
  falta en una auditoría.

### Microsoft.Web.WebView2 1.0.2651.64 (dependencia transitiva de WindowsAppSDK)

- **Licencia:** verificada en `LICENSE.txt` dentro del `.nupkg` — texto
  **BSD de 3 cláusulas** (con el nombre "Microsoft Corporation" en la
  cláusula de no aprobación/promoción en vez de un autor individual, pero
  estructuralmente BSD-3-Clause, no MIT y no la EULA del SDK). Texto completo
  abajo.
- **Copyright:** © Microsoft Corporation. Todos los derechos reservados.
- **Proyecto:** https://aka.ms/webview
- **Uso:** llega como dependencia transitiva declarada en el `.nuspec` de
  `Microsoft.WindowsAppSDK` (`Microsoft.Web.WebView2` 1.0.2651.64). Secuence
  no usa WebView2 directamente en el hito 0, pero sus DLL
  (`Microsoft.Web.WebView2.Core.dll` y `.Core.Projection.dll`) sí aparecen en
  la carpeta de salida porque WindowsAppSDK las arrastra.
- **Texto:** licencia BSD-3-Clause (abajo).

### Microsoft.Windows.SDK.BuildTools 10.0.22621.756 / 10.0.22621.3233

- **No se distribuye con la aplicación — verificado, no supuesto.** Es
  herramental de compilación (`makepri.exe`, `midl.exe`, `mc.exe`, etc., bajo
  `bin\<version>\{x86,x64,arm,arm64}\` dentro del propio paquete). Se comparó
  la lista de DLL de la carpeta de salida real
  (`tools/winui3-lab/bin/Release/net8.0-windows10.0.19041.0/win-x64/`, 57
  archivos) contra el contenido de este paquete: **ningún archivo de
  `Microsoft.Windows.SDK.BuildTools` aparece en la salida.** Solo se usa en
  tiempo de build (targets de MSBuild), nunca en tiempo de ejecución.
- **Licencia: no se ha podido verificar el texto completo, y no se da por
  MIT ni por ninguna otra cosa sin comprobar.** El `.nuspec` no trae
  `<license type="file">` (a diferencia de WindowsAppSDK) — solo
  `<licenseUrl>https://aka.ms/WinSDKLicenseURL</licenseUrl>`, que redirige a
  un `.rtf` de Microsoft (`sdk_license.rtf`, el acuerdo de licencia del
  Windows SDK) que no se ha podido renderizar como texto legible desde aquí.
  **Queda anotado como no verificado en vez de asumido**, tal como pide el
  encargo — pero, al no distribuirse, no genera una obligación de aviso para
  Secuence. Si en algún hito futuro se decide distribuir alguna pieza de este
  paquete (no es el caso hoy), esto hay que revisarlo primero.
- **Proyecto:** https://aka.ms/WinSDKProjectURL

### Microsoft.Graphics.Win2D — aún no referenciado (entra en el hito 3)

- **No está en la caché de NuGet de esta máquina** (no se ha añadido todavía
  ninguna referencia): no hay `.nupkg` real que inspeccionar hoy. Esta
  entrada es un aviso adelantado, no una verificación cerrada.
- **Lo que sí se pudo comprobar de fuera:** el repositorio de origen
  (https://github.com/microsoft/Win2D) sí declara licencia **MIT**. Pero la
  ficha del paquete en NuGet.org muestra un campo de licencia **heredado**
  (`licenseUrl` apuntando a
  `https://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm`, un
  patrón de EULA antiguo de Microsoft) en vez de una expresión SPDX limpia —
  y esa URL concreta ya no resuelve a nada legible (redirige a una página
  genérica de Microsoft Learn).
- **Conclusión: no se da la MIT por buena todavía.** Cuando el hito 3 añada
  la referencia real al `.csproj`, hay que repetir esta comprobación sobre el
  `.nupkg` que se restaure entonces (`.nuspec` + cualquier `license.txt`
  embebido), exactamente como se hizo aquí con `Microsoft.WindowsAppSDK` —
  que es precisamente el caso que demuestra por qué no basta con mirar el
  repositorio de GitHub.

---

## Texto de la licencia BSD de 3 cláusulas (Microsoft.Web.WebView2)

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Licencia de Microsoft.WindowsAppSDK

**No se reproduce aquí.** Es un documento largo (90 líneas), propietario, y
retranscribirlo a mano en este Markdown es exactamente el tipo de paso que
puede introducir un error de transcripción en un documento legal. El texto
real vive dentro del propio paquete NuGet
(`microsoft.windowsappsdk\<version>\license.txt`) y **hito 2 tiene que
copiarlo tal cual** a `licenses/MICROSOFT-WINDOWSAPPSDK-LICENSE.txt` junto al
ejecutable — ver la tarea pendiente marcada arriba.
