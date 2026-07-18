# Issue 003: Fix Thread-Safety Gap in Git Metadata Cache

## Priority
**P2 — MEDIUM**

## Impact
**Correctness issue** with low probability (pre-warm should cover most cases) but **high severity** if triggered. Plain `Dictionary<string, FileMetadata>` in `GitMetadataCache` is accessed from `Parallel.ForEachAsync` without synchronization. Concurrent writes on cache miss can corrupt bucket state → silent wrong reads or thrown `InvalidOperationException`.

## Problem Statement

`GitMetadataCache` holds `private readonly Dictionary<string, FileMetadata> _cache`. `GitService.GetFileMetadata` (lines 221-254):
- Cache hit path: returns immediately (safe — read-only)
- Cache miss path: spawns a git process, then calls `metadataCache.Set(filePath, result)` (line 253) — **writing into a plain `Dictionary` from inside `Parallel.ForEachAsync`**

`SolutionProcessor` line 119: `Parallel.ForEachAsync` with degree `Math.Max(2, Environment.ProcessorCount)` calls `ProcessFile`, which calls `GitService.GetFileMetadata`.

**Thread-safety violation**: Plain `Dictionary` is not thread-safe for concurrent writes or concurrent read/write. Pre-warm makes misses rare, but MAUI's generated files, SDK-injected sources, and untracked files will still miss. Concurrent unsynchronized writes risk corrupting internal bucket state.

**Observed failure mode**: None in MAUI run (pre-warm covered ~99% of files). Latent defect — will manifest under:
- Parallel processing of solutions with many untracked/generated files
- Increased `Parallel.ForEachAsync` degree (e.g., 32+ threads on cloud VMs)
- Cache pre-warm failure or partial coverage

## Acceptance Criteria

1. **Cache backing store is thread-safe**
   - `GitMetadataCache` swaps `Dictionary<string, FileMetadata>` to `ConcurrentDictionary<string, FileMetadata>`
   - Key comparer: `StringComparer.OrdinalIgnoreCase` (preserve existing case-insensitive file-path behavior)
   - `TryGetValue`, `TryAdd`/indexer, `Clear` all use `ConcurrentDictionary` atomic operations

2. **Cache miss path is safe for concurrent writes**
   - On miss, `GitService.GetFileMetadata` line 253 uses `cache[filePath] = result` (indexer-set semantics, last-writer-wins)
   - Acceptable because all git-log outputs for the same file path are identical (idempotent)
   - Alternative: use `ConcurrentDictionary.GetOrAdd` with value factory — deferred to future optimization (avoids duplicate git spawns on concurrent misses, but adds complexity)

3. **API surface is unchanged**
   - `IGitMetadataCache` interface unchanged (only `GitMetadataCache` implementation modified)
   - `TryGet`, `Set`, `Clear`, `Count` signatures and semantics preserved
   - No DI registration changes

4. **No observable performance regression**
   - `ConcurrentDictionary` read cost on cache hit is acceptable (negligible overhead vs. plain `Dictionary` for read-heavy workloads)
   - Benchmark: 10,000-file solution with 100% cache hit rate → ingestion time within 2% of baseline

5. **Thread-safety verified under stress**
   - Stress test: `Parallel.ForEachAsync` with degree = 32 on 10,000 files across 5 runs
   - Cache pre-warm covers 90% of files (10% miss to exercise concurrent write path)
   - No data races detectable under thread sanitizer or runtime exceptions
   - Cache hit rate = 100% after warm-up (no lost writes)

## Test Plan

- **Unit test**: Mock `IGitMetadataCache` with `ConcurrentDictionary`. Spawn 100 threads, each calling `Set(filePath, metadata)` for the same key concurrently. Verify no exceptions, final value is one of the written values (last-writer-wins).
- **Integration test**: Run ingestion on a solution with 1,000 files, 10% untracked (cache miss). Set `Parallel.ForEachAsync` degree = 32. Verify cache contains 1,000 entries after completion (no lost writes).
- **Stress test**: Run the above integration test 10 times in sequence. Zero runtime exceptions or corrupted cache state across all runs.
- **Performance regression test**: Run ingestion on a 10,000-file solution with 100% cache hit rate. Compare elapsed time to baseline (plain `Dictionary`). Acceptable: <2% regression.

