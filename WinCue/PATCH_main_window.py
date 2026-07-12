# =============================================================================
# PATCH_main_window.py — Cambios exactos para main_window.py
# =============================================================================
# Archivo de documentación. No se ejecuta. Describe el diff preciso.
# Los cambios ya están aplicados en main_window.py.
# =============================================================================
#
# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO (Paso 6) — Verificación de FFmpeg al arrancar
# Ubicación: clase MainWindow, método __init__, después de _populate_table()
# ─────────────────────────────────────────────────────────────────────────────
#
# ANTES (~línea 171):
#         self._setup_shortcuts()
#         self._populate_table()
#
#     # Configuración inicial ...
#
# DESPUÉS:
#         self._setup_shortcuts()
#         self._populate_table()
#         self._check_ffmpeg()
#
#     def _check_ffmpeg(self) -> None:
#         from audio_decoder import AudioDecoder
#         if not AudioDecoder.is_ffmpeg_available():
#             self.statusBar().showMessage(
#                 "⚠ FFmpeg no encontrado — solo WAV, FLAC y OGG disponibles",
#                 0,  # 0 = permanente hasta que cambie
#             )
# ─────────────────────────────────────────────────────────────────────────────

CHANGES = {

# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 1 — Import adicional
# DESPUÉS de: "from cue_model import STATE_COLORS, Cue, CueModel"  (línea 33)
# ─────────────────────────────────────────────────────────────────────────────
"1_import": """
from video_engine import VideoContainer  # Paso 3
""",

# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 2 — Reemplazar zona de tabla en _build_ui()
# ELIMINAR (líneas 163-166):
#     # -- Tabla de cues --
#     self._table_view = self._build_table()
#     root_layout.addWidget(self._table_view, stretch=1)
# SUSTITUIR POR:
# ─────────────────────────────────────────────────────────────────────────────
"2_build_ui_content_area": """
        # -- Zona central: VideoContainer (izq ≈60%) + Tabla de cues (der ≈40%) --
        self._video_container = VideoContainer()

        content = QWidget()
        content_layout = QHBoxLayout(content)
        content_layout.setContentsMargins(0, 0, 0, 0)
        content_layout.setSpacing(0)
        content_layout.addWidget(self._video_container, stretch=3)   # ~60 %

        self._table_view = self._build_table()
        content_layout.addWidget(self._table_view, stretch=2)        # ~40 %

        root_layout.addWidget(content, stretch=1)
""",

# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 3 — Propiedad pública video_container
# INSERTAR justo ANTES de set_stress_button_state() (~línea 443)
# ─────────────────────────────────────────────────────────────────────────────
"3_video_container_property": """
    @property
    def video_container(self) -> VideoContainer:
        \"\"\"Expone el VideoContainer para que el controlador lo pase a VideoEngine.\"\"\"
        return self._video_container
""",

}
