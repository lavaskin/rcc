# Porting `review_clips_creator--pi` wins into `rcc`

Working checklist for bringing four features across from the sibling prototype, plus repo
cleanup. Sequenced smallest-risk-first; each phase is independently shippable.

## Context

`review_clips_creator--pi` ("pi") is an earlier, independent implementation of the same tool —
same solution name, same goal, ~6.5k source LOC against rcc's ~9.8k. It is less developed
overall, but it solved several problems rcc has not, and it is worth reading before starting.

Assumed layout (adjust paths if yours differs):

```
../review_clips_creator--pi/     # pi, for reference
./                              # rcc, the target
```

pi is **not under version control** — there is no `.git`. Do not modify it; treat it as
read-only reference material.

### What pi does better (the four ports)

| | pi | rcc today |
| --- | --- | --- |
| External audio muxing | `--audio track.wav --match-audio` | absent |
| Environment check | `doctor` command | absent |
| Source-usage guardrail | `--max-source-percent`, hard-fails | warns on repetition only |
| Effect catalogue | 24 named effects | ~14 typed stages |

### What rcc does better — do not regress these

Read this before touching the filter or planning code.

- **Layering is compiler-enforced.** `Core` / `Ffmpeg` / `Cli` are separate projects and `Core`
  has no FFmpeg knowledge; everything external enters through `Core/Pipeline/Abstractions.cs`.
  pi keeps concrete FFmpeg classes inside `Core/Ffmpeg/`, so its equivalent rule is
  convention-only. Keep new FFmpeg code in `ReviewClips.Ffmpeg`.
- **Filter stage ordering is canonical and reasoned.** `Filters/IVideoFilterStage.cs` +
  `FilterStageOrder` encode *why* each step sits where it does. pi applies effects in whatever
  order the user typed them, which lets you e.g. add grain and then blur it away. We are keeping
  rcc's model — see Phase 5.
- **Analysis is one cached pass per source.** pi spawns an ffmpeg decode *per candidate attempt*
  during planning and caches nothing, which is why its README claim that `plan` is "instant" only
  holds with `--keep-dead-frames`. Do not introduce per-candidate probing.
- **Darkening is multiplicative.** See the warning in Phase 5.
- **Decomposition.** pi's `SegmentPlanner.cs` is a 699-line god class covering slot building, cue
  slots, scene snapping, QC re-rolls, timeline layout and guardrails. rcc splits the same work
  across `SegmentSelectorBase` + five strategies + `SplicePlanner` + `ClipSequencer` +
  `ChapterFilter`. Keep new logic in its own unit.

### Verification

Every phase must end green:

```bash
dotnet build          # must stay 0 warnings; TreatWarningsAsErrors is on
dotnet test           # 346 tests at time of writing
```

Integration tests synthesize their inputs with FFmpeg's `lavfi` sources, so no sample media is
needed. New FFmpeg behaviour gets a test in `tests/ReviewClips.Ffmpeg.Tests/`.

---

## Phase 1 — Cleanup

- [ ] Delete `src/ReviewClips.Core/Class1.cs`
- [ ] Delete `src/ReviewClips.Ffmpeg/Class1.cs`
- [ ] Delete `tests/ReviewClips.Core.Tests/UnitTest1.cs`
- [ ] Delete `tests/ReviewClips.Ffmpeg.Tests/UnitTest1.cs`

These are `dotnet new` leftovers. `public class Class1` is currently part of both libraries'
public API surface.

- [ ] Add `.editorconfig` at the repo root, using `../review_clips_creator--pi/.editorconfig`
      as the starting point.

> **Risk.** `Directory.Build.props` sets both `TreatWarningsAsErrors=true` and
> `EnforceCodeStyleInBuild=true`, but there is no `.editorconfig`, so only compiler defaults are
> enforced today. Dropping one in will likely turn a pile of `IDExxxx` diagnostics into build
> errors at once.
>
> Land the file with all new rules at `suggestion`, confirm `dotnet build` is still 0-warning,
> then promote to `warning` in a **separate** commit. Do not add rules and promote severities in
> the same change.

Note pi has the opposite problem — it ships an `.editorconfig` but sets
`EnforceCodeStyleInBuild=false`, so the file is decorative. Also do **not** copy pi's
`WarningsNotAsErrors=NU1901;NU1902;NU1903;NU1904`; that downgrades NuGet security advisories.

---

## Phase 2 — `doctor` command

Self-contained: no `Core` changes, immediate user value. Reference: pi's
`src/ReviewClips.Cli/Commands/DoctorCommand.cs`.

