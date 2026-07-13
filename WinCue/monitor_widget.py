# =============================================================================
# monitor_widget.py — Mini-monitor PGM/PVW (miniatura por ffmpeg)
# =============================================================================
# Muestra un fotograma extraído con ffmpeg. Se usa como monitor de PROGRAMA
# (frame al tiempo actual del vídeo que se está reproduciendo) o de PREVIEW
# (primer frame del cue en standby). Lleva un distintivo PGM/PVW en la esquina.
# =============================================================================

from __future__ import annotations

import shutil
import subprocess
from typing import Optional

from PySide6.QtCore import QObject, QRect, QRunnable, Qt, QThreadPool, Signal
from PySide6.QtGui import QColor, QImage, QPainter, QPixmap
from PySide6.QtWidgets import QWidget

_NO_WINDOW = 0x08000000 if hasattr(subprocess, "CREATE_NO_WINDOW") else 0


class _Sig(QObject):
    done:     Signal = Signal(object, object)   # (QImage, token)
    finished: Signal = Signal(object)           # token (siempre, éxito o fallo)


class _FrameTask(QRunnable):
    def __init__(self, path: str, t: float, sig: _Sig, token) -> None:
        super().__init__()
        self._path, self._t, self._sig, self._token = path, t, sig, token

    def run(self) -> None:
        ffmpeg = shutil.which("ffmpeg")
        try:
            if ffmpeg:
                out = subprocess.run(
                    [ffmpeg, "-nostdin", "-ss", f"{max(0.0, self._t):.3f}", "-i", self._path,
                     "-frames:v", "1", "-vf", "scale=-1:180", "-c:v", "png",
                     "-f", "image2pipe", "-"],
                    capture_output=True, timeout=8, creationflags=_NO_WINDOW)
                img = QImage()
                if out.stdout and img.loadFromData(out.stdout, "PNG"):
                    try:
                        self._sig.done.emit(img, self._token)
                    except RuntimeError:
                        pass
        except Exception:
            pass
        finally:
            try:
                self._sig.finished.emit(self._token)   # libera el guard "busy"
            except RuntimeError:
                pass


class MonitorWidget(QWidget):
    """Mini-monitor que muestra un fotograma (PGM o PVW)."""

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.setFixedSize(320, 180)
        self._pix: Optional[QPixmap] = None
        self._badge = ""
        self._badge_color = QColor("#888")
        self._sig = _Sig(self)
        self._sig.done.connect(self._on_frame)
        self._sig.finished.connect(self._on_finished)
        self._token = 0
        self._busy = False
        self._empty_msg = "—"

    def set_badge(self, text: str, color: str) -> None:
        self._badge = text
        self._badge_color = QColor(color)
        self.update()

    def show_frame(self, path: str, t: float, force: bool = False) -> None:
        """Extrae y muestra el fotograma de `path` al segundo `t`.

        Si ya hay una extracción en curso y no es `force`, se descarta (evita
        acumular procesos ffmpeg y que el monitor vaya a tirones)."""
        if self._busy and not force:
            return
        self._busy = True
        self._token += 1
        task = _FrameTask(path, t, self._sig, self._token)
        QThreadPool.globalInstance().start(task)

    def _on_finished(self, token) -> None:
        self._busy = False

    def clear(self, msg: str = "—") -> None:
        self._pix = None
        self._empty_msg = msg
        self.update()

    def _on_frame(self, img, token) -> None:
        if token != self._token:
            return   # llegó un frame viejo
        self._pix = QPixmap.fromImage(img)
        self.update()

    def paintEvent(self, event) -> None:  # noqa: N802
        p = QPainter(self)
        w, h = self.width(), self.height()
        p.fillRect(0, 0, w, h, QColor("#000000"))
        if self._pix is not None:
            # letterbox preservando aspecto
            pr = self._pix.width() / self._pix.height()
            if pr > w / h:
                dw = w; dh = int(w / pr)
            else:
                dh = h; dw = int(h * pr)
            x = (w - dw) // 2; y = (h - dh) // 2
            p.drawPixmap(QRect(x, y, dw, dh), self._pix)
        else:
            p.setPen(QColor("#555"))
            p.drawText(self.rect(), Qt.AlignmentFlag.AlignCenter, self._empty_msg)
        # Distintivo PGM/PVW
        if self._badge:
            p.fillRect(QRect(0, 0, 44, 18), self._badge_color)
            p.setPen(QColor("#000000"))
            f = p.font(); f.setBold(True); f.setPointSize(8); p.setFont(f)
            p.drawText(QRect(0, 0, 44, 18), Qt.AlignmentFlag.AlignCenter, self._badge)
        p.end()
