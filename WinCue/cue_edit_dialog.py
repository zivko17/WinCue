# =============================================================================
# cue_edit_dialog.py — Diálogo de edición de un Cue (Paso 4)
# =============================================================================
#
# Responsabilidad: QDialog modal para editar los campos editables de un Cue.
#   Solo pertenece a la capa de Vista. No toca CueModel directamente:
#   devuelve un dict con los nuevos valores y el controlador decide si aplica.
#
# Campos editables:
#   · Tipo     — QComboBox  (AUDIO | VIDEO | WAIT | SCRIPT)
#   · Nombre   — QLineEdit
#   · Duración — QDoubleSpinBox  (0.00 – 7200.00 s)
#   · Archivo  — QLineEdit + botón "…" → QFileDialog
#
# El botón "Guardar" se desactiva si el campo Nombre está vacío.
# QFileDialog se abre con filtros adaptados al tipo seleccionado.
#
# Hilos: solo el main thread (QDialog.exec() bloquea el event loop modalmente
#   pero no bloquea el OS; no se necesitan QThreads).
# =============================================================================

from __future__ import annotations

from pathlib import Path

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QDoubleSpinBox,
    QFileDialog,
    QFormLayout,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPushButton,
    QVBoxLayout,
    QWidget,
)

from cue_model import CUE_TYPES, Cue

# ---------------------------------------------------------------------------
# Stylesheet — hereda la familia tipográfica del padre pero define el resto
# ---------------------------------------------------------------------------

_DIALOG_STYLE = """
QDialog {
    background-color: #1a1a1a;
    color: #d4d4d4;
}
QLabel {
    color: #d4d4d4;
    background-color: transparent;
    font-size: 12px;
}
QLineEdit, QDoubleSpinBox, QComboBox {
    background-color: #252525;
    color: #d4d4d4;
    border: 1px solid #3a3a3a;
    border-radius: 3px;
    padding: 4px 8px;
    font-size: 12px;
    min-height: 24px;
    selection-background-color: #e8a200;
    selection-color: #000000;
}
QLineEdit:focus, QDoubleSpinBox:focus, QComboBox:focus {
    border-color: #e8a200;
}
QLineEdit:disabled, QDoubleSpinBox:disabled {
    color: #666666;
    border-color: #2a2a2a;
}
QComboBox::drop-down {
    border: none;
    width: 22px;
}
QComboBox::down-arrow {
    width: 10px;
    height: 10px;
}
QComboBox QAbstractItemView {
    background-color: #252525;
    color: #d4d4d4;
    border: 1px solid #3a3a3a;
    selection-background-color: #e8a200;
    selection-color: #000000;
    outline: none;
}
QDoubleSpinBox::up-button, QDoubleSpinBox::down-button {
    width: 18px;
    background-color: #333333;
    border: none;
}
QDoubleSpinBox::up-button:hover, QDoubleSpinBox::down-button:hover {
    background-color: #444444;
}
QPushButton {
    background-color: #333333;
    color: #d4d4d4;
    border: none;
    border-radius: 3px;
    padding: 5px 16px;
    font-size: 11px;
    font-weight: bold;
    min-height: 26px;
}
QPushButton:hover   { background-color: #444444; }
QPushButton:pressed { background-color: #555555; }
QPushButton#btnSave {
    background-color: #2ecc71;
    color: #000000;
    padding: 5px 24px;
}
QPushButton#btnSave:hover   { background-color: #27ae60; }
QPushButton#btnSave:pressed { background-color: #1e8449; }
QPushButton#btnSave:disabled {
    background-color: #1a4a2a;
    color: #4a7a5a;
}
QPushButton#btnBrowse {
    padding: 5px 10px;
    letter-spacing: 1px;
}
"""

# Filtros por tipo de cue
_FILTERS: dict[str, str] = {
    "AUDIO":  "Audio (*.mp3 *.wav *.ogg *.flac *.aac *.m4a *.opus *.aiff);;Todos (*)",
    "VIDEO":  "Vídeo (*.mp4 *.mov *.avi *.mkv *.webm *.mxf *.m4v *.ts *.mts);;Todos (*)",
    "WAIT":   "Todos los archivos (*)",
    "SCRIPT": "Scripts (*.py *.sh *.bat *.cmd);;Todos (*)",
}


# ---------------------------------------------------------------------------
# CueEditDialog
# ---------------------------------------------------------------------------

