# =============================================================================
# vu_meter.py — Widget medidor de nivel de audio (VU / Peak meter)
# =============================================================================
#
# VuMeter(QWidget) de anchura fija (320 px × 14 px).
#
# DINÁMICA:
#   · Ataque instantáneo: si el nuevo nivel > nivel actual, salta al instante.
#   · Decaimiento suave: ×0.82 por tick (50 ms) ≈ −1.7 dB/100 ms.
#   · Peak hold: mantiene el pico 1 segundo (20 ticks) antes de decaer.
#
# ESCALA:
#   · RMS lineal → dBFS: 20·log10(rms).  Rango mostrado: −60 dBFS a 0 dBFS.
#   · 0 %–70 % del ancho: verde  (#2ecc71)
#   · 70 %–90 %: ámbar (#e8a200)
#   · 90 %–100 %: rojo  (#e74c3c)
#   · Marcador de pico: línea de 2 px del color de la zona.
#
# HILOS:
#   · set_level() se llama desde el main thread (señal queued desde el hilo de
#     audio de sounddevice). El timer de decay también corre en el main thread.
#   · paintEvent solo en el main thread.
# =============================================================================

from __future__ import annotations

import math

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QColor, QPainter
from PySide6.QtWidgets import QWidget


class VuMeter(QWidget):
    """Medidor de nivel RMS con peak hold, ataque rápido y decay suave."""

    # Rango de dBFS mostrado
    _DB_MIN: float = -60.0
    _DB_MAX: float =   0.0

    # Factor de decay por tick de 50 ms  (0.82 ≈ −1.7 dB/100 ms)
    _DECAY: float = 0.82

    # Ticks de hold antes de empezar a decaer el pico (20 × 50 ms = 1 s)
    _PEAK_HOLD_TICKS: int = 20

    # Umbrales de color en fracción 0–1 del ancho
    _GREEN_END:  float = 0.70
    _AMBER_END:  float = 0.90

    # Colores por zona
    _C_GREEN = QColor("#2ecc71")
    _C_AMBER = QColor("#e8a200")
    _C_RED   = QColor("#e74c3c")
    _C_BG    = QColor(0x0a, 0x0a, 0x0a)

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self.setFixedSize(320, 14)
        self.setAttribute(Qt.WidgetAttribute.WA_OpaquePaintEvent, True)

        self._level: float     = 0.0   # 0–1 nivel del bar actual
        self._peak:  float     = 0.0   # 0–1 nivel del pico
        self._peak_ticks: int  = 0     # ticks transcurridos desde el último pico

        self._decay_timer = QTimer(self)
        self._decay_timer.setInterval(50)
        self._decay_timer.timeout.connect(self._tick)
        self._decay_timer.start()

    # ------------------------------------------------------------------
    # API pública
    # ------------------------------------------------------------------

    def set_level(self, rms: float) -> None:
        """Actualiza el nivel con un nuevo valor RMS lineal (0.0 – ~1.0+).

        Llamar SOLO desde el main thread (la señal queued lo garantiza).
        """
        # dBFS → fracción lineal 0–1 dentro del rango mostrado
        db = 20.0 * math.log10(max(rms, 1e-9))
        linear = max(0.0, min(1.0, (db - self._DB_MIN) / (self._DB_MAX - self._DB_MIN)))

        # Ataque instantáneo
        if linear > self._level:
            self._level = linear

        # Peak hold: resetea el contador si superamos el pico actual
        if linear >= self._peak:
            self._peak = linear
            self._peak_ticks = 0

        self.update()

    def reset(self) -> None:
        """Pone el medidor a cero (llamar cuando el cue para)."""
        self._level = 0.0
        self._peak  = 0.0
        self._peak_ticks = 0
        self.update()

    # ------------------------------------------------------------------
    # Decay interno
    # ------------------------------------------------------------------

    def _tick(self) -> None:
        """Tick de 50 ms: aplica decay al bar y al pico si corresponde."""
        dirty = False

        # Decay del bar
        if self._level > 0.001:
            self._level *= self._DECAY
            dirty = True
        elif self._level > 0.0:
            self._level = 0.0
            dirty = True

        # Pico: hold → luego decay
        if self._peak > 0.001:
            self._peak_ticks += 1
            if self._peak_ticks > self._PEAK_HOLD_TICKS:
                self._peak *= self._DECAY
                dirty = True
        elif self._peak > 0.0:
            self._peak = 0.0
            dirty = True

        if dirty:
            self.update()

    # ------------------------------------------------------------------
    # Pintado
    # ------------------------------------------------------------------

    def paintEvent(self, event) -> None:  # noqa: N802
        w = self.width()
        h = self.height()

        bar_w  = int(self._level * w)
        peak_x = int(self._peak  * w)

        g_end = int(self._GREEN_END * w)   # px donde termina el verde
        a_end = int(self._AMBER_END * w)   # px donde termina el ámbar

        p = QPainter(self)

        # ── Fondo negro ───────────────────────────────────────────────────
        p.fillRect(0, 0, w, h, self._C_BG)

        # ── Barra de nivel (izquierda → derecha, segmentos de color) ──────
        if bar_w > 0:
            draw_w = min(bar_w, g_end)
            p.fillRect(0, 1, draw_w, h - 2, self._C_GREEN)

            if bar_w > g_end:
                draw_w = min(bar_w, a_end) - g_end
                p.fillRect(g_end, 1, draw_w, h - 2, self._C_AMBER)

            if bar_w > a_end:
                p.fillRect(a_end, 1, bar_w - a_end, h - 2, self._C_RED)

        # ── Marcador de pico (2 px, del color de la zona donde cae) ──────
        if self._peak > 0.01 and peak_x < w:
            if peak_x >= a_end:
                peak_color = self._C_RED
            elif peak_x >= g_end:
                peak_color = self._C_AMBER
            else:
                peak_color = QColor("#ffffff")
            p.fillRect(max(0, peak_x - 1), 1, 2, h - 2, peak_color)

        p.end()
