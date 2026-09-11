# Captured UCCM output

**No longer empty.** A Trimble UCCM-P answered on 10 Sep 2026 -
`trimble-uccm-p-2026-09-10.txt`, with its provenance beside it - so #416's step one,
**identification, not code**, is done for one module of one variant. **The same module sat again on
11 Sep**, `trimble-uccm-p-2026-09-11.txt`, and that second sitting is the screen
`UccmStatusParser` is written against and tested on.

Until that day nothing was here, because the UCCM driver had never met a receiver: every command,
timeout, field meaning and state code in `src/WinZ3805A.Device/Drivers/Uccm/` was read out of Lady
Heather's source rather than a vendor document or a capture, and the driver says so at length. Most
of it still is. One sitting settles what one module does; it does not settle the family, and a plain
UCCM has still never been seen.

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

## What the two sittings answered

| Hypothesis | Verdict |
|---|---|
| 1. The module echoes the command | **Refuted.** 0 of 9. Replies begin with the answer. |
| 2. `C5` time codes interleave | **The codes are binary.** 4 packets, 0 of them mid-reply. |
| 3. `COMMAND COMPLETE` terminates | **Confirmed.** 6 of 9, spelled `Command complete`. |

Hypothesis 2 is the one worth reading twice. The codes are **44-byte binary packets**, `0xC5` to
`0xCA`, broadcast about every 2 s and arriving with **no line terminator** — one came back appended
directly to the prompt. The script had been matching the *characters* `C5` against text decoded with
`Encoding.ASCII`, which renders every byte above `0x7F` as `?`, **so that row could only ever have
read 0 whatever the module did**, and the self-test passed because it fed the analysis a time code
written as hex text — a shape no module produces. Both are fixed; the search now runs over bytes.

**The count is still 0 mid-reply, across both sittings, and that still refutes nothing** — four
packets have now been seen and every one arrived *after* its reply, appended to the prompt, which
is an ordinary broadcast. Whether one lands mid-reply depends on timing and wants a longer sitting.

**The 11 Sep sitting was taken with the blind harness, and its figures were re-measured afterwards**
from the `---- BYTES` sections it had recorded faithfully; its note shows the working. That is the
argument for dumping the bytes whether or not anything can read them yet — the sitting could be
re-read without putting the module back on the bench.

## Why the captures are `.txt` here but not in `Fixtures/`

`FixtureCorpusTests` globs `*.txt` under the build output's `Fixtures` directory and asserts each one
**is a SmartClock status screen**, because an arbitrary text file parses to nulls and then satisfies
every assertion vacuously — a corpus of junk passes and reports itself as covered. It has caught
exactly that before (#221).

This directory is not `Fixtures/`, and the corpus globs only under `Fixtures` — so nothing here is
collected by it. A UCCM transcript is not a status screen and must not be read as one.

**These files are copied to the build output**, which they were not when this paragraph was first
written: `UccmStatusScreenTests` reads the 11 Sep capture from `AppContext.BaseDirectory`, so the
csproj copies both the `.txt` and the `.md`. Being out of the output is therefore not what keeps
these clear of the corpus; being out of `Fixtures/` is.

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

> **That prediction fired, on the first sitting.** The UCCM-P emits `UCCM-P >`, on 9 replies of 9,
> and it arrived exactly as described: one unexplained extra payload line each time, with `*IDN?`
> reporting two payload lines where it has one value. The paragraph above is left standing because
> the design worked — the anomaly announced itself instead of hiding — and because it is the better
> argument for reporting an unknown than any rewrite of it would be. The script now names prompts,
> which is evidence rather than assumption. **A plain UCCM has still never been seen**, so whether
> it prompts is still open.

## Why a third capture script

Neither of the others can do this, and the reasons are the design:

- **`Capture-Fixtures.ps1`** is built for the SmartClock. It sends a mnemonic and **strips** the
  echoed command and the `scpi > ` prompt to leave a status screen. A UCCM has no such prompt, and
  its echo is the evidence rather than noise.
- **`Capture-Talker.ps1`** is built for a broadcast talker, which answers nothing and is never
  asked. A UCCM is query/response.
