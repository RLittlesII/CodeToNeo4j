# Baseline Profiling Results — Issue 000

Local only, not filed on GitHub. Run date/time: 2026-07-18, ~17:28-17:40 local.

## Setup

- Target: `Controls.Core.csproj` (single project, not full `.sln`) — 2792 files discovered (pulls in Core, Graphics, Essentials, BindingSourceGen via project references, several of which the workspace flags as broken — same "Project does not contain 'Compile' target" warnings seen in the original full-solution run).
- Tooling: `dotnet-trace collect --process-id <pid>` attached mid-run (launch-mode `dotnet-trace collect -- codetoneo4j ...` failed twice with exit code 1 — root cause not diagnosed, worked around via attach-to-live-PID instead).
- Sampling: custom poll loop every 5s recording `%CPU`, `RSS`, and live git subprocess count (`pgrep -f "git "`) against the running PID, plus a one-off macOS `sample` stack capture during an active stall.
- Trace artifacts in this directory: `controls-core-attach.nettrace` (33.6MB, ~9 min live capture, kept locally only — not committed, too large for git history) and `controls-core-trace.nettrace` (4MB, from the failed launch-mode attempt, also local-only), `samples.txt` (raw CPU/RSS/git-proc samples), `stackSample.txt` (native stack sample during stall), `direct-run.txt` (full tool output).

## Observed Behavior — NOT what Issue 001 predicted

Issue 001 claims the dominant cost is CPU-bound O(files × trees) AST traversal — i.e. a gradually worsening slope as file count grows. **That is not what was observed.**

Actual pattern: **bursty, not sloped.**

- Files 1-28 processed near-instantly (17:28:26, all same timestamp).
- Then a **4m42s gap with zero progress** (17:28:26 → 17:33:08).
- Files 29-93 processed near-instantly in a burst (17:33:08 / 17:36:34, again same-timestamp clusters).
- Then another stall, still at 93/2792 after **3+ more minutes with zero progress**, at which point the run was terminated for this baseline (11 min elapsed, 3.33% complete).

This is a **burst-then-freeze pattern**, not a smoothly worsening quadratic curve. A quadratic AST-traversal cost would show every file getting incrementally slower — it would not show large batches completing in milliseconds followed by multi-minute dead air with the file count going nowhere.

## CPU/Memory During the Stalls

`samples.txt` — 82 samples across the first stall window (5s interval, ~282s / 4m42s span):

- **`%CPU` was 0.0% for all but one sample** (one spike to 133.7% for a single 5s window, likely a brief GC or JIT burst).
- RSS mostly flat or slowly drifting (280-365MB), with a couple of drops to as low as 58MB then climbing back — inconsistent with a busy AST-walk (which would hold RSS steady/climbing while pinning CPU, not idle at 0%).
- Git subprocess count was 0 or 1 (single short-lived spawn) for nearly the entire window — ruling out Issue 003's concurrent-git-spawn theory as the cause of *this* delay (it may still be a real bug, just not what's producing this stall).
- **No MSBuild/dotnet child processes were spawned by the ingestion process** during the stall (checked via `ps --ppid`) — rules out a lazy per-file MSBuild re-evaluation of referenced broken projects as the cause.

## Stack Sample During Active Stall

A 3-second macOS `sample` capture (`stackSample.txt`) taken while the process was stalled at 93/2792 shows **only 12 threads total, all runtime-infrastructure threads** (`.NET SynchManager`, `.NET EventPipe`, `.NET DebugPipe`, `.NET Debugger`, `.NET SigHandler`, `.NET Timer`, `.NET TP Gate`, `.NET Sockets`, main thread). **Zero `.NET TP Worker` threads were present** — meaning there was no active managed work running or queued on the thread pool at all during the stall. `sample` cannot resolve JIT'd managed frames, so we can't see the exact await point, but the thread-pool-empty result combined with a live `.NET Sockets` thread is most consistent with the whole pipeline blocked on a single outstanding async socket operation — most likely a Neo4j bolt-protocol round trip that is slow or hung, not a CPU-bound computation.

Checked `Neo4j/Neo4jExtensions.cs` retry policy: base delay is 10ms exponential backoff, max 5 attempts — this cannot produce multi-minute waits on its own, ruling out retry-backoff as the explanation.

## Verdict Against Issue 000's Gating Question

**Refuted (partially):** Issue 001's claimed root cause (CPU-bound quadratic AST traversal) does not match observed behavior. CPU was idle, not pegged, during the multi-minute stalls. The AST-traversal cost described in `RoslynSymbolProcessor.cs` lines 70-98 may still be a real inefficiency, but it is very unlikely to be *the* cause of the multi-hour full-solution stall — that traversal is CPU-bound work and would show high `%CPU`, which was not observed.

