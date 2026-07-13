# =============================================================================
# geometry_preview.py — Preview de posición/escala/rotación (Display & Geometry)
# =============================================================================
# Dibuja la "surface" (rectángulo negro 16:9) con una miniatura del vídeo
# posicionada según la geometría del cue. Se puede arrastrar para trasladar y
# hacer scroll para escalar. Emite geometry_changed({translate_x/y, scale_x/y}).
# =============================================================================

from __future__ import annotations

import shutil
import subprocess
from typing import Optional

from PySide6.QtCore import QObject, QPointF, QRectF, Qt, QRunnable, QThreadPool, Signal
from PySide6.QtGui import QColor, QImage, QPainter, QPen, QPixmap
from PySide6.QtWidgets import QApplication, QWidget

from settings_manager import SettingsManager

_NO_WINDOW = 0x08000000 if hasattr(subprocess, "CREATE_NO_WINDOW") else 0


def _surface_dims():
    """Dimensiones del canvas: de Settings, o de la pantalla primaria, o 1920×1080."""
    sm = SettingsManager.instance()
    w = int(sm.get("surface_width", 0) or 0)
    h = int(sm.get("surface_height", 0) or 0)
    if w > 0 and h > 0:
        return float(w), float(h)
    scr = QApplication.primaryScreen()
    if scr is not None:
        g = scr.geometry()
        if g.width() > 0:
            return float(g.width()), float(g.height())
    return 1920.0, 1080.0


class _FrameSignals(QObject):
    done: Signal = Signal(object)   # QImage


class _FrameTask(QRunnable):
    def __init__(self, path: str, signals: _FrameSignals) -> None:
        super().__init__()
        self._path = path
        self._signals = signals
        self._cancelled = False

    def cancel(self) -> None:
        self._cancelled = True

    def run(self) -> None:
        ffmpeg = shutil.which("ffmpeg")
        if not ffmpeg:
            return
        try:
            out = subprocess.run(
                [ffmpeg, "-nostdin", "-ss", "1", "-i", self._path,
                 "-frames:v", "1", "-vf", "scale=-1:180",
                 "-c:v", "png", "-f", "image2pipe", "-"],
                capture_output=True, timeout=8, creationflags=_NO_WINDOW)
            img = QImage()
            if out.stdout and img.loadFromData(out.stdout, "PNG") and not self._cancelled:
                try:
                    self._signals.done.emit(img)
                except RuntimeError:
                    pass   # objeto de señales destruido (cierre)
        except Exception:
            pass


class GeometryPreview(QWidget):
    """Preview arrastrable de la geometría del vídeo sobre la surface."""

    geometry_changed: Signal = Signal(dict)

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.setMinimumSize(220, 130)
        self.setMouseTracking(True)
        self._pix: Optional[QPixmap] = None
        self._mode = "FULL"
        self._tx = 0.0; self._ty = 0.0
        self._sx = 1.0; self._sy = 1.0
        self._rot = 0.0
        self._preserve = True
        self._opacity = 100.0
        self._sig = _FrameSignals(self)
        self._sig.done.connect(self._on_frame)
        self._task: Optional[_FrameTask] = None
        self._drag = False
        self._last = QPointF()

    # ------------------------------------------------------------------
    def load(self, path: str) -> None:
        self._pix = None
        self.update()
        if self._task is not None:
            self._task.cancel()
        self._task = _FrameTask(path, self._sig)
        QThreadPool.globalInstance().start(self._task)

    def clear(self) -> None:
        self._pix = None
        self.update()

    def _on_frame(self, img) -> None:
        self._pix = QPixmap.fromImage(img)
        self.update()

    def set_geometry(self, mode, tx, ty, sx, sy, rot, preserve, opacity) -> None:
        self._mode = mode
        self._tx, self._ty = tx, ty
        self._sx, self._sy = sx, sy
        self._rot = rot
        self._preserve = preserve
        self._opacity = opacity
        self.update()

    # ------------------------------------------------------------------
    def _surf_rect(self) -> QRectF:
        surf_w, surf_h = _surface_dims()
        w, h = self.width(), self.height()
        margin = 8
        aw, ah = w - 2 * margin, h - 2 * margin
        if aw / ah > surf_w / surf_h:
            sh = ah; sw = ah * surf_w / surf_h
        else:
            sw = aw; sh = aw * surf_h / surf_w
        x = (w - sw) / 2; y = (h - sh) / 2
        return QRectF(x, y, sw, sh)

    def _k(self) -> float:
        surf_w, _ = _surface_dims()
        return self._surf_rect().width() / surf_w

    def paintEvent(self, event) -> None:  # noqa: N802
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.fillRect(self.rect(), QColor("#0a0a0a"))
        surf = self._surf_rect()
        # Surface
        p.fillRect(surf, QColor("#000000"))
        p.setPen(QPen(QColor("#333"), 1))
        p.drawRect(surf)

        if self._pix is None:
            p.setPen(QColor("#555"))
            p.drawText(surf, Qt.AlignmentFlag.AlignCenter, "…")
            p.end(); return

        # Rect base = vídeo ajustado a la surface preservando aspecto (escala 1)
        pr = self._pix.width() / self._pix.height()
        sr = surf.width() / surf.height()
        if self._mode == "FULL" and not self._preserve:
            base_w, base_h = surf.width(), surf.height()   # estirar
        else:
            if pr > sr:
                base_w = surf.width(); base_h = surf.width() / pr
            else:
                base_h = surf.height(); base_w = surf.height() * pr

        if self._mode == "CUSTOM":
            base_w *= self._sx; base_h *= self._sy
            cx = surf.center().x() + self._tx * self._k()
            cy = surf.center().y() - self._ty * self._k()
        else:
            cx, cy = surf.center().x(), surf.center().y()

        p.setOpacity(max(0.0, min(1.0, self._opacity / 100.0)))
        p.save()
        p.translate(cx, cy)
        if self._mode == "CUSTOM" and abs(self._rot) > 0.01:
            p.rotate(self._rot)
        target = QRectF(-base_w / 2, -base_h / 2, base_w, base_h)
        p.drawPixmap(target, self._pix, QRectF(self._pix.rect()))
        p.setOpacity(1.0)
        p.setPen(QPen(QColor("#4a9eff"), 1))
        p.drawRect(target)
        p.restore()
        p.end()

    # ------------------------------------------------------------------
    def mousePressEvent(self, event) -> None:  # noqa: N802
        if self._mode != "CUSTOM" or self._pix is None:
            return
        self._drag = True
        self._last = event.position()

    def mouseMoveEvent(self, event) -> None:  # noqa: N802
        if not self._drag:
            return
        pos = event.position()
        dx = pos.x() - self._last.x()
        dy = pos.y() - self._last.y()
        self._last = pos
        k = self._k()
        self._tx += dx / k
        self._ty -= dy / k
        self.update()
        self.geometry_changed.emit({"translate_x": round(self._tx), "translate_y": round(self._ty)})

    def mouseReleaseEvent(self, event) -> None:  # noqa: N802
        self._drag = False

    def wheelEvent(self, event) -> None:  # noqa: N802
        if self._mode != "CUSTOM" or self._pix is None:
            return
        factor = 1.08 if event.angleDelta().y() > 0 else 0.926
        self._sx = max(0.05, self._sx * factor)
        self._sy = max(0.05, self._sy * factor)
        self.update()
        self.geometry_changed.emit({"scale_x": round(self._sx, 3), "scale_y": round(self._sy, 3)})
