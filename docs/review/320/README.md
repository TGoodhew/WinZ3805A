# Visual review captures for #320

Screenshots taken on 30 August 2026 against the live Z3805A on COM3, to give
[#345](https://github.com/TGoodhew/WinZ3805A/issues/345)'s visual checklist something to point
at rather than describing pixels in prose.

**These are review artefacts, not documentation.** They record how the application looked at one
moment. They are deliberately outside `docs/images/how-to-use/` — that folder ships inside the
package as a linked `Content` item (`WinZ3805A.csproj`) and `HelpDocumentTests` asserts every image
the guide names. Nothing here ships and nothing here is asserted.

## Why they are on `main`, having been written to be deleted

They lived on an unmerged branch, `docs/visual-checklist-320`, purely to give #345 a stable raw URL
— and both that branch's README and its commit message said to delete it once the checklist closed,
on the reasoning that *"the issue's images will break, which is the correct outcome for a review
that is over."*

**That was wrong, and it was reconsidered on 31 August 2026 rather than carried out.** #345 is not
a scratch pad; it is the **record of an approval**. It is where the icon set was reviewed over four
rounds and signed off, and `09-icons-approved.png` is the 5× magnification at which the weight
difference between a 1.07 px stroke and a 1.45 px one is visible at all. Deleting the branch would
have turned that record into seven broken placeholders in a closed issue, and left no way to see
what had been approved.

The argument for deleting was that these go stale. A review record is *supposed* to go stale: it
records a moment, and that is the whole of its value. The cost of keeping it is about a megabyte in
a folder that ships nowhere.

**The images are here rather than on a branch because a branch is a fragile host.** Nothing warns
you that a closed issue depends on one, and the next person tidying branches takes it out. #345
references these by commit SHA, so they survive this folder being moved or renamed as well.

They were nearly attached to the issue directly instead, which would have been better still —
GitHub stores attachments on its own CDN, independent of the repository. There is no API for it:
the upload endpoint is the web UI's and is not in the public API or the `gh` CLI.

| File | Shows |
|---|---|
| `01-main-window.png` | §10.3 main window: readouts, merit pills, footer |
| `02-overview-identity.png` | P0-1's receiver identity card (#332) |
| `03-satellites-legend.png` | §10.5's five-entry plot legend and the status column (#340) — and the §9.6.1 column question |
| `04-settings-cards.png` | §10.13 rebuilt on `SettingsCard` / `SettingsExpander` (#338) |
| `05-holdover-readback.png` | §10.8's duration limit read from the receiver (#336) |
| `06-diagnostics-cards.png` | §11.2 parse warnings and §10.9 Lifetime (#333) |
| `07-compact-rail-before.png` | The 40 × 200 px rail items, before #344 |
| `08-compact-rail-after.png` | The same window after, at 900 px |
| `09-icons-approved.png` | §9.9's icons at 5× after the review (#345) — the approved set |
| `10-nav-after-review.png` | The Details window carrying them |

**Do not add to this folder for new reviews.** One closed review's evidence is a record; a habit of
committing screenshots is a folder nobody prunes. A future checklist can host its own images on its
own branch and decide, at the end, whether the issue is worth preserving — which is the decision
this folder exists to document having been made once.
