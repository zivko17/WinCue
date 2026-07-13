# =============================================================================
# media_import.py — Validación y asignación de archivos de media a cues
# =============================================================================
#
# RESPONSABILIDAD
# ───────────────
# Clase estática pura: sin estado, sin hilos, sin UI.
# Encapsula toda la lógica de:
#   · Validación de archivos (existencia, extensión, tipo)
#   · Asignación de un archivo validado al campo file_path de un Cue
#   · Generación del filtro de formatos para QFileDialog
#
# MODELO DE USO
# ─────────────
#   # Validar antes de tocar el modelo:
#   result = MediaImporter.validate_file(path)
#   if result['valid']:
#       ok, err = MediaImporter.assign_to_cue(cue, path)
#
#   # Filtro para QFileDialog.getOpenFileName():
#   flt = MediaImporter.get_filter_string()
#
# NOTA: El campo en Cue se llama 'file_path' (no 'filepath').
# =============================================================================

from __future__ import annotations

from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from cue_model import Cue


# ---------------------------------------------------------------------------
# Conjuntos de formatos aceptados
# ---------------------------------------------------------------------------

AUDIO_FORMATS: frozenset[str] = frozenset({
    ".wav", ".flac", ".aiff", ".aif",
    ".ogg", ".mp3", ".aac", ".m4a", ".opus",
})

IMAGE_FORMATS: frozenset[str] = frozenset({
    ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp",
})

VIDEO_FORMATS: frozenset[str] = frozenset({
    ".mp4", ".mov", ".avi", ".mkv",
    ".webm", ".prores", ".mxf",
}) | IMAGE_FORMATS   # las imágenes se tratan como cues de VÍDEO (mpv las muestra)

ALL_FORMATS: frozenset[str] = AUDIO_FORMATS | VIDEO_FORMATS


# ---------------------------------------------------------------------------
# MediaImporter
# ---------------------------------------------------------------------------

class MediaImporter:
    """Lógica de importación de media. Todos los métodos son estáticos."""

    # ------------------------------------------------------------------
    # Validación
    # ------------------------------------------------------------------

    @staticmethod
    def validate_file(filepath: str) -> dict:
        """Valida que filepath sea un archivo de media utilizable.

        Devuelve un dict con la estructura:
          valid      : bool
          filepath   : str   — ruta absoluta normalizada (vacía si inválida)
          filename   : str   — nombre con extensión
          format     : str   — extensión en minúsculas (ej. '.mp3')
          media_type : str   — 'AUDIO' | 'VIDEO' | 'UNKNOWN'
          error      : str | None

        El llamador SOLO debe mirar 'valid' y 'error'; los demás campos
        solo son significativos cuando valid=True.
        """
        path = Path(filepath)

        # ── 1. El archivo debe existir ─────────────────────────────────
        if not path.exists():
            return {
                "valid":      False,
                "filepath":   "",
                "filename":   path.name,
                "format":     path.suffix.lower(),
                "media_type": "UNKNOWN",
                "error":      f"El archivo no existe: {filepath!r}",
            }

        if not path.is_file():
            return {
                "valid":      False,
                "filepath":   "",
                "filename":   path.name,
                "format":     path.suffix.lower(),
                "media_type": "UNKNOWN",
                "error":      f"La ruta no es un archivo: {filepath!r}",
            }

        ext = path.suffix.lower()

        # ── 2. La extensión debe estar en los formatos soportados ──────
        if ext not in ALL_FORMATS:
            accepted = sorted(ALL_FORMATS)
            return {
                "valid":      False,
                "filepath":   "",
                "filename":   path.name,
                "format":     ext,
                "media_type": "UNKNOWN",
                "error": (
                    f"Formato no soportado: {ext!r}.\n"
                    f"Extensiones aceptadas: {', '.join(accepted)}"
                ),
            }

        # ── 3. Determinar tipo ─────────────────────────────────────────
        media_type = (
            "AUDIO" if ext in AUDIO_FORMATS else
            "VIDEO" if ext in VIDEO_FORMATS else
            "UNKNOWN"
        )

        return {
            "valid":      True,
            "filepath":   str(path.resolve()),
            "filename":   path.name,
            "format":     ext,
            "media_type": media_type,
            "error":      None,
        }

    # ------------------------------------------------------------------
    # Asignación
    # ------------------------------------------------------------------

    @staticmethod
    def assign_to_cue(cue: "Cue", filepath: str) -> tuple[bool, str]:
        """Valida filepath y, si es válido, actualiza cue.file_path y cue.type.

        El campo cue.latency_ms se pone a None cuando el archivo cambia,
        porque la latencia medida con el archivo anterior ya no es válida.

        Devuelve:
          (True,  "")             si la asignación fue exitosa
          (False, mensaje_error)  si el archivo no pasó la validación
        """
        result = MediaImporter.validate_file(filepath)
        if not result["valid"]:
            return False, result["error"] or "Error desconocido"

        old_path = getattr(cue, "file_path", "")
        cue.file_path = result["filepath"]

        # Actualizar tipo solo si el formato lo indica con claridad
        if result["media_type"] in ("AUDIO", "VIDEO"):
            cue.type = result["media_type"]

        # Invalidar latencia medida si el archivo cambió
        if cue.file_path != old_path:
            cue.latency_ms = None

        return True, ""

    # ------------------------------------------------------------------
    # Filtro para QFileDialog
    # ------------------------------------------------------------------

    @staticmethod
    def get_filter_string() -> str:
        """Devuelve el string de filtros listo para QFileDialog.getOpenFileName().

        Formato Qt: "Descripción (*.ext1 *.ext2);;Descripción2 (...);;..."
        """
        def _exts_to_glob(exts: frozenset[str]) -> str:
            return " ".join(f"*{e}" for e in sorted(exts))

        all_glob   = _exts_to_glob(ALL_FORMATS)
        audio_glob = _exts_to_glob(AUDIO_FORMATS)
        video_glob = _exts_to_glob(VIDEO_FORMATS)

        return (
            f"Todos los formatos de media ({all_glob});;"
            f"Audio ({audio_glob});;"
            f"Vídeo ({video_glob});;"
            "Todos los archivos (*)"
        )
