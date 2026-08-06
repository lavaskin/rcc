# Fix plan: duration accuracy, `--speed` placement, shortfall reporting

Four bugs and five nits found reviewing `improvemements`. Five commits, each green.

Measurements below come from a sweep over targets 10-600s x 20 seeds, and from end-to-end
renders against a synthetic 300s source.

---

## 1. Transition-aware minimum segment

`SplicePlanner.Plan` can emit a final runt clip as short as `MinimumSegment` (0.75s).
`EffectiveTransition` clamps to half the *shortest* clip, so one runt collapses overlap
compensation for the whole render and the fixed-point loop then oscillates — its variable is
named `best` but only ever holds the last iterate.

| config | runs off by >0.5s | worst |
| --- | --- | --- |
| default (splice 5, jitter 1, T 0.4) | 0.4% | -2.4s @ 566s |
| `--profile subtle` (T 0.6) | 9.8% | +26.8s @ 595s |
| `--profile kenburns` (splice 7, T 0.8) | 11.9% | +36.4s @ 577s |
| splice 3, jitter 0, T 1.0 | 72.8% | +148s @ 598s |

Repro: `rcc generate -d 30s --splice 3s --splice-jitter 0 --transition dissolve
--transition-duration 1s` produces a 36.0s file.

**Invariant.** If every clip is at least `2 x transition`, `EffectiveTransition == transition`
unconditionally, the predicted output stops depending on the jitter draw, and the iteration
becomes a contraction. Measured 0.0% error across six configurations including `2T > splice`.
Tracking the genuinely-best iterate instead does *not* help — the cycle contains no good member.

- `Planning/SplicePlanner.cs` — add optional `TimeSpan? minimumSegment` to `Plan`, used at both
  the `maxJitter` bound and the runt fold-in. `PlanForOutput` passes
  `Max(MinimumSegment, 2 x transition)`. Add `internal static MinimumSegmentFor(TimeSpan)` so
  every caller derives the floor identically. Keep the public `MinimumSegment` property.
- `Planning/ClipSequencer.cs` — same defect on the `--max-clips` path (`Fill`, and the same
  `best`-is-`last` loop in `FillForOutput`). Confirmed live: `-d 120s --max-clips 8
  --profile kenburns` undershoots on seeds 1 and 2.
- `Cli/ClipRequestBuilder.cs` — `ShortestSegment` floors at `MinimumSegment`; switch it to
  `MinimumSegmentFor(transition)` or the `--fade-edges` bound drifts. Update its doc comment.

**Tests.** Replace `PlanForOutputTests.CompensatesSoTheRenderedRuntimeMatchesTheRequest` — five
hardcoded tuples at seed 42 that all happen to land, including `(300, 7, 0.8)` which fails on
other seeds. This is why manual testing missed the bug. Make it a sweep over
`{(5,1,0.4), (5,1,0.6), (7,1,0.8), (3,0,1.0), (2,0.5,1.0), (5,1,2.0)}` x targets x seeds 1-20,
asserting within 0.25s. Same for `FillForOutput`. Pin the 30s repro.

---

## 2. Warn when the render misses `--duration`

`-d 300s --cues 30,120,240` on a 300s source yields 259.7s, exit 0, no warning. The plan is
honest (`Target | 300s -> 259.7s`) but nothing flags it. The existing shortfall warning
(`RenderPipeline.cs:254`) is keyed on segment *count* and excludes cues, so it cannot fire here.

- Lift `RenderPlan.EffectiveDuration`'s body to
  `internal static SplicePlanner.RenderedDuration(durations, transition)`; have the property
  delegate. One implementation, callable mid-plan.
- `Pipeline/RenderPipeline.cs` — after the usage guard, warn when
  `|rendered - target| > max(0.5s, 1%)`, in either direction, for every strategy. Name both
  figures and the likely cause. Suppress when the count-based warning already fired.

Tolerance: the `-frames:v` rounding residual is ~±0.05s and does not compound (+1 frame on both
a 12-segment 60s and an 18-segment 90s render), so 0.5s is clear of noise.

**Tests.** Via the existing `PipelineHarness`: clamped cue render warns; normal render does not;
the double-warning case reports once; inside tolerance stays silent.

---

## 3. Make `--speed` visible to clip placement

The selector reserves `Duration` seconds of source; `FfmpegSegmentExtractor.cs:66` reads
`Duration x Speed`. Nothing reconciles them. Measured at `--speed 2 --splice 4s`:

- a clip near the source end extracted 3.03s instead of 4.00s (91 frames against 120);
- a clip at 135.10s read to 143.10s under `--range 100-140`;
- clips at 123.04 and 128.08 overlapped by 2.96s despite `--min-gap 5s`, breaking the documented
  "clips never overlap" invariant;
- `distinctSourceSeconds` reported 22s for a render that read 44s — the guardrail under-reports
  by the speed factor.

The default `--skip-tail 8%` masks the overrun; `--skip-tail 0`, which the README recommends for
chaptered sources, removes the mask entirely.

