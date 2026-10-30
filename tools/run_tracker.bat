@echo off
rem YuzuPet face capture sidecar launcher (OpenSeeFace, BSD-2)
rem Lives next to the OpenSeeFace checkout; Godot spawns it on toggle.
cd /d "%~dp0OpenSeeFace"
set PY=python
for %%V in (314 313 312 311) do (
  if exist "%LOCALAPPDATA%\Programs\Python\Python%%V\python.exe" set PY=%LOCALAPPDATA%\Programs\Python\Python%%V\python.exe
)
"%PY%" facetracker.py -c 0 -p 7901 -F 30 -W 640 -H 360 > "%~dp0osf.log" 2>&1
