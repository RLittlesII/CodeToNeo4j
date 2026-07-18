# Issue 000 — Baseline Profiling Before Remediation (LOCAL ONLY — not filed on GitHub)

**Priority:** P0 — BLOCKING (gates Issues 001 and 004)
**Effort:** Low
**Status:** RUN — see `baseline-metrics.md`. Verdict: Issue 001's CPU-bound quadratic-traversal theory is **refuted** by observed behavior (0% CPU during multi-minute stalls, bursty not sloped progress, zero active thread-pool workers during a stall). Leading new hypothesis: I/O-bound stall, likely Neo4j-side, blocking the flush consumer and backpressuring the whole pipeline via the bounded channel. Issue 001 must be re-scoped before implementation; Issue 004 gains new relevance. Issues 002/003 unaffected, proceed as planned.

## Problem

All 4 remediation findings (Issues 001-004) were derived by reading source code, not by profiling a live run. The one hard data point available — the Microsoft.Maui.sln ingestion stalling at exactly **61%** after 4.5 hours — is a stall pattern, not a slope. A gradual O(F×T) AST-traversal cost (Issue 001's claimed root cause) would show as a widening slowdown curve, not a wall at a fixed percentage. A stall at a threshold is at least as consistent with:

- GC pressure from `Compilation` object accumulation across projects
- Git subprocess exhaustion/serialization from concurrent cache misses (Issue 003's territory)
- A deadlock or backpressure stall between `Parallel.ForEachAsync` producers and the single Neo4j-flush consumer channel
- MSBuild evaluation cost on the unbuildable multi-platform heads (Issue 002)

Per the-orkin-man's review of the groomed backlog: shipping Issue 001 as P0-critical on a complexity argument alone risks spending the highest-effort fix slot on the wrong target if the actual bottleneck is elsewhere.

## Acceptance Criteria

1. Capture a `dotnet-trace` (or equivalent CPU + GC + thread-contention profile) on a single large representative project within Microsoft.Maui.sln (e.g. Controls.Core or Compatibility.Core — both flagged as thousands-of-files, multi-broken-TFM projects in the original run log), for at least 5 minutes of steady-state ingestion.
2. Capture concurrent git subprocess count (e.g. `ps`/process-count sampling) during the same window, to confirm or refute Issue 003's subprocess-exhaustion risk.
3. Confirm or refute, with trace evidence, that AST traversal / `GetSemanticModel` calls in `RoslynSymbolProcessor.cs` are the dominant CPU consumer (Issue 001's claim) versus GC time, MSBuild evaluation, or I/O wait.
4. Record per-file and per-project processing time at three checkpoints (early progress, ~50%, ~61% — the observed stall point) to establish whether the curve is linear, quadratic, or a step function.
5. Write findings to `baseline-metrics.md` in this worktree, including raw trace file references, with an explicit verdict: does the trace confirm Issue 001 as dominant cost, or point elsewhere?

## Gating Rule

- **Issues 002 and 003 may proceed to implementation without waiting on this** — their acceptance criteria are independently falsifiable and don't depend on which root cause dominates.
- **Issues 001 and 004 are BLOCKED until this issue's AC #5 verdict is recorded.** If the trace does not confirm AST traversal as dominant, Issue 001's priority and design must be re-evaluated before implementation starts.

## Out of Scope

- Filing this as a GitHub issue — this is a local-only prerequisite tracked in this worktree, not part of the public backlog.
- Full production APM/telemetry setup — a one-time local trace capture is sufficient to unblock 001/004.

## Related Documents

- `BACKLOG.md` — updated sequencing reflects this as the first step
- `issues/001-global-using-cache.md` — blocked pending this issue's verdict
- `issues/004-neo4j-session-reuse.md` — blocked pending this issue's verdict
- the-orkin-man's review (session record) — origin of this gating requirement
