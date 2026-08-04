# Review Clips Creator (`rcc`)

A CLI that builds background footage by cutting short clips out of one or more video sources and
joining them into a render of a specified length.

Typical use is B-roll for talking-head or podcast video, where the output sits behind narration.
Output is muted, visually suppressed, and timed to an exact runtime.

```bash
rcc generate -i movie.mkv -d 90s -o background.mp4
```

Sources can be a file, a directory, or a glob, and are handled identically — a feature film, a
folder of trailers, stock footage, and public-domain material all work through the same pipeline.

Using material you do not own carries constraints worth understanding before you start — see
[LEGAL.md](LEGAL.md).

## Contents

- [Requirements](#requirements)
- [Quick start](#quick-start)
- [Commands](#commands)
- [Options](#options)
- [Profiles](#profiles)
- [Cue files](#cue-files)
- [Configuration](#configuration)
- [Exit codes](#exit-codes)
- [How it works](#how-it-works)
- [Performance](#performance)
- [Copyright and sourcing](#copyright-and-sourcing)
- [Development](#development)

---

## Requirements

| | |
| --- | --- |
| .NET | 10 SDK |
| FFmpeg | 6 or later, with `ffmpeg` and `ffprobe` on `PATH` |
| Encoders | `libx264` required; NVENC used automatically when available |

Hardware encoding is detected at startup with a two-frame probe encode and falls back to software
if unavailable. No configuration is required.

```bash
dotnet build
alias rcc="$PWD/src/ReviewClips.Cli/bin/Debug/net10.0/rcc"
```

---

## Quick start

```bash
# 90 seconds, default settings
rcc generate -i movie.mkv -d 90s -o bg.mp4

# Vertical output for Shorts
rcc generate -i movie.mkv -d 60s --profile shorts -o vertical.mp4

# 16 minutes built from only 50 distinct clips
rcc generate -i movie.mkv -d 16m --max-clips 50 -o bg.mp4

# Ten variants in one run
rcc batch -i movie.mkv -d 60s --count 10 --out-dir ./backgrounds

# Print the plan and FFmpeg commands without rendering
rcc generate -i movie.mkv -d 90s --dry-run

# Build from specific timestamps
rcc generate -i movie.mkv --cues notes.txt -d 2m -o bg.mp4

# Let the source's own chapter markers exclude the titles and credits
rcc generate -i movie.mkv -d 90s --skip-head 0 --skip-tail 0 -o bg.mp4
```

The first run against a source analyses it and caches the result. Later runs against the same file
skip that step.

---

## Commands

| Command | Description |
| --- | --- |
| `generate` | Render one output file. |
| `batch` | Render several variants from the same sources, using derived seeds. |
| `scan` | Analyze sources and populate the cache without rendering. |
| `probe` | Print resolution, frame rate, HDR status, pixel aspect ratio, and chapters. |
| `profiles` | List available presets. |

`rcc <command> --help` lists all options for that command.

`batch` adds `--count <n>` (variants to render, default `3`) and `--out-dir <path>`. Each variant
uses a seed derived from the base seed, so the whole batch is reproducible from one `--seed`.

`scan` accepts `--force` to re-analyze regardless of cache state, plus `--analysis-fps`,
`--scene-threshold`, and `--hwdecode`.

`probe` accepts `--chapters` to list a source's chapter markers and show which of them selection
would skip.

`probe` and `scan` take one or more paths as positional arguments rather than `-i`.

---

## Options

`generate` and `batch` share the option set below. `batch` adds `--count` and `--out-dir`.

### Input and output

| Option | Default | Description |
| --- | --- | --- |
| `-i, --input <path>` | required | Source file, directory, or glob. Repeatable. |
| `-o, --output <path>` | derived from settings | Output file path. |
| `-d, --duration <dur>` | `60s` | Runtime of the finished file. |
| `-p, --profile <name>` | none | Preset to use as a base. Explicit options override it. |

Durations accept `90`, `90s`, `2m`, `1m30s`, `1.5m`, `1h2m3s`, `1:30`, or `00:01:30.500`.

### Clip selection

| Option | Default | Description |
| --- | --- | --- |
| `-s, --splice <dur>` | `5s` | Length of each clip. |
| `--splice-jitter <dur>` | `1s` | Random variation applied per clip. `0` for a fixed cadence. |
| `--max-clips <n>` | unlimited | Cap distinct clips and repeat them to fill the runtime. |
| `--strategy <name>` | `scored` | `uniform`, `random`, `scene`, `scored`, or `cues`. |
| `--seed <int>` | random | Fixes clip selection. Recorded in the manifest when omitted. |
| `--min-gap <dur>` | `20s` | Minimum spacing between clip start points. |
| `--skip-head <t\|%>` | `5%` | Ignored at the start of each source. |
| `--skip-tail <t\|%>` | `8%` | Ignored at the end of each source. |
| `--range <a-b>` | none | Restrict sampling to this range. Repeatable. |
| `--exclude <a-b>` | none | Exclude this range. Repeatable. |
| `--chapters <mode>` | `auto` | `auto` skips chapters titled as openings, credits, or recaps. `off` disables it. |
| `--skip-chapter <pat>` | none | Also skip chapters whose title matches. Repeatable. |
| `--cues <file\|list>` | none | Timestamps to build from. Implies `--strategy cues`. |
| `--no-reject-black` | off | Allow clips from stretches detected as black. |
| `--no-reject-frozen` | off | Allow clips from static or frozen shots. |
| `--no-shuffle` | off | Emit clips in chronological order. |

`--skip-head` and `--skip-tail` accept a percentage or an absolute time. Percentages let one value
apply to both a feature and a short trailer. The defaults approximate opening titles and end
credits; `--chapters` locates them exactly where the source says where they are.

### Chapter markers

Most MKV rips, and many MP4s, carry chapter markers. Where those chapters are named, they state
exactly where the front and back matter is, which is strictly better than inferring it from a
percentage of the runtime. Chapters whose title reads as an opening sequence, end credits, or
recycled footage are excluded from sampling by default:

```
$ rcc probe --chapters movie.mkv

  # │ Start │ Length │ Title             │ Default
 ───┼───────┼────────┼───────────────────┼──────────
  1 │ 00:00 │ 45s    │ Studio Logos      │ skipped
  2 │ 00:45 │ 75s    │ Opening Titles    │ skipped
  3 │ 02:00 │ 180s   │ Act One           │ eligible
  4 │ 05:00 │ 220s   │ The Confrontation │ eligible
  5 │ 08:40 │ 80s    │ End Credits       │ skipped
```

Matching is on titles only, never on position, so nothing is guessed. Two consequences follow:

- A rip whose chapters are named `Chapter 01`, `Chapter 02`, ... carries no usable information.
  Filtering degrades to a no-op and the `--skip-head` / `--skip-tail` trims remain the only defence.
  `probe --chapters` says so explicitly when this is the case.
- A named chapter is caught wherever it sits, not only at the ends. An episode whose opening theme
  starts at 1:30 or whose next-episode preview sits before the credits is handled.

Chapter exclusions are additive: they are subtracted alongside `--exclude` and the detected black
and frozen stretches, and they do not replace the percentage trims. For a well-chaptered source the
trims are then redundant, and dropping them recovers real footage:

```bash
rcc generate -i movie.mkv -d 90s --skip-head 0 --skip-tail 0
```

`--skip-chapter` adds patterns of your own. A pattern containing `*` or `?` is a glob matched
against the whole title; anything else is a case-insensitive substring:

```bash
rcc generate -i ep01.mkv -d 90s --skip-chapter "cold open" --skip-chapter "stinger"
rcc generate -i ep01.mkv -d 90s --chapters off --skip-chapter "ending*"
```

`--skip-chapter` is honoured whether or not `--chapters off` is set, so `off` disables the built-in
titles while leaving explicit patterns in force. A `--skip-chapter` that matches nothing produces a
warning rather than silently doing nothing.

The plan summary reports which chapters were excluded and how much runtime that removed, so the
exclusion is never invisible. `--strategy cues` is unaffected: explicit timestamps are taken as
given, as with every other eligibility filter.

### Scored strategy tuning

| Option | Default | Description |
| --- | --- | --- |
| `--motion-target <m>` | `1.25` | Preferred motion level, as a multiple of the source median. |
| `--motion-floor <m>` | `0.35` | Reject clips below this multiple. Filters near-static shots. |
| `--motion-ceiling <m>` | `4.0` | Reject clips above this multiple. Filters fast action and strobing. |

Thresholds are relative to each source's own median motion, not absolute, so the same values apply
across genres. Raise `--motion-floor` if output is too static; lower `--motion-ceiling` if it is
too busy. These values are approximate and benefit from per-title adjustment.

### Framing

| Option | Default | Description |
| --- | --- | --- |
| `--aspect <w:h>` | source | Target aspect ratio, e.g. `16:9`, `9:16`, `1:1`. |
| `--resolution <WxH>` | `1920x1080` | Also accepts `720p`, `1080p`, `1440p`, `4k`. |
| `--fps <n>` | `30` | Output frame rate. |
| `--fit <mode>` | `crop` | `crop`, `pad`, `blur-pad`, or `stretch`. |
| `--fg-scale <1-4>` | `1.0` | With `blur-pad`, enlarges the foreground and crops the overflow. |
| `--tonemap <op>` | `auto` | `auto`, `none`, `hable`, `mobius`, `reinhard`. |

Fit modes:

- `crop` — scale to cover the frame, then centre-crop. Fills the frame, loses the edges.
- `pad` — scale to fit, then letterbox. Keeps the whole image.
- `blur-pad` — scale to fit over a blurred, zoomed copy of itself. Common for vertical output.
- `stretch` — ignore aspect ratio.

`--fg-scale` applies to `blur-pad` only. It matters most for 2.35:1 source in a 9:16 frame, where
`1.0` fills roughly a quarter of the height with real image and the rest with blur. `1.5` is a
middle ground; `2.0` fills most of the height and crops the sides noticeably.

`--tonemap auto` converts HDR (PQ or HLG) sources to SDR BT.709 and leaves SDR sources untouched.
Without it, HDR sources render washed out.

### Look

| Option | Default | Description |
| --- | --- | --- |
| `--darken <0-1>` | `0.35` | Scales luma. `0.35` renders at 65% brightness. |
| `--saturation <m>` | `0.80` | Saturation multiplier. |
| `--contrast <m>` | `1.0` | Contrast multiplier. |
| `--blur <sigma>` | `0` | Gaussian blur. |
| `--grain <0-100>` | `0` | Film grain. |
| `--vignette` | off | Darkens frame corners. |
| `--mirror` | off | Horizontal flip. |
| `--zoom <factor>` | `1.0` | Slow zoom across each clip, e.g. `1.08`. |
| `--speed <m>` | `1.0` | Playback rate. Source read length is adjusted to keep output length. |
| `--lut <path>` | none | 3D LUT (`.cube`). |
| `--overlay <path>` | none | Image composited over every clip. |
| `--overlay-opacity <0-1>` | `1.0` | Overlay alpha. |

Darkening is multiplicative and anchored on the black level, so shadow detail is preserved on dark
source material. The defaults reduce brightness and saturation so the footage sits behind
narration and on-screen text rather than competing with it.

### Transitions

| Option | Default | Description |
| --- | --- | --- |
| `--transition <kind>` | `dip-to-black` | See list below. |
| `--transition-duration <dur>` | `0.4s` | Clamped to half the shortest clip. |
| `--fade-in <dur>` | `0.5s` | Fade from black at the start. |
| `--fade-out <dur>` | `0.5s` | Fade to black at the end. |

Available kinds: `none`, `fade`, `dip-to-black`, `dip-to-white`, `dissolve`, `wipeleft`,
`wiperight`, `slide-up`, `slide-down`, `smooth-left`, `circle-close`.

`--transition none` with both fades at `0` allows the final join to be a stream copy, which
removes the last encode pass entirely.

### Encoding

| Option | Default | Description |
| --- | --- | --- |
| `--encoder <name>` | `auto` | `auto`, `nvenc`, `x264`, `x265`. |
| `--codec <name>` | `h264` | `h264` or `hevc`. |
| `-q, --quality <0-51>` | `20` | CRF for software encoders, CQ for NVENC. Lower is better. |
| `--preset <name>` | encoder default | `medium` for x264, `p5` for NVENC. |
| `--audio` | off | Keep source audio. Off by default; output is intended for use under narration. |
| `--hwdecode` | off | CUDA decode. Fixed setup cost; benefits long sources only. |
| `-j, --parallelism <n>` | `4` | Concurrent extraction processes. Capped at 8 for NVENC. |

`-q 20` is high quality for background footage. `-q 26` typically halves file size with no visible
difference at normal viewing size.

### Analysis

| Option | Default | Description |
| --- | --- | --- |
| `--analysis-fps <n>` | `4` | Sampling rate during analysis. Higher is slower and more precise. |
| `--scene-threshold <0-100>` | `8` | Scene-cut sensitivity. Lower detects more cuts. |
| `--no-cache` | off | Re-analyze and overwrite the cached result. |

### Diagnostics

| Option | Description |
| --- | --- |
| `--dry-run` | Print the plan, selected clips, and FFmpeg commands. Renders nothing. |
| `--manifest <path>` | Write a JSON record of the render. |
| `--keep-temp` | Retain intermediate clip files. |
| `-v, --verbose` | Debug logging, including every FFmpeg command. |

The manifest records the seed, strategy, encoder, per-source usage, and every clip's start and
duration. `distinctSourceSeconds` is the union of referenced ranges, so repeats and overlaps count
once.

### Limiting source footage used

`--max-clips` caps the number of distinct clips and repeats them to fill the runtime. This
separates output length from the amount of source material consumed.

```bash
rcc generate -i movie.mkv -d 16m --max-clips 50 -o bg.mp4
```

For a 16-minute render from a 99.5-minute feature:

| | distinct clips | source used | share of source |
| --- | --- | --- | --- |
| no cap | 210 | 17.4 min | 17.5% |
| `--max-clips 100` | 100 | 8.3 min | 8.4% |
| `--max-clips 50` | 50 | 4.2 min | 4.2% |
| `--max-clips 25` | 25 | 2.1 min | 2.1% |

The clip pool is reshuffled on each pass and no clip follows itself across a cycle boundary.
Identical clips are encoded once and referenced multiple times, so capping also reduces render
time.

A warning is emitted above roughly 4 repeats per clip. Whether repetition is noticeable depends on
how prominent the footage is; heavily darkened background material tolerates more than footage the
viewer is watching directly.

With `--cues`, the cap limits how many cues are used and each cue produces a full
`--splice`-length clip.

Bounding source usage is also relevant to fair use; see [LEGAL.md](LEGAL.md).

---

## Profiles

```bash
rcc profiles
```

| Profile | Sets |
| --- | --- |
| `youtube` | 1920x1080, `--fit crop`. |
| `shorts` | 1080x1920, `--fit blur-pad`, `--fg-scale 1.5`. |
| `square` | 1080x1080, `--fit crop`. |
| `subtle` | `--darken 0.20 --saturation 0.92`, cross-dissolves at `0.6s`. |
| `moody` | `--darken 0.50 --saturation 0.55 --blur 1.5 --grain 6 --vignette`. |
| `kenburns` | `--zoom 1.10 --splice 7s`, cross-dissolves at `0.8s`. |
| `raw` | No look adjustments, no transitions. Renders by stream copy. |

Profiles supply defaults. Any option given on the command line overrides the profile. Custom
profiles can be added under `Profiles` in the configuration file.

---

## Cue files

`--cues` accepts a comma-separated list of timestamps or a path to a file. Files may contain
comments and trailing text after each timestamp:

```txt
# episode 42
00:08:15   opening shot
0:35:20    the twist
1:12:04    ending
```

Each cue produces one clip. Clips appear in the order listed, and `--duration` is divided equally
between them unless `--max-clips` is set. Cues within 3 seconds of a detected cut snap back to
that cut so clips start on a shot boundary.

Use this mode when the footage should correspond to specific points in the audio. It also has the
strongest fair-use position, since the footage illustrates the commentary rather than decorating it
(see [LEGAL.md](LEGAL.md)).

---

## Configuration

Settings are read from, in increasing precedence:

1. `appsettings.json` beside the executable
2. `~/.config/reviewclips/appsettings.json`
3. Environment variables prefixed `RCC_`
4. Command-line options

```json
{
  "Ffmpeg": {
    "FfmpegPath": "ffmpeg",
    "FfprobePath": "ffprobe"
  },
  "Cache": {
    "Directory": null
  },
  "Profiles": [
    {
      "Name": "mychannel",
      "Description": "House style",
      "Width": 1920,
      "Height": 1080,
      "Darken": 0.4,
      "Saturation": 0.75,
      "Transition": "Fade",
      "TransitionSeconds": 0.5
    }
  ]
}
```

The analysis cache defaults to `~/.local/share/reviewclips/analysis`. Entries are keyed on file
path, size, modification time, and all analysis settings, so changing any of those invalidates
them automatically. Deleting the directory is safe.

---

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Argument parse error |
| `2` | Invalid arguments or configuration |
| `3` | No usable clips could be selected |
| `4` | FFmpeg or ffprobe failed or was not found |
| `70` | Unexpected error |
| `130` | Interrupted (SIGINT or SIGTERM) |

On interrupt, child FFmpeg processes are terminated, temporary files are removed, and no partial
output file is left behind.

---

## How it works

1. **Probe** — `ffprobe` reports duration, dimensions, frame rate, pixel aspect ratio, color
   transfer characteristics, and chapter markers for each source.
2. **Analyse** — one FFmpeg pass, decimated to 4 fps and downscaled to 320px wide, produces scene
   cut timestamps, a per-frame motion curve, and the black and frozen ranges. Cached on disk.
3. **Select** — clips are chosen by the active strategy from the eligible ranges, which exclude the
   head/tail trims, any `--exclude` ranges, chapters skipped by title, and detected black and
   frozen stretches.
4. **Extract** — clips are cut in parallel and normalized to identical geometry, frame rate, pixel
   format, GOP length, and timebase.
5. **Stitch** — with no transitions or fades, the normalized clips are concatenated by stream copy.
   Otherwise a single re-encode pass applies transitions and fades.

Analysis is a single pass because `scdet` exports a mean-absolute-frame-difference value per frame,
which serves as both the scene-cut signal and the motion metric.

### Selection strategies

| Strategy | Behavior | Needs analysis |
| --- | --- | --- |
| `scored` | Prefers shot-aligned clips ranked by motion suitability. Default. | yes |
| `scene` | Starts each clip shortly after a detected cut. | yes |
| `uniform` | Evenly spaced across the eligible runtime. | no |
| `random` | Random positions, reproducible via `--seed`. | no |
| `cues` | Explicit timestamps. | for cut snapping |

All strategies divide the eligible footage into one stratum per required clip and take the best
candidate from each, which keeps clips distributed across the source rather than clustered in
high-scoring regions.

Two invariants hold regardless of settings:

- Clips never overlap. If the requested spacing cannot be met, fewer clips are produced and a
  warning is emitted.
- `--min-gap` is clamped to the widest achievable spacing rather than failing.

`--duration` refers to the runtime of the finished file. Transition overlap is accounted for, so
the output length matches the request.

---

## Performance

Measured on an RTX 5090 with NVENC, using a 1h39m 1080p source.

| Operation | Time |
| --- | --- |
| First analysis of a source | 40s (~148x realtime) |
| Subsequent runs (cached) | ~0.2s |
| 16-minute render, 210 clips | 2m15s |
| 16-minute render, `--max-clips 50` | 1m31s |
| 90-second render | ~10s |

Notes:

- Analysis dominates the first run against any source and is cached thereafter.
- `--transition none` with both fades at `0` removes the final encode pass.
- `--fit blur-pad` is significantly slower than other fit modes; `gblur` runs on the CPU.
- `--hwdecode` has a fixed CUDA setup cost that can exceed its benefit on short reads. It is off by
  default and worth benchmarking before enabling.
- `-q 26` roughly halves output size versus the `-q 20` default.

---

## Copyright and sourcing

Using material you do not own carries constraints that are widely misunderstood. See
**[LEGAL.md](LEGAL.md)** for the detail. In brief:

- Clip length is not a safe harbour. US copyright law has no de minimis duration threshold, and
  amount is assessed in aggregate rather than per clip.
- Muting, darkening, and blurring do not change copyright status.
- Fair use for criticism is strongest when a clip illustrates a specific point. Decorative
  background use is a weaker position than footage tied to the commentary.
- Ripping encrypted discs violates DMCA §1201 independently of fair use.
- Content ID does not evaluate fair use.

`--cues` and `--max-clips` are the two options that bear on this: the first ties footage to the
commentary, the second bounds how much of the source is used. Lower-risk sources (stock, public
domain, your own footage) work through the same pipeline with no workflow change.

Not legal advice.

---

## Development

```txt
src/
  ReviewClips.Core/     Domain model, selection strategies, pipeline orchestration.
  ReviewClips.Ffmpeg/   Process runner, probe, filter graph builder, extraction, stitching.
  ReviewClips.Cli/      Command-line surface, console output, profiles.
tests/
  ReviewClips.Core.Tests/     Selection, scoring, planning, primitives.
  ReviewClips.Ffmpeg.Tests/   Filter graphs, stitch arithmetic, parsers, FFmpeg integration.
```

`ReviewClips.Core` contains no FFmpeg knowledge; everything external enters through interfaces
defined in `Core/Pipeline/Abstractions.cs`.

```bash
dotnet build
dotnet test
```

Integration tests synthesize their inputs with FFmpeg's `lavfi` sources, so the suite requires no
sample media and runs anywhere. Tests are skipped if FFmpeg is unavailable.

### Implementation notes

Non-obvious decisions, each covered by regression tests.

**FFmpeg is invoked as a subprocess, not through a wrapper library.** The tool depends on direct
control of NVENC parameters and filter graphs, which wrapper libraries abstract away. Arguments are
passed via `ProcessStartInfo.ArgumentList` rather than a command string, since media paths commonly
contain spaces, quotes, and brackets.

**Analysis metadata is read from stdout, not a file.** FFmpeg reopens `metadata=print:file=`
targets in truncate mode when the filter graph reinitialises, which any mid-stream format change
triggers. A file target therefore retains only the frames after the final reinitialisation.
Progress is derived from the metadata timestamps, since `-progress` would contend for stdout.

**Detector output requires `info` log level.** `scdet`, `blackdetect`, and `freezedetect` report on
stderr at `info`. Running analysis with `-loglevel error` silently discards all of it.

**Tone mapping tags frames with `setparams` before `zscale`.** `zscale` fails with "no path between
colorspaces" when transfer, matrix, or primaries is unspecified, which is common in HDR rips with
partial metadata. `zscale`'s own `tin`/`min`/`pin` options do not resolve it.

**Darkening uses `lutyuv=y=minval+(val-minval)*k`.** `eq=brightness` is an additive offset and
crushes shadows to black on dark source material. `colorlevels` is equivalent numerically but
segfaults FFmpeg 8.1 at this position in the graph. `lutyuv` is a lookup table, bit-depth agnostic,
and luma-only, so chroma is unaffected.

**Clip selection is stratified, not greedy.** Score-greedy selection with a relaxing minimum gap
degrades to "take the N highest scores" when spacing cannot be satisfied, and since scores are
spatially correlated the result clusters into a few seconds of source. A non-overlap floor is
enforced and never relaxed.

**Chapter skipping matches titles, never positions.** "The last chapter is the credits" is true
often enough to be tempting and wrong often enough to put a credit crawl in the output, so it is not
inferred. Only a title that names the material triggers an exclusion, which means the feature does
nothing at all on the many rips whose chapters are named `Chapter 01` onwards — an honest no-op is
preferable to a heuristic that silently discards an act of the film. Titles that only restate the
chapter number are recognised as such and never matched, so a pattern like `*1*` cannot catch them.
Chapter ranges are subtracted unpadded, unlike detected black stretches, because a container states
its boundaries exactly; clips still cannot bleed across one, since a candidate window must fit
entirely inside a single eligible range.

**Interruption uses `PosixSignalRegistration`, not `Console.CancelKeyPress`.** The latter handles
only SIGINT and never sees SIGTERM, which is what `kill`, systemd, Docker, and CI send. Handlers
dispatch cancellation to the thread pool, because `CancellationTokenSource.Cancel()` runs
callbacks synchronously on the calling thread and would otherwise run pipeline cancellation on the
signal thread.

**Output length is solved iteratively.** Cross-transitions overlap two clips each, so N clips lose
(N-1) transition durations, and the clip count depends on how much material is planned.
`SplicePlanner.PlanForOutput` and `ClipSequencer.FillForOutput` resolve this by fixed-point
iteration.

**Clips are normalized at extraction.** Identical geometry, frame rate, pixel format, GOP, and
timebase are what permit the stream-copy stitch path. Anamorphic sources are corrected to square
pixels before any aspect-ratio arithmetic, and variable frame rate sources are forced to CFR.
