@echo off
rem perch claude wrapper — intercepts `claude` invocations inside a perch pane.
rem
rem This .cmd file is staged into <app>/tools/ next to perch.exe and that
rem directory is prepended to PATH for every pane shell. The real claude
rem binary lives elsewhere on PATH; perch.exe finds it (skipping our own dir)
rem and execs it with --settings injected.
"%~dp0perch.exe" wrap-claude %*
