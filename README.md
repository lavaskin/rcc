# reviewclips (`rcc`)

Generates background footage for video essays and podcast clips by splicing short segments out
of one or more sources and stringing them together into a render of a chosen length.

Built for movie-review channels: you have a podcast segment discussing a film and no visuals to
put behind it. `rcc` produces a muted, visually suppressed background track that sits behind
narration without competing with it.

```bash
rcc generate -i movie.mkv -d 90s -o background.mp4
```

**Please read [Copyright, honestly](#copyright-honestly) before pointing this at a film you do
not own the rights to.** It is the part of this README most likely to save you trouble.

---

## Requirements

- **.NET 10 SDK**
- **FFmpeg 6+** with `ffmpeg` and `ffprobe` on `PATH` (built with `libx264`; NVENC optional)

Hardware encoding is detected at runtime by attempting a two-frame probe encode, and falls back
to software automatically. Nothing needs configuring.

```bash
dotnet build
alias rcc="$PWD/src/ReviewClips.Cli/bin/Debug/net10.0/rcc"
```

---

## Quick start

```bash
# 90 seconds of background footage, sensible defaults
rcc generate -i movie.mkv -d 90s -o bg.mp4

# Vertical, for Shorts
rcc generate -i movie.mkv -d 60s --profile shorts -o vertical.mp4

# Ten interchangeable variants for one episode
rcc batch -i movie.mkv -d 60s --count 10 --out-dir ./backgrounds

# Show the plan and the exact FFmpeg commands, render nothing
rcc generate -i movie.mkv -d 90s --dry-run

# Footage from the scenes you actually discuss (see Cue-driven mode)
rcc generate -i movie.mkv --cues notes.txt -d 2m -o bg.mp4
```

The first run against a title scans it, which is the slow part. Every later run reuses the
cached analysis and starts effectively instantly.

---

## Commands

| Command | Purpose |
|---|---|
| `generate` | Render one background clip. |
| `batch` | Render several variants from the same sources in one go. |
| `scan` | Analyse sources and populate the cache, without rendering. |
| `probe` | Show resolution, frame rate, HDR and anamorphic status of sources. |
| `profiles` | List the built-in presets. |

`rcc <command> --help` lists every option.

---

## How it works

1. **Probe** every source with `ffprobe`, noting HDR transfer functions and non-square pixels.
2. **Analyse** in a single FFmpeg pass, decimated to 4 fps and downscaled to 320px wide. That one
   pass yields scene cuts, a motion curve, and the black and frozen stretches, because `scdet`
   exports a mean-absolute-frame-difference value per frame that doubles as a motion metric.
   Results are cached to `~/.local/share/reviewclips/analysis`.
3. **Select** segments using the chosen strategy, avoiding credits, fades to black and static shots.
4. **Extract** each segment in parallel, normalising every one to identical geometry, frame rate,
   pixel format and timebase.
5. **Stitch** them. With `--transition none` that uniformity permits a pure stream copy, which is
   effectively instant; otherwise a single re-encode pass applies the transitions.

### Selection strategies

| `--strategy` | Behaviour |
|---|---|
| `scored` | **Default.** Scene-aligned, ranked by motion suitability. |
| `scene` | Starts each clip just after a detected cut. |
| `uniform` | Evenly spaced. Predictable; needs no analysis. |
| `random` | Random, reproducible via `--seed`. |
| `cues` | Driven by timestamps you supply. |

All strategies use **stratified sampling**: the usable footage is divided into one stratum per
required clip and the best candidate is taken from each. This matters more than it sounds.
Plain score-greedy selection collapses when the requested spacing cannot be met — it degrades to
"take the N highest scores", and because scores are spatially correlated, every clip ends up
coming from the same few seconds of film.

Two guarantees hold regardless of settings:

- Clips **never overlap**. If spacing cannot be satisfied, you get fewer clips and a warning,
  never the same footage twice.
- `--min-gap` is clamped to what is arithmetically achievable rather than failing.

### Cue-driven mode

`--cues` accepts a comma-separated list or a file. The file format tolerates comments and
trailing labels, so your working notes can be used directly:

```
# notes for episode 42
00:08:15   the opening shot
0:35:20    the twist, we spend a while here
1:12:04    the ending
```

Each cue becomes one clip, in the order listed, sharing `--duration` between them. Cues snap back
to the start of the shot containing them so clips open cleanly.

This is the mode worth preferring. Showing the scene you are discussing is better content than
decorative footage, and a materially stronger fair-use position — see below.

---

## Profiles

```bash
rcc profiles
```

| Profile | What it is for |
|---|---|
| `youtube` | 1080p 16:9. |
| `shorts` | 1080x1920 vertical with a blurred backdrop fill. |
| `square` | 1080x1080. |
| `subtle` | Light treatment, gentle cross-dissolves. |
| `moody` | Dark, desaturated, vignetted, grainy. Recedes furthest behind a voice-over. |
| `kenburns` | Slow drift, longer clips. Good over slower commentary. |
| `raw` | No treatment, hard cuts. Renders by stream copy, so it is by far the fastest. |

Profiles set defaults; any option you type explicitly wins. Add your own in
`~/.config/reviewclips/appsettings.json` under `Profiles`.

---

## Options worth knowing

**Cadence.** `--splice 5s` sets clip length and `--splice-jitter 1s` varies it. Jitter is on by
default because a perfectly regular cadence reads as robotic.

**Trimming.** `--skip-head 5%` and `--skip-tail 8%` default to percentages so one setting works
for both a feature film and a two-minute trailer. They exist to clear studio logos and end credits.

**Framing.** `--aspect 9:16` with `--fit crop|blur-pad|pad`. `blur-pad` is the standard vertical
treatment: your footage centred over a blurred, zoomed copy of itself.

Most films are 2.35:1 scope, which is awkward in a 9:16 frame — `crop` throws away three quarters
of the width, while a plain `blur-pad` fills only a quarter of the height with real image.
`--fg-scale` dials between them by enlarging the foreground and cropping the overflow:

```bash
rcc generate -i movie.mkv -d 60s --profile shorts --fg-scale 1.5   # the shorts default
```

`1.0` shows the whole frame, `1.5` is a good compromise for scope material, `2.0` fills most of
the height at the cost of noticeable side cropping.

**Look.** `--darken 0.35 --saturation 0.8` are the defaults. Darkening is multiplicative — it
scales luma to 65% rather than subtracting a constant — so shadow detail survives on films with
dark scenes. Background footage should recede;
bright, saturated B-roll competes with your captions and voice and tends to hurt retention rather
than help it. Also available: `--blur`, `--grain`, `--vignette`, `--zoom`, `--speed`, `--mirror`,
`--lut`, `--overlay`.

**Audio** is stripped by default. A second audio bed fighting your narration is worse than silence.
`--audio` keeps it.

**Reproducibility.** `--seed` makes a render exactly repeatable. `--manifest out.json` records
which source, which timestamps and how much of each title was used.

### Tuning `scored`

Motion thresholds are expressed as multiples of each title's **own median** motion, not absolute
values, because a slow drama and an action film differ by an order of magnitude.

```bash
rcc generate -i movie.mkv -d 90s \
    --motion-target 1.25 \   # ideal motion level
    --motion-floor 0.35 \    # reject quieter (near-static shots)
    --motion-ceiling 4.0     # reject busier (frantic action, strobing)
```

These defaults are empirical and **expect to tune them per genre**. There is no single correct
value for "interesting". If a render feels too static, raise `--motion-floor`; if it feels
chaotic, lower `--motion-ceiling`.

### Performance notes

- Analysis dominates the first run on a title; it is cached thereafter. Measured: **40s to scan a
  1h39m feature** (about 148x realtime), then ~0.2s on later runs.
- A 16-minute render from that feature took **2m15s** end to end on an RTX 5090 with NVENC:
  210 clips extracted in parallel, then one stitch pass.
- Default quality (`-q 20`) is generous for background footage — a 16-minute 1080p render came to
  1.15 GB. `-q 26` cut that to 568 MB with no visible difference behind narration.
- `--transition none` (or `--profile raw`) skips the final re-encode entirely.
- `--fit blur-pad` is noticeably expensive: `gblur` runs on the CPU, not the GPU.
- `--hwdecode` is **off by default on purpose.** CUDA decode has a fixed setup cost that was
  measured to exceed its benefit on short reads. It helps on long scans; benchmark before trusting it.

---

## Copyright, honestly

Not legal advice, US-centric, and I am not a lawyer. But the common assumptions here are wrong in
ways that matter, so this section is blunt.

### Things that are not true

**"Short clips are fine."** There is no de minimis duration threshold in US copyright law. The
"under 7 / 10 / 30 seconds is safe" belief is a myth. Length affects one of four fair-use factors;
it is not a switch. And **aggregate** amount is what counts: 18 × 5s is 90 seconds of the film.

**"Muting it makes it fine."** Muting removes the sound-recording claim. The audiovisual work is
separately copyrighted.

**"Darkening or blurring makes it fine."** A derivative of an infringing copy is still infringing.
Obscuring footage does not create a licence.

### The part people miss

Film criticism is the textbook fair use case — §107 names "criticism, comment" explicitly. But
that is strongest when a clip **illustrates the specific point you are making**.

Using footage as decorative wallpaper to boost retention consumes the work for its aesthetic and
entertainment value, which is precisely what the copyright holder is entitled to. Ironically,
**the more purely decorative your B-roll, the weaker your fair use claim.**

This is why `--cues` exists, and why it is the mode to prefer.

### Two more things

**Acquisition is a separate violation.** If you ripped a Blu-ray or DVD, circumventing AACS or CSS
violates DMCA §1201 *independently of* fair use. You can win on fair use and still lose on §1201.
Downloading from torrents is plain infringement.

**Content ID is not a legal system.** It is automated fingerprint matching that never evaluates
fair use. The usual outcome is a claim redirecting revenue to the studio, not a strike. The real
channel-ending risk is a *manual* studio strike, and studio tolerance varies enormously.

### On the obfuscation filters in this tool

`--darken`, `--blur`, `--grain` and `--mirror` exist because they are genuinely useful: background
footage **must** be visually suppressed to work behind narration.

To the extent their purpose is defeating Content ID, be clear-eyed: that is **evading a detection
system, not achieving legality**. It may breach YouTube's Terms of Service, and deliberate
obfuscation can be argued as evidence of *willful* infringement, which raises statutory damages.
Use them as a look, not as a shield.

### On trailers

A trailer has identical copyright status — studio-owned, fully protected. "Everyone uses trailers"
works because studios *want* trailers distributed and rarely enforce. That is a real and
meaningful difference in **practical outcome**, but not a difference in legality.

### Lower-risk sources

`rcc` is deliberately source-agnostic. A file, a directory or a glob all work, so you can change
where footage comes from without changing anything else about your workflow.

| Source | Risk | Notes |
|---|---|---|
| Burned-in captions, waveforms | **None** | FFmpeg can generate these. Often *beats* movie B-roll on retention. |
| Your own podcast multicam / b-roll | **None** | |
| Public domain film (pre-1930 US, Archive.org, Prelinger) | **None** | Great generic cinematic texture |
| Free stock (Pexels, Pixabay, Videvo) | **None** | Genre-matched abstract footage |
| Official trailers | **Low** | Tolerated in practice, not legally distinct |
| Poster / key art with `--zoom` | **Low** | Promotional, near-universally tolerated |
| Full film as decorative wallpaper | **Highest** | Weakest fair use plus §1201 exposure |

```bash
# Same pipeline, much lower risk
rcc generate -i ./stock-footage/ -d 90s --profile shorts
rcc generate -i ./trailers/ -d 90s
```

---

## Project layout

```
src/
  ReviewClips.Core/     Domain, selection strategies, pipeline orchestration. No FFmpeg knowledge.
  ReviewClips.Ffmpeg/   Process runner, probe, filter graph builder, extraction, stitching.
  ReviewClips.Cli/      System.CommandLine surface, Spectre output, profiles.
tests/
  ReviewClips.Core.Tests/     Selection, scoring, planning, primitives.
  ReviewClips.Ffmpeg.Tests/   Filter graphs, stitch arithmetic, parsers, FFmpeg integration.
```

FFmpeg is driven by direct process invocation rather than a wrapper library, because the NVENC and
filter-graph control this tool depends on is exactly what wrappers abstract away. Arguments are
always passed via `ProcessStartInfo.ArgumentList`, never a concatenated string — media paths
routinely contain spaces, quotes and brackets.

```bash
dotnet test
```

All test media is synthesised with FFmpeg's `lavfi` sources, so the suite needs no sample files —
and, fittingly, no copyrighted material — to run anywhere.

### Notes for future maintenance

Three non-obvious things, all found the hard way and all covered by regression tests:

- **Analysis metadata is read from stdout, not a file.** FFmpeg reopens `metadata=print:file=`
  targets in truncate mode whenever the filter graph reinitialises, which any mid-stream format
  change triggers. Using a file silently discarded everything before the last reinit.
- **Tone mapping tags frames with `setparams` first.** `zscale` fails with "no path between
  colorspaces" when transfer, matrix or primaries is unspecified, and real HDR rips often carry
  partial metadata. `zscale`'s own `tin`/`min`/`pin` options were measured not to fix it.
- **Interruption uses `PosixSignalRegistration`, not `Console.CancelKeyPress`.** The latter only
  covers SIGINT and never sees SIGTERM, which is what `kill`, systemd, Docker and CI actually send.
  Handlers do minimal work and dispatch the cancel to the thread pool, because
  `CancellationTokenSource.Cancel()` runs callbacks synchronously on the calling thread.
- **Darkening uses `lutyuv`, not `eq=brightness` and not `colorlevels`.** `eq=brightness` is an
  additive offset: on a real film it drove the median pixel to 0 and produced a black rectangle.
  `colorlevels` is the natural multiplicative replacement and gives nearly identical numbers, but
  it segfaults FFmpeg 8.1 in this position in the graph. `lutyuv` anchored on `minval` is
  multiplicative, bit-depth agnostic, luma-only, and a plain lookup table.
- **`--duration` means the runtime of the finished file.** Each cross-transition overlaps two clips,
  so N clips lose (N-1) transition durations. At 16 minutes and 210 clips that was a 77-second
  shortfall, so the required material is solved for by fixed-point iteration in `SplicePlanner`.
