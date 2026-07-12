# WinCue — Setup en Windows

## Dependencias Python

```
pip install PySide6 python-mpv
```

## libmpv-2.dll (requerida para reproducción de vídeo)

`libmpv-2.dll` **no está incluida en el repositorio** (121 MB). Debes
descargarla manualmente y colocarla en la raíz de este directorio
(`WinCue/libmpv-2.dll`).

### Descarga

1. Ve a <https://sourceforge.net/projects/mpv-player-windows/files/libmpv/>
2. Descarga el archivo más reciente con el patrón:
   ```
   mpv-dev-x86_64-vYYYYMMDD-git-XXXXXXX.7z
   ```
3. Extrae el archivo `.7z` (necesitas [7-Zip](https://www.7-zip.org/))
4. Dentro del archivo encontrarás `libmpv-2.dll`. Cópiala a:
   ```
   C:\Proyectos\ffmpeg-gui\WinCue\libmpv-2.dll
   ```

### Verificar que carga correctamente

Desde el directorio `WinCue\`:

```powershell
python -c "import os; os.environ['PATH'] = os.getcwd() + os.pathsep + os.environ['PATH']; import mpv; print('OK', mpv.__file__)"
```

Salida esperada:
```
OK C:\Users\<usuario>\AppData\Local\Programs\Python\...\site-packages\mpv.py
```

Si ves un error `OSError: [WinError 126]` significa que la DLL no está en
el directorio o que le faltan dependencias (Visual C++ Redistributable).

### Nota sobre el PATH

`main.py` añade automáticamente el directorio del proyecto al `PATH` de
Windows antes de importar `mpv`, por lo que **no es necesario** añadir
nada al PATH del sistema. Basta con que `libmpv-2.dll` esté en la carpeta
`WinCue\`.

## Ejecutar

```powershell
cd WinCue
python main.py
```
