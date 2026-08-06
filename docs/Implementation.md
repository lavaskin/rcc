# Specific Implementation Notes

Design decisions that might not be readily apparent, documented here for further record keeping.

## Notes

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

**Cue renders solve that arithmetic separately, in closed form.** A cue list fixes the clip count
up front, so there is nothing to iterate towards: `SplicePlanner.PlanEqualForOutput` divides
`target + transition * (N-1)` by N directly, re-solving against the clamped form when the
transition exceeds half a clip. It exists because the budget from `PlanForOutput` is only valid for
the clip count that method arrived at. Spending it over a cue count instead pays back a different
number of overlaps — three cues consuming a budget planned for twenty-six clips keep the material
for twenty-three transitions they never perform, overshooting the request by 8%.

**Segments are cut to an exact frame count, not to `-t`.** `-ss`/`-t` bound which *source*
timestamps are read and the `fps` filter then resamples that window onto the output rate; both
round outwards, so a segment reliably comes out one to two frames long. The bias is invisible per
clip and cumulative across a render: the concat stitcher sums actual segment lengths, and 119 slots
of it reached five seconds of overshoot on a ten-minute request. The filter-graph stitcher is hurt
differently but no less, since it schedules `xfade` offsets from the *planned* lengths. `-frames:v`
caps each segment at `round(duration * fps)`, which is always fewer frames than the read window
supplies, so nothing is ever short.

**A zero exit code from FFmpeg is not proof it encoded anything.** A filter graph that yields no
frames produces "Output file is empty, nothing was encoded" on stderr, a syntactically valid but
stream-less container, and exit code 0. The empty file then travels downstream and fails somewhere
that cannot explain itself — typically as `Stream specifier ':v' matches no streams` from a
stitcher three stages later. `RunFfmpegCheckedAsync(requireFrames: true)` reads the frame count
FFmpeg already reports through `-progress` and fails at the step that actually went wrong, at no
extra cost.

**An empty selection is explained by measurement, not by listing suspects.**
`EligibilityDiagnostics` relaxes each filter in turn and recomputes the eligible footage through
the real `SelectionContext.EligibleRanges`; whichever relaxation restores footage is the one that
removed it. Enumerating whatever happened to be enabled is easier and misleads: black and frozen
rejection are on by default, so a render refused because `--range` named a two-second window was
being advised to try `--no-reject-black`, which costs an analysis pass and changes nothing. Going
through the real code path rather than reimplementing the subtractions is what keeps the
explanation from drifting away from the behaviour it explains.

**Attribution text is passed through `drawtext`'s `textfile=`, never `text=`.** A film title is
precisely the kind of string that breaks FFmpeg's filter grammar: `Leon: The Professional` has a
colon, `Ocean's Eleven` an apostrophe, and either terminates the filter argument early and fails
the entire graph. Escaping correctly requires three simultaneous levels of quoting, and getting it
wrong turns a caption into a failed render. Reading the text from a file removes the problem
rather than managing it, so no user string can break a graph. `expansion=none` accompanies it,
because `%` and `{}` in a title are otherwise read as strftime and expression syntax. The file is
named by a hash of its own contents, so parallel extractions of one render share it without
coordination and cannot collide across renders.

**The concat stitcher muxes external audio and stays a stream copy.** Adding a second input with
`-map 0:v -map 1:a -c:v copy -c:a aac` touches only the audio, so the fast path survives: a
transition-free render with an audio bed still completes in about a second regardless of runtime.
The audio input is seeked with `-ss` before `-i` rather than trimmed afterwards, keeping it to a
single pass, and `-shortest` stops a long track padding the output with frozen video.

**`--match-audio` derives the target duration before cadence planning.** The number and length of
the splices are computed from the target, so deriving the target afterwards would size the render
against the wrong number and produce a fraction of the intended length. The resolved request is
rebound rather than a local threaded through, so the plan summary and the manifest report the
duration the render actually has.

**`IMediaProbe` has a separate `ProbeDurationAsync`.** `MediaInfo` requires a video stream — width,
height, frame rate and pixel format are all non-nullable — and `PlanAsync` discards any source
reporting zero duration, so an audio-only file cannot go through `ProbeAsync` at all. Reading the
container duration with `-show_format` alone is what `--match-audio` needs and all it needs.

**The source-usage guardrail measures the distinct union per source, not total footage.** The union
of the referenced ranges counts repeats and overlaps once. Multiplying splice count by splice
length is easier and wrong: with `--max-clips` repeating a pool it overstates consumption
several-fold, and a strict limit computed that way refuses renders that touch a twentieth of a
feature. The limit is tested against the largest share taken from any one source rather than
against the pool, because an aggregate can be diluted to nothing by adding footage the render never
touches, and because the question the guardrail exists to answer is about a work rather than about
a collection. The guardrail warns by default rather than failing, since a hard limit at 10% would
refuse a 30-second render cut from a three-minute source.

**`doctor` shares the encoder probe with the selector.** Both go through `IEncoderProbe`, which
decides usability by encoding two frames and caching the verdict. Reading `ffmpeg -encoders`
instead would report NVENC as available on any machine whose FFmpeg was built with it, including
those with no GPU, and the diagnostic would then disagree with the renderer it exists to describe.
The `drawtext` check works the same way, and for a sharper reason: rcc deliberately pins no
`fontfile` so captions match the system font, which means the filter can be compiled in and still
fail on an image that has libfreetype and no fonts.

**`doctor` reports filters in two tiers.** A filter a default render cannot avoid is fatal; one
that only gates an opt-in flag is a limitation named against that flag. Treating them alike meant
an FFmpeg built without libzimg — the common case on Alpine and on minimal container images —
reported itself unusable despite rendering every SDR job perfectly, which sends people chasing a
rebuild they do not need. The same reasoning already applied to encoders, where only `libx264` is
required.

**Clips are normalized at extraction.** Identical geometry, frame rate, pixel format, GOP, and
timebase are what permit the stream-copy stitch path. Anamorphic sources are corrected to square
pixels before any aspect-ratio arithmetic, and variable frame rate sources are forced to CFR.
