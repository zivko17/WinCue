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
> `.nuspec` de cada paquete y, **cuando existía**, el fichero de licencia
> real dentro del `.nupkg`, en `C:\Users\andre\.nuget\packages\` — no
> siempre lo había: `Microsoft.Windows.SDK.BuildTools`,
> `Microsoft.Windows.SDK.NET.Ref` y **`Microsoft.Graphics.Win2D`** (ver más
> abajo) solo traen una `licenseUrl` que no se ha podido leer (un `.rtf` los
> dos primeros, un 301 a una página genérica de Microsoft Learn el tercero),
> sin ningún fichero de licencia embebido en el `.nupkg`. El resultado, para
> quien lea esto sin memoria del proceso: **el encargo original tenía razón
> en sospechar** — `Microsoft.WindowsAppSDK` **no es MIT**, y **Win2D
> tampoco se puede dar por MIT** aunque su repositorio de GitHub sí lo sea:
> es la MISMA trampa dos veces. Ver el detalle de cada uno abajo.

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
- **NOTICE.txt propio del paquete — expuesto, no dictaminado.** El `.nupkg`
  de WindowsAppSDK incluye, además del `license.txt` de arriba, un
  `NOTICE.txt` (51 líneas) de atribuciones de terceros embebidas dentro del
  propio SDK. No es una lista corta ni trivial: incluye **Newtonsoft.Json
  13.0.1 (MIT)**, cuyo texto de licencia exige que el aviso "shall be
  included in all copies or **substantial portions**" del software; una
  **oferta escrita de código fuente** de Microsoft
  (`3rdpartysource.microsoft.com`, o un cheque físico de 5 USD); y esta
  cláusula, literal: *"you may reverse engineer this software to the extent
  required to debug changes to any libraries licensed under the **GNU
  Lesser General Public License**."* Es decir: material de terceros con
  obligación de *pass-through* y una referencia LGPL, dentro de binarios que
  Secuence sí redistribuye (todo el SDK, self-contained), en un producto de
  código cerrado — el mismo tipo de situación para la que WinCue ya tiene un
  guardián de LGPL en `publish.ps1`. **Este informe no concluye si eso
  genera o no una obligación adicional para Secuence** — es una decisión de
  negocio/legal, no algo que corresponda dictaminar aquí. Lo que sí queda
  como tarea concreta:
  1. `publish-secuence.ps1` (hito 2) tiene que copiar este `NOTICE.txt`
     íntegro a `licenses/`, igual que ya hace con el `license.txt` de
     arriba.
  2. La decisión de si hace falta algo más (pass-through explícito del
     aviso de Newtonsoft.Json, mención de la oferta de código fuente,
     evaluación de la cláusula LGPL) queda registrada en
     `docs/legal/checklist-distribucion.md`, no en este documento.

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
- **`NOTICE.txt` propio del paquete:** igual que WindowsAppSDK arriba, el
  `.nupkg` de WebView2 trae su propio `NOTICE.txt` de atribuciones de
  terceros — no se había mencionado en la primera versión de este
  documento. Mismo tratamiento: se expone, no se dictamina; la copia a
  `licenses/` y la decisión de si hace falta algo más quedan en
  `docs/legal/checklist-distribucion.md`.

### Microsoft.Windows.SDK.NET.Ref 10.0.19041.56 (runtime pack — invisible a `dotnet list package`)

- **Este componente no lo encontró el método que pedía el encargo.**
  `dotnet list package --include-transitive` no ve los runtime packs — son
  un mecanismo del SDK de .NET distinto de una referencia NuGet normal.
  Este en concreto aparece en `WinUi3Lab.deps.json` como
  `runtimepack.Microsoft.Windows.SDK.NET.Ref/10.0.19041.56`, y de él salen
  dos DLL que **sí están en la carpeta de salida real**
  (`Microsoft.Windows.SDK.NET.dll` y `WinRT.Runtime.dll`, confirmadas con
  `find` sobre
  `tools/winui3-lab/bin/Release/net8.0-windows10.0.19041.0/win-x64/`) y que
  WinCue no distribuye hoy.
- **Licencia: no verificada, y no se da por buena por analogía.** El
  `.nuspec` de este paquete
  (`~/.nuget/packages/microsoft.windows.sdk.net.ref/10.0.19041.56/`) tiene
  el mismo patrón que `Microsoft.Windows.SDK.BuildTools` de abajo: sin
  `<license type="file">`, solo
  `<licenseUrl>https://aka.ms/WinSDKLicenseURL</licenseUrl>` (el mismo
  `.rtf` no legible desde aquí). La diferencia que importa con BuildTools es
  que **este sí se distribuye** — las dos DLL viajan en la carpeta del
  `.exe` — así que aquí la licencia sin verificar sí es relevante, y no se
  puede cerrar como "no genera obligación por no distribuirse". No se
  supone MIT solo porque `WinRT.Runtime.dll` venga del proyecto CsWinRT
  (que sí es MIT en GitHub): la entrada de `Microsoft.WindowsAppSDK` de
  arriba ya demuestra que código fuente MIT y paquete NuGet compilado
  pueden llevar licencias distintas.
- **No estaba en `THIRD-PARTY-NOTICES.md`, ni en `licencias-nuget.md`, ni en
  este documento hasta ahora** — ninguno de los inventarios existentes de
  WinCue cubre runtime packs porque WinCue no usa ninguno. Queda anotado
  como pendiente de verificar antes de que el hito 2 publique nada.