- [ ] Refactor the two-frame probe encode out of `FfmpegEncoderSelector.IsUsableAsync`
      (`src/ReviewClips.Ffmpeg/Encoding/FfmpegEncoderSelector.cs:108`) into a shared
      `IEncoderProbe`, keeping the existing `ConcurrentDictionary` result cache.
- [ ] New `src/ReviewClips.Ffmpeg/Diagnostics/EnvironmentReport.cs` reporting:
  - [ ] ffmpeg / ffprobe presence and parsed version — reuse
        `FfmpegToolset.EnsureAvailableAsync` (`src/ReviewClips.Ffmpeg/Process/FfmpegToolset.cs:13`)
  - [ ] Encoder usability: `libx264`, `libx265`, `h264_nvenc`, `hevc_nvenc`, `h264_qsv`,
        `h264_vaapi`, `h264_videotoolbox`
  - [ ] Required filters present, via `ffmpeg -filters`: `lutyuv`, `gblur`, `zscale`,
        `setparams`, `xfade`, `vignette`, `noise`, `unsharp`, `drawtext`, `lut3d`, `scdet`,
        `blackdetect`, `freezedetect`
  - [ ] Analysis cache path, entry count, size on disk
- [ ] New `src/ReviewClips.Cli/Commands/DoctorCommand.cs`, modelled on
      `Commands/ProfilesCommand.cs` (Spectre table, `ExitCodes.Success`)
- [ ] Flags: `--clear-cache`, `--cache` (cache stats only)
- [ ] Register in the root command list, `src/ReviewClips.Cli/Program.cs:21-26`
- [ ] Tests: `ffmpeg -filters` output parser; integration test asserting `libx264` reports usable

**Do this better than pi.** pi's doctor reports encoders from `ffmpeg -encoders`, which rcc's own
comment correctly calls insufficient — NVENC is compiled into most builds and fails at runtime
without a suitable driver or with all sessions busy
(`src/ReviewClips.Ffmpeg/Encoding/FfmpegEncoderSelector.cs:12-16`). Because rcc's doctor shares
the actual probe-encode path with the selector, it reports what `generate` will really do rather
than what happens to be compiled in. Preserve that property.

---

## Phase 3 — Source-usage guardrail

Small, `Core`-only. The hard part already exists.

`RenderPlan.DistinctSourceDuration` (`src/ReviewClips.Core/Pipeline/RenderPlan.cs:32`, via
`Planning/ClipSequencer.cs:182`) is the **union** of referenced source ranges — dedup- and
overlap-aware. That is a strictly better guardrail input than pi's `SourceUsageFraction`, which
multiplies splice count by splice length and so overcounts whenever `--max-clips` is repeating a
pool. Preserve and test this distinction.

- [ ] `ClipRequest` (`src/ReviewClips.Core/Options/ClipRequest.cs`): add `MaxSourceFraction`
      (default `0.10`) and `EnforceMaxSourceFraction` (default `false`)
- [ ] `RenderPipeline.PlanAsync`: after the repeat-fill step
      (`src/ReviewClips.Core/Pipeline/RenderPipeline.cs:225`) compute
      `DistinctSourceDuration / Σ source durations`; append to `warnings`, or throw
      `RenderPlanningException` when `EnforceMaxSourceFraction`
- [ ] CLI: `--max-source-percent <n>`, `--strict-source-limit` in
      `src/ReviewClips.Cli/Cli/GenerateOptions.cs` + `ClipRequestBuilder.cs`
- [ ] Report the percentage in the plan summary — `Presentation/PlanPrinter.cs:48` already prints
      the raw seconds
- [ ] Add `sourceUsageFraction` to the manifest, next to `distinctSourceSeconds`
      (`Presentation/RenderManifest.cs:53`)
- [ ] README: document under a fair-use heading; note it pairs with `--max-clips`
- [ ] Tests: threshold boundary; strict mode throws; **repeats do not double-count**

**Default is warn, not fail** — deliberate divergence from pi, which hard-fails at 10%. pi's
default refuses ordinary short renders outright (a 30s output from a 3-minute source is 16.7% and
dies). Warning keeps existing invocations working while still surfacing the exposure; `--strict-source-limit`
is there for anyone who wants pi's behaviour.

**Exit code:** a policy refusal is not "no usable clips" (`3`). Map it to `2` (invalid arguments
or configuration) and update the exit-code table in the README, or add a dedicated code.

---

## Phase 4 — External audio

rcc's one genuine feature gap, and the largest phase — it touches all three projects. Do it
before Phase 5, which is additive and splits naturally into many small commits.

