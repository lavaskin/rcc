# Copyright and sourcing

Constraints and risk considerations for using `rcc` with material you do not own.

**Not legal advice.** US-centric. Other jurisdictions differ substantially, particularly on fair
dealing versus fair use. Consult a lawyer for your specific situation.

## Contents

- [Common misconceptions](#common-misconceptions)
- [Purpose matters more than length](#purpose-matters-more-than-length)
- [Acquisition is a separate issue](#acquisition-is-a-separate-issue)
- [Content ID](#content-id)
- [Source options by risk](#source-options-by-risk)
- [Tool features relevant to exposure](#tool-features-relevant-to-exposure)

---

## Common misconceptions

| Claim | Status |
|---|---|
| "Short clips are fine." | False. US copyright law has no de minimis duration threshold. Clip length affects one of four fair-use factors and is not decisive. |
| "Aggregate length doesn't matter." | False. Total amount taken is what the third factor weighs. 200 clips of 5s is over 16 minutes of the work. |
| "Muting it makes it fine." | Partly. Muting removes the sound-recording claim. The audiovisual work is separately protected. |
| "Darkening or blurring makes it fine." | False. A derivative of an infringing copy is still infringing. |
| "Trailers are legally different." | False. Trailers are studio-owned and fully protected. Enforcement is rarer, which is a difference in practice, not in law. |
| "Crediting the source makes it fine." | False. Attribution is not a licence and is not a fair-use factor. |

## Purpose matters more than length

Film criticism is an enumerated fair-use purpose. 17 U.S.C. §107 names "criticism, comment" in its
preamble. The four statutory factors are:

1. **Purpose and character** of the use, including whether it is transformative and commercial.
2. **Nature** of the copyrighted work. Creative works receive stronger protection than factual ones.
3. **Amount and substantiality** of the portion used, in both quantity and qualitative importance.
4. **Effect on the market** for the original.

The claim under the first factor is strongest when a clip illustrates a specific point being made
about it. Footage used as decorative background consumes the work for its entertainment value,
which is what the rights holder is entitled to. Purely decorative use is therefore a weaker
position than footage tied to the commentary, regardless of clip length.

The third factor is where clip selection matters. Quantity is assessed in aggregate, not per clip,
and sampling across an entire runtime is more likely to include qualitatively important material
than a bounded selection.

## Acquisition is a separate issue

Circumventing disc encryption (AACS, CSS) violates DMCA §1201 independently of fair use. A
successful fair-use defence does not resolve a §1201 claim; they are separate causes of action.

Downloading from unauthorised sources is direct infringement of the reproduction right, regardless
of what the copy is subsequently used for.

Screen-recording a stream you subscribe to generally avoids the §1201 circumvention question, since
no technological protection measure is defeated, though it may breach the service's terms of
service. This is a contract question rather than a copyright one.

## Content ID

Content ID is automated fingerprint matching. It does not evaluate fair use, and no fair-use
argument affects whether a match occurs.

| | |
|---|---|
| Typical outcome of a match | A claim redirecting monetisation to the rights holder. |
| Severity | Low. Not a copyright strike; does not affect channel standing. |
| Dispute path | Available, but reviewed by the claimant in the first instance. |
| Higher-severity risk | A manual takedown from the rights holder, which is a strike. |

Enforcement posture varies considerably between studios. Some tolerate review content broadly;
others issue manual claims routinely.

## Source options by risk

| Source | Risk | Notes |
|---|---|---|
| Your own footage | None | Podcast multicam, self-shot B-roll. |
| Generated visuals | None | Burned-in captions, waveforms, abstract backdrops. |
| Public domain film | None | Pre-1930 US works; Internet Archive, Prelinger Archives. |
| Licensed stock | None | Pexels, Pixabay, Videvo (free); Artgrid, Storyblocks (paid). |
| Official trailers | Low | Studio-distributed and rarely enforced, but not legally distinct. |
| Poster and key art | Low | Promotional material, widely tolerated. Pair with `--zoom`. |
| Full film as background | Highest | Weakest fair-use position, plus §1201 exposure if ripped. |

`rcc` treats a file, a directory, and a glob identically, so switching to lower-risk material
requires no workflow change:

```bash
rcc generate -i ./stock-footage/ -d 90s --profile shorts
rcc generate -i ./trailers/ -d 90s
```

## Tool features relevant to exposure

Two options bear directly on the factors above.

**`--cues`** builds the render from timestamps you supply, so footage corresponds to the points
being discussed. This is the stronger position under the first factor than footage sampled at
random.

**`--max-clips`** caps the number of distinct clips and repeats them to fill the runtime, which
bounds the total amount taken under the third factor. A 16-minute render from a 99.5-minute feature
uses 17.5% of the source with no cap, and 4.2% at `--max-clips 50`. The figure is reported at
render time and recorded in the manifest as `distinctSourceSeconds`, computed as the union of
referenced ranges.

**On the visual treatment options.** `--darken`, `--blur`, `--grain`, and `--mirror` exist because
background footage needs to be visually suppressed to work under narration. They do not affect
copyright status. Used to evade automated matching, they may breach platform terms of service, and
deliberate obfuscation can support a finding of willful infringement, which raises statutory
damages.
