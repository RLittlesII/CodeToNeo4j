# Performance Remediation Backlog

This backlog addresses four confirmed performance root causes from the Microsoft.Maui.sln ingestion run (13,056 files, stalled at 61% after 4.5 hours). Each issue is scoped, prioritized, and mapped to the architect's remediation design in `architect.assessment.md`.

**the-orkin-man review note**: all 4 findings below were derived from code reading, not a profiler trace. The 61% stall point looks like a cliff, which is at least as consistent with GC pressure, git-subprocess exhaustion, or a producer/consumer deadlock as with Issue 001's claimed quadratic AST cost. See Issue 000 (local-only, not filed on GitHub) — Issues 001 and 004 are gated on its verdict.

## Issue Summary

| Issue | Title | Priority | Effort | Impact |
|-------|-------|----------|--------|--------|
| [000](issues/000-baseline-profiling.md) | Baseline Profiling Before Remediation (LOCAL ONLY) | P0 — BLOCKING | Low | Confirms or refutes root cause before 001/004 implementation |
| [001](issues/001-global-using-cache.md) | Eliminate Quadratic Global-Using AST Traversal | P0 — CRITICAL (blocked on 000) | Low-Medium | Eliminates dominant CPU cost — O(F × T) redundant AST walks |
| [002](issues/002-skip-unbuildable-projects.md) | Skip Unbuildable Multi-Platform Target Frameworks | P1 — HIGH | Medium | Eliminates 150+ MSBuild warnings, wasted compilation on broken projects |
| [003](issues/003-thread-safe-git-cache.md) | Fix Thread-Safety Gap in Git Metadata Cache | P2 — MEDIUM | Trivial | Correctness fix — prevents rare but severe data-race corruption |
| [004](issues/004-neo4j-session-reuse.md) | Reuse Neo4j Session Across Flush Cycle | P3 — LOW (blocked on 000) | Low-Medium | Opportunistic optimization — 60% reduction in session churn |

## Sequencing Recommendation

1. **Issue 000 first** (baseline profiling, local only) — confirms or refutes which finding actually dominates the 61% stall before spending implementation effort.
2. **Issue 003 next** (thread-safe git cache) — trivial effort, correctness fix, unblocks safe parallelism increases, does not depend on Issue 000's verdict.
3. **Issue 002 next** (skip unbuildable projects) — high UX impact (progress clarity, warning reduction), medium effort, does not depend on Issue 000's verdict.
4. **Issue 001** (global-using cache) — BLOCKED until Issue 000's trace confirms AST traversal as dominant cost. If refuted, re-scope before implementing.
5. **Issue 004 last** (Neo4j session reuse) — BLOCKED until Issue 000's trace rules out producer/consumer deadlock as the actual stall cause; otherwise low priority, opportunistic optimization.

## Success Metrics (Overall)

- Microsoft.Maui.sln ingestion completes to 100% in under 90 minutes (down from 4.5+ hours stalled at 61%)
- Per-file processing time on large projects (1,000+ files) reduced by 50%+
- Zero thread-safety failures under stress test (10 runs, 32 threads, 10,000 files)
- Neo4j session open count reduced by 60% (measured via driver metrics)

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

1. **No end-to-end performance baseline documented**: Success metrics reference "90 minutes" and "50% reduction" but no baseline numbers are recorded for current performance (e.g., per-file processing time on a 1,000-file project before fixes). **RESOLVED BY**: Issue 000 (local only, not filed on GitHub) — gates Issues 001 and 004 until a profiler trace confirms root cause.

2. **No combined-fix verification plan**: Each issue has its own test plan, but no acceptance criterion verifies that all four fixes together deliver the overall success metrics. **RECOMMEND**: Add a final integration test to the backlog: "Run Microsoft.Maui.sln ingestion with all four fixes active, verify completion in <90 minutes and zero errors."

3. **No rollback coordination strategy**: Each issue has its own rollback plan, but what if a regression only manifests when multiple fixes are active together? **RECOMMEND**: Tag each issue with a feature-flag variable (e.g., `ENABLE_GLOBAL_USING_CACHE`, `ENABLE_GIT_CACHE_CONCURRENT_DICT`) to allow selective disable in production without code rollback.

### Issue-Specific Gaps

See "Gaps Flagged During Grooming" section at the end of each issue document:
- **Issue 001**: No cache eviction strategy, no explicit pre-warming decision (lazy vs. eager), thread-safety test not in main AC
- **Issue 002**: No acceptance criterion for "what was skipped" reporting, architect design does not implement auto-detection (gap vs. requirements), glob pattern semantics undefined, no test for "warnings surfaced but do not block ingestion"
- **Issue 003**: Requirements mention "OR: eliminate cache miss path entirely" but architect design preserves miss path (gap vs. requirements), no test for concurrent read/write safety, no guidance on `GetOrAdd` follow-up optimization
- **Issue 004**: Transaction scope ambiguity (one transaction vs. sequential transactions within one session), no acceptance criterion for partial flush failure handling (added during grooming), no guidance on driver metrics collection (counter instrumentation needed), unclear whether `FlushPayload` should be immutable

## Related Documents

- `issue-description.md` — Original requirements document (superseded by issues/ structure)
- `architect.assessment.md` — Architectural assessment and proposed designs (authoritative reference for implementation)
- `issues/000-baseline-profiling.md` — Issue 000 detail (LOCAL ONLY, not filed on GitHub — blocks 001/004)
- `issues/001-global-using-cache.md` — Issue 001 detail
- `issues/002-skip-unbuildable-projects.md` — Issue 002 detail
- `issues/003-thread-safe-git-cache.md` — Issue 003 detail
- `issues/004-neo4j-session-reuse.md` — Issue 004 detail
