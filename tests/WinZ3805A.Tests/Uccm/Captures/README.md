# Captured UCCM output

**Empty, and that is the point.** Nothing here yet, because the UCCM driver has never met a
receiver. Every command, timeout, field meaning and state code in
`src/WinZ3805A.Device/Drivers/Uccm/` is read out of Lady Heather's source rather than a vendor
document or a capture, and the driver says so at length. #416 is explicit that step one is
**identification, not code**.

`build/Capture-Uccm.ps1` fills this directory. Run its self-test now and the real thing the day a
module is on the bench:

```powershell
pwsh build\Capture-Uccm.ps1                       # no -Port: lists the ports, and stops
pwsh build\Capture-Uccm.ps1 -SelfTest
pwsh build\Capture-Uccm.ps1 -Port COMn -Label <what-this-sitting-is>
```

## The three hypotheses a capture here settles

Each is in the driver as a claim with a citation and nothing else behind it. The script reports each
as a count rather than assuming any of them, because a script built on a hypothesis confirms it by
construction — the one outcome that would be worthless.

1. **The module echoes the command before answering it.** Heather's `decode_uccm_msg()` is built
   around this: with no pending message id it matches incoming text against the mnemonics and reads
   the *next* message as the answer. If true, the first line back is the question.
2. **Unsolicited `C5` time codes interleave with replies.** `uccm_time_line()` exists because "the
   Symmetricom units send a time code packet in the middle of another message's response".
3. **`COMMAND COMPLETE` terminates a reply.**

**A count of zero for the second does not refute it.** An interleaved broadcast depends on timing, so
zero means this sitting did not see one — a weaker statement, and the note records it as such.

## Why the captures are `.txt` here but not in `Fixtures/`

`FixtureCorpusTests` globs `*.txt` under the build output's `Fixtures` directory and asserts each one
**is a SmartClock status screen**, because an arbitrary text file parses to nulls and then satisfies
every assertion vacuously — a corpus of junk passes and reports itself as covered. It has caught
exactly that before (#221).

This directory is not `Fixtures/` and is not copied to the build output, so nothing here is collected
by that corpus. A UCCM transcript is not a status screen and must not be read as one.

## The provenance note is `.md`, deliberately

For the reason #221 established: `.txt` gets collected by the fixture corpus and `.log` is
gitignored, so a note written to either never reaches the repository. Both have happened.

## The script's serial half has been smoke-run, against the wrong receiver on purpose

**8 Sep 2026, against the bench Z3805A on COM3.** Not a UCCM — but a real port, real replies, and
the one chance to find out whether the machinery works before it matters. Every command it sends is
a query, so the only cost was two entries in the receiver's error queue, drained afterwards.

It walked the baud rates, identified the unit as `SYMMETRICOM,Z3805A,3625A02931,1.01.03-A`, read all
nine commands, dumped the bytes and wrote the note. And it reported **0 of 9 echoes, 0 of 9
`COMMAND COMPLETE`** — the right answer for a family that does neither, and the answer that would
have been embarrassing to get wrong on the day.

**It also found a gap in itself.** The reply's line endings were visible only to somebody reading
the hex carefully, so the script now reports them outright: the SmartClock came back
`CRLF x33 (9 replies end unterminated)`, the trailing `scpi > ` prompt having no terminator. For an
unknown module that is a transport question rather than a parsing one, and `LineProtocol` is
line-oriented — so it belongs in the summary rather than in the bytes.

**One thing the smoke run confirms by contrast:** the SmartClock's `scpi > ` prompt is counted as a
payload line, because this script does not know about it and must not. A UCCM is believed to have no
prompt. If the module turns out to emit one, it will show up as an unexplained extra payload line on
every reply — and the terminator summary is how you would notice.

## Why a third capture script

Neither of the others can do this, and the reasons are the design:

- **`Capture-Fixtures.ps1`** is built for the SmartClock. It sends a mnemonic and **strips** the
  echoed command and the `scpi > ` prompt to leave a status screen. A UCCM has no such prompt, and
  its echo is the evidence rather than noise.
- **`Capture-Talker.ps1`** is built for a broadcast talker, which answers nothing and is never
  asked. A UCCM is query/response.
