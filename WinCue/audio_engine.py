# =============================================================================
# audio_engine.py — Motor de Audio multi-stream (estilo QLab)
# =============================================================================
#
# RESPONSABILIDAD
# ───────────────
# Gestiona la carga (pre-decode en RAM) y la reproducción SIMULTÁNEA de varios
# cues de audio a la vez, cada uno con su propio volumen, fundidos (fade in/out)
# y bucle. No conoce nada de la UI ni del CueModel; solo emite señales Qt.
#
# CAMBIO CLAVE RESPECTO A LA VERSIÓN MONO-STREAM
# ──────────────────────────────────────────────
# Antes existía UN solo stream activo (self._active_stream / self._ctx). Ahora
# hay un diccionario cue_id → _PlaybackCtx, cada uno con su OutputStream propio.
# GO ya no corta el audio anterior: varios audios pueden sonar a la vez (mezcla
# natural en el dispositivo de salida del SO).
#
# MODELO DE HILOS
# ───────────────
#  · MAIN THREAD  : toda la API pública (load/play/stop/fade/set_volume/…).
#  · QThread      : _LoaderWorker por cue (sf.read → ndarray). Sin UI.
#  · Hilo audio RT: callback de cada OutputStream. Solo lee su ctx (RAM) y
#                   aplica la envolvente de ganancia. Sin disco, sin malloc
#                   pesado. Emite señales Qt (queued) de latencia y nivel.
#
# GARANTÍA DE DIRECTO
# ───────────────────
#  Durante PLAYING el audio sale íntegramente desde un ndarray en RAM. Un fallo
#  de disco posterior NO interrumpe la reproducción en curso.
# =============================================================================

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from typing import Callable, Optional

import numpy as np
import sounddevice as sd
from PySide6.QtCore import QObject, QThread, Signal
from audio_decoder import AudioDecoder, AudioDecoderError


# ---------------------------------------------------------------------------
# Worker de carga — vive en un QThread dedicado por cue
# ---------------------------------------------------------------------------

class _LoaderWorker(QObject):
    """Lee un archivo de audio completo a un ndarray en el hilo del loader.

    Nunca toca la UI. Emite finished o failed al terminar; ambas señales
    se entregan en el main thread mediante Qt queued connection automática.
    """

    finished: Signal = Signal(int, object, int)   # (cue_id, data, samplerate)
    failed:   Signal = Signal(int, str)           # (cue_id, error_message)

    def __init__(self, cue_id: int, file_path: str) -> None:
        super().__init__()
        self._cue_id:    int = cue_id
        self._file_path: str = file_path

    def run(self) -> None:
        """Ejecutado en el QThread del loader. Solo I/O y conversión numpy."""
        try:
            data, samplerate = AudioDecoder.decode(self._file_path)
            self.finished.emit(self._cue_id, data, samplerate)
        except (FileNotFoundError, AudioDecoderError, Exception) as exc:
            self.failed.emit(self._cue_id, str(exc))


# ---------------------------------------------------------------------------
# Contexto de reproducción activa — uno por cue en PLAYING
# ---------------------------------------------------------------------------

@dataclass
class _PlaybackCtx:
    """Estado mutable de UNA sesión de reproducción activa.

    Instanciado en el main thread; leído por el callback de audio de su stream.
    Las mutaciones externas (fade/set_volume/stop) escriben campos escalares;
    el flag `fade_active` se activa SIEMPRE en último lugar para que el callback
    no lea una envolvente a medio configurar (GIL protege lecturas escalares).
    """
    cue_id:     int
    data:       np.ndarray
    samplerate: int
    channels:   int
    device:     Optional[int]

    position:      int   = 0
    t_start:       float = field(default_factory=time.perf_counter)
    first_cb_done: bool  = False
    latency_ms:    Optional[float] = None
    level_counter: int   = 0

    # Envolvente de ganancia
    volume:  float = 1.0     # ganancia objetivo en régimen permanente
    gain:    float = 1.0     # ganancia actual aplicada
    loop:    bool  = False
    play_count: int = 1      # nº total de reproducciones (1 = una vez)
    passes:     int = 0      # repeticiones ya completadas

    fade_active:      bool  = False
    fade_from:        float = 1.0
    fade_to:          float = 1.0
    fade_total:       int   = 1     # frames totales del fade
    fade_pos:         int   = 0     # frames de fade ya aplicados
    stop_after_fade:  bool  = False

    fade_out_frames:  int   = 0     # longitud del fade-out automático al final
    fade_out_armed:   bool  = False # ya se disparó el fade-out final

    request_stop: bool = False      # el callback debe terminar tras este bloque

    # Reproducción / stream
    stream: object = None           # sd.OutputStream
    paused: bool   = False


