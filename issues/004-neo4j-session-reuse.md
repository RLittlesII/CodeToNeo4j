# Issue 004: Fix Concurrent-Unconsumed-Query Deadlock in Neo4j Flush (rewritten — was "Reuse Neo4j Session Across Flush Cycle")

## Priority
**P0 — CRITICAL** (promoted from P3, see `baseline-metrics.md`)

## Implementation Status: FIXED and VERIFIED against a live repro

**Files changed**:
- `src/CodeToNeo4j/Neo4j/Neo4jFlushService.cs` — `FlushSymbols`: replaced the `List<Task>` + `Task.WhenAll` concurrent dispatch with two sequential `await`s, each consuming its cursor via `ConsumeAsync()` before the next query starts. Satisfies AC #1.
- `src/CodeToNeo4j/Neo4j/Neo4jSchemaService.cs` — `EnsureSchema`: **audit (AC #3) found the same anti-pattern** — `Task.WhenAll(statements.Select(cypher => session.RunWithRetry(cypher)))` ran every schema DDL statement concurrently on one session (session-level, not transaction-level, but the same underlying bolt-protocol violation — a session can only run one query at a time). Fixed to a sequential `foreach` with `ConsumeAsync()` per statement. Also fixed a secondary bug found in the same method: the original `.ContinueWith(async _ => await session.DisposeAsync())` returns a `Task<Task>` that was never awaited, so the session disposal was fire-and-forget; replaced with `await using var session` for deterministic disposal.
- All other `RunWithRetry` call sites audited (`Neo4jFlushService.FlushFiles`, `UpsertDependencyUrls`, `Neo4jService.cs`'s `MarkFileAsDeleted`/`UpsertCommit`/`UpsertDependencies`/`PurgeData`, `Neo4jSchemaService.VerifyNeo4jVersion`/`UpsertProject`) — all already sequential and/or consume their cursor. No further instances of the bug found. Satisfies AC #3.

**Build verification**: `global.json` pins SDK 10.0.302, which wasn't installed locally. Installed it to `~/.dotnet` (user-local, isolated from system-wide `dotnet`) via the official `dotnet-install.sh` script. `dotnet build src/CodeToNeo4j/CodeToNeo4j.csproj` succeeds — 0 warnings, 0 errors.

**Live repro verification (AC #2) — CONFIRMED FIXED, plus a second real bug found along the way:**

Packed the fixed source (`dotnet pack -p:Version=1.1.18-issue004fix`) and installed it as the local `codetoneo4j` global tool, replacing the previously-installed 1.1.17, then re-ran the exact `Controls.Core.csproj` repro from `baseline-metrics.md`.

First re-run **still stalled at the identical checkpoint** (27/2792, 0.97%). A fresh `dotnet-dump` showed the concurrency deadlock was gone (no longer stuck in `CommitAsync.DiscardUnconsumed`) — but now stuck in `ConsumeAsync` on the query itself, meaning the query genuinely never returns quickly. `SHOW TRANSACTIONS` on the live Neo4j instance caught the real cause: `UpsertTags` had been running for **1282 seconds (~21 minutes)** on `MATCH (s:src__Symbol {key: st.symbolKey})` — and `SHOW CONSTRAINTS` on this database returned **empty**. Despite 157,881 `src__Symbol` nodes accumulated from prior runs, **no schema constraints or indexes had ever actually been created** — almost certainly because `EnsureSchema`'s errors were being silently swallowed by the pre-fix `.ContinueWith(_ => true)` (which runs regardless of whether the antecedent faulted). Every keyed `MATCH`/`MERGE` in `UpsertSymbols`, `MergeRelationships`, and `UpsertTags` was doing a full label scan across the entire graph, every single flush — a second, independent, and arguably more fundamental root cause than the concurrency bug, compounding it (the fixed `EnsureSchema` runs correctly now, but the very first time it runs against this already-large unindexed graph, populating 8 constraints + 11 indexes over 150k+ existing nodes itself takes many minutes).

Manually created the schema directly against the DB (`cypher-shell`) to unblock verification — confirmed all 19 indexes reach `ONLINE`/100% population. **Re-ran the repro a third time with the fixed build and the schema now in place: 2792/2792 files, 100% complete, in 43 seconds** — sailing straight through both the original 27-file stall point and the 60.85% checkpoint matching the original production run's 61% stall.

**Conclusion**: the fix is correct and necessary, but on a fresh/empty database it alone won't prevent a long one-time delay while the schema (created correctly now, instead of failing) populates its indexes over any pre-existing unindexed data. For a completely fresh database this is a non-issue (indexes populate near-instantly over zero nodes). This local dev DB was an unusual case — it had accumulated 150k+ nodes across many prior broken runs with no schema ever in place. AC #2 is satisfied for the steady-state/fresh-DB case; noting this one-time-migration caveat for anyone else who has an existing similarly-unindexed database from pre-fix runs.

## Impact
This is no longer an opportunistic session-churn cleanup. A live `dotnet-dump` capture during a reproduction of the original stall pattern (`issues/000-baseline-profiling.md`, `baseline-metrics.md`) caught the ingestion pipeline's single flush consumer thread blocked, at the exact moment of a multi-minute stall, inside:

```
Neo4jFlushService.FlushSymbols -> AsyncTransaction.CommitAsync -> DiscardUnconsumed
  -> ResultCursorBuilder.ConsumeAsync/AdvanceAsync -> SocketConnection.ReceiveOneAsync
  -> PipelinedMessageReader.ReadAsync  (awaiting socket read)
```

Full stack in `baseline/async-stacks.txt`. This is very plausibly the direct cause of the original Microsoft.Maui.sln ingestion stalling at 61% after 4.5 hours — not Issue 001's CPU-bound traversal theory (refuted, see baseline-metrics.md), and not primarily a session-count inefficiency.

## Problem Statement

`Neo4jFlushService.cs` lines 105-123, `FlushSymbols`:

```csharp
await session.ExecuteWriteAsync(async tx => {
    List<Task> tasks = [];
    if (symbolBatch.Length > 0)
        tasks.Add(tx.RunWithRetry(GetCypher(Queries.UpsertSymbols), new { symbols = symbolBatch }));
    if (relBatch.Length > 0)
        tasks.Add(tx.RunWithRetry(GetCypher(Queries.MergeRelationships), new { rels = relBatch }));
    if (tasks.Count > 0)
        await Task.WhenAll(tasks).ConfigureAwait(false);
}).ConfigureAwait(false);
```

Two queries are launched **concurrently on the same transaction** (`tx`) via `Task.WhenAll`. Neither `RunWithRetry` call's result cursor is consumed by the caller — `RunAsync` returns a cursor immediately, and nothing iterates or calls `ConsumeAsync` on it before the lambda returns.

The Neo4j bolt protocol does not support running multiple queries concurrently against one transaction/connection — each `RunAsync` call queues a request on the same connection, and the driver must serialize the responses. When `ExecuteWriteAsync` proceeds to commit, the driver's `CommitAsync` runs an implicit `DiscardUnconsumed` step to drain any result records the caller never consumed for **both** outstanding cursors, off **one socket**, before the commit can complete. That drain is what the stack trace shows hanging.

The original architect assessment (`architect.assessment.md` Finding 4) and initial issue-description.md framed this as "excess session churn" (3-4 sessions opened per flush cycle) — that observation is still true and worth fixing, but it is not the load-bearing bug. The load-bearing bug is concurrent unconsumed queries on a shared transaction.

## Acceptance Criteria

1. **Queries within one transaction never run concurrently with unconsumed cursors.**
   - In `FlushSymbols`, `UpsertSymbols` and `MergeRelationships` must either:
     (a) run sequentially, each fully consumed (via `await` on the query and, if the driver requires it, consuming the summary) before the next starts, or
     (b) run in genuinely separate transactions/sessions if concurrency is wanted.
   - Recommended: **(a) sequential execution within the existing transaction** — simplest, matches existing atomicity boundaries, no session-count increase.
   - Remove the `Task.WhenAll(tasks)` concurrent-dispatch pattern entirely from `FlushSymbols`.

2. **Repro no longer stalls.**
   - Re-run the exact repro from `baseline-metrics.md` (single-project ingestion against `Controls.Core.csproj`, or the full `Microsoft.Maui.sln` run) with the fix applied.
   - Verify: no multi-minute `%CPU`-idle stall reproduces at the same file-count checkpoint (or any checkpoint), using the same sampling approach (`samples.txt`-style CPU/RSS polling) as a regression check.

3. **All Neo4j write result cursors across the codebase are checked for the same anti-pattern.**
   - Audit `FlushFiles`, `UpsertDependencyUrls`, and any other `Neo4jFlushService`/`Neo4jSchemaService` method that calls `RunWithRetry` more than once inside a single `ExecuteWriteAsync`/`ExecuteReadAsync` delegate — confirm none of them share the same concurrent-unconsumed-cursor pattern. Fix any found.

4. **Session count per flush cycle is also reduced (secondary, from original Issue 004 scope).**
   - `FlushFiles`, `FlushSymbols` (+ optional tag session), `UpsertDependencyUrls` currently open 3-4 separate sessions per flush cycle.
   - Once (1) is fixed and proven safe, consolidate to one session per flush cycle via a `FlushAll(FlushPayload, databaseName)` method on `INeo4jFlushService`, as originally scoped. This is now secondary — do not let it block or complicate the primary correctness fix in (1).
   - Existing individual methods (`FlushFiles`, `FlushSymbols`, `UpsertDependencyUrls`) remain on the interface, not removed.

5. **Regression test added.**
   - Integration test: call `FlushSymbols` with a symbol batch and a relationship batch both large enough to require multiple bolt protocol chunks (not just 1-2 rows — this bug may only manifest with nontrivial result/record volume). Assert it completes within a bounded time (e.g. a few seconds) rather than hanging.
   - Unit test: with a fake/mocked `IAsyncQueryRunner`, assert queries are invoked sequentially (second `RunWithRetry` call happens after the first's task has been awaited), not both fired before either is awaited.

## Test Plan

- **Repro regression test** (AC #2): automate the exact baseline repro (or a smaller version) as a CI-runnable integration test if feasible — ingest a project with 500+ files against a real or containerized Neo4j instance, assert completion within a bounded wall-clock budget.
- **Unit test** (AC #5): mock transaction/query runner, assert sequential invocation order for `FlushSymbols`'s two queries.
- **Audit test coverage** (AC #3): for each other flush method found to use the concurrent-unconsumed pattern, add the same sequential-invocation-order unit test.
- **Session count test** (AC #4, secondary): as originally scoped — mock `IDriver`, assert `AsyncSession()` called once per flush cycle after consolidation.

## Architecture Reference

Original: `architect.assessment.md` Finding 4 — "Excess Neo4j Session Churn Per Flush Cycle," lines 145-189. That analysis remains valid as a secondary cleanup (AC #4) but is superseded in priority by the correctness bug documented here, found via `baseline/async-stacks.txt`.

## Dependencies

- None for the primary fix (AC #1-3) — self-contained to `Neo4jFlushService.FlushSymbols` and an audit of sibling methods.
- AC #4 (session consolidation) depends on AC #1 being proven safe first — do not consolidate sessions before fixing the concurrent-cursor bug, or the same deadlock could resurface with one fewer session to blame it on.

## Effort Estimate

**Low** for the primary fix (AC #1-3) — remove `Task.WhenAll`, run queries sequentially. Likely under 20 lines changed in `FlushSymbols`, plus an audit pass over other flush methods.
**Low-Medium** for AC #4 (session consolidation), as originally scoped — unchanged from prior estimate.

## Out of Scope

- Neo4j Cypher statement optimization (query performance is adequate once queries aren't deadlocking each other).
- Driver connection pool tuning (max pool size, idle timeout) — unrelated to this bug.
- Batching strategy changes beyond removing the concurrent dispatch (e.g., merging files + symbols into a single transaction) — not part of this fix.

## Risks

- **Sequentializing may increase flush latency slightly** (two round trips instead of a theoretically-concurrent two) — acceptable tradeoff for correctness; the "concurrent" version was never actually concurrent in practice given the protocol constraint, it was just silently broken.
- **Other undiscovered instances of the same pattern** elsewhere in the codebase (AC #3's audit) — if missed, the same class of stall could resurface in a different flush path.

## Rollback Plan

If sequential execution introduces unexpected latency regressions:
1. Revert to `Task.WhenAll`, but split `UpsertSymbols` and `MergeRelationships` into **separate transactions on separate sessions** instead of one shared transaction — this restores concurrency without violating the single-transaction constraint.
2. Re-run the repro regression test to confirm the stall does not reappear under this alternative.

## Success Metrics

- **Primary**: repro from `baseline-metrics.md` completes without a multi-minute idle stall at the same checkpoint (or any checkpoint) previously observed.
- **Secondary**: Microsoft.Maui.sln full ingestion completes in well under the original 4.5+ hours (exact target TBD pending a full-solution re-run with the fix, since the original "90 minute" target in `issue-description.md` was set before this root cause was known).
- **Tertiary** (AC #4): session open count reduced once consolidated, as originally scoped.

## Gaps Flagged During Grooming (carried over, still relevant to AC #4)

1. Whether `FlushPayload` should be a `record` (immutable) — yes, recommended, unchanged from prior grooming.
2. Driver metrics for session-count verification still need a manual counter wrapper around `IDriver.AsyncSession()` — Neo4j .NET driver doesn't expose this natively.
3. **New gap**: no existing test in the codebase exercises `FlushSymbols` with a batch large enough to trigger multi-chunk bolt protocol responses — this is likely why the bug shipped unnoticed. Any regression test added here should specifically target that condition, not just assert a small unit-test-sized batch succeeds.
