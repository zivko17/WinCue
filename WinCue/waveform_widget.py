# =============================================================================
# waveform_widget.py — Barra de trim (in/out) con onda opcional
# =============================================================================
# Barra horizontal cuya anchura = duración del clip, con dos manejadores
# arrastrables (entrada/salida). La región seleccionada se resalta y el resto
# se atenúa. Regla de tiempo arriba. En AUDIO dibuja la forma de onda de fondo;
# en VÍDEO una barra lisa (los thumbnails se veían mal). Emite
# markers_changed(start_s, end_s) al soltar.
# =============================================================================

from __future__ import annotations

import shutil
import subprocess
from typing import Optional

import numpy as np
from PySide6.QtCore import QObject, QRect, QRunnable, Qt, QThreadPool, Signal
from PySide6.QtGui import QColor, QLinearGradient, QPainter, QPen
from PySide6.QtWidgets import QWidget

from audio_decoder import AudioDecoder, AudioDecoderError

_BUCKETS = 1600
_NO_WINDOW = 0x08000000 if hasattr(subprocess, "CREATE_NO_WINDOW") else 0
_RULER_H = 16
_HANDLE_HIT = 8   # px de tolerancia para agarrar un manejador


# ---------------------------------------------------------------------------
# Tareas en segundo plano
# ---------------------------------------------------------------------------

class _Signals(QObject):
    wave: Signal = Signal(object, float)   # (peaks 2xN, duration)  — audio
    dur:  Signal = Signal(float)           # duration               — vídeo
    failed: Signal = Signal(str)


def _safe(sig, *args) -> None:
    try:
        sig.emit(*args)
    except RuntimeError:
        pass


class _WaveTask(QRunnable):
    def __init__(self, path, signals):
        super().__init__()
        self._path = path; self._signals = signals; self._cancelled = False

    def cancel(self): self._cancelled = True

    def run(self):
        try:
            data, sr = AudioDecoder.decode(self._path)
            if self._cancelled:
                return
            mono = data.mean(axis=1) if data.ndim > 1 else data
            n = len(mono)
            if n == 0 or not sr:
                _safe(self._signals.failed, "sin audio"); return
            dur = n / float(sr)
            buckets = min(_BUCKETS, n)
            edges = np.linspace(0, n, buckets + 1).astype(int)
            mins = np.empty(buckets, dtype=np.float32)
            maxs = np.empty(buckets, dtype=np.float32)
            for i in range(buckets):
                seg = mono[edges[i]:edges[i + 1]]
                if len(seg):
                    mins[i] = float(seg.min()); maxs[i] = float(seg.max())
                else:
                    mins[i] = 0.0; maxs[i] = 0.0
            _safe(self._signals.wave, np.vstack([mins, maxs]), dur)
        except (FileNotFoundError, AudioDecoderError, Exception) as exc:
            _safe(self._signals.failed, str(exc))


class _DurTask(QRunnable):
    def __init__(self, path, signals):
        super().__init__()
        self._path = path; self._signals = signals; self._cancelled = False

    def cancel(self): self._cancelled = True

    def run(self):
        probe = shutil.which("ffprobe")
        if not probe:
            _safe(self._signals.failed, "ffprobe no encontrado"); return
        try:
            out = subprocess.run(
                [probe, "-v", "error", "-show_entries", "format=duration",
                 "-of", "default=nw=1:nk=1", self._path],
                capture_output=True, text=True, timeout=8, creationflags=_NO_WINDOW)
            if not self._cancelled:
                _safe(self._signals.dur, float(out.stdout.strip()))
        except Exception as exc:
            _safe(self._signals.failed, str(exc))


# ---------------------------------------------------------------------------
# Barra de trim
# ---------------------------------------------------------------------------

