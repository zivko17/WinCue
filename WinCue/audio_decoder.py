# =============================================================================
# audio_decoder.py — Capa de compatibilidad universal de audio
# =============================================================================
#
# RESPONSABILIDAD
# ───────────────
# Decodificar archivos de audio a un array numpy float32 normalizado (−1.0…1.0)
# con shape (frames, channels), independientemente del formato de origen.
# AudioEngine.py llama AudioDecoder.decode() en su _LoaderWorker (QThread),
# en lugar de llamar soundfile.read() directamente.
#
# FLUJO DE DECISIÓN
# ─────────────────
#  1. ¿Existe el archivo?          → No  → FileNotFoundError (sube sin captura)
#  2. ¿Extensión nativa soundfile? → Sí  → sf.read() + cast a float32
#  3. ¿Extensión necesita FFmpeg?
#       FFmpeg disponible          → Sí  → pydub.AudioSegment + conversión numpy
#       FFmpeg NO disponible       → AudioDecoderError con instrucciones de
#                                    instalación claras
#  4. Archivo existe pero corrupto → AudioDecoderError con causa original
#
# FORMATOS SOPORTADOS
# ───────────────────
#  Siempre disponibles (soundfile nativo, libsndfile):
#    .wav  .flac  .aiff  .aif  .ogg
#
#  Solo con FFmpeg instalado y accesible en PATH (decodificados vía pydub):
#    .mp3  .aac  .m4a  .mp4 (audio de video)  .opus
#    .wma  .ac3  .dts
#
# NOTA SOBRE NORMALIZACIÓN
# ────────────────────────
#  Se divide entre 2^(bit_depth - 1) y NO entre 32768 fijo porque el
#  bit depth real depende del archivo: puede ser 8-bit (÷128), 16-bit (÷32768),
#  24-bit (÷8388608) o 32-bit (÷2147483648). Usar 32768 en un archivo de 24-bit
#  o 32-bit produciría overflow (valores >1.0 o <−1.0) que sounddevice clipa
#  o distorsiona. pydub expone sample_width en bytes, de modo que el divisor
#  genérico correcto es siempre 2 ** (sample_width * 8 - 1).
#
# DEPENDENCIAS
# ────────────
#  pip install pydub
#  FFmpeg en PATH (https://ffmpeg.org/download.html) — opcional pero recomendado
# =============================================================================

from __future__ import annotations

import subprocess
import warnings
from pathlib import Path

import numpy as np
import soundfile as sf


# ---------------------------------------------------------------------------
# Excepción pública
# ---------------------------------------------------------------------------

class AudioDecoderError(Exception):
    """Raised when a file cannot be decoded (bad format, missing FFmpeg, etc.)."""


# ---------------------------------------------------------------------------
# Detección de FFmpeg — se ejecuta UNA vez al importar el módulo
# ---------------------------------------------------------------------------

