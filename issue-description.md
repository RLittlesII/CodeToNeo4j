# Performance: Optimize Roslyn Ingestion Pipeline for Large Solutions

## Context

A real ingestion run of Microsoft.Maui.sln (13,056 files) stalled at 61% progress after 4.5 hours. Performance investigation identified four confirmed root causes in the Roslyn ingestion pipeline that scale poorly on large codebases.

## Problem Statement

The Roslyn ingestion pipeline exhibits quadratic-scale behavior, wasted work on unbuildable projects, thread-safety gaps, and excess Neo4j session churn. These issues compound on large solutions (1,000+ files), resulting in stalled or impractically slow ingestion runs that prevent the tool from being used on real-world enterprise codebases.

**Business Impact**: Current performance makes the tool unusable for production-scale .NET solutions (e.g., MAUI, Roslyn itself, large enterprise monorepos), limiting adoption and blocking the primary use case.

## Acceptance Criteria

### Priority 1: Eliminate Quadratic Global Using Resolution

**Root Cause**: `RoslynSymbolProcessor.cs` (~lines 70-98) walks every syntax tree in the compilation to resolve global usings for EVERY file processed. Repeated full AST traversal across all trees, once per file, produces O(n²) behavior on projects with thousands of files.

**Acceptance Criteria**:
- Global-using symbol resolution is cached once per project (not per file)
- Cache is populated on first file processed in a project
- All subsequent files in the same project reuse the cached result
- No full AST traversal (`tree.GetRoot().DescendantNodes()`) occurs more than once per project
- Ingestion time on large projects (1,000+ files in a single project) shows measurable reduction (target: 50%+ improvement)

**Constraints**:
- Must preserve existing symbol resolution behavior (no semantic changes to graph output)
- Cache invalidation logic must account for project boundaries (separate cache per `Project` instance)

**Impact**: CRITICAL — highest cost driver identified in the investigation

---

### Priority 2: Skip Unbuildable Projects and Target Frameworks

**Root Cause**: `MsBuildWorkspaceFactory.cs` and `SolutionProcessor.cs` attempt to process Android/iOS/Windows/Tizen target frameworks not buildable on the host platform, producing 150+ MSBuild diagnostic warnings and wasted per-file processing on broken projects.

**Acceptance Criteria**:
- CLI accepts a `--projects` or `--exclude-projects` option to scope ingestion to a buildable subset
- OR: Automatically detect and skip target frameworks/projects that fail MSBuild restore/load before entering the per-file loop
- MSBuild diagnostic warnings on unbuildable projects are surfaced to the user but do not block ingestion of buildable projects
- Progress reporting excludes skipped files from the denominator (e.g., "Processing 5,000 files" not "Processing 13,056 files" when 8,000 are unbuildable)

**Constraints**:
- Default behavior remains "process everything" (opt-in filtering)
- User must receive clear feedback on what was skipped and why

**Impact**: HIGH — reduces wasted work and clarifies progress reporting

---

### Priority 3: Fix Thread-Safety Gap in Git Metadata Cache

**Root Cause**: `GitMetadataCache.cs` uses a non-thread-safe `Dictionary<string, FileMetadata>` accessed from `Parallel.ForEachAsync` in `SolutionProcessor.cs` (~line 248). Cache miss path (`GitService.GetFileMetadata`) reads/writes the dictionary without locking, creating a race condition.

**Acceptance Criteria**:
- `GitMetadataCache` uses `ConcurrentDictionary<string, FileMetadata>` instead of `Dictionary`
- OR: Cache miss path is eliminated entirely (pre-warm covers 100% of files, miss path throws or logs a warning)
- No data races detectable under thread sanitizer or stress test (e.g., `Parallel.ForEachAsync` with degree = 32 on a 10,000-file solution run 10 times)

**Constraints**:
- Must preserve existing cache behavior (pre-warm + miss fallback)
- No observable performance regression on cache hits (ConcurrentDictionary read cost is acceptable)

**Impact**: MEDIUM — correctness issue with low probability (pre-warm should cover most cases) but high severity if triggered

---

### Priority 4: Reuse Neo4j Session Across Flush Cycle

**Root Cause**: `Neo4jFlushService.cs` opens separate sessions for `FlushFiles`, `FlushSymbols`, `FlushSymbols.tagSession`, and `UpsertDependencyUrls` — up to 3 sessions per flush cycle instead of reusing one session across the batch.

**Acceptance Criteria**:
- All writes in a single flush cycle (files, symbols, tags, dependency URLs) reuse a single Neo4j session
- Session is opened once at the start of the flush cycle, closed once at the end
- Transaction batching behavior remains unchanged (still batches by `--batch-size` equivalent)
- Neo4j connection pool churn is reduced by ~60% (measured via driver metrics or connection count logs)

**Constraints**:
- Must preserve transactional boundaries (existing batch semantics unchanged)
- Must handle partial flush failures gracefully (e.g., files succeed, symbols fail — session cleanup)

**Impact**: LOW — opportunistic optimization, measurable reduction in connection churn but not a primary bottleneck

---

## Success Metrics

- Microsoft.Maui.sln ingestion completes to 100% in under 90 minutes (down from 4.5+ hours stalled at 61%)
- Per-file processing time on large projects (1,000+ files) reduced by 50%+
- Zero thread-safety failures under stress test (10 runs, 32 threads, 10,000 files)
- Neo4j session open count reduced by 60% (measured via driver metrics)

## Out of Scope

- Roslyn Workspace API performance (Roslyn's internal semantic model caching is already O(1) and not a bottleneck)
- Neo4j query optimization (Cypher statement performance is adequate; session churn is the issue)
- Git metadata collection performance (pre-warm is already efficient; cache access is the issue)
- CLI UX improvements unrelated to performance (e.g., progress bar styling, log verbosity)

## Priority Matrix

| Finding | Business Value | Delivery Risk | Priority |
|---------|---------------|---------------|----------|
| #1: Global Using Cache | High | Low | P0 |
| #2: Skip Unbuildable | High | Medium | P1 |
| #3: Thread-Safe Cache | Medium | Low | P2 |
| #4: Session Reuse | Low | Low | P3 |

## Dependencies

- Performance baseline: Re-run Microsoft.Maui.sln ingestion with instrumentation to capture per-finding cost breakdown
- Testing infrastructure: Stress test harness for thread-safety validation (#3)

## References

- Investigation findings: Verified against source by separate architecture review
- Affected files: `RoslynSymbolProcessor.cs`, `MsBuildWorkspaceFactory.cs`, `SolutionProcessor.cs`, `GitMetadataCache.cs`, `Neo4jFlushService.cs`