Reference: pi's
`Core/Options/AudioOptions.cs`, `Core/Rendering/TimelineAssembler.cs`, and the
`--audio` / `--match-audio` handling in `Cli/Commands/JobOptionsFactory.cs`.

### Core

- [ ] New `src/ReviewClips.Core/Options/AudioOptions.cs`: `Mode` (`Mute` / `Source` / `External`),
      `ExternalPath`, `Offset`, `Volume`, `MatchDuration`
- [ ] Make `ClipRequest.Mute` (`Options/ClipRequest.cs:52`) a computed passthrough over
      `AudioOptions` so the existing `Mute` consumers in the extractor and stitchers keep working
      without a wide refactor
- [ ] Add `Task<TimeSpan> ProbeDurationAsync(string, CancellationToken)` to `IMediaProbe`
      (`Pipeline/Abstractions.cs:9`)

  `ProbeAsync` returns a `MediaInfo` whose required fields assume a video stream, and
  `PlanAsync` drops any source reporting zero duration (`Pipeline/RenderPipeline.cs:70`), so a
  `.wav` cannot go through it. pi hit the same wall and added the same method.

- [ ] `RenderPipeline.PlanAsync`: when `MatchDuration`, derive `TargetDuration` from the audio
      **before** cadence planning — it must land ahead of `SplicePlanner.PlanForOutput`
      (`Pipeline/RenderPipeline.cs:98`)

### Ffmpeg

- [ ] Implement `ProbeDurationAsync` in `Probe/FfprobeMediaProbe.cs` using `-show_format` only
- [ ] `StitchRequest` (`Pipeline/Abstractions.cs:78`) carries `AudioOptions`
- [ ] `Stitching/ConcatDemuxerStitcher.cs` — **keep the stream-copy fast path.** Add
      `-i <audio>`, then `-map 0:v -map 1:a -c:v copy -c:a aac -shortest`. Video is still never
      re-encoded, so `--transition none` with zero fades stays free.
- [ ] `Stitching/FilterGraphStitcher.cs` — add the audio as an extra input; apply offset via
      `-ss` on that input and volume via a `volume=` filter; map it **instead of** building the
      `acrossfade` chain in `BuildAudioChains` (`FilterGraphStitcher.cs:278`), which only applies
      to per-segment source audio

### CLI

- [ ] `--audio` currently parses as a bare `bool`
      (`src/ReviewClips.Cli/Cli/GenerateOptions.cs`). Declare it `Arity.ZeroOrOne` so bare
      `--audio` keeps meaning "keep source audio" and `--audio track.wav` selects external.

  This is the only breaking CLI change in the plan, and this shape avoids it. pi uses
  `--audio <MODE|FILE>` with `mute` as an explicit mode; we cannot adopt that spelling without
  breaking existing invocations.

- [ ] Add `--audio-offset`, `--audio-volume`, `--match-audio`
- [ ] Reject `--match-audio` combined with an explicit `-d/--duration`, with a clear message
- [ ] Error clearly when the offset exceeds the audio track's own length

### Tests

- [ ] Core: target duration derived from audio; offset-longer-than-track errors
- [ ] Integration: synthesize a `sine` track in `tests/ReviewClips.Ffmpeg.Tests/Integration/FfmpegFixture.cs`,
      render, and assert output duration matches the track and an audio stream is present — on
      **both** stitcher paths

---

## Phase 5 — Missing effects, as typed stages

Add pi's genuinely-missing effects as new `IVideoFilterStage` implementations. We are **not**
porting pi's `EffectLibrary` registry or its user-ordered application model;
`FilterStageOrder` stays authoritative.

Most of pi's 24 effects are already covered in rcc under different names — check here before
implementing anything:

| pi effect | rcc equivalent |
| --- | --- |
| `crop`, `letterbox` | `--fit crop` / `--fit pad` (`FitStage`) |
| `kenburns`, `pushin` | `--zoom` (`ZoomStage`) |
| `curves`, `grade` | `--lut` (`LutStage`) — strictly more powerful |
| `flip` (horizontal) | `--mirror` (`MirrorStage`) |
| `blur`, `grain`, `vignette`, `contrast`, `saturate`, `desaturate`, `speed` | existing stages |
| `darken` | existing — **and better; see below** |

