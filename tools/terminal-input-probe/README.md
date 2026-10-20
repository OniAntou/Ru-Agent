# Native terminal input acceptance

Run `scripts/test_terminal_input.ps1` on Windows. It builds this probe and uses
an isolated ConPTY with the production `TgTerminalInput`, chat window and paste
route. It never reads or overwrites the clipboard and never sends keys to an
existing window. The script shares the repository's test-matrix mutex.

The six cases check a plain-console Unicode control, BMP multilingual paste,
emoji paste, UTF-8 split into single-byte writes, cursor editing after resize,
and a typed emoji before paste. Each TUI case requires one paste and no submitted
prompt; all cases require correct text and natural child shutdown within five seconds. Each
evidence directory must be fresh. A failed case stays failed and stops the run;
the JSON result and raw terminal log remain available for investigation.

Each TUI child also renders a structured transcript containing Markdown emphasis,
Unicode, a table and highlighted code before accepting input. The result records
the committed transcript count so input acceptance cannot pass on an empty screen.