### Microsoft.Windows.SDK.BuildTools 10.0.22621.756 / 10.0.22621.3233

- **No se distribuye con la aplicación — verificado, no supuesto.** Es
  herramental de compilación (`makepri.exe`, `midl.exe`, `mc.exe`, etc., bajo
  `bin\<version>\{x86,x64,arm,arm64}\` dentro del propio paquete). Se comparó
  la lista de **archivos `.dll`** de la carpeta de salida real
  (`tools/winui3-lab/bin/Release/net8.0-windows10.0.19041.0/win-x64/`, 56 en
  total) contra el contenido de este paquete: **ningún archivo de
  `Microsoft.Windows.SDK.BuildTools` aparece en la salida.** Solo se usa en
  tiempo de build (targets de MSBuild), nunca en tiempo de ejecución.
  **Corrección de alcance:** esa comparación cubrió las DLL, no la carpeta
  entera — la carpeta de salida real tiene **86 ficheros en su raíz** (las
  56 `.dll`, 19 `.winmd`, ficheros `.pri`/`.xbf`, dos `.exe`) más
  `runtimes/` y **86 carpetas de idioma** (contadas con `find`, no de
  memoria); no se auditó "una por una" esa
  carpeta completa, solo las DLL de esta comparación concreta (ver también
  la entrada de `Microsoft.Windows.SDK.NET.Ref` de arriba, que es
  precisamente lo que se escapó de mirar solo DLL "obvias").
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

### Microsoft.Graphics.Win2D 1.2.0

- **Referenciado en esta tarea** (piloto del VU meter, ver el encargo
  hitos-0-1: es el paquete que da `CanvasControl` para pintar a mano en
  WinUI 3, que no tiene `DrawingContext`). La entrada anterior de este
  documento decía "aún no referenciado — entra en el hito 3" y avisaba de
  que había que repetir la comprobación sobre el `.nupkg` real cuando
  llegara el momento. Este es ese momento, y **la comprobación confirma la
  sospecha: NO es MIT.**
- **Licencia real, verificada en el paquete: NO es MIT, y no se puede leer
  el texto completo.** El `.nuspec`
  (`~/.nuget/packages/microsoft.graphics.win2d/1.2.0/microsoft.graphics.win2d.nuspec`)
  no trae `<license type="file">` — solo
  `<licenseUrl>http://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm</licenseUrl>`
  y `<requireLicenseAcceptance>true</requireLicenseAcceptance>`. El propio
  `.nupkg` **no contiene ningún fichero de licencia ni de aviso** (se buscó
  con `find … -iname "*license*" -o -iname "*eula*" -o -iname "*notice*"`
  sobre el árbol completo del paquete: cero resultados). Y la URL del EULA
  **ya no resuelve a nada legible**: hace un 301 a
  `https://learn.microsoft.com/windows/web`, una página genérica de
  Microsoft Learn sin relación con ninguna licencia — comprobado hoy, no de
  memoria. Mismo patrón exacto que `Microsoft.Windows.SDK.BuildTools` (más
  abajo): `requireLicenseAcceptance=true` + URL muerta + nada embebido.
- **El repositorio de origen (https://github.com/microsoft/Win2D) SÍ es
  MIT — y no basta.** Es la misma trampa, otra vez, que
  `Microsoft.WindowsAppSDK` de arriba: código fuente MIT en GitHub y paquete
  NuGet compilado bajo una EULA distinta (o, aquí, bajo una EULA que ni
  siquiera se puede leer) son dos cosas separadas. No se da la MIT por
  buena por el repositorio.
- **Conclusión: licencia NO VERIFICABLE con lo disponible, y NO se supone
  MIT.** A diferencia de `Microsoft.Windows.SDK.BuildTools`, este paquete
  **sí se distribuye** — las DLL de Win2D (`Microsoft.Graphics.Canvas.*`)
  viajan en la carpeta del `.exe` de Secuence, self-contained igual que el
  resto del Windows App SDK — así que aquí la licencia sin verificar SÍ es
  relevante para la distribución y no se puede cerrar como "no genera
  obligación". Queda como **tarea pendiente concreta antes de distribuir
  Secuence**: conseguir el texto real del EULA de Win2D por otra vía (p. ej.
  Wayback Machine de la URL original, o contacto directo con Microsoft) y
  decidir con ese texto en la mano si el uso de Secuence lo cumple.
- **Copyright:** © Microsoft Corporation. Todos los derechos reservados
  (declarado en el `.nuspec`).
- **Proyecto:** https://github.com/microsoft/Win2D (fuente, MIT) · paquete
  NuGet: https://www.nuget.org/packages/Microsoft.Graphics.Win2D
- **Uso en Secuence:** `Microsoft.Graphics.Canvas.CanvasControl`, para pintar
  a mano los siete controles que WinCue pinta con `DrawingContext` (WPF) y
  que WinUI 3 no tiene manera de pintar sin esto. Entra con el VU meter
  (piloto); los otros seis lo irán sumando según se traspasen.
- **Dependencia declarada:** `Microsoft.WindowsAppSDK 1.5.240227000` (el
  `.csproj` de Secuence ya trae `1.6.*`, que la satisface; no añade una
  versión nueva del SDK, solo confirma la que ya había).

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