class WaveformWidget(QWidget):

    markers_changed: Signal = Signal(float, float)   # (start_s, end_s)

    _C_BG = QColor("#0e0e0e")
    _C_WAVE = QColor("#4a86b8")
    _C_WAVE_DIM = QColor("#243038")
    _C_MARK = QColor("#e8c000")
    _C_REGION = QColor(232, 192, 0, 34)
    _C_DIM = QColor(0, 0, 0, 150)
    _C_RULER = QColor("#777")

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.setMinimumHeight(96)
        self.setMinimumWidth(240)
        self.setMouseTracking(True)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self._mode = "audio"
        self._peaks: Optional[np.ndarray] = None
        self._duration = 0.0
        self._start = 0.0
        self._end = 0.0
        self._msg = "— sin media —"
        self._sig = _Signals(self)
        self._sig.wave.connect(self._on_wave)
        self._sig.dur.connect(self._on_dur)
        self._sig.failed.connect(self._on_failed)
        self._task = None
        self._drag: Optional[str] = None

    # -- API -----------------------------------------------------------
    def load(self, path, trim_start, trim_end):
        self._mode = "audio"; self._peaks = None
        self._start = max(0.0, trim_start); self._end = max(0.0, trim_end)
        self._msg = "cargando…"; self.update()
        self._run(_WaveTask(path, self._sig))

    def load_video(self, path, trim_start, trim_end):
        self._mode = "video"; self._peaks = None
        self._start = max(0.0, trim_start); self._end = max(0.0, trim_end)
        self._msg = "leyendo duración…"; self.update()
        self._run(_DurTask(path, self._sig))

    def clear(self):
        self._peaks = None; self._duration = 0.0
        self._msg = "— sin media —"; self.update()

    def set_markers(self, start, end):
        self._start = max(0.0, start); self._end = max(0.0, end); self.update()

    def _run(self, task):
        if self._task is not None:
            self._task.cancel()
        self._task = task
        QThreadPool.globalInstance().start(task)

    def _on_wave(self, peaks, dur):
        self._peaks = peaks; self._duration = dur
        if self._end <= 0.0:
            self._end = dur
        self._msg = ""; self.update()

    def _on_dur(self, dur):
        self._duration = dur
        if self._end <= 0.0:
            self._end = dur
        self._msg = ""; self.update()

    def _on_failed(self, _msg):
        self._msg = "no disponible"; self.update()

    # -- Geometría interna ---------------------------------------------
    def _sec_to_x(self, sec):
        if self._duration <= 0:
            return 0.0
        return max(0.0, min(1.0, sec / self._duration)) * self.width()

    def _x_to_sec(self, x):
        if self._duration <= 0:
            return 0.0
        return max(0.0, min(1.0, x / max(1, self.width()))) * self._duration

    @staticmethod
    def _fmt(s):
        m = int(s) // 60
        return f"{m}:{s - m*60:04.1f}"

    # -- Pintado -------------------------------------------------------
    def paintEvent(self, event):  # noqa: N802
        p = QPainter(self)
        w, h = self.width(), self.height()
        p.fillRect(0, 0, w, h, self._C_BG)

        if self._duration <= 0.0:
            p.setPen(QColor("#666"))
            p.drawText(self.rect(), Qt.AlignmentFlag.AlignCenter, self._msg)
            p.end(); return

        body = QRect(0, _RULER_H, w, h - _RULER_H)
        s_x = self._sec_to_x(self._start)
        e_x = self._sec_to_x(self._end if self._end > 0 else self._duration)

        # Cuerpo de la barra
        if self._mode == "audio" and self._peaks is not None:
            mid = body.center().y()
            half = body.height() // 2 - 2
            n = self._peaks.shape[1]
            mins, maxs = self._peaks[0], self._peaks[1]
            for x in range(w):
                i = min(n - 1, int(x / max(1, w) * n))
                y0 = mid - int(maxs[i] * half)
                y1 = mid - int(mins[i] * half)
                p.setPen(self._C_WAVE if s_x <= x <= e_x else self._C_WAVE_DIM)
                p.drawLine(x, y0, x, y1)
        else:
            # Barra lisa (vídeo)
            grad = QLinearGradient(0, body.top(), 0, body.bottom())
            grad.setColorAt(0.0, QColor("#2a3540")); grad.setColorAt(1.0, QColor("#1a222a"))
            p.fillRect(body, grad)

        # Región seleccionada resaltada + zonas recortadas atenuadas
        p.fillRect(QRect(int(s_x), body.top(), int(e_x - s_x), body.height()), self._C_REGION)
        if s_x > 0:
            p.fillRect(QRect(0, body.top(), int(s_x), body.height()), self._C_DIM)
        if e_x < w:
            p.fillRect(QRect(int(e_x), body.top(), w - int(e_x), body.height()), self._C_DIM)

        # Regla de tiempo (0, 25, 50, 75, 100 %)
        p.setPen(self._C_RULER)
        for frac in (0.0, 0.25, 0.5, 0.75, 1.0):
            x = int(frac * w)
            xx = min(max(x, 1), w - 1)
            p.drawLine(xx, 0, xx, _RULER_H - 4)
            label = self._fmt(frac * self._duration)
            tx = xx + (2 if frac == 0 else (-p.fontMetrics().horizontalAdvance(label) - 2 if frac == 1 else -p.fontMetrics().horizontalAdvance(label) // 2))
            p.drawText(tx, _RULER_H - 4, label)

        # Manejadores in/out
        self._draw_handle(p, s_x, h, left=True)
        self._draw_handle(p, e_x, h, left=False)
        p.end()

    def _draw_handle(self, p, x, h, left):
        pen = QPen(self._C_MARK); pen.setWidth(2)
        p.setPen(pen)
        p.drawLine(int(x), _RULER_H, int(x), h)
        gw = 5   # pequeño grip a un lado del manejador
        p.fillRect(QRect(int(x) - (gw if left else 0), _RULER_H, gw, 10), self._C_MARK)

    # -- Interacción ---------------------------------------------------
    def mousePressEvent(self, event):  # noqa: N802
        if self._duration <= 0.0:
            return
        x = event.position().x()
        s_x = self._sec_to_x(self._start)
        e_x = self._sec_to_x(self._end if self._end > 0 else self._duration)
        if abs(x - s_x) <= _HANDLE_HIT and abs(x - s_x) <= abs(x - e_x):
            self._drag = "start"
        elif abs(x - e_x) <= _HANDLE_HIT:
            self._drag = "end"
        else:
            self._drag = "start" if abs(x - s_x) <= abs(x - e_x) else "end"
        self._update_drag(x)

    def mouseMoveEvent(self, event):  # noqa: N802
        if self._drag:
            self._update_drag(event.position().x())

    def mouseReleaseEvent(self, event):  # noqa: N802
        if self._drag:
            self._drag = None
            self.markers_changed.emit(self._start, self._end if self._end > 0 else self._duration)

    def _update_drag(self, x):
        sec = self._x_to_sec(x)
        if self._drag == "start":
            self._start = min(sec, (self._end if self._end > 0 else self._duration))
        else:
            self._end = max(sec, self._start)
        self.update()