def _probe_ffmpeg() -> bool:
    """Devuelve True si 'ffmpeg' está accesible desde PATH."""
    try:
        subprocess.run(
            ["ffmpeg", "-version"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
            timeout=5,
        )
        return True
    except (FileNotFoundError, OSError, subprocess.TimeoutExpired):
        return False


_FFMPEG_AVAILABLE: bool = _probe_ffmpeg()

if not _FFMPEG_AVAILABLE:
    warnings.warn(
        "FFmpeg no encontrado en PATH. "
        "Solo WAV, FLAC, AIFF y OGG estarán disponibles. "
        "Descarga FFmpeg desde https://ffmpeg.org/download.html "
        "y añádelo al PATH del sistema.",
        RuntimeWarning,
        stacklevel=2,
    )

# Formatos decodificados nativamente por libsndfile (sin FFmpeg)
_NATIVE_EXTS: frozenset[str] = frozenset({".wav", ".flac", ".aiff", ".aif", ".ogg"})

# Formatos que requieren FFmpeg (decodificados a través de pydub)
_FFMPEG_EXTS: frozenset[str] = frozenset({
    ".mp3", ".aac", ".m4a", ".mp4", ".opus", ".wma", ".ac3", ".dts",
})


# ---------------------------------------------------------------------------
# AudioDecoder
# ---------------------------------------------------------------------------

class AudioDecoder:
    """Decodificador estático de audio. Sin estado, sin hilos propios.

    AudioEngine._LoaderWorker lo llama desde su QThread; nunca desde el
    main thread directamente.
    """

    # ------------------------------------------------------------------
    # API pública
    # ------------------------------------------------------------------

    @staticmethod
    def is_ffmpeg_available() -> bool:
        """Devuelve True si FFmpeg está accesible en PATH.

        Usa la comprobación realizada al importar el módulo (no relanza el
        subprocess en cada llamada). Nunca lanza excepciones.
        """
        return _FFMPEG_AVAILABLE

    @staticmethod
    def get_supported_formats() -> dict[str, bool]:
        """Devuelve todos los formatos conocidos con su disponibilidad actual.

        Tabla de formatos:
        ─────────────────────────────────────────────────────────────────
        Extensión  │ Necesita FFmpeg │ Disponible en este sistema
        ───────────┼─────────────────┼───────────────────────────────────
        .wav       │ No (nativo)     │ Siempre True
        .flac      │ No (nativo)     │ Siempre True
        .aiff      │ No (nativo)     │ Siempre True
        .aif       │ No (nativo)     │ Siempre True
        .ogg       │ No (nativo)     │ Siempre True
        .mp3       │ Sí              │ Solo si FFmpeg disponible
        .aac       │ Sí              │ Solo si FFmpeg disponible
        .m4a       │ Sí              │ Solo si FFmpeg disponible
        .mp4       │ Sí              │ Solo si FFmpeg disponible
        .opus      │ Sí              │ Solo si FFmpeg disponible
        .wma       │ Sí              │ Solo si FFmpeg disponible
        .ac3       │ Sí              │ Solo si FFmpeg disponible
        .dts       │ Sí              │ Solo si FFmpeg disponible
        ─────────────────────────────────────────────────────────────────
        """
        ffmpeg_ok = _FFMPEG_AVAILABLE
        return {
            # Nativo libsndfile — siempre disponibles
            ".wav":  True,
            ".flac": True,
            ".aiff": True,
            ".aif":  True,
            ".ogg":  True,
            # Requieren FFmpeg
            ".mp3":  ffmpeg_ok,
            ".aac":  ffmpeg_ok,
            ".m4a":  ffmpeg_ok,
            ".mp4":  ffmpeg_ok,
            ".opus": ffmpeg_ok,
            ".wma":  ffmpeg_ok,
            ".ac3":  ffmpeg_ok,
            ".dts":  ffmpeg_ok,
        }

    @staticmethod
    def decode(filepath: str) -> tuple[np.ndarray, int]:
        """Decodifica un archivo de audio a numpy float32.

        Parámetros
        ----------
        filepath : str
            Ruta absoluta o relativa al archivo de audio.

        Devuelve
        --------
        (array, samplerate)
            array  : np.ndarray float32, shape (frames, channels), rango −1.0…1.0
            samplerate : int, frecuencia de muestreo en Hz

        Excepciones
        -----------
        FileNotFoundError
            El archivo no existe. No se captura: sube hasta el llamador.
        AudioDecoderError
            Formato sin soporte (FFmpeg no disponible), archivo corrupto o
            cualquier otro error de decodificación no relacionado con la
            existencia del archivo.
        """
        path = Path(filepath)

        # ── 1. Verificar existencia del archivo ───────────────────────────
        if not path.exists():
            raise FileNotFoundError(f"Archivo no encontrado: {filepath!r}")

        ext = path.suffix.lower()

        # ── 2. Ruta nativa: soundfile puede manejarlo directamente ────────
        if ext in _NATIVE_EXTS:
            return AudioDecoder._decode_native(filepath)

        # ── 3. Ruta FFmpeg: pydub necesita ffmpeg en PATH ─────────────────
        if ext in _FFMPEG_EXTS:
            if not _FFMPEG_AVAILABLE:
                raise AudioDecoderError(
                    f"El formato {ext!r} requiere FFmpeg.\n"
                    "Instálalo desde https://ffmpeg.org/download.html\n"
                    "y añádelo al PATH del sistema."
                )
            return AudioDecoder._decode_ffmpeg(filepath)

        # ── 4. Extensión desconocida: intentar con soundfile como fallback ─
        # (soundfile conoce más formatos de los que figuran en _NATIVE_EXTS)
        return AudioDecoder._decode_native(filepath)

    # ------------------------------------------------------------------
    # Helpers privados
    # ------------------------------------------------------------------

    @staticmethod
    def _decode_native(filepath: str) -> tuple[np.ndarray, int]:
        """Decodifica usando soundfile (libsndfile). Siempre devuelve float32."""
        try:
            data, samplerate = sf.read(
                filepath,
                dtype="float32",
                always_2d=True,  # shape (frames, channels) garantizado
            )
            return data, samplerate
        except FileNotFoundError:
            raise   # Dejar subir sin envolver
        except Exception as exc:
            raise AudioDecoderError(
                f"No se pudo decodificar '{filepath}': {exc}"
            ) from exc

    @staticmethod
    def _decode_ffmpeg(filepath: str) -> tuple[np.ndarray, int]:
        """Decodifica usando pydub + FFmpeg. Convierte a float32 normalizado."""
        try:
            # Import diferido: pydub es opcional y caro de importar en frío
            from pydub import AudioSegment  # noqa: PLC0415

            audio = AudioSegment.from_file(filepath)

            # Extraer muestras enteras raw de pydub
            samples = np.array(audio.get_array_of_samples())

            # Reorganizar según número de canales
            if audio.channels == 2:
                samples = samples.reshape((-1, 2))
            else:
                samples = samples.reshape((-1, 1))

            # Normalizar al rango −1.0…1.0
            #
            # Se divide entre 2^(bit_depth − 1) y NO entre 32768 fijo porque el
            # rango de un entero con signo de N bits es [−2^(N-1), 2^(N-1)−1].
            # Para 16-bit: ÷32768. Para 24-bit: ÷8388608. Para 32-bit: ÷2147483648.
            # Usar 32768 en archivos de 24 o 32 bits produce overflow (>1.0 / <−1.0)
            # que sounddevice clipea o distorsiona el audio.
            bit_depth = audio.sample_width * 8
            array = samples.astype(np.float32) / float(2 ** (bit_depth - 1))

            return array, audio.frame_rate

        except FileNotFoundError:
            raise
        except AudioDecoderError:
            raise
        except Exception as exc:
            raise AudioDecoderError(
                f"No se pudo decodificar '{filepath}' con FFmpeg: {exc}"
            ) from exc
