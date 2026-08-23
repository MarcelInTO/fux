# fux

A cross-platform terminal XML editor: a Terminal.Gui v2 front end over the reused
XML Notepad `Model` engine. See `readme.md` for what it does and `Makefile` for how to
build, run and drill it.

## Issue tracker

Issues for this project go in **GitHub Issues on `MarcelInTO/fux`**, filed with the `gh`
CLI — `gh issue create`, `gh issue list`, `gh issue view`. This is a GitHub project: there
is no GitLab tracker and `glab` does not apply to it.

Conventions, as the tracker actually uses them:

- **Labels are GitHub's defaults, and only `bug` and `enhancement` are in use.** Check with
  `gh label list` before filing and apply one of the labels already there. Do not invent a
  label — new ones are a deliberate decision, not a side effect of filing.
- **Bodies lead with `## Summary`, then evidence, then `## Cause`.** The evidence is real
  data — a reproducing document, measured numbers, the drill output — not a description of
  it, and the cause cites `file:line`. Issues #1, #2 and #10 are the worked examples.
- **Verify before filing, and label a theory as a theory.** A suspected cause goes in only
  when it is evidence-backed; otherwise say plainly that diagnosis needs instrumentation.
  A guess written into an issue gets read later as a finding. **This applies to the proposed
  fix as much as to the cause, and that is the half that keeps going wrong.** #21's mock-up
  listed Cancel first "so it is the default"; MessageBox defaults to the *last* button, so
  implementing the issue as written armed a reflexive Enter to delete — the very thing the
  prompt existed to prevent. #26 reasoned that a release's output could not change, which held
  for `0.3.0` and not for `0.2.0-rc.1`. Both read as findings and were assumptions about a
  framework nobody had run. Write the mechanism as a theory unless you have executed it.
- **An issue's evidence goes stale — re-check it before relying on it.** #1 cited
  `src/Application/Samples/basket.xml` as a second reproducing document; that file went with
  the WinForms tree months earlier, so half its evidence could not be reproduced from the repo.
  Picking up an old issue starts with confirming its fixtures and `file:line` references still
  exist.
- One issue is one piece of work. Confirm the title and body with the user before creating
  it, unless they have said to file autonomously.

`main` is a protected ruleset: it takes no direct pushes, and a merge needs a PR with the
three `build & smoke` jobs green. Branch, PR, merge — including for docs-only changes.