**New leading hypothesis:** the stall is I/O-bound, likely a blocked/slow async operation — candidate suspects, in order of likelihood given the evidence:
1. A Neo4j network call (schema/session/query) that hangs or is unexpectedly slow, blocking the single flush consumer, which in turn blocks all producers once the bounded `Channel` (capacity 100) fills up — this would explain the exact burst-then-freeze pattern (burst = channel fills to capacity fast, freeze = waiting on the one consumer that's stuck).
2. A blocking synchronous call somewhere in the pipeline that isn't visible as CPU or as a subprocess (e.g. a `Task.Result`/`.Wait()` deadlock, or a lock held indefinitely).

Git subprocess spawning (Issue 003's suspect) and MSBuild re-evaluation of broken projects (Issue 002's suspect) are both **ruled out** as causes of this specific stall pattern based on direct observation (no child processes spawned during the stalls).

## Exact Wait Point — Confirmed via Live Memory Dump

The `dotnet-trace` capture above only carried informational CLR events (no CPU-sampling provider was enabled), so it could not show a managed callstack for a genuinely async, non-spinning wait. To get the exact blocked line, a second repro was run and, on hitting the same stall pattern (dead at 27/2792, `%CPU` 0.0), a full memory dump was taken with `dotnet-dump collect --process-id <pid>` and analyzed with `dotnet-dump analyze` + SOS's `dumpasync --coalesce --fields` (async-aware; walks GC-heap Task/state-machine objects and their continuations regardless of whether a thread is currently running them — full stacks in `async-stacks.txt`).

**Confirmed exact wait point** (STACKS 5 in `async-stacks.txt`):

```
SolutionProcessor.RunConsumer -> FlushBuffers -> Neo4jFlushService.FlushSymbols
  -> AsyncSession.RunTransactionAsync -> AsyncTransaction.CommitAsync -> DiscardUnconsumed
  -> ResultCursorBuilder.ConsumeAsync/AdvanceAsync -> SocketConnection.ReceiveOneAsync
  -> PipelinedMessageReader.ReadAsync/ReadNextMessage  (awaiting socket read)
```

The single flush consumer is blocked inside the Neo4j driver's implicit **`DiscardUnconsumed`** step during `CommitAsync`, waiting on a bolt-protocol socket read that isn't completing. STACKS 3 confirms the producer side is exactly where expected — `ChannelReader.ReadAllAsync`, i.e. genuinely backpressured waiting for this one consumer to drain, matching the burst-then-freeze pattern precisely.

**Root cause identified in source** — `Neo4jFlushService.cs` lines 105-123, `FlushSymbols`:

```csharp
await session.ExecuteWriteAsync(async tx => {
    tasks.Add(tx.RunWithRetry(GetCypher(Queries.UpsertSymbols), new { symbols = symbolBatch }));
    tasks.Add(tx.RunWithRetry(GetCypher(Queries.MergeRelationships), new { rels = relBatch }));
    await Task.WhenAll(tasks).ConfigureAwait(false);
});
```

Both queries are launched **concurrently on the same transaction (`tx`)** via `Task.WhenAll`, and neither result cursor is consumed by the caller (`RunWithRetry` just calls `RunAsync`, which returns a cursor immediately — nothing calls `ConsumeAsync`/iterates the records). The Neo4j bolt protocol does not support running multiple queries concurrently against one transaction/connection — this is a documented anti-pattern in the driver. The two unconsumed result streams sit on the wire until `CommitAsync` runs its implicit `DiscardUnconsumed`, which then has to drain both off a single socket before the commit can complete. That drain is what's hanging.

This is a **correctness bug**, not a resource-churn inefficiency — it changes Issue 004 from "reduce session count" to "stop running concurrent unconsumed queries on a shared transaction." It is also the most direct, evidence-backed explanation available for the original multi-hour production stall, and should be treated as the new P0 fix ahead of Issue 001.

## Recommendation

1. **Re-open/re-scope Issue 001** — the global-using cache fix may still be worth doing, but it is not implicated by this evidence. Do not treat it as P0 until proven independently (e.g. once the Issue 004 fix below lands, re-run this same repro and see if a *new*, CPU-bound stall pattern emerges).
2. **Promote to new P0**: fix `Neo4jFlushService.FlushSymbols` to not run concurrent queries on one transaction with unconsumed cursors. Options: run the two queries sequentially within the transaction (simplest, safest), or consume each cursor's summary (`await result.ConsumeAsync()`) before considering it done, or use separate transactions per query if true concurrency is wanted. This supersedes Issue 004's original "reduce session churn" framing — rewrite Issue 004 around this specific bug.
3. Issues 002 and 003 remain valid, independently-justified correctness/efficiency fixes regardless of this finding — proceed with those as already gated in `BACKLOG.md`.

## Artifacts

- `controls-core-attach.nettrace` — 33.6MB CLR runtime event trace (CPU/GC/exception events), kept locally only (not committed — too large for git history), not yet analyzed with a `.nettrace` viewer (speedscope/PerfView) — do that before deciding on a fix.
- `samples.txt` — raw CPU/RSS/git-subprocess-count samples, 5s interval.
- `stackSample.txt` — native thread listing during an active stall, showing zero active thread-pool workers.
- `direct-run.txt` — full tool console output for this run.
- `launch-mode-failure.txt`, `attach.txt` — dotnet-trace launch-mode failure logs (kept for reference; launch mode did not work, attach mode did).
- `async-stacks.txt` — `dumpasync --coalesce --fields` output from a live `dotnet-dump` capture during a second repro's stall — the exact wait point (see section above). The 6.5GB raw `.dmp` file was deleted after analysis, not kept.
