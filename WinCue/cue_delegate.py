# =============================================================================
# cue_delegate.py — Delegado de Renderizado de la Tabla
# =============================================================================
# Responsabilidad: Pintar cada celda de la QTableView con los colores correctos
#   según el rol de la fila (activa, precarga, error, normal).
#
# POR QUÉ DELEGADO EN VEZ DE setBackground() / setForeground()
# ─────────────────────────────────────────────────────────────
# El enfoque de Paso 1 (setBackground en QStandardItems) tiene tres problemas
# críticos en producción:
#
# 1. CONFLICTO CON LA SELECCIÓN NATIVA: Qt usa el rol Qt.BackgroundRole para
#    pintar la selección. Si ya hay un color fijo asignado, la selección no se
#    ve o queda superpuesta de forma imprevisible según el tema del SO.
#
# 2. FALLO EN TEMAS OSCUROS DE WINDOWS: Windows 11 con DWM activo puede
#    ignorar los colores de rol y renderizar el color del sistema, dejando
#    filas ámbar visibles solo en algunos monitores.
#
# 3. ACOPLAMIENTO MODELO-VISTA: Guardar colores en el QStandardItem mezcla
#    datos de presentación con datos del modelo. Si el controlador hace un
#    reset o repobla la tabla, todos los colores se pierden.
#
# El delegado soluciona los tres problemas:
# · Pinta directamente sobre el QPainter antes de que Qt procese la selección.
# · Lee el estado desde el texto del item (QStandardItemModel), no desde CueModel.
#   Esto permite que "Reset visual" y "Simular ERROR" funcionen sin tocar el
#   modelo de negocio.
# · Es completamente stateless respecto a la tabla: solo necesita saber
#   qué filas son "activa" y "precarga" (dos enteros).
#
# Hilos permitidos: SOLO el main thread. Paint() es siempre llamado por Qt
# desde el hilo principal durante el repintado del widget.
# =============================================================================

from __future__ import annotations

from PySide6.QtCore import QModelIndex, QSize, Qt
from PySide6.QtGui import QColor, QFont, QPainter
from PySide6.QtWidgets import QStyle, QStyledItemDelegate, QStyleOptionViewItem


# Índices de columna — deben coincidir con COL_* en main_window.py
_COL_TYPE  = 1
_COL_NAME  = 2
_COL_STATE = 4
_COL_LAT   = 5

# Colores del icono ●/○ en la columna NOMBRE
_C_ICON_AUDIO = QColor("#2ecc71")   # verde  — cue de audio con archivo
_C_ICON_VIDEO = QColor("#64b5f6")   # azul   — cue de vídeo con archivo
_C_ICON_EMPTY = QColor("#444444")   # gris   — cue sin archivo asignado

# ── Paleta de roles ──────────────────────────────────────────────────────────

# Fila activa (PLAYING)
_BG_ACTIVE      = QColor("#1a5c2a")
_FG_ACTIVE      = QColor("#ffffff")
_FG_STATE_ACTIVE = QColor("#2ecc71")

# Fila en precarga (LOADING / LOADED)
_BG_PRELOAD     = QColor("#3d2e00")
_FG_PRELOAD     = QColor("#e8a200")

# Error — solo la columna ESTADO cambia de color
_FG_STATE_ERROR = QColor("#e74c3c")

# Filas normales
_BG_EVEN        = QColor("#1a1a1a")
_BG_ODD         = QColor("#1f1f1f")
_FG_NORMAL      = QColor("#cccccc")

# Columna ESTADO según valor
_STATE_COLORS: dict[str, QColor] = {
    "IDLE":    QColor("#555555"),
    "LOADING": QColor("#e8a200"),
    "LOADED":  QColor("#3498db"),
    "PLAYING": QColor("#2ecc71"),
    "STOPPED": QColor("#888888"),
    "ERROR":   QColor("#e74c3c"),
}

# Selección: overlay blanco semitransparente que no destruye el color de fila
_SELECTION_OVERLAY = QColor(255, 255, 255, 18)


