# =============================================================================
# cue_edit_dialog.py — Inspector de propiedades de un Cue (estilo QLab)
# =============================================================================
#
# QDialog modal para editar TODAS las propiedades de un Cue. Pertenece a la capa
# de Vista: no toca CueModel; devuelve un dict con los valores y el controlador
# decide si aplica.
#
# Campos:
#   · Número, Tipo, Nombre, Color, Notas, Armado
#   · Archivo (AUDIO/VIDEO/SCRIPT)
#   · Objetivo (FADE/START/STOP) — otro cue de la lista
#   · Pre-wait, Duración/Acción, Post-wait, Modo de continuación
#   · Volumen, Fade in, Fade out, Loop (media / fade)
#
# Los campos se muestran/ocultan según el tipo de cue seleccionado.
# =============================================================================

from __future__ import annotations

from pathlib import Path
from typing import Optional

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (
    QCheckBox,
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

from cue_model import CONTINUE_MODES, CUE_COLORS, CUE_TYPES, GROUP_MODES, Cue

# Etiquetas legibles de los modos de grupo
_GROUP_MODE_LABELS: dict[str, str] = {
    "TIMELINE": "Timeline — disparar todos los hijos a la vez",
    "ENTER":    "Entrar — disparar el 1º y entrar al grupo",
    "GO_NEXT":  "1º + salir — disparar el 1º y saltar tras el grupo",
    "RANDOM":   "Azar — disparar un hijo aleatorio (round-robin)",
}

# ---------------------------------------------------------------------------
# Estilo
# ---------------------------------------------------------------------------

_DIALOG_STYLE = """
QDialog { background-color: #1a1a1a; color: #d4d4d4; }
QLabel { color: #d4d4d4; background-color: transparent; font-size: 12px; }
QLineEdit, QDoubleSpinBox, QComboBox {
    background-color: #252525; color: #d4d4d4;
    border: 1px solid #3a3a3a; border-radius: 3px;
    padding: 4px 8px; font-size: 12px; min-height: 22px;
    selection-background-color: #e8a200; selection-color: #000000;
}
QLineEdit:focus, QDoubleSpinBox:focus, QComboBox:focus { border-color: #e8a200; }
QLineEdit:disabled, QDoubleSpinBox:disabled, QComboBox:disabled {
    color: #666666; border-color: #2a2a2a; background-color: #1e1e1e;
}
QComboBox::drop-down { border: none; width: 22px; }
QComboBox QAbstractItemView {
    background-color: #252525; color: #d4d4d4; border: 1px solid #3a3a3a;
    selection-background-color: #e8a200; selection-color: #000000; outline: none;
}
QDoubleSpinBox::up-button, QDoubleSpinBox::down-button {
    width: 16px; background-color: #333333; border: none;
}
QCheckBox { color: #d4d4d4; font-size: 12px; }
QCheckBox::indicator { width: 16px; height: 16px; }
QCheckBox::indicator:unchecked { border: 1px solid #555; background: #252525; border-radius: 3px; }
QCheckBox::indicator:checked { border: 1px solid #e8a200; background: #e8a200; border-radius: 3px; }
QPushButton {
    background-color: #333333; color: #d4d4d4; border: none; border-radius: 3px;
    padding: 5px 16px; font-size: 11px; font-weight: bold; min-height: 24px;
}
QPushButton:hover { background-color: #444444; }
QPushButton#btnSave { background-color: #2ecc71; color: #000000; padding: 5px 24px; }
QPushButton#btnSave:hover { background-color: #27ae60; }
QPushButton#btnSave:disabled { background-color: #1a4a2a; color: #4a7a5a; }
QPushButton#btnBrowse { padding: 5px 10px; }
"""

_FILTERS: dict[str, str] = {
    "AUDIO":  "Audio (*.mp3 *.wav *.ogg *.flac *.aac *.m4a *.opus *.aiff);;Todos (*)",
    "VIDEO":  "Vídeo (*.mp4 *.mov *.avi *.mkv *.webm *.mxf *.m4v *.ts *.mts);;Todos (*)",
    "SCRIPT": "Scripts (*.py *.bat *.cmd *.ps1 *.sh);;Todos (*)",
}

# Etiquetas legibles para el modo de continuación
_CONT_LABELS: dict[str, str] = {
    "NONE":          "No continuar",
    "AUTO_CONTINUE": "Auto-continue (tras post-wait)",
    "AUTO_FOLLOW":   "Auto-follow (al terminar)",
}
_CONT_LABEL_TO_KEY = {v: k for k, v in _CONT_LABELS.items()}

_COLOR_LABELS: dict[str, str] = {
    "": "— sin color —", "red": "Rojo", "orange": "Naranja", "yellow": "Amarillo",
    "green": "Verde", "blue": "Azul", "purple": "Morado",
}


class CueEditDialog(QDialog):
    """Inspector modal de un Cue. get_values() devuelve el dict de cambios."""

    def __init__(
        self, cue: Cue, all_cues: Optional[list] = None, parent=None
    ) -> None:
        super().__init__(parent)
        self._cue = cue
        self._all_cues = all_cues or []
        self.setWindowTitle(f"Cue [{cue.display_number}] — {cue.name}")
        self.setModal(True)
        self.setMinimumWidth(560)
        self.setStyleSheet(_DIALOG_STYLE)
        self.setWindowFlag(Qt.WindowType.WindowContextHelpButtonHint, False)

        root = QVBoxLayout(self)
        root.setContentsMargins(22, 18, 22, 18)
        root.setSpacing(12)

        form = QFormLayout()
        form.setLabelAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        form.setHorizontalSpacing(14)
        form.setVerticalSpacing(8)

        # ── Número + Tipo (fila combinada) ────────────────────────────────
        self._edit_number = QLineEdit(cue.display_number)
        self._edit_number.setMaxLength(8)
        self._edit_number.setFixedWidth(70)
        self._combo_type = QComboBox()
        for t in CUE_TYPES:
            self._combo_type.addItem(t)
        self._combo_type.setCurrentText(cue.type)
        self._combo_type.currentTextChanged.connect(self._update_visibility)
        numtype = QWidget(); ntl = QHBoxLayout(numtype)
        ntl.setContentsMargins(0, 0, 0, 0); ntl.setSpacing(10)
        ntl.addWidget(QLabel("Nº")); ntl.addWidget(self._edit_number)
        ntl.addWidget(QLabel("Tipo")); ntl.addWidget(self._combo_type, 1)
        form.addRow("Número / Tipo:", numtype)

        # ── Nombre ─────────────────────────────────────────────────────────
        self._edit_name = QLineEdit(cue.name)
        self._edit_name.setMaxLength(120)
        self._edit_name.textChanged.connect(self._validate)
        form.addRow("Nombre:", self._edit_name)

        # ── Color + Armado ─────────────────────────────────────────────────
        self._combo_color = QComboBox()
        for key in CUE_COLORS:
            self._combo_color.addItem(_COLOR_LABELS.get(key, key), key)
        i = self._combo_color.findData(cue.color)
        self._combo_color.setCurrentIndex(i if i >= 0 else 0)
        self._chk_armed = QCheckBox("Armado")
        self._chk_armed.setChecked(cue.armed)
        colorrow = QWidget(); crl = QHBoxLayout(colorrow)
        crl.setContentsMargins(0, 0, 0, 0); crl.setSpacing(10)
        crl.addWidget(self._combo_color, 1); crl.addWidget(self._chk_armed)
        form.addRow("Color:", colorrow)

        # ── Archivo ────────────────────────────────────────────────────────
        self._file_row = QWidget()
        frl = QHBoxLayout(self._file_row)
        frl.setContentsMargins(0, 0, 0, 0); frl.setSpacing(6)
        self._edit_path = QLineEdit(cue.file_path)
        self._edit_path.setPlaceholderText("(archivo de media o script)")
        self._btn_browse = QPushButton("…"); self._btn_browse.setObjectName("btnBrowse")
        self._btn_browse.setFixedWidth(36); self._btn_browse.clicked.connect(self._browse)
        frl.addWidget(self._edit_path); frl.addWidget(self._btn_browse)
        self._row_file_label = QLabel("Archivo:")
        form.addRow(self._row_file_label, self._file_row)

        # ── Objetivo (FADE/START/STOP) ─────────────────────────────────────
        self._combo_target = QComboBox()
        self._combo_target.addItem("— ninguno / todos —", None)
        for c in self._all_cues:
            if c.id == cue.id:
                continue
            self._combo_target.addItem(f"[{c.display_number}] {c.name}", c.id)
        ti = self._combo_target.findData(cue.target_id)
        self._combo_target.setCurrentIndex(ti if ti >= 0 else 0)
        self._row_target_label = QLabel("Objetivo:")
        form.addRow(self._row_target_label, self._combo_target)

        # ── Temporización: pre / dur / post ────────────────────────────────
        self._spin_pre = self._make_spin(cue.pre_wait, 0.0, 3600.0)
        self._spin_dur = self._make_spin(cue.duration, 0.0, 7200.0)
        self._spin_post = self._make_spin(cue.post_wait, 0.0, 3600.0)
        timing = QWidget(); tl = QHBoxLayout(timing)
        tl.setContentsMargins(0, 0, 0, 0); tl.setSpacing(8)
        tl.addWidget(QLabel("Pre")); tl.addWidget(self._spin_pre)
        self._dur_label = QLabel("Acción")
        tl.addWidget(self._dur_label); tl.addWidget(self._spin_dur)
        tl.addWidget(QLabel("Post")); tl.addWidget(self._spin_post)
        form.addRow("Tiempos (s):", timing)

        # ── Continuación ───────────────────────────────────────────────────
        self._combo_cont = QComboBox()
        for key in CONTINUE_MODES:
            self._combo_cont.addItem(_CONT_LABELS[key], key)
        ci = self._combo_cont.findData(cue.continue_mode)
        self._combo_cont.setCurrentIndex(ci if ci >= 0 else 0)
        form.addRow("Al disparar:", self._combo_cont)

        # ── Modo de grupo (solo GROUP) ─────────────────────────────────────
        self._combo_group = QComboBox()
        for key in GROUP_MODES:
            self._combo_group.addItem(_GROUP_MODE_LABELS.get(key, key), key)
        gmi = self._combo_group.findData(cue.group_mode)
        self._combo_group.setCurrentIndex(gmi if gmi >= 0 else 0)
        self._row_group_label = QLabel("Modo de grupo:")
        form.addRow(self._row_group_label, self._combo_group)

        # ── Volumen / fades / loop ─────────────────────────────────────────
        self._spin_vol = self._make_spin(cue.volume * 100.0, 0.0, 100.0, suffix=" %", step=5.0)
        self._spin_fin = self._make_spin(cue.fade_in, 0.0, 600.0)
        self._spin_fout = self._make_spin(cue.fade_out, 0.0, 600.0)
        self._chk_loop = QCheckBox("Loop")
        self._chk_loop.setChecked(cue.loop)
        media = QWidget(); ml = QHBoxLayout(media)
        ml.setContentsMargins(0, 0, 0, 0); ml.setSpacing(8)
        ml.addWidget(QLabel("Vol")); ml.addWidget(self._spin_vol)
        self._fin_label = QLabel("Fade in"); ml.addWidget(self._fin_label); ml.addWidget(self._spin_fin)
        self._fout_label = QLabel("Fade out"); ml.addWidget(self._fout_label); ml.addWidget(self._spin_fout)
        ml.addWidget(self._chk_loop)
        self._row_media_label = QLabel("Media:")
        form.addRow(self._row_media_label, media)

        # ── Detener los demás (modo GO per-cue) ────────────────────────────
        self._chk_stop_others = QCheckBox(
            "Detener los demás cues al disparar este  (modo GO «per-cue»)")
        self._chk_stop_others.setChecked(cue.stop_others)
        form.addRow("", self._chk_stop_others)

        # ── Notas ──────────────────────────────────────────────────────────
        self._edit_notes = QLineEdit(cue.notes)
        self._edit_notes.setPlaceholderText("Nota del operador (opcional)")
        form.addRow("Notas:", self._edit_notes)

        root.addLayout(form)

        sep = QLabel(); sep.setFixedHeight(1); sep.setStyleSheet("background-color: #2a2a2a;")
        root.addWidget(sep)

        self._buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
        self._btn_save = self._buttons.button(QDialogButtonBox.StandardButton.Ok)
        self._btn_save.setObjectName("btnSave")
        self._btn_save.setText("Guardar cambios")
        self._buttons.button(QDialogButtonBox.StandardButton.Cancel).setText("Cancelar")
        self._buttons.accepted.connect(self.accept)
        self._buttons.rejected.connect(self.reject)
        root.addWidget(self._buttons)

        self._update_visibility(cue.type)
        self._validate()
        self._edit_name.setFocus(); self._edit_name.selectAll()

    # ------------------------------------------------------------------
    # Helpers de construcción
    # ------------------------------------------------------------------

    @staticmethod
    def _make_spin(value, lo, hi, suffix="", step=0.5) -> QDoubleSpinBox:
        s = QDoubleSpinBox()
        s.setRange(lo, hi)
        s.setDecimals(2 if not suffix else 0)
        s.setSingleStep(step)
        if suffix:
            s.setSuffix(suffix)
        s.setValue(value)
        s.setFixedWidth(84 if not suffix else 76)
        return s

    # ------------------------------------------------------------------
    # Visibilidad dinámica según tipo
    # ------------------------------------------------------------------

    def _update_visibility(self, cue_type: str) -> None:
        is_media   = cue_type in ("AUDIO", "VIDEO")
        is_target  = cue_type in ("FADE", "START", "STOP")
        has_file   = cue_type in ("AUDIO", "VIDEO", "SCRIPT")
        is_fade    = cue_type == "FADE"
        is_group   = cue_type == "GROUP"

        self._row_file_label.setVisible(has_file)
        self._file_row.setVisible(has_file)
        self._row_target_label.setVisible(is_target)
        self._combo_target.setVisible(is_target)
        self._row_group_label.setVisible(is_group)
        self._combo_group.setVisible(is_group)

        show_media = is_media or is_fade
        self._row_media_label.setVisible(show_media)
        self._spin_vol.parentWidget().setVisible(show_media)
        # Volumen relevante en media y fade; fade in/out y loop solo en media
        self._fin_label.setVisible(is_media)
        self._spin_fin.setVisible(is_media)
        self._fout_label.setVisible(is_media)
        self._spin_fout.setVisible(is_media)
        self._chk_loop.setVisible(is_media)

        self._dur_label.setText("Fade" if is_fade else "Acción")

    def _validate(self) -> None:
        self._btn_save.setEnabled(bool(self._edit_name.text().strip()))

    def _browse(self) -> None:
        cue_type = self._combo_type.currentText()
        filt = _FILTERS.get(cue_type, "Todos (*)")
        current = self._edit_path.text().strip()
        start_dir = str(Path(current).parent) if current else ""
        path, _ = QFileDialog.getOpenFileName(
            self, "Seleccionar archivo", start_dir, filt)
        if path:
            self._edit_path.setText(path)
            ext = Path(path).suffix.lower()
            audio = {".mp3", ".wav", ".ogg", ".flac", ".aac", ".m4a", ".opus", ".aiff"}
            video = {".mp4", ".mov", ".avi", ".mkv", ".webm", ".mxf", ".m4v", ".ts", ".mts"}
            if ext in audio and self._combo_type.currentText() not in ("AUDIO",):
                self._combo_type.setCurrentText("AUDIO")
            elif ext in video and self._combo_type.currentText() not in ("VIDEO",):
                self._combo_type.setCurrentText("VIDEO")

    # ------------------------------------------------------------------
    # Valores
    # ------------------------------------------------------------------

    def get_values(self) -> dict:
        """Devuelve todos los campos como dict (el controlador aplica el diff)."""
        number = self._edit_number.text().strip()
        return {
            "number":        number,
            "type":          self._combo_type.currentText(),
            "name":          self._edit_name.text().strip(),
            "color":         self._combo_color.currentData() or "",
            "armed":         self._chk_armed.isChecked(),
            "file_path":     self._edit_path.text().strip(),
            "target_id":     self._combo_target.currentData(),
            "pre_wait":      self._spin_pre.value(),
            "duration":      self._spin_dur.value(),
            "post_wait":     self._spin_post.value(),
            "continue_mode": self._combo_cont.currentData(),
            "group_mode":    self._combo_group.currentData(),
            "volume":        self._spin_vol.value() / 100.0,
            "fade_in":       self._spin_fin.value(),
            "fade_out":      self._spin_fout.value(),
            "loop":          self._chk_loop.isChecked(),
            "stop_others":   self._chk_stop_others.isChecked(),
            "notes":         self._edit_notes.text().strip(),
        }
