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

## Recommendation

1. **Re-open/re-scope Issue 001** — do not implement the global-using cache fix as currently scoped until the actual CPU-bound cost is confirmed with a proper managed stack trace (e.g. `dotnet-trace` analyzed with PerfView/speedscope, or `dotnet-dump` on a live hang) showing time actually spent inside `RoslynSymbolProcessor`.
2. **New priority-0 investigation**: trace what the single Neo4j-flush consumer thread is awaiting during a stall — instrument `Neo4jFlushService` and the channel consumer loop in `SolutionProcessor.RunConsumer` with a stopwatch/logging around each `await`, or attach a debugger during a live stall and get a real managed callstack (`dotnet-dump collect` + `dotnet-dump analyze`, or a Visual Studio/Rider remote attach) to identify the exact blocked line.
3. Issues 002 and 003 remain valid, independently-justified correctness/efficiency fixes regardless of this finding — proceed with those as already gated in `BACKLOG.md`.
4. Issue 004 (Neo4j session churn) gains new relevance in light of this finding — if the stall is Neo4j-side, session/connection handling is now the top suspect, not just an opportunistic cleanup.

## Artifacts

- `controls-core-attach.nettrace` — 33.6MB CLR runtime event trace (CPU/GC/exception events), kept locally only (not committed — too large for git history), not yet analyzed with a `.nettrace` viewer (speedscope/PerfView) — do that before deciding on a fix.
- `samples.txt` — raw CPU/RSS/git-subprocess-count samples, 5s interval.
- `stackSample.txt` — native thread listing during an active stall, showing zero active thread-pool workers.
- `direct-run.txt` — full tool console output for this run.
- `launch-mode-failure.txt`, `attach.txt` — dotnet-trace launch-mode failure logs (kept for reference; launch mode did not work, attach mode did).
