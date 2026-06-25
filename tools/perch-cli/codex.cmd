@echo off
rem perch codex wrapper — intercepts `codex` invocations inside a perch pane.
rem
rem Staged into <app>/tools/ next to perch.exe, which is prepended to PATH for
rem every pane shell. perch.exe finds the real codex elsewhere on PATH (skipping
rem our own dir), brackets it with an agent=codex / agent="" badge signal, and
rem forwards all args + the exit code.
"%~dp0perch.exe" wrap-codex %*
