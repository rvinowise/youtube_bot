@echo off
rem Convenience wrapper so the asker can be used from cmd without quoting pain.
rem   tools\ask.cmd "your question"
rem   type work\err.txt ^| tools\ask.cmd -System "You are a DevOps expert."
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ask.ps1" %*
