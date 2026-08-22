# Raw captures

Output from `build/Capture-Fixtures.ps1`. Nothing in here is part of the assertion corpus yet.

**These files are not gitignored, deliberately.** A capture of power-up or acquiring exists only
because someone was mid-move when it happened, and it cannot be taken again on demand. A raw
capture sitting untracked in a working tree is one `git clean` away from gone, so they are
committed as they land and sorted out afterwards.

## Promoting one

1. Move it up a level into `Fixtures/`, renaming it after the state it captures.
2. Add a row to `Fixtures/README.md` — the file, the state, and anything else it covers.
3. Point a test at it.

`capture-log.txt` records what each file was named for: the mode line, the three status
brackets and the tracked count at the moment it was written. That is what tells you whether two
similar-looking captures are actually different states.

## What the harness guarantees

The bytes are the device's own. Framing is stripped by offset — the echoed command from the
front if the unit echoes at all, the prompt from the back — and nothing in between is decoded,
re-encoded or trimmed. `.gitattributes` marks this whole tree `-text`, so no end-of-line
conversion happens in either direction.

A capture verified against the delivered `locked-stabilizing.txt` matched it structurally:
27 CRLF line endings, no bare LF or CR, same header row, same trailing CRLF.
