# Performance Remediation Backlog

This backlog addresses four confirmed performance root causes from the Microsoft.Maui.sln ingestion run (13,056 files, stalled at 61% after 4.5 hours). Each issue is scoped, prioritized, and mapped to the architect's remediation design in `architect.assessment.md`.

**Issue 000 has been RUN.** A live memory dump during a reproduction pinpointed the exact wait point: `Neo4jFlushService.FlushSymbols` runs two Cypher queries concurrently on one transaction with unconsumed result cursors, and the driver's implicit `DiscardUnconsumed` step during commit hangs draining them. This is a correctness bug, not Issue 001's claimed CPU-bound AST traversal (refuted) or a session-count inefficiency. See `baseline-metrics.md` and `baseline/async-stacks.txt` for the full evidence chain. Priorities below reflect this finding — Issue 004 was rewritten and promoted to P0; Issue 001 was demoted pending re-justification.

## Issue Summary

| Issue | Title | Priority | Effort | Impact |
|-------|-------|----------|--------|--------|
| [000](issues/000-baseline-profiling.md) | Baseline Profiling Before Remediation (LOCAL ONLY) | P0 — DONE | Low | Identified the actual root cause — see verdict above |
| [004](issues/004-neo4j-session-reuse.md) | Fix Concurrent-Unconsumed-Query Deadlock in Neo4j Flush | **P0 — CRITICAL** (promoted) | Low | Confirmed cause of the observed multi-minute stalls — see `baseline/async-stacks.txt` |
| [002](issues/002-skip-unbuildable-projects.md) | Skip Unbuildable Multi-Platform Target Frameworks | P1 — HIGH | Medium | Eliminates 150+ MSBuild warnings, wasted compilation on broken projects |
| [003](issues/003-thread-safe-git-cache.md) | Fix Thread-Safety Gap in Git Metadata Cache | P2 — MEDIUM | Trivial | Correctness fix — prevents rare but severe data-race corruption |
| [001](issues/001-global-using-cache.md) | Eliminate Quadratic Global-Using AST Traversal | **P3 — demoted**, not proven | Low-Medium | Not implicated by profiling evidence — re-justify before implementing |

## Sequencing Recommendation

1. **Issue 000 — done.** Baseline profiling identified the real root cause.
2. **Issue 004 first** (concurrent-unconsumed-query deadlock in `FlushSymbols`) — confirmed cause of the observed stall via live dump, low effort, fix ahead of everything else.
3. **Issue 003 next** (thread-safe git cache) — trivial effort, independent correctness fix.
4. **Issue 002 next** (skip unbuildable projects) — high UX impact, independent of the others.
5. **Issue 001 last, and only if still justified** — re-run the baseline repro after Issue 004 lands; only pursue the global-using cache if a *new*, CPU-bound stall pattern emerges. Do not implement on the original complexity argument alone.

## Success Metrics (Overall)

