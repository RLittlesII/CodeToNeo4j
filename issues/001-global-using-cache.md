# Issue 001: Eliminate Quadratic Global-Using AST Traversal

## Priority
**P0 — CRITICAL**

## Impact
Highest-cost driver in the performance investigation. Eliminates dominant CPU cost — redundant AST walks across 13,056 files in Microsoft.Maui.sln. On large projects (1,000+ files in a single project), this is the primary bottleneck.

## Problem Statement

`RoslynSymbolProcessor.cs` (lines 70-98) performs a full AST traversal of **every** syntax tree in the compilation for **every** file processed to resolve global-using directives.

**Current behavior**: For each file, loops over `semanticModel.Compilation.SyntaxTrees`, calls `tree.GetRoot()`, then `root.DescendantNodes().OfType<UsingDirectiveSyntax>()` (a full AST walk), then resolves each global-using symbol via `semanticModel.Compilation.GetSemanticModel(tree).GetSymbolInfo(u.Name)`.

**Cost**: O(F × T) where F = files in project, T = syntax trees in project. At MAUI scale (~13,000 files, ~500 trees in large projects), this produces millions of tree-root fetches and descendant-node scans before any type processing runs.

**Secondary defect** (line 87): `relBuffer.Any(r => r.FromKey == fileKey && r.ToKey == depKey && ...)` — O(relBuffer.Count) linear scan inside the inner loop, compounds cost on large buffers.

## Acceptance Criteria

1. **Global-using resolution is cached once per compilation**
   - Cache is populated lazily on first file processed in a compilation (or eagerly at compilation open)
   - All subsequent files in the same compilation reuse the cached result
   - Cache is keyed by `compilation.Assembly.Identity.ToString()`
   - Cache is implemented as `ConcurrentDictionary<string, IReadOnlyList<ResolvedGlobalUsing>>` (thread-safe, no lock on hot path)

2. **No full AST traversal per file**
   - `tree.GetRoot().DescendantNodes()` is called at most once per syntax tree per compilation (not once per file)
   - `ProcessSyntaxTree` replaces lines 70-98 with a call to `IGlobalUsingCache.GetGlobalUsings(compilation)` and iterates the pre-resolved list

3. **Linear relationship buffer dedupe fixed**
   - Replace `relBuffer.Any(...)` O(n) scan with a `HashSet<(string from, string to)>` for O(1) dedup at the call site

4. **API surface is backward-compatible**
   - `RoslynSymbolProcessor` constructor gains one parameter: `IGlobalUsingCache globalUsingCache`
   - `IGlobalUsingCache` registered as singleton or compilation-scoped factory in DI
   - `IRoslynSymbolProcessor.ProcessSyntaxTree` signature remains unchanged
   - Existing test doubles for `IRoslynSymbolProcessor` are unaffected

5. **Semantic behavior is preserved**
   - Graph output for symbols and relationships is semantically identical to current behavior (no missing edges, no extra edges)
   - Global-using resolution correctly identifies external vs. internal symbols as before

6. **Performance improvement is measurable**
   - Ingestion time on large projects (1,000+ files in a single project) shows **50%+ reduction** compared to baseline
   - Per-file processing time on projects with high global-using density (e.g., net9.0 MAUI heads) drops measurably

## Test Plan

- **Unit test**: Mock `Compilation` with 500 synthetic syntax trees, each with 10 global-using directives. Process 1,000 files. Verify `GetRoot().DescendantNodes()` is called 500 times (once per tree), not 1,000 times (once per file).
- **Integration test**: Run ingestion on a known large project (e.g., subset of MAUI with 1,000+ files), capture elapsed time and compare to baseline.
- **Regression test**: Verify graph output (symbol nodes, USING edges) on a small project is identical before and after the cache change.
- **Thread-safety test**: Stress-test cache under `Parallel.ForEachAsync` with degree = 32 on 10,000 files across 10 compilations. No cache corruption or missed entries.

## Architecture Reference

See `architect.assessment.md` **Finding 1 — Quadratic Global-Using AST Traversal (Highest Priority)**, lines 34-78.

Proposed design introduces:
- `IGlobalUsingCache` interface with `GetGlobalUsings(Compilation) : IReadOnlyList<ResolvedGlobalUsing>`
- `GlobalUsingCache` implementation backed by `ConcurrentDictionary<string, IReadOnlyList<ResolvedGlobalUsing>>`
- `ResolvedGlobalUsing` record: `{ ISymbol Symbol, string NameText, bool IsExternal }`

## Dependencies

- None. Self-contained change within `RoslynSymbolProcessor` and new cache component.

## Effort Estimate

**Low-Medium** — Single new interface + implementation, surgical edit to one method (~30 lines replaced), DI registration. No cross-cutting refactor.

## Out of Scope

- Roslyn Workspace API performance (Roslyn's internal semantic model caching is already efficient and not a bottleneck)
- Caching of type symbols or other Roslyn constructs beyond global-using directives
- Changes to `Parallel.ForEachAsync` degree or parallelism model

## Risks

- **Cache invalidation boundary**: Ensure cache is scoped to `Compilation` instance, not shared across unrelated projects. If multiple target frameworks produce separate `Compilation` instances for the same project, each needs its own cache.
- **Memory pressure**: Large solutions with many compilations (e.g., 100 projects each with 500 trees) may accumulate significant cache memory. Acceptable tradeoff for performance gain, but monitor in production.

## Rollback Plan

If semantic regression is detected (missing or extra USING edges in graph output):
1. Revert `RoslynSymbolProcessor` to call the original inline traversal logic
2. Remove `IGlobalUsingCache` registration from DI
3. Re-run integration tests to confirm graph output matches baseline
4. Root-cause the cache key collision or resolution logic error before re-attempting

## Success Metrics

- **Primary**: Per-file processing time on large projects reduced by 50%+
- **Secondary**: Microsoft.Maui.sln ingestion time drops from 4.5+ hours (stalled at 61%) to measurable progress toward completion
- **Verification**: Zero semantic differences in graph output on regression test suite (all existing symbol/relationship assertions pass)

## Gaps Flagged During Grooming

1. **No cache eviction strategy documented**: If cache grows unbounded across a long-running process (e.g., ingesting 100 solutions in one run), memory may become a concern. Consider adding a `Clear()` method on the cache and invoking it at solution boundaries.
2. **No explicit cache pre-warming strategy**: Architect doc mentions "lazily on first file, or eagerly at compilation open" — which one? Recommend: lazy (simpler, no change to compilation-open path), but document the decision.
3. **Thread-safety test not in main acceptance criteria**: Added to test plan but should be surfaced as a MUST-PASS gate before merging (see Issue 003 for git cache thread-safety precedent).