> **Do not port pi's `darken`.** pi uses `eq=brightness=-0.55*amount`, an additive offset that
> crushes dark footage to solid black. rcc uses `lutyuv=y=minval+(val-minval)*k`, which scales
> about the black pedestal and preserves shadow structure — the reasoning is in
> `src/ReviewClips.Ffmpeg/Filters/LookStages.cs:28-43`.
>
> Deterministic repro — a static dark luma ramp, `YLOW=16 YAVG=33.31 YHIGH=56`:
>
> ```bash
> ffmpeg -y -loglevel error -f lavfi \
>   -i "nullsrc=s=256x256,geq=lum='X/4':cb=128:cr=128" \
>   -frames:v 1 -pix_fmt yuv420p ramp.png
>
> stats() { ffmpeg -v error -i ramp.png -vf "$1,signalstats,metadata=print:file=-" -f null - 2>&1 \
>   | grep -E 'YLOW|YAVG|YHIGH' | sed 's/lavfi.signalstats.//' | tr '\n' ' '; echo; }
>
> # pi, darken=0.45   -> YLOW=0  YAVG=0.11   YHIGH=0
> stats "eq=brightness=-0.2475:contrast=0.919"
>
> # rcc, darken=0.45  -> YLOW=16 YAVG=25.17  YHIGH=38
> stats "lutyuv=y=minval+(val-minval)*0.55"
> ```
>
> pi flattens a 40-step dynamic range to zero. rcc compresses it to 22 steps and holds the black
> pedestal at exactly 16, which is legal limited-range black.
>
> This is not a corner case: pi's default `backdrop` look uses `darken=0.45`, and night scenes are
> everywhere in the films this tool targets. If you are ever tempted by `eq=brightness` because it
> reads more naturally, this is why not.

### New stages

Each needs a `FilterStageOrder` constant carrying a comment that explains *why* it sits there,
matching the existing house style in `Filters/IVideoFilterStage.cs:26`.

- [ ] **`AttributionStage`** @ `1350` — burn a credit / disclaimer line into a corner.
      Highest value of the five: usability plus fair-use posture. Port pi's `textfile=`
      technique (see its `EffectLibrary` `text` effect) so no user string can break the graph
      through `drawtext` escaping. Must sit after `Overlay` (1300) and before
      `OutputFormat` (9000) so it is not blurred or grained away.
- [ ] **`SharpenStage`** (`unsharp`) @ `950` — after `Look` (900), before `Blur` (1000), so an
      explicit `--blur` still dominates
- [ ] **`PixelateStage`** @ `1010`
- [ ] **`FadeEdgesStage`** @ `1150` — edge feathering, alongside `Vignette` (1100)

### Fold into existing stages

All are `eq` parameters or trivial variants; they do not warrant their own stage.

- [ ] `gamma` → extend `LookStage` (`Filters/LookStages.cs`)
- [ ] `grayscale` → extend `LookStage` as a convenience for `saturation=0`
- [ ] vertical flip → extend `MirrorStage` (`Filters/GeometryStages.cs`)

### Per-stage checklist

For **each** new stage or option, rcc's existing standard requires all five:

- [ ] `LookOptions` field (`src/ReviewClips.Core/Options/LookOptions.cs`)
- [ ] CLI option in `Cli/GenerateOptions.cs` + mapping in `Cli/ClipRequestBuilder.cs`
- [ ] `RenderProfile` support (`Profiles/RenderProfile.cs`) so profiles and the config file can set it
- [ ] Unit test in `tests/ReviewClips.Ffmpeg.Tests/Filters/VideoFilterGraphBuilderTests.cs`
- [ ] Executing test in `tests/ReviewClips.Ffmpeg.Tests/Integration/FilterGraphExecutionTests.cs`

That last one is not optional. That suite actually runs the graphs through FFmpeg, and it is the
reason rcc catches filter bugs — including the `colorlevels` segfault and the `zscale`
"no path between colorspaces" failure documented in the README.

---

## Cross-cutting, once phases land

- [ ] README: new option tables for audio, guardrail and the new look options
- [ ] README: `doctor` in the command table and its own short section
- [ ] README: exit-code table, if Phase 3 adds a code
- [ ] README **"Implementation notes"**: add entries for the non-obvious new decisions —
      why attribution writes through `textfile=`, why the concat stitcher can mux audio and stay
      a stream copy, why the guardrail measures the distinct union rather than total footage.

That section is the most valuable documentation in the repo. Keep feeding it.

---

## Explicitly out of scope

Considered and dropped for now. pi has these if they are wanted later:

- `init` — scaffolds a starter config / cue-sheet template
- `legal` — prints the copyright rundown (rcc has `LEGAL.md` instead)
- `--contact-sheet` — 4x4 JPEG grid of the finished render
- `.edl.csv` sidecar — rcc's JSON manifest already carries the same edit decision list
- pi's `EffectLibrary` registry and user-ordered effect application — rejected on purpose; see
  Phase 5
