# =============================================================================
# inspector_widgets.py — Widgets auxiliares del inspector (estilo QLab)
# =============================================================================
# · TimeEdit       — campo de tiempo en formato MM:SS.mmm (o H:MM:SS.mmm).
# · ColorSwatches  — fila de puntos de color (X + 6 colores) como en QLab.
# =============================================================================

from __future__ import annotations

import math

from PySide6.QtCore import Qt, QTimer, Signal
from PySide6.QtGui import QColor, QPainter
from PySide6.QtWidgets import QHBoxLayout, QLineEdit, QPushButton, QWidget

from cue_model import CUE_COLORS


class VerticalVu(QWidget):
    """VU meter vertical (para la tira de canal del Audio Levels)."""

    _DB_MIN, _DB_MAX = -60.0, 0.0
    _DECAY = 0.80
    _C_GREEN = QColor("#2ecc71")
    _C_AMBER = QColor("#e8a200")
    _C_RED = QColor("#e74c3c")
    _C_BG = QColor("#0a0a0a")

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.setFixedWidth(16)
        self.setMinimumHeight(120)
        self._level = 0.0
        self._peak = 0.0
        self._t = QTimer(self); self._t.setInterval(50)
        self._t.timeout.connect(self._tick); self._t.start()

    def set_level(self, rms: float) -> None:
        db = 20.0 * math.log10(max(rms, 1e-9))
        lin = max(0.0, min(1.0, (db - self._DB_MIN) / (self._DB_MAX - self._DB_MIN)))
        if lin > self._level:
            self._level = lin
        if lin >= self._peak:
            self._peak = lin
        self.update()

    def _tick(self) -> None:
        if self._level > 0.001:
            self._level *= self._DECAY; self.update()
        if self._peak > 0.001:
            self._peak *= 0.94; self.update()

    def paintEvent(self, event) -> None:  # noqa: N802
        p = QPainter(self)
        w, h = self.width(), self.height()
        p.fillRect(0, 0, w, h, self._C_BG)
        g_end = int(0.70 * h); a_end = int(0.90 * h)
        lv = int(self._level * h)
        for y in range(h):
            if y > lv:
                continue
            frac_from_bottom = y
            yy = h - y
            if frac_from_bottom >= a_end:
                col = self._C_RED
            elif frac_from_bottom >= g_end:
                col = self._C_AMBER
            else:
                col = self._C_GREEN
            p.fillRect(1, yy - 1, w - 2, 1, col)
        if self._peak > 0.01:
            py = h - int(self._peak * h)
            p.fillRect(1, max(0, py - 1), w - 2, 2, QColor("#ffffff"))
        p.end()


class TimeEdit(QLineEdit):
    """Campo de tiempo tipo QLab: muestra MM:SS.mmm y devuelve segundos."""

    valueChanged: Signal = Signal(float)

    def __init__(self, parent=None, width: int = 92) -> None:
        super().__init__(parent)
        self.setFixedWidth(width)
        self.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        self._value: float = 0.0
        self.setText(self._fmt(0.0))
        self.editingFinished.connect(self._on_edit)

    def setValue(self, seconds: float) -> None:
        self._value = max(0.0, float(seconds))
        self.setText(self._fmt(self._value))

    def value(self) -> float:
        return self._value

    @staticmethod
    def _fmt(s: float) -> str:
        total = max(0.0, s)
        h = int(total) // 3600
        m = (int(total) % 3600) // 60
        sec = total - (h * 3600 + m * 60)
        if h > 0:
            return f"{h}:{m:02d}:{sec:06.3f}"
        return f"{m:02d}:{sec:06.3f}"

    @staticmethod
    def _parse(txt: str):
        parts = txt.strip().split(":")
        try:
            nums = [float(p) for p in parts if p != ""]
        except ValueError:
            return None
        if not nums:
            return None
        total = 0.0
        for p in nums:            # de más significativo a menos
            total = total * 60 + p
        return total

    def _on_edit(self) -> None:
        v = self._parse(self.text())
        if v is None:
            self.setText(self._fmt(self._value))
            return
        self._value = v
        self.setText(self._fmt(v))
        self.valueChanged.emit(v)


class ColorSwatches(QWidget):
    """Fila de puntos de color estilo QLab: ✕ (sin color) + 6 colores."""

    colorChanged: Signal = Signal(str)   # clave en CUE_COLORS ("" = sin color)

    _ORDER = ("", "red", "orange", "yellow", "green", "blue", "purple")

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        lo = QHBoxLayout(self)
        lo.setContentsMargins(0, 0, 0, 0)
        lo.setSpacing(4)
        self._current: str = ""
        self._buttons: dict[str, QPushButton] = {}
        for key in self._ORDER:
            b = QPushButton("✕" if key == "" else "")
            b.setFixedSize(16, 16)
            b.setCursor(Qt.CursorShape.PointingHandCursor)
            b.clicked.connect(lambda _=False, k=key: self._pick(k))
            self._buttons[key] = b
            lo.addWidget(b)
        self._restyle()

    def setColor(self, key: str) -> None:
        self._current = key if key in self._buttons else ""
        self._restyle()

    def _pick(self, key: str) -> None:
        self._current = key
        self._restyle()
        self.colorChanged.emit(key)

    def _restyle(self) -> None:
        for key, b in self._buttons.items():
            sel = (key == self._current)
            ring = "2px solid #ffffff" if sel else "1px solid #333"
            if key == "":
                b.setStyleSheet(
                    f"QPushButton{{border:{'2px solid #fff' if sel else '1px solid #555'};"
                    "border-radius:8px;color:#aaa;font-size:9px;background:#222;}")
            else:
                b.setStyleSheet(
                    f"QPushButton{{border:{ring};border-radius:8px;"
                    f"background:{CUE_COLORS.get(key, '#888')};}}")
