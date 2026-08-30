# Raw captures

Output from `build/Capture-Fixtures.ps1`, plus two screens taken by hand during the same sitting
where the harness could not (#242, #247). **These are part of the assertion corpus.** The plan on
21 August was to promote a capture by moving it up a level; what happened instead was that the
tests were pointed at these files where they lie, so the harness's names and `capture-log.md`
stay attached to the bytes they describe. The file list, the state each one holds and the tests
that assert against it are in [`../README.md`](../README.md); this folder keeps the log and the
guarantees.

**These files are not gitignored, deliberately.** A capture of power-up or acquiring exists only
because someone was mid-move when it happened, and it cannot be taken again on demand. A raw
capture sitting untracked in a working tree is one `git clean` away from gone, so they are
committed as they land and sorted out afterwards.

## Adding one

1. Run `pwsh build/Capture-Fixtures.ps1 -SelfTest` first, exit the application — closing its
   window only hides it, and it keeps the port — then leave `pwsh build/Capture-Fixtures.ps1
   [-Port COMn]` running through whatever is being done to the receiver; the full procedure is
   `docs/manual-qa.md` §5. It writes one file per state it has not seen and appends a line to
   `capture-log.md` — the mode line, the three status brackets and the tracked count at the moment
   it was written, which is what tells you whether two similar-looking captures are actually
   different states. (One caveat: the survey-versus-hold distinction is in the harness's
   signature but not yet in the line it writes, so two lines can agree in every fact while the
   files differ — the filename carries it.)
2. Add a row to `../README.md`: the file, the state, anything else it covers, and the tests that
   assert against it.
3. Point a test at it. The file does not move. Commit the capture and its log line together.

The log also carries lines for files that are not here: the six `-2` entries from 27 August are
restart duplicates, removed by hand before #236 taught the harness to seed its seen-set from disk.

The harness will not take a second sample of a signature it has already seen. When a second
sample of the same state is wanted — the deep-holdover screen exists to pin the minutes format
that a three-second holdover cannot show — take it by hand into a scratch directory with an empty
seen-set, name it outside the harness's numeric suffixes, and write its log line yourself, saying
so.

## What the harness guarantees

The bytes are the device's own. Framing is stripped by offset — the echoed command from the
front if the unit echoes at all, the prompt from the back — and nothing in between is decoded,
re-encoded or trimmed. `.gitattributes` marks this whole tree `-text`, so no end-of-line
conversion happens in either direction.

A capture verified against the delivered `locked-stabilizing.txt` matched it structurally:
27 CRLF line endings, no bare LF or CR, same header row, same trailing CRLF. Nine of the ten files
are that shape today; `power-up-gps-acquisition.txt` is 28 lines, the last a stray `*IDN?` answer
the reconnect path let through — see its row in `../README.md`.