class CueDelegate(QStyledItemDelegate):
    """Delegado personalizado para la tabla de cues.

    Lee el estado del cue directamente desde el texto de la columna ESTADO
    (QStandardItemModel), sin acceder a CueModel. Esto lo hace reutilizable
    y permite simulaciones visuales sin afectar la lógica de negocio.
    """

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self._active_index:  int = 0
        self._preload_index: int = -1
        self._mono_font = QFont("Consolas", 11)
        self._mono_bold = QFont("Consolas", 11)
        self._mono_bold.setBold(True)

    # ------------------------------------------------------------------
    # API pública — llamada solo desde MainWindow (main thread)
    # ------------------------------------------------------------------

    def set_active_index(self, idx: int) -> None:
        """Establece qué fila es el cue actualmente en PLAYING."""
        self._active_index = idx

    def set_preload_index(self, idx: int) -> None:
        """Establece qué fila es el siguiente cue (candidato a precarga)."""
        self._preload_index = idx

    # ------------------------------------------------------------------
    # Pintura
    # ------------------------------------------------------------------

    def paint(
        self,
        painter: QPainter,
        option:  QStyleOptionViewItem,
        index:   QModelIndex,
    ) -> None:
        row = index.row()
        col = index.column()

        # Estado del cue: leído desde la columna ESTADO del mismo modelo Qt,
        # no desde CueModel → permite simulaciones visuales sin tocar la lógica.
        state_idx   = index.model().index(row, _COL_STATE)
        cue_state   = state_idx.data(Qt.ItemDataRole.DisplayRole) or "IDLE"

        is_active   = (row == self._active_index  and cue_state == "PLAYING")
        is_preload  = (row == self._preload_index and cue_state in ("LOADING", "LOADED"))
        is_error    = (cue_state == "ERROR")

        painter.save()

        # ── 1. Fondo de fila ─────────────────────────────────────────────
        if is_active:
            painter.fillRect(option.rect, _BG_ACTIVE)
        elif is_preload:
            painter.fillRect(option.rect, _BG_PRELOAD)
        else:
            painter.fillRect(option.rect, _BG_EVEN if row % 2 == 0 else _BG_ODD)

        # ── 2. Overlay de selección (no destruye el color de fondo) ──────
        if option.state & QStyle.StateFlag.State_Selected:
            painter.fillRect(option.rect, _SELECTION_OVERLAY)

        # ── 3. Texto y color según rol + columna ─────────────────────────
        cell_text = index.data(Qt.ItemDataRole.DisplayRole) or ""

        raw_align = index.data(Qt.ItemDataRole.TextAlignmentRole)
        try:
            alignment = Qt.AlignmentFlag(int(raw_align))
        except (TypeError, ValueError):
            alignment = Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft

        if is_active:
            if col == _COL_STATE:
                painter.setFont(self._mono_bold)
                painter.setPen(_FG_STATE_ACTIVE)
            else:
                painter.setFont(self._mono_font)
                painter.setPen(_FG_ACTIVE)

        elif is_preload:
            painter.setFont(self._mono_font)
            painter.setPen(_FG_PRELOAD)

        elif is_error:
            if col == _COL_STATE:
                painter.setFont(self._mono_bold)
                painter.setPen(_FG_STATE_ERROR)
            else:
                painter.setFont(self._mono_font)
                painter.setPen(_FG_NORMAL)

        else:
            painter.setFont(self._mono_font)
            if col == _COL_STATE:
                painter.setPen(_STATE_COLORS.get(cue_state, _FG_NORMAL))
            elif col == _COL_LAT:
                # Ámbar cuando hay valor, gris apagado cuando está vacío
                painter.setPen(QColor("#e8a200") if cell_text else QColor("#444444"))
            else:
                painter.setPen(_FG_NORMAL)

        rect = option.rect.adjusted(4, 0, -4, 0)

        # ── Columna NOMBRE: prefijo ●/○ con color según tipo y media ─────
        if col == _COL_NAME:
            type_idx = index.model().index(row, _COL_TYPE)
            cue_type = type_idx.data(Qt.ItemDataRole.DisplayRole) or ""
            has_media = bool(index.data(Qt.ItemDataRole.UserRole))

            if has_media:
                icon = "●"
                if cue_type == "AUDIO":
                    icon_color = _C_ICON_AUDIO
                elif cue_type == "VIDEO":
                    icon_color = _C_ICON_VIDEO
                else:
                    icon_color = _FG_NORMAL
            else:
                icon = "○"
                icon_color = _C_ICON_EMPTY

            icon_w = painter.fontMetrics().horizontalAdvance(icon + " ")
            painter.setPen(icon_color)
            painter.drawText(rect, Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft, icon)

            # Nombre del cue desplazado después del icono
            text_rect = rect.adjusted(icon_w, 0, 0, 0)
            # Restaurar el color de texto calculado antes
            if is_active:
                painter.setPen(_FG_ACTIVE)
            elif is_preload:
                painter.setPen(_FG_PRELOAD)
            else:
                painter.setPen(_FG_NORMAL)
            painter.drawText(text_rect, alignment, cell_text)
        else:
            painter.drawText(rect, alignment, cell_text)

        painter.restore()

    def sizeHint(self, option: QStyleOptionViewItem, index: QModelIndex) -> QSize:
        sh = super().sizeHint(option, index)
        sh.setHeight(28)
        return sh