## Architecture Reference

See `architect.assessment.md` **Finding 3 — Non-Thread-Safe Git Metadata Cache (Medium Priority)**, lines 110-143.

Proposed design:
- Swap `Dictionary<string, FileMetadata>` to `ConcurrentDictionary<string, FileMetadata>` with `StringComparer.OrdinalIgnoreCase`
- `IGitMetadataCache` contract unchanged — fix confined to `GitMetadataCache.cs`
- On cache miss, indexer-set semantics (last-writer-wins) are acceptable since git-log outputs for the same path are identical

## Dependencies

- None. Self-contained change within `GitMetadataCache.cs`.

## Effort Estimate

**Trivial** — Single-file change, swap backing collection type. Zero API impact. ~5 lines modified.

## Out of Scope

- Optimizing cache miss path to avoid duplicate git spawns on concurrent misses (e.g., using `GetOrAdd` with async value factory). This is a follow-up optimization, not required for correctness.
- Pre-warming coverage improvements (e.g., detecting untracked files before ingestion starts). Pre-warm logic is already efficient; this issue only addresses the safety of the miss path.
- Cache eviction or memory management (cache grows unbounded but scoped to a single ingestion run — acceptable).

## Risks

- **ConcurrentDictionary memory overhead**: `ConcurrentDictionary` has higher per-entry memory cost than plain `Dictionary` (~24 bytes vs. ~16 bytes per entry on 64-bit CLR). On a 100,000-file solution, this is ~800 KB additional memory. Acceptable tradeoff for correctness.
- **Last-writer-wins semantics**: If git-log output for the same file path can differ across concurrent calls (e.g., file modified between calls), last-writer-wins may produce inconsistent metadata. **Mitigation**: git metadata is collected once at ingestion start (snapshot), not re-queried during processing. This scenario cannot occur in current design.

## Rollback Plan

If performance regression is detected (>2% slowdown on cache-hit-heavy workloads):
1. Revert `ConcurrentDictionary` to `Dictionary`
2. Add explicit locking around `cache.Set()` in `GitService.GetFileMetadata` (correctness fix without collection swap)
3. Benchmark lock contention under parallelism
4. Re-evaluate tradeoff: `ConcurrentDictionary` (no lock, higher memory) vs. `Dictionary` + lock (lower memory, potential contention)

## Success Metrics

- **Primary**: Zero thread-safety failures under stress test (10 runs, 32 threads, 10,000 files, 10% cache miss rate)
- **Secondary**: Cache hit rate = 100% after concurrent writes (no lost entries)
- **Verification**: Performance regression <2% on cache-hit-heavy workloads

## Gaps Flagged During Grooming

1. **No acceptance criterion for "cache miss path is eliminated entirely"**: Original issue-description.md AC says "OR: Cache miss path is eliminated entirely (pre-warm covers 100% of files, miss path throws or logs a warning)." Architect design does NOT implement this alternative — only swaps to `ConcurrentDictionary` and preserves miss-path fallback. This is a gap between requirements and design. **RECOMMEND**: Architect's design is correct — preserving the miss-path fallback is safer than throwing on unknown files (e.g., files added to disk after pre-warm but before processing). Close the gap by removing the "OR" clause from requirements.
2. **No explicit test for concurrent read/write safety**: AC focuses on concurrent writes, but `ConcurrentDictionary` also needs to be safe for concurrent reads during writes. Add to stress test: half the threads read (`TryGet`) while the other half write (`Set`) the same keys. Verify no exceptions or stale reads.
3. **No guidance on `GetOrAdd` value factory optimization**: Architect doc mentions "Alternative considered: use `GetOrAdd` with value factory — rejected for complexity." But this is a known optimization to avoid duplicate git spawns on concurrent misses. **RECOMMEND**: Document as a follow-up issue tagged "performance optimization" — not required for correctness but may reduce git process churn on cache-miss-heavy workloads.