- `Selection/Segment.cs` — add `double SpeedFactor = 1d`, `ReadDuration => Duration * SpeedFactor`,
  and make `Range` the read range. `Duration` keeps meaning output length.
  **Check `Segment.End`'s callers first** — if anything uses it for output-timeline arithmetic,
  it must not follow `ReadDuration`.
- `Selection/SelectionContext.cs` — add `SpeedFactor = 1d`; `LongestSegment` multiplies by it.
  That one line reaches everything that was wrong: `EligibleRanges`, `CanFit`, and the
  never-relaxed non-overlap floor `preferredGap = window`.
- `Selection/SegmentSelectorBase.cs` — `Materialize` sets `SpeedFactor` from the context.
- `Selection/CueDrivenSegmentSelector.cs` — source-end clamp becomes
  `min(perCue, available / speed)`; set `SpeedFactor`.
- `Pipeline/RenderPipeline.cs` — set `SpeedFactor = request.Look.Speed` on the context. Single writer.
- `Extraction/FfmpegSegmentExtractor.cs` — read `segment.ReadDuration` instead of recomputing.
- `Selection/EligibilityDiagnostics.cs` — picks the fix up automatically, but its "no source is
  long enough for a Xs clip" message will quote the read window; reword when `SpeedFactor != 1`.
- `SourceUsageGuard` and `ClipSequencer` union `s.Range`, so both become correct for free.
  Assert it rather than trusting it.

**Behaviour change.** Constrained sources place fewer clips under `--speed > 1` and warn via
step 2, rather than silently truncating. Add a README note under `--speed`.

**Tests.** Core: at `SpeedFactor = 2` no `Range` crosses `--exclude`, a skipped chapter, or the
end of `--range`; no two overlap; usage reports `2 x sum(Duration)`. Integration: a `Speed = 2.0`
extraction near a source end hits the planned frame count (fails today).
`SegmentLengthTests.ASpeedChangedSegmentIsStillCutToTheOutputLength` must set `SpeedFactor = 0.5`
— it uses a *shorter* read window, which is why the suite never caught the overrun.

---

## 4. Floor `PlanEqualForOutput`

`PlanForOutput` respects `MinimumSegment`; its new sibling does not. `-d 5s` with 15 cues yields
0.6s clips and nothing stops it going sub-frame.

Clamp each clip to `MinimumSegmentFor(transition)`. Do *not* drop cues to compensate — silently
ignoring a typed timestamp is worse than overshooting, and step 2 reports the overshoot.

---

## 5. Nits

| | File | Change |
| --- | --- | --- |
| L2 | `GenerateCommand` / `BatchCommand` | Fire-and-forget `TextResources.Sweep()` after a render using `--attribution`. Reachable today only from `doctor --clear-cache`, so the class doc's "keeps it from growing without bound" is false. Fix the comment either way. |
| L3 | `Diagnostics/EnvironmentReport.cs:645` | Wrap `InspectTextRenderingAsync`'s ffmpeg call in `WithTimeoutAsync`. The only unbounded probe; `doctor` bounds everything else to stay responsive on a broken machine, and a first-run fontconfig cache build is exactly what hangs. |
| L4 | `Pipeline/RenderPlan.cs:32` | `DistinctSourceDuration => SourceUsage.Used`. Delete `ClipSequencer.DistinctSourceDuration`, move its three tests to `SourceUsageGuard`. Removes a duplicate union that must stay in step with the first — more so once step 3 changes `Range`. |
| L5 | `Cli/ClipRequestBuilder.cs:139` | Evaluate `Toggle(Grayscale, NoGrayscale, ...)` before the `--saturation` override, so `--grayscale --no-grayscale --saturation 0.5` is refused like every other contradictory pair. |
| L6 | `Primitives/Offset.cs:31` | `IsZero` is false for `default(Offset)` (both fields null). Unreachable from the CLI but `EligibilityDiagnostics` depends on it. |

---

## Verification

1. `dotnet build` + `RCC_REQUIRE_FFMPEG=1 dotnet test` (660 tests today).
2. Re-run the H1 sweep against fixed `SplicePlanner` and `ClipSequencer`: expect 0.0%.
3. End-to-end duration matrix on a synthetic 300s source, output within ±0.2s of request:
   - `-d 30s --splice 3s --splice-jitter 0 --transition dissolve --transition-duration 1s`
   - `--profile subtle` and `--profile kenburns` at 90s / 300s / 595s, several seeds
   - `-d 120s --max-clips 8 --profile kenburns`, seeds 1-5
   - `-d 300s --cues 30,120,240` — still 259.7s, but now warns
4. Confirm unbroken: external audio on both stitch paths, `--match-audio`, attribution with
   reserved characters, exit codes 2/3/4, `-frames:v` frame counts.

---

## Out of scope

Filed, not fixed: `Validate()` running before `--match-audio` settles the duration (L1);
`ScratchPaths.EnsureDirectory` following symlinks (L7); `EnsureDirectory` silently skipping
tightening off-root (L8); `batch` ignoring the user's `--manifest` path (L9).
