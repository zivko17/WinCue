# WinCue — Beta privada de evaluación

Gracias por probar WinCue: un reproductor de cues de audio y vídeo para
directo (estilo QLab, para Windows). Esta es una **beta privada**: no la
redistribuyas ni la publiques. Condiciones en `EULA-EVALUACION` (docs/legal).

## Requisitos

- Windows 10/11 de 64 bits.
- No hace falta instalar nada más (.NET va embebido en el exe).
- **Opcional pero recomendado**: FFmpeg en el PATH
  (https://www.gyan.dev/ffmpeg/builds/ → "release full", descomprimir y
  añadir la carpeta `bin` al PATH). Sin él funciona todo lo esencial, pero
  se pierden: análisis de sonoridad (LUFS), conversión a WAV al exportar
  shows portables y la detección de códecs HAP.

## Primer arranque (aviso de SmartScreen)

El ejecutable no va firmado digitalmente (es una beta), así que Windows
mostrará un aviso azul de SmartScreen:

1. Si descargaste un ZIP: clic derecho sobre el ZIP → Propiedades →
   marca **«Desbloquear»** → Aceptar → descomprime.
2. Doble clic en `WinCue.App.exe` → en el aviso azul pulsa
   **«Más información»** → **«Ejecutar de todas formas»**.

## Para empezar

- `Espacio` = GO · `S` = parar todo · `B` = blackout de vídeo.
- Añade cues con el botón **➕ Add** o arrastra archivos de audio/vídeo a
  la lista.
- Cada cue de vídeo elige su salida en el inspector (pestaña Geometry);
  el monitor físico de cada salida se asigna en **Settings → Video**
  (las salidas 2-4 vienen deshabilitadas de fábrica).
- La interfaz está en inglés por defecto; puedes cambiarla a español en
  **Settings → Interface** (reiniciando).

## Qué nos interesa que pruebes

- Disparo de cues de audio y vídeo encadenados (auto-continue, crossfades).
- Estabilidad en sesiones largas y con archivos grandes.
- Vídeo HAP: se reproduce por la "ruta clásica" (sin fundidos de opacidad
  entre capas) — cuéntanos si lo usas y cómo va.

## Si algo falla

- La app intenta sobrevivir a los errores y deja rastro en la carpeta
  `logs/` junto al exe:
  - `crash-*.log` → errores internos (mándanoslos SIEMPRE que aparezcan).
  - `show-AAAAMMDD.log` → registro de la sesión.
- Cuéntanos: qué hacías justo antes, qué esperabas y qué pasó. Una
  captura o un vídeo corto del problema vale oro.

## Contacto

Feedback y bugs: Andrés — andres.zvk@gmail.com
