# Issue 004: Reuse Neo4j Session Across Flush Cycle

## Priority
**P3 — LOW**

## Impact
Opportunistic optimization. Reduces Neo4j connection pool churn by ~60% (measured via driver metrics or connection count logs). Not a primary bottleneck, but measurable efficiency gain on long-running ingestion with frequent flushes. Improves semantic coherence: one flush cycle (delete-prior-symbols, upsert-file, upsert-symbols, merge-rels, upsert-tags, upsert-url-nodes) is one logical operation that should run in one session context.

## Problem Statement

`SolutionProcessor.FlushBuffers` calls `FlushFiles`, `FlushSymbols`, `UpsertDependencyUrls` sequentially. Each opens its own `await using var session = driver.AsyncSession(...)`. `FlushSymbols` conditionally opens a **second** session for tag writes (line 128).

**Per flush cycle**: 3-4 sessions opened.

Driver pools connections internally, so cost is checkout/return overhead, not full TCP handshake — but on a long run with frequent flushes (e.g., every 500 files across 13,056 files = ~26 flush cycles), measurable and unnecessary.

**Semantic issue**: One flush cycle is conceptually one atomic unit of work (delete old symbols, upsert new files/symbols, merge relationships, upsert tags, upsert dependency URLs). Breaking it across 3-4 separate sessions obscures this logical boundary and increases connection churn.

## Acceptance Criteria

1. **All writes in a flush cycle reuse one session**
   - Add `FlushAll` method to `INeo4jFlushService` accepting all four payload types: files, symbols, relationships, urlNodes
   - `FlushAll` opens one Neo4j session at the start, executes all writes within that session, closes at the end
   - `SolutionProcessor.FlushBuffers` builds a `FlushPayload` record and calls `graphService.FlushAll(payload, databaseName)`

2. **Transaction batching behavior is unchanged**
   - Existing `--batch-size` semantics preserved (still batches by configured chunk size within a flush cycle)
   - Each write operation (files, symbols, rels, tags, URLs) runs in its own transaction **within the same session** (not across separate sessions)
   - Transactional boundaries remain the same (e.g., files + symbols are not merged into one transaction unless that was already the case)

3. **Session lifecycle is predictable**
   - Session opened once at `FlushAll` entry
   - Session closed once at `FlushAll` exit (via `await using`)
   - On partial flush failure (e.g., files succeed, symbols throw), session is disposed cleanly (no leaked connections)

4. **Existing methods remain on the interface**
   - `INeo4jFlushService.FlushFiles`, `FlushSymbols`, `UpsertDependencyUrls` are **not removed** (backward-compatible)
   - Independent callers (e.g., dependency ingestor, future incremental-update flows) can still call individual methods
   - `FlushAll` is an additive convenience method, not a replacement

5. **API surface is backward-compatible**
   - `INeo4jFlushService` gains `FlushAll(FlushPayload payload, string databaseName) : Task`
   - New `FlushPayload` record: `{ IReadOnlyList<FileNode> Files, IReadOnlyList<SymbolNode> Symbols, IReadOnlyList<Relationship> Relationships, IReadOnlyList<DependencyUrlNode> UrlNodes }`
   - `SolutionProcessor.FlushBuffers` is updated to call `FlushAll` instead of individual methods
   - Existing callers of individual methods are unaffected

6. **Session churn is measurably reduced**
   - Neo4j driver metrics (or connection count logs) show **60%+ reduction** in session open count on a long ingestion run (e.g., MAUI 13,056 files = ~26 flush cycles → 26 sessions opened instead of 78-104)
   - Baseline: 3-4 sessions per flush cycle. Target: 1 session per flush cycle.

## Test Plan

- **Unit test**: Mock `IDriver` and `IAsyncSession`. Call `FlushAll` with non-empty payloads for all four types. Verify `AsyncSession()` is called **once**, all four write operations execute within that session, session is disposed.
- **Integration test**: Run ingestion on a solution with 1,000 files (triggering 2-3 flush cycles). Instrument Neo4j driver to count session opens. Verify session count = flush cycle count (not 3x or 4x).
- **Partial failure test**: Mock `FlushSymbols` to throw after `FlushFiles` succeeds. Verify session is disposed cleanly, no leaked connections in driver pool.
- **Backward-compatibility test**: Call `INeo4jFlushService.FlushFiles()` directly (not via `FlushAll`). Verify it still opens its own session and executes successfully (existing callers unaffected).

## Architecture Reference

See `architect.assessment.md` **Finding 4 — Excess Neo4j Session Churn Per Flush Cycle (Lower Priority)**, lines 145-189.