- Microsoft.Maui.sln ingestion completes to 100% without the multi-minute stall pattern observed in `baseline-metrics.md` (exact end-to-end time target TBD — re-baseline after Issue 004 lands, since the original "90 minutes" figure was set before the real bottleneck was known)
- Zero thread-safety failures under stress test (10 runs, 32 threads, 10,000 files) — Issue 003
- Neo4j session open count reduced (secondary goal, Issue 004 AC #4) once the primary deadlock fix is proven safe
- Per-file processing time reduction from Issue 001 — deferred pending re-justification, not currently a tracked metric

## Architecture Reference

All issues are cross-referenced to `architect.assessment.md` findings:
- Issue 001 → Finding 1 (lines 34-78)
- Issue 002 → Finding 2 (lines 80-107)
- Issue 003 → Finding 3 (lines 110-143)
- Issue 004 → Finding 4 (lines 145-189)

## Dependencies and Cross-Issue Coordination

- **No blocking dependencies** between issues — all can be developed in parallel.
- **Recommended order** (above) minimizes risk: correctness fix first, then highest-impact performance fix, then UX improvements, then opportunistic optimization.
- **Integration verification**: After all four issues are merged, run full Microsoft.Maui.sln ingestion with all fixes active to verify combined performance improvement meets the 90-minute target.

## Out of Scope (Deferred)

- Roslyn Workspace API performance (semantic model caching is already efficient)
- Neo4j Cypher query optimization (statement performance is adequate)
- Git metadata collection performance (pre-warm is already efficient)
- CLI UX improvements unrelated to performance (progress bar styling, log verbosity)
- Automatic detection of buildable target frameworks (Issue 002 is user-specified filtering only)
- Cache eviction strategy for global-using cache (Issue 001 — acceptable for single-run scope)
- `GetOrAdd` value-factory optimization for git cache (Issue 003 — follow-up perf optimization)

## Gaps and Open Questions

### Cross-Cutting Gaps

1. **No end-to-end performance baseline documented**: **RESOLVED.** Issue 000 was run — see `baseline-metrics.md` and `baseline/async-stacks.txt`. Root cause identified via live memory dump: concurrent unconsumed queries on one transaction in `Neo4jFlushService.FlushSymbols`, not Issue 001's CPU-bound theory. The original "90 minutes"/"50% reduction" success metrics are stale and should be re-set after Issue 004 lands and a fresh full-solution baseline is captured.

2. **No combined-fix verification plan**: Each issue has its own test plan, but no acceptance criterion verifies that all four fixes together deliver the overall success metrics. **RECOMMEND**: Add a final integration test to the backlog: "Run Microsoft.Maui.sln ingestion with all four fixes active, verify completion in <90 minutes and zero errors."

3. **No rollback coordination strategy**: Each issue has its own rollback plan, but what if a regression only manifests when multiple fixes are active together? **RECOMMEND**: Tag each issue with a feature-flag variable (e.g., `ENABLE_GLOBAL_USING_CACHE`, `ENABLE_GIT_CACHE_CONCURRENT_DICT`) to allow selective disable in production without code rollback.

### Issue-Specific Gaps

See "Gaps Flagged During Grooming" section at the end of each issue document:
- **Issue 001**: No cache eviction strategy, no explicit pre-warming decision (lazy vs. eager), thread-safety test not in main AC
- **Issue 002**: No acceptance criterion for "what was skipped" reporting, architect design does not implement auto-detection (gap vs. requirements), glob pattern semantics undefined, no test for "warnings surfaced but do not block ingestion"
- **Issue 003**: Requirements mention "OR: eliminate cache miss path entirely" but architect design preserves miss path (gap vs. requirements), no test for concurrent read/write safety, no guidance on `GetOrAdd` follow-up optimization
- **Issue 004**: Rewritten around confirmed root cause (concurrent unconsumed queries on one transaction, `Neo4jFlushService.FlushSymbols`). Original session-churn scope retained as secondary AC #4. New gap: no existing test exercises `FlushSymbols` with a batch large enough to trigger multi-chunk bolt protocol responses — likely why the bug shipped unnoticed; regression test must target that condition specifically.
- **Issue 001**: Demoted — not implicated by profiling evidence (CPU was idle during the observed stalls, not pegged). Re-justify with fresh profiling after Issue 004 lands before implementing.

## Related Documents

- `issue-description.md` — Original requirements document (superseded by issues/ structure)
- `architect.assessment.md` — Architectural assessment and proposed designs (authoritative reference for implementation)
- `baseline-metrics.md` — baseline profiling results, refutes Issue 001, identifies Issue 004's real bug
- `baseline/async-stacks.txt` — exact wait-point evidence (live `dotnet-dump` capture, `dumpasync` output)
- `issues/000-baseline-profiling.md` — Issue 000 detail (LOCAL ONLY, not filed on GitHub — run, verdict recorded)
- `issues/001-global-using-cache.md` — Issue 001 detail
- `issues/002-skip-unbuildable-projects.md` — Issue 002 detail
- `issues/003-thread-safe-git-cache.md` — Issue 003 detail
- `issues/004-neo4j-session-reuse.md` — Issue 004 detail
