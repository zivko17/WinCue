# =============================================================================
# PATCH_audio_engine.py
# Modificaciones quirúrgicas a audio_engine.py para integrar AudioDecoder.
# NO reescribe el archivo completo. Aplica 3 cambios puntuales.
# =============================================================================

# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 1 — Añadir import al inicio del archivo
# Ubicación: después de los imports existentes de soundfile y antes de PySide6
# ─────────────────────────────────────────────────────────────────────────────

# ANTES (línea ~54):
#     import soundfile as sf
#     from PySide6.QtCore import QObject, QThread, Signal

# DESPUÉS:
#     import soundfile as sf
#     from PySide6.QtCore import QObject, QThread, Signal
#     from audio_decoder import AudioDecoder, AudioDecoderError


# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 2 — Reemplazar sf.read() en _LoaderWorker.run()
# Ubicación: clase _LoaderWorker, método run(), bloque try
# ─────────────────────────────────────────────────────────────────────────────

# ANTES (~línea 82):
#     def run(self) -> None:
#         """Ejecutado en el QThread del loader. Solo I/O y conversión numpy."""
#         try:
#             data, samplerate = sf.read(
#                 self._file_path,
#                 dtype="float32",
#                 always_2d=True,   # garantiza shape (frames, channels) siempre
#             )
#             self.finished.emit(self._cue_id, data, samplerate)
#         except Exception as exc:
#             self.failed.emit(self._cue_id, str(exc))

# DESPUÉS:
#     def run(self) -> None:
#         """Ejecutado en el QThread del loader. Solo I/O y conversión numpy."""
#         try:
#             data, samplerate = AudioDecoder.decode(self._file_path)
#             self.finished.emit(self._cue_id, data, samplerate)
#         except (FileNotFoundError, AudioDecoderError, Exception) as exc:
#             self.failed.emit(self._cue_id, str(exc))


# ─────────────────────────────────────────────────────────────────────────────
# CAMBIO 3 — (Ningún cambio adicional requerido)
# El except ya capturaba Exception; con AudioDecoderError en el except se hace
# explícito el tipo para claridad, pero la cobertura es idéntica dado que
# AudioDecoderError hereda de Exception.
# ─────────────────────────────────────────────────────────────────────────────
