# =============================================================================
# PATCH_cue_controller.py — Cambios exactos para Paso 3 en cue_controller.py
# =============================================================================
# Archivo de documentación. No se ejecuta. Describe el diff preciso.
# Los cambios ya están aplicados en cue_controller.py.
# =============================================================================

CHANGES = {

# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 1 — Import de VideoEngine
# DESPUÉS de: "from audio_engine import AudioEngine"  (línea 21)
# ─────────────────────────────────────────────────────────────────────────────
"1_import": """
from video_engine import VideoEngine   # Paso 3
""",

# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 2 — Instanciar VideoEngine en __init__
# DESPUÉS de: "self._audio_engine.error_occurred.connect(...)"
# ─────────────────────────────────────────────────────────────────────────────
"2_init_video_engine": """
        # Paso 3 — motor de vídeo
        self._video_engine: VideoEngine = VideoEngine(view.video_container)
        self._video_engine.latency_report.connect(self._on_latency_report)
        self._video_engine.error_occurred.connect(self._on_video_error)
""",

# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 3 — Rama VIDEO en on_go_pressed
# DESPUÉS del bloque "if cue.type == 'AUDIO': ... preload(...)"
# ─────────────────────────────────────────────────────────────────────────────
"3_go_video_branch": """
        elif cue.type == "VIDEO":
            self._video_engine.play(cue.id, cue.file_path)
            next_cue = self._model.get_cue(
                (self._active_index + 1) % self._model.count()
            )
            if next_cue and next_cue.type == "VIDEO" and next_cue.file_path:
                self._video_engine.preload_next(next_cue.id, next_cue.file_path)
""",

# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 4 — Parada de vídeo en on_stop_pressed
# DESPUÉS de: "self._model.set_state(self._active_index, 'STOPPED')"
# ANTES de:   "self._view.refresh_row(...)"
# ─────────────────────────────────────────────────────────────────────────────
"4_stop_video": """
        if cue.type == "VIDEO":
            self._video_engine.stop(cue.id)
""",

# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 5 — Handler de errores de vídeo
# DESPUÉS de: _on_audio_error(), ANTES de _finalize_current_cue()
# ─────────────────────────────────────────────────────────────────────────────
"5_on_video_error": """
    def _on_video_error(self, cue_id: int, message: str) -> None:
        \"\"\"Propaga un error del VideoEngine. [AVISO] no transiciona a ERROR.\"\"\"
        for i, cue in enumerate(self._model.cues):
            if cue.id == cue_id:
                if not message.startswith("[AVISO]"):
                    self._model.set_state(i, "ERROR")
                self._view.refresh_row(i)
                break
""",

}
