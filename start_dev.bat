@echo off
setlocal
cd /d "%~dp0gui-tauri"
echo Starting Ru Desktop. Tauri owns and stops its authenticated backend.
npm run tauri dev
