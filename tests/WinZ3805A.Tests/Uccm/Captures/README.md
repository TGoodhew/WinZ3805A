# Captured UCCM output

**No longer empty.** A Trimble UCCM-P answered on 10 Sep 2026 -
`trimble-uccm-p-2026-09-10.txt`, with its provenance beside it - so #416's step one,
**identification, not code**, is done for one module of one variant. **The same module sat again on
11 Sep**, `trimble-uccm-p-2026-09-11.txt`, and that second sitting is the screen
`UccmStatusParser` is written against and tested on.

**Seven sittings now, over four days**, and the later five were each taken to answer one question
the earlier ones had raised. In date order:

| Sitting | Taken to find out |
|---|---|
| `trimble-uccm-p-2026-09-10` | Whether anything answers at all, and on what line settings (#470) |
| `trimble-uccm-p-2026-09-11` | The status screen `UccmStatusParser` is written against |
| `bench-12sep2026` | The catalogue against a settled module, with the prompt understood |
| `hypothesis2-12sep2026` | 50 reads with a reply on the wire 38 % of the time, to put the interleaving claim to the module properly (#481) |
| `catalog-spellings-12sep2026` | Which of the catalogued spellings this firmware actually answers (#416) |
| `frames-13sep2026` | 300 s of pure listening: 150 frames, 2 s apart, no gaps — the broadcast's own shape rather than a reply's |
| `coldstart-no-antenna-13sep2026`, `transitions-13sep2026` | **The only capture of this family in any state but locked and settled** (#534). Every earlier sitting caught a module that had been up for hours on a good antenna, so forty of the forty-four time-code bytes never moved and the corpus could say nothing about what any of them meant. |

The last row is the one that changed the driver: it is what `UccmTransitionStateTests` pins, and
what #534 corrected a shipped misreading from — a coasting module had been reported as *Locked to
GPS*, because the lock byte alone cannot tell holdover from lock and nothing else was being read.

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

`build/Watch-UccmTransitions.ps1` fills it too, and answers a different question. That one listens
continuously instead of taking one catalogue pass, because power-up, acquisition and holdover
happen once, take minutes, and arrive **unasked** — a catalogue pass cannot see them at all. It
produced `transitions-13sep2026.*`, still the only capture of this family in any state other than
locked and settled, and it was committed on 14 Sep 2026 (#544) after a day existing nowhere but
the scratchpad on the machine it ran on.

```powershell
pwsh build\Watch-UccmTransitions.ps1 -SelfTest
pwsh build\Watch-UccmTransitions.ps1 -Port COMn -Label <what-this-sitting-is>
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

## What the sittings answered

| Hypothesis | Verdict |
|---|---|
| 1. The module echoes the command | **Refuted.** 0 echoes in every sitting — 9 of 9 replies, and 50 of 50 in `hypothesis2`. Replies begin with the answer. |
| 2. `C5` time codes interleave | **The codes are binary, and this module defers them.** 44-byte packets, `0xC5`–`0xCA`, one every 2 s. `hypothesis2-12sep2026` kept a reply on the wire 38 % of the time and caught **0 of 25** mid-reply against ~9 expected by chance, so this is a measurement rather than a quiet sitting. |
| 3. `COMMAND COMPLETE` terminates | **Confirmed.** 6 of 9, spelled `Command complete`. |

**Hypothesis 2 stands untested rather than refuted for the family.** Heather's claim names
*Symmetricom* units and every sitting here is one Trimble UCCM-P. The driver's tolerance for a
mid-reply code costs nothing and stays.

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
