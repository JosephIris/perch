@echo off
rem cmux claude wrapper — intercepts `claude` invocations inside a cmux pane.
rem
rem This .cmd file is staged into <app>/tools/ next to cmux.exe and that
rem directory is prepended to PATH for every pane shell. The real claude
rem binary lives elsewhere on PATH; cmux.exe finds it (skipping our own dir)
rem and execs it with --settings injected.
"%~dp0cmux.exe" wrap-claude %*