# ---------------------------------------------------------------------------
# AudioEngine — API pública
# ---------------------------------------------------------------------------

class AudioEngine(QObject):
    """Motor de audio multi-stream con pre-decode en RAM y fundidos.

    Todos los métodos públicos deben llamarse desde el main thread.
    """

    # ------------------------------------------------------------------
    # Señales Qt
    # ------------------------------------------------------------------

    state_changed:  Signal = Signal(int, str)    # (cue_id, new_state)
    latency_report: Signal = Signal(int, float)  # (cue_id, ms)
    error_occurred: Signal = Signal(int, str)    # (cue_id, message)
    #: Nivel RMS instantáneo (0.0–1.0 lineal) de un stream cualquiera. El VU
    #: meter aplica peak-hold, así que con varios streams muestra el más alto.
    level_report:   Signal = Signal(float)       # rms

    # ------------------------------------------------------------------
    # Construcción
    # ------------------------------------------------------------------

    def __init__(self, device_index: Optional[int] = None) -> None:
        super().__init__()
        self._device_index: Optional[int] = device_index

        # cue_id → (ndarray, samplerate) — buffers en RAM (caché de pre-carga)
        self._buffers: dict[int, tuple[np.ndarray, int]] = {}

        # cue_id → estado interno del engine
        self._states: dict[int, str] = {}

        # cue_id → (QThread, _LoaderWorker) en vuelo
        self._loaders: dict[int, tuple[QThread, _LoaderWorker]] = {}

        # cue_id → parámetros de reproducción pendientes (mientras carga)
        self._pending_play: dict[int, dict] = {}

        # cue_id → _PlaybackCtx de las reproducciones activas
        self._active: dict[int, _PlaybackCtx] = {}
        self._lock: threading.Lock = threading.Lock()

        # Volumen master (multiplicador global aplicado en el callback RT)
        self._master: float = 1.0

    # ------------------------------------------------------------------
    # API pública — device
    # ------------------------------------------------------------------

    def set_device(self, device_index: Optional[int]) -> None:
        """Cambia el dispositivo de salida para futuras reproducciones."""
        self._device_index = device_index

    def set_master_volume(self, volume: float) -> None:
        """Volumen master global (0..1), aplicado a todos los streams en vivo."""
        self._master = max(0.0, min(1.0, float(volume)))

    # ------------------------------------------------------------------
    # API pública — carga
    # ------------------------------------------------------------------

    def load(self, cue_id: int, file_path: str) -> None:
        """Inicia la carga asíncrona del archivo en RAM (estado LOADING)."""
        if not file_path:
            self._emit_error(cue_id, "Ruta de archivo vacía; asigna un archivo al cue.")
            return
        if cue_id in self._buffers:
            self._set_state(cue_id, "LOADED")
            return
        current = self._states.get(cue_id, "IDLE")
        if current == "LOADING":
            return
        self._set_state(cue_id, "LOADING")
        self._launch_loader(cue_id, file_path)

    def preload(self, cue_id: int, file_path: str) -> None:
        """Pre-carga un cue en RAM sin reproducir (latencia cero en el GO)."""
        self.load(cue_id, file_path)

    # ------------------------------------------------------------------
    # API pública — reproducción
    # ------------------------------------------------------------------

    def play(
        self,
        cue_id: int,
        file_path: str,
        volume: float = 1.0,
        fade_in: float = 0.0,
        fade_out: float = 0.0,
        loop: bool = False,
        trim_start: float = 0.0,
        trim_end: float = 0.0,
        play_count: int = 1,
    ) -> None:
        """Reproduce el cue. Si ya está sonando, lo reinicia desde el principio.

        Si el buffer está en RAM arranca de inmediato; si no, lo carga primero
        y arranca al terminar la carga (los parámetros quedan en _pending_play).
        """
        if not file_path:
            self._emit_error(cue_id, "No hay archivo asignado a este cue de audio.")
            return

        params = {
            "volume":     max(0.0, min(1.0, float(volume))),
            "fade_in":    max(0.0, float(fade_in)),
            "fade_out":   max(0.0, float(fade_out)),
            "loop":       bool(loop),
            "trim_start": max(0.0, float(trim_start)),
            "trim_end":   max(0.0, float(trim_end)),
            "play_count": max(1, int(play_count)),
        }

        # Reinicio si ya estaba sonando
        if cue_id in self._active:
            self._stop_ctx(cue_id, emit_state=False)

        if cue_id in self._buffers:
            self._start_playback(cue_id, params)
        else:
            # Cargar y reproducir al terminar
            self._pending_play[cue_id] = params
            if self._states.get(cue_id) != "LOADING":
                self._set_state(cue_id, "LOADING")
                self._launch_loader(cue_id, file_path)

    def stop(self, cue_id: int, fade: float = 0.0) -> None:
        """Detiene un cue. fade>0 → fundido de salida antes de parar."""
        ctx = self._active.get(cue_id)
        if ctx is None:
            return
        if fade > 0.0:
            self._begin_fade(ctx, target=0.0, duration=fade, stop_after=True)
        else:
            self._stop_ctx(cue_id, emit_state=True)

    def stop_all(self, fade: float = 0.0) -> None:
        """Detiene TODOS los cues de audio activos (opcionalmente con fade)."""
        for cue_id in list(self._active.keys()):
            self.stop(cue_id, fade=fade)

    def fade(
        self,
        cue_id: int,
        target_volume: float,
        duration: float,
        stop_after: bool = False,
    ) -> bool:
        """Funde el volumen de un cue activo hacia target_volume en `duration` s.

        Devuelve True si el cue estaba activo y el fade se inició.
        """
        ctx = self._active.get(cue_id)
        if ctx is None:
            return False
        self._begin_fade(
            ctx,
            target=max(0.0, min(1.0, float(target_volume))),
            duration=max(0.0, float(duration)),
            stop_after=stop_after,
        )
        return True

    def set_volume(self, cue_id: int, volume: float) -> None:
        """Fija instantáneamente el volumen de un cue activo."""
        ctx = self._active.get(cue_id)
        if ctx is None:
            return
        v = max(0.0, min(1.0, float(volume)))
        ctx.volume = v
        if not ctx.fade_active:
            ctx.gain = v

    def pause_all(self) -> None:
        """Pausa todas las reproducciones activas (congela posición)."""
        for ctx in list(self._active.values()):
            if ctx.paused:
                continue
            ctx.paused = True
            stream = ctx.stream
            ctx.stream = None
            self._close_stream(stream)

    def resume_all(self) -> None:
        """Reanuda las reproducciones pausadas desde su posición congelada."""
        for cue_id, ctx in list(self._active.items()):
            if not ctx.paused:
                continue
            ctx.paused = False
            self._open_stream_for(ctx)

    def is_playing(self, cue_id: int) -> bool:
        return cue_id in self._active

    def get_duration(self, cue_id: int) -> float:
        """Duración total del audio en segundos (del stream activo o del buffer)."""
        ctx = self._active.get(cue_id)
        if ctx is not None and ctx.samplerate:
            return len(ctx.data) / ctx.samplerate
        buf = self._buffers.get(cue_id)
        if buf is not None:
            data, sr = buf
            return len(data) / sr if sr else 0.0
        return 0.0

    def get_elapsed(self, cue_id: int) -> float:
        """Segundos reproducidos del cue activo (posición del stream)."""
        ctx = self._active.get(cue_id)
        if ctx is not None and ctx.samplerate:
            return ctx.position / ctx.samplerate
        return 0.0

    def any_playing(self) -> bool:
        return bool(self._active)

    def get_state(self, cue_id: int) -> str:
        return self._states.get(cue_id, "IDLE")

    def cleanup(self) -> None:
        """Cierra todos los streams y loaders (al salir de la aplicación)."""
        for cue_id in list(self._active.keys()):
            self._stop_ctx(cue_id, emit_state=False)
        for cue_id in list(self._loaders.keys()):
            self._cleanup_loader(cue_id)

    @staticmethod
    def list_devices() -> list[dict]:
        """Devuelve los dispositivos de salida disponibles en el sistema."""
        result: list[dict] = []
        try:
            all_devs = sd.query_devices()
            if isinstance(all_devs, dict):
                all_devs = [all_devs]
            for idx, dev in enumerate(all_devs):
                if dev.get("max_output_channels", 0) <= 0:
                    continue
                try:
                    hostapi_info = sd.query_hostapis(dev["hostapi"])
                    hostapi_name: str = hostapi_info.get("name", "")
                except Exception:
                    hostapi_name = ""
                result.append({
                    "index":               idx,
                    "name":                dev.get("name", ""),
                    "max_output_channels": dev.get("max_output_channels", 0),
                    "default_samplerate":  int(dev.get("default_samplerate", 44100)),
                    "hostapi_name":        hostapi_name,
                    "is_asio":             "asio"   in hostapi_name.lower(),
                    "is_wasapi":           "wasapi" in hostapi_name.lower(),
                })
        except Exception:
            pass
        return result

    # ------------------------------------------------------------------
    # Gestión de loaders
    # ------------------------------------------------------------------

    def _launch_loader(self, cue_id: int, file_path: str) -> None:
        self._cleanup_loader(cue_id)

        worker = _LoaderWorker(cue_id, file_path)
        thread = QThread(self)
        worker.moveToThread(thread)

        worker.finished.connect(self._on_loaded)
        worker.failed.connect(self._on_load_failed)

        thread.started.connect(worker.run)
        worker.finished.connect(thread.quit)
        worker.failed.connect(thread.quit)
        thread.finished.connect(thread.deleteLater)

        self._loaders[cue_id] = (thread, worker)
        thread.start()

    def _cleanup_loader(self, cue_id: int) -> None:
        entry = self._loaders.pop(cue_id, None)
        if entry is not None:
            thread, _ = entry
            if thread.isRunning():
                thread.quit()
                thread.wait(2_000)

    def _on_loaded(self, cue_id: int, data: object, samplerate: int) -> None:
        self._buffers[cue_id] = (data, samplerate)  # type: ignore[assignment]
        self._loaders.pop(cue_id, None)
        self._set_state(cue_id, "LOADED")
        params = self._pending_play.pop(cue_id, None)
        if params is not None:
            self._start_playback(cue_id, params)

    def _on_load_failed(self, cue_id: int, message: str) -> None:
        self._loaders.pop(cue_id, None)
        self._pending_play.pop(cue_id, None)
        self._emit_error(cue_id, f"Error al cargar audio: {message}")

    # ------------------------------------------------------------------
    # Reproducción — sounddevice OutputStream con callback por cue
    # ------------------------------------------------------------------

    def _start_playback(self, cue_id: int, params: dict) -> None:
        buf = self._buffers.get(cue_id)
        if buf is None:
            self._emit_error(cue_id, "Buffer en RAM no disponible para reproducción.")
            return

        data, samplerate = buf

        # Recorte (in/out) dentro del archivo, estilo QLab
        trim_start = float(params.get("trim_start", 0.0))
        trim_end = float(params.get("trim_end", 0.0))
        if trim_start > 0.0 or trim_end > 0.0:
            s = int(trim_start * samplerate)
            e = int(trim_end * samplerate) if trim_end > 0.0 else len(data)
            s = max(0, min(s, len(data)))
            e = max(s, min(e, len(data)))
            if e > s:
                data = data[s:e]

        channels: int = data.shape[1] if data.ndim > 1 else 1

        volume  = params.get("volume", 1.0)
        fade_in = params.get("fade_in", 0.0)
        fade_out = params.get("fade_out", 0.0)
        loop     = params.get("loop", False)

        ctx = _PlaybackCtx(
            cue_id=cue_id,
            data=data,
            samplerate=samplerate,
            channels=channels,
            device=self._device_index,
            volume=volume,
            loop=loop,
        )
        ctx.play_count = int(params.get("play_count", 1))
        ctx.fade_out_frames = int(fade_out * samplerate) if fade_out > 0 else 0
        ctx.t_start = time.perf_counter()

        # Fade-in automático: de 0 al volumen objetivo
        if fade_in > 0.0:
            ctx.gain = 0.0
            ctx.fade_from = 0.0
            ctx.fade_to = volume
            ctx.fade_total = max(1, int(fade_in * samplerate))
            ctx.fade_pos = 0
            ctx.stop_after_fade = False
            ctx.fade_active = True
        else:
            ctx.gain = volume

        self._active[cue_id] = ctx
        if not self._open_stream_for(ctx):
            self._active.pop(cue_id, None)
            return
        self._set_state(cue_id, "PLAYING")

    def _open_stream_for(self, ctx: _PlaybackCtx) -> bool:
        """Abre (o reabre, tras pausa) el OutputStream de un ctx."""
        try:
            stream = sd.OutputStream(
                samplerate=ctx.samplerate,
                channels=ctx.channels,
                dtype="float32",
                device=ctx.device,
                callback=self._make_audio_callback(ctx),
                finished_callback=self._make_finished_callback(ctx.cue_id),
            )
            ctx.stream = stream
            stream.start()
            return True
        except Exception as exc:
            self._emit_error(ctx.cue_id, f"No se pudo abrir el dispositivo de audio: {exc}")
            return False

    def _make_audio_callback(self, ctx: _PlaybackCtx) -> Callable:
        """Genera el closure de callback para el stream de este ctx.

        REGLAS (hilo RT de PortAudio): sin I/O de disco, sin malloc pesado,
        sin GIL largo. Solo lectura de RAM y aritmética sobre el bloque.
        """
        def callback(outdata, frames, time_info, status) -> None:
            # ── Latencia (una sola vez, primer frame) ──────────────────
            if not ctx.first_cb_done:
                ctx.latency_ms = (time.perf_counter() - ctx.t_start) * 1_000
                ctx.first_cb_done = True
                self.latency_report.emit(ctx.cue_id, ctx.latency_ms)

            data = ctx.data
            n = len(data)

            def _wrap() -> bool:
                # ¿Repetir desde el principio? (loop infinito o play_count)
                if ctx.request_stop:
                    return False
                if ctx.loop:
                    ctx.position = 0
                    return True
                if ctx.passes < ctx.play_count - 1:
                    ctx.passes += 1
                    ctx.position = 0
                    return True
                return False

            # ── Fade-out automático al final (si no hay loop ni stop) ───
            if (ctx.fade_out_frames > 0 and not ctx.fade_out_armed
                    and not ctx.loop and not ctx.fade_active
                    and (n - ctx.position) <= ctx.fade_out_frames):
                ctx.fade_out_armed = True
                ctx.fade_from = ctx.gain
                ctx.fade_to = 0.0
                ctx.fade_total = max(1, n - ctx.position)
                ctx.fade_pos = 0
                ctx.stop_after_fade = False   # el fin natural del buffer para
                ctx.fade_active = True

            remaining: int = n - ctx.position
            if remaining <= 0:
                if _wrap():
                    remaining = n
                else:
                    outdata[:] = 0.0
                    raise sd.CallbackStop()

            chunk: int = min(frames, remaining)
            block = np.array(data[ctx.position:ctx.position + chunk], dtype=np.float32)

            # ── Aplicar envolvente de ganancia ─────────────────────────
            if ctx.fade_active:
                total = ctx.fade_total
                p0 = ctx.fade_pos
                p1 = min(p0 + chunk, total)
                g0 = ctx.fade_from + (ctx.fade_to - ctx.fade_from) * (p0 / total)
                g1 = ctx.fade_from + (ctx.fade_to - ctx.fade_from) * (p1 / total)
                ramp = np.linspace(g0, g1, chunk, dtype=np.float32)
                if block.ndim > 1:
                    block *= ramp[:, None]
                else:
                    block *= ramp
                ctx.fade_pos += chunk
                if ctx.fade_pos >= total:
                    ctx.gain = ctx.fade_to
                    ctx.fade_active = False
                    if ctx.stop_after_fade:
                        ctx.request_stop = True
            elif ctx.gain != 1.0:
                block *= ctx.gain

            # Volumen master global
            if self._master != 1.0:
                block *= self._master

            outdata[:chunk] = block
            if chunk < frames:
                outdata[chunk:] = 0.0

            ctx.position += chunk

            # ── RMS para VU (throttle: 1 de cada 4 callbacks ≈ 20 Hz) ──
            ctx.level_counter += 1
            if ctx.level_counter >= 4:
                ctx.level_counter = 0
                flat = block.ravel()
                m = len(flat)
                if m:
                    rms = float(np.dot(flat, flat) / m) ** 0.5
                    self.level_report.emit(rms)

            if ctx.request_stop or ctx.position >= n:
                if not _wrap():
                    raise sd.CallbackStop()

        return callback

    def _make_finished_callback(self, cue_id: int) -> Callable:
        def finished() -> None:
            # Llamado por sounddevice (hilo RT) al terminar el stream.
            ctx = self._active.get(cue_id)
            if ctx is None or ctx.paused:
                return   # parada manual / pausa ya gestionada en main thread
            self._active.pop(cue_id, None)
            self._states[cue_id] = "STOPPED"
            self.state_changed.emit(cue_id, "STOPPED")
        return finished

    # ------------------------------------------------------------------
    # Helpers de fade / stop
    # ------------------------------------------------------------------

    def _begin_fade(
        self, ctx: _PlaybackCtx, target: float, duration: float, stop_after: bool
    ) -> None:
        """Configura una envolvente de fade sobre un ctx activo (thread-safe)."""
        if duration <= 0.0:
            # Cambio instantáneo
            ctx.volume = target
            ctx.gain = target
            ctx.fade_active = False
            if stop_after:
                self._stop_ctx(ctx.cue_id, emit_state=True)
            return
        ctx.fade_from = ctx.gain
        ctx.fade_to = target
        ctx.fade_total = max(1, int(duration * ctx.samplerate))
        ctx.fade_pos = 0
        ctx.stop_after_fade = stop_after
        if not stop_after:
            ctx.volume = target
        ctx.fade_active = True   # activar el flag SIEMPRE en último lugar

    def _stop_ctx(self, cue_id: int, emit_state: bool) -> None:
        """Cierra el stream de un cue activo y lo retira del registro."""
        ctx = self._active.pop(cue_id, None)
        if ctx is None:
            return
        stream = ctx.stream
        ctx.stream = None
        self._close_stream(stream)
        if emit_state:
            self._set_state(cue_id, "STOPPED")

    @staticmethod
    def _close_stream(stream) -> None:
        if stream is not None:
            try:
                stream.stop(ignore_errors=True)
                stream.close(ignore_errors=True)
            except Exception:
                pass

    # ------------------------------------------------------------------
    # Helpers de estado
    # ------------------------------------------------------------------

    def _set_state(self, cue_id: int, new_state: str) -> None:
        self._states[cue_id] = new_state
        self.state_changed.emit(cue_id, new_state)

    def _emit_error(self, cue_id: int, message: str) -> None:
        self._states[cue_id] = "ERROR"
        self.state_changed.emit(cue_id, "ERROR")
        self.error_occurred.emit(cue_id, message)
