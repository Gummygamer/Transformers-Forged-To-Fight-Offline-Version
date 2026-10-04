# Documentation style for this repository

## Table of contents

- [Why this exists](#why-this-exists)
- [Markdown is canonical](#markdown-is-canonical)
- [Structure](#structure)
- [Provenance marks — how a claim says what backs it](#provenance-marks--how-a-claim-says-what-backs-it)
- [Tone and content](#tone-and-content)
- [When you find something wrong](#when-you-find-something-wrong)
- [Checklist before you open the PR](#checklist-before-you-open-the-pr)

## Why this exists

This project is open source and most of its value is in what it *documents*, not what it
runs. People arrive from outside, with no context, trying to work out what is known, what is
guessed, and what has been tried and abandoned. These conventions exist so every document
answers those three questions the same way.

Nothing here is about taste. Each rule is here because its absence cost someone time.

## Markdown is canonical

**Write Markdown. Do not fork a second copy in another format.** Two documents describing the
same wire format disagree within weeks, and the reader has no way to tell which one is stale.

Need HTML, Word or PDF? Generate it:

```bash
pandoc DOC.md -o doc.html --standalone --toc --toc-depth=3
pandoc DOC.md -o doc.docx --toc --toc-depth=3
pandoc DOC.md -o doc.pdf  --toc --toc-depth=3
```

The conventions below are what make that conversion survive.

## Structure

- **A table of contents** at the top of any document long enough to scroll. Plain Markdown
  links to anchors, so it works on GitHub and converts cleanly.
- **Strict heading ranks, no skipped levels.** An `###` under an `#` with no `##` between
  breaks Word's outline view, breaks `--toc`, and produces a malformed contents list.
- **Plain GFM tables.** No raw HTML — it trips converters.
- **Language tags on every code fence** (` ```bash `, ` ```json `, ` ```csharp `).
- **Cite with `file:line`** where a claim comes from code. A reader must be able to check you
  without asking you.
- **Link related documents** rather than restating them, so facts have one home.

## Provenance marks — how a claim says what backs it

The rule that matters most here. **Every factual claim carries a mark showing how it is
known.** An unmarked claim is indistinguishable from a guess, and guesses in this repository
have cost real debugging time.

| Mark | Meaning |
|---|---|
| **[👁 live]** | **Observed in a running client** — in instrumentation, in a log, or on screen. The strongest mark. |
| **[📡 served]** | **Present in the payload we serve** and accepted without error, but never observed doing anything. |
| **[📄 binary]** | **Read from decompiled source, a dump, or a shipped asset bundle**, with a line number or entry path. True about the client; says nothing about whether it works as authored. |
| **[⚠ inferred]** | **Reasoning only.** No citation. A hypothesis, never a fact. |

**The weakest mark wins.** A sentence resting on one binary read and one inference is
`[⚠ inferred]`.

**The two-source rule:** a claim needs two *different kinds* of evidence — binary plus a live
observation, or binary plus a served payload — before it is treated as settled. Single-source
claims stay provisional no matter how convincing they read.

> A confident reading is not a correct one. An icon mapping in this repository was recorded
> as "confirmed in-client" when the screenshot only proved that the codepoint we sent
> rendered *something*. It shipped, and was reverted. Confirming that a thing appears is not
> confirming that it means what you think.

## Tone and content

- **Write for someone who arrives with no context.** Not for the person who already knows.
- **State what does NOT work, prominently.** A scope note near the top of a document beats a
  caveat buried on line 400, because summaries travel further than the qualifications
  attached to them.
- **Record negative results.** "No writer for this field exists in either build" is a finding
  that saves the next person the same search. Delete nothing that was true.
- **Name the pitfall as a symptom, not a rule.** "Restart after code changes" is forgettable.
  *"If a data edit takes effect but a behaviour change does not, you changed code and did not
  restart"* is what someone will actually recognise at 1am.
- **Give the environment.** Versions, flags, hashes — whatever a claim depends on. A timing
  number measured on a stale interpreter is not a fact about this project.

## When you find something wrong

**Correct it in place and say what changed.** Do not quietly overwrite — a reader who
remembers the old claim needs to know it was retracted rather than assume they misread.

**A retracted premise reopens what it ruled out.** If a claim was the reason something was
abandoned, go back and re-read that thing. A wrong mechanism can leave a correct conclusion
standing for the wrong reason, and the next person will inherit the reasoning, not the
conclusion.

## Checklist before you open the PR

- [ ] Table of contents present and its links resolve
- [ ] No skipped heading ranks
- [ ] Every factual claim carries a provenance mark
- [ ] Every `file:line` citation actually points at what you say it does
- [ ] What the change does **not** cover is stated near the top
- [ ] Environment and versions recorded for anything measured
- [ ] Every Markdown link resolves to a real file
- [ ] Indexed in [`README.md`](README.md) so someone can find it