Proposed design:
- Add `FlushAll` to `INeo4jFlushService` accepting `FlushPayload` record (files, symbols, rels, urlNodes)
- `FlushAll` opens one session, runs all writes within a single `ExecuteWriteAsync` transaction scope (or sequentially within the session, per atomicity needs), closes
- Existing three methods remain on the interface for independent callers (not removed)
- `SolutionProcessor.FlushBuffers` builds `FlushPayload` and calls `graphService.FlushAll(payload, databaseName)`

Alternative considered: pass `IAsyncSession` into each method — rejected, inverts session-lifetime ownership onto callers and leaks infrastructure concerns across the interface boundary.

## Dependencies

- None. Self-contained change within `Neo4jFlushService` and `SolutionProcessor.FlushBuffers`.

## Effort Estimate

**Low-Medium** — Additive interface change + new `FlushPayload` record + updated `FlushBuffers` implementation. No structural refactor. ~50 lines of new code, ~10 lines modified in `FlushBuffers`.

## Out of Scope

- Neo4j Cypher statement optimization (query performance is adequate; session churn is the issue here, not query cost)
- Batching strategy changes (e.g., merging files + symbols into a single transaction instead of separate transactions within the same session). Current transactional boundaries are preserved.
- Driver connection pool tuning (e.g., max pool size, idle timeout). Session reuse reduces churn but does not change pool configuration.

## Risks

- **Partial flush failure + rolled-back state**: If `FlushFiles` succeeds but `FlushSymbols` throws, files are already committed (separate transaction). Session disposal does not roll back committed transactions. This is **existing behavior** — not a new risk introduced by session reuse. Document that `FlushAll` preserves current transactional boundaries (each write is atomic, but the full cycle is not one transaction).
- **Transaction scope ambiguity**: Architect doc says "within a single `ExecuteWriteAsync` transaction scope (or sequentially within the session, per atomicity needs)." Which one? **RECOMMEND**: Sequential transactions within the same session (preserve current atomicity boundaries). Merging into one transaction is a separate change with different failure semantics.

## Rollback Plan

If session reuse introduces transactional correctness issues (e.g., unintended cross-write isolation):
1. Revert `SolutionProcessor.FlushBuffers` to call individual `FlushFiles`, `FlushSymbols`, `UpsertDependencyUrls` methods
2. Remove `FlushAll` method from `INeo4jFlushService` (or mark as obsolete)
3. Re-run integration tests to confirm flush behavior matches baseline
4. Root-cause the transaction-scope issue (e.g., unintended read-your-writes visibility) before re-attempting

## Success Metrics

- **Primary**: Neo4j session open count reduced by 60%+ on a long ingestion run (e.g., MAUI 26 flush cycles → 26 sessions instead of 78-104)
- **Secondary**: No observable change to graph output (files, symbols, relationships, tags, dependency URLs identical to baseline)
- **Verification**: Partial flush failure test passes (session disposed cleanly, no leaked connections)

## Gaps Flagged During Grooming

1. **Transaction scope ambiguity**: Architect doc does not specify whether `FlushAll` should wrap all writes in **one transaction** or keep them as **separate transactions within the same session**. Original issue-description.md says "Transaction batching behavior remains unchanged (still batches by `--batch-size` equivalent)" → implies separate transactions (preserve current boundaries). But architect doc says "single `ExecuteWriteAsync` transaction scope (or sequentially within the session, per atomicity needs)" → two options, no clear choice. **RECOMMEND**: Clarify before implementation. Likely answer: sequential transactions (safer, preserves current semantics).
2. **No acceptance criterion for "partial flush failures gracefully"**: Original issue-description.md constraint says "Must handle partial flush failures gracefully (e.g., files succeed, symbols fail — session cleanup)." This is stated as a constraint but not tested in the acceptance criteria. Added as AC #3 and to the test plan (partial failure test).
3. **No guidance on driver metrics collection**: AC #6 says "measured via driver metrics or connection count logs" — but how? Neo4j .NET driver does not expose session-open-count metrics out of the box. **RECOMMEND**: Add instrumentation wrapper around `IDriver.AsyncSession()` that logs or increments a counter on each call. Verify in integration test via this counter, not external driver metrics (which may not exist).
4. **Unclear whether `FlushPayload` should be immutable**: Architect diagram shows `FlushPayload` as a record (immutable by default in C#). Confirm: should it be a `record` (immutable) or a `class` (mutable)? **RECOMMEND**: `record` (immutable) — flush payload is a snapshot of buffered data at a point in time, should not be mutated after construction.