class CueEditDialog(QDialog):
    """Diálogo modal para editar los campos de un Cue.

    Uso:
        dlg = CueEditDialog(cue, parent=self)
        if dlg.exec() == CueEditDialog.DialogCode.Accepted:
            fields = dlg.get_values()   # dict con los cambios
    """

    def __init__(self, cue: Cue, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.setWindowTitle(f"Cue  #{cue.id:02d}  —  Editar propiedades")
        self.setModal(True)
        self.setMinimumWidth(520)
        self.setStyleSheet(_DIALOG_STYLE)
        # Quitar botón "?" en Windows
        self.setWindowFlag(Qt.WindowType.WindowContextHelpButtonHint, False)

        root = QVBoxLayout(self)
        root.setContentsMargins(24, 20, 24, 20)
        root.setSpacing(16)

        # ── Formulario ────────────────────────────────────────────────────
        form = QFormLayout()
        form.setLabelAlignment(
            Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter
        )
        form.setHorizontalSpacing(14)
        form.setVerticalSpacing(10)

        # · Tipo
        self._combo_type = QComboBox()
        for t in CUE_TYPES:
            self._combo_type.addItem(t)
        self._combo_type.setCurrentText(cue.type)
        form.addRow("Tipo:", self._combo_type)

        # · Nombre
        self._edit_name = QLineEdit(cue.name)
        self._edit_name.setPlaceholderText("Nombre descriptivo del cue")
        self._edit_name.setMaxLength(120)
        form.addRow("Nombre:", self._edit_name)

        # · Duración
        self._spin_dur = QDoubleSpinBox()
        self._spin_dur.setRange(0.0, 7_200.0)   # hasta 2 horas
        self._spin_dur.setDecimals(2)
        self._spin_dur.setSingleStep(0.5)
        self._spin_dur.setSuffix("  s")
        self._spin_dur.setValue(cue.duration)
        form.addRow("Duración:", self._spin_dur)

        # · Archivo
        file_row = QWidget()
        file_layout = QHBoxLayout(file_row)
        file_layout.setContentsMargins(0, 0, 0, 0)
        file_layout.setSpacing(6)
        self._edit_path = QLineEdit(cue.file_path)
        self._edit_path.setPlaceholderText("(vacío para cues WAIT / SCRIPT)")
        self._btn_browse = QPushButton("…")
        self._btn_browse.setObjectName("btnBrowse")
        self._btn_browse.setFixedWidth(36)
        self._btn_browse.setToolTip("Explorar archivos de media")
        self._btn_browse.clicked.connect(self._browse)
        file_layout.addWidget(self._edit_path)
        file_layout.addWidget(self._btn_browse)
        form.addRow("Archivo:", file_row)

        root.addLayout(form)

        # ── Separador ─────────────────────────────────────────────────────
        sep = QLabel()
        sep.setFixedHeight(1)
        sep.setStyleSheet("background-color: #2a2a2a;")
        root.addWidget(sep)

        # ── Botonera ──────────────────────────────────────────────────────
        self._buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok
            | QDialogButtonBox.StandardButton.Cancel
        )
        self._btn_save = self._buttons.button(QDialogButtonBox.StandardButton.Ok)
        self._btn_save.setObjectName("btnSave")
        self._btn_save.setText("Guardar cambios")
        self._buttons.button(QDialogButtonBox.StandardButton.Cancel).setText("Cancelar")
        self._buttons.accepted.connect(self.accept)
        self._buttons.rejected.connect(self.reject)
        root.addWidget(self._buttons)

        # Activar/desactivar "Guardar" según el nombre
        self._edit_name.textChanged.connect(self._validate)
        self._validate()

        # Foco inicial en el campo nombre con todo seleccionado
        self._edit_name.setFocus()
        self._edit_name.selectAll()

    # ------------------------------------------------------------------
    # Slots privados
    # ------------------------------------------------------------------

    def _validate(self) -> None:
        """Desactiva "Guardar" si el nombre está vacío."""
        self._btn_save.setEnabled(bool(self._edit_name.text().strip()))

    def _browse(self) -> None:
        """Abre el explorador de archivos con filtros adaptados al tipo del cue."""
        cue_type = self._combo_type.currentText()
        filt = _FILTERS.get(cue_type, _FILTERS["AUDIO"])

        current = self._edit_path.text().strip()
        start_dir = str(Path(current).parent) if current else ""

        path, _ = QFileDialog.getOpenFileName(
            self, "Seleccionar archivo de media", start_dir, filt
        )
        if path:
            self._edit_path.setText(path)
            # Auto-corregir el tipo si la extensión no coincide con el selector
            ext = Path(path).suffix.lower()
            audio_exts = {".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a", ".opus", ".aiff"}
            video_exts = {".mp4", ".mov", ".avi", ".mkv", ".webm", ".mxf", ".m4v", ".ts", ".mts"}
            if ext in audio_exts and self._combo_type.currentText() not in ("AUDIO",):
                self._combo_type.setCurrentText("AUDIO")
            elif ext in video_exts and self._combo_type.currentText() not in ("VIDEO",):
                self._combo_type.setCurrentText("VIDEO")

    # ------------------------------------------------------------------
    # API pública
    # ------------------------------------------------------------------

    def get_values(self) -> dict:
        """Devuelve los valores actuales del formulario como dict.

        Llamar únicamente si exec() devolvió DialogCode.Accepted.
        El controlador aplica estos valores al Cue en el modelo.
        """
        return {
            "type":      self._combo_type.currentText(),
            "name":      self._edit_name.text().strip(),
            "duration":  self._spin_dur.value(),
            "file_path": self._edit_path.text().strip(),
        }
