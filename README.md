# Recovid — ClipRecorder

Grabador de clips de pantalla para Windows que corre desde la bandeja del sistema y se dispara con un atajo de teclado (por defecto `Ctrl+Alt+Shift+R`).

- Graba clips cortos (5 s por defecto) a 30 FPS, con audio del sistema opcional y micrófono opcional.
- Permite seleccionar una región de la pantalla con una superposición y **seguirla**: sin seguimiento, siguiendo el mouse o siguiendo un objeto (tracking por imagen).
- Borde visible durante la grabación, ajustes desde una ventana de configuración, inicio automático con Windows.
- Captura con [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) (incluida en `ClipRecorder/lib`, con su licencia).

## Por qué no hay `.exe`

`ClipRecorder.ps1` se ejecuta dentro de Windows PowerShell 5.1 (firmado por Microsoft) y **compila en memoria** el código de `ClipRecorder/src`. Así nunca se escribe un ejecutable nuevo en disco y Smart App Control no tiene nada que bloquear.

## Uso

Doble clic en `ClipRecorder/Iniciar ClipRecorder.cmd` (solo la primera vez; después se crean accesos directos en el Escritorio y en el inicio de Windows). Los clips se guardan en `ClipRecorder/Recordings/`.

Requisitos: Windows 10/11.
