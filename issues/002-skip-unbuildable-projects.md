# Issue 002: Skip Unbuildable Multi-Platform Target Frameworks

## Priority
**P1 — HIGH**

## Impact
Eliminates wasted work on Android/iOS/Windows/Tizen/Gtk target frameworks not buildable on the host platform. Reduces 150+ MSBuild diagnostic warnings and prevents wasted per-file compilation attempts on broken projects. Clarifies progress reporting (e.g., "Processing 5,000 files" not "13,056 files" when 8,000 are unbuildable).

## Problem Statement

`MsBuildWorkspaceFactory.Create()` sets MSBuild properties for design-time builds (`DesignTimeBuild=true`, `SkipCompilerExecution=true`, etc.) but does **not** set a `TargetFramework` property. `OpenSolutionAsync` evaluates **all** target heads (net9.0-android, net9.0-ios, net9.0-windows, net9.0-tizen, net9.0-gtk, net9.0-maccatalyst) plus the base net9.0.

**Result**: Each unbuildable head generates diagnostic warnings. `SolutionProcessor.ProcessSolution` has no project/target-framework filter before `discoveryService.GetFilesToProcess` — the workspace-warning handler logs but never skips. `ProcessFile` (line 267) still attempts to compile each broken head, producing logged exceptions per unbuildable target framework.

**User-facing problem**: Progress bar shows 13,056 files when only ~5,000 are buildable. User has no control over which projects/frameworks to process.

## Acceptance Criteria

### Option A: MSBuild Property Injection (Primary Mechanism)

1. **Workspace factory accepts optional MSBuild properties**
   - `IWorkspaceFactory.Create()` gains optional parameter: `IDictionary<string, string>? additionalProperties = null`
   - If caller passes `{ "TargetFramework": "net9.0" }`, workspace evaluation is constrained to a single target head
   - Standard MSBuild property behavior — suppresses multi-targeting

2. **CLI exposes target-framework filter**
   - New CLI option: `--target-framework <TFM>` (e.g., `--target-framework net9.0`)
   - CLI plumbs this value into `MsBuildWorkspaceFactory.Create()` as `additionalProperties`
   - Default behavior (no flag) remains "evaluate all target heads" (backward-compatible)

3. **MSBuild diagnostic warnings are reduced**
   - When `--target-framework` is specified, unbuildable heads are not evaluated (no Android/iOS SDK warnings on macOS/Linux)
   - Baseline: 150+ warnings on MAUI. Target: 0 warnings when targeting a single buildable framework.

### Option B: Project Exclusion Filter (Complementary Mechanism)

4. **CLI accepts project exclusion list**
   - New CLI option: `--exclude-project <ProjectName>` (repeatable, e.g., `--exclude-project Foo.Android --exclude-project Bar.iOS`)
   - Alternative: `--exclude-projects <pattern>` (glob, e.g., `--exclude-projects "*.Android,*.iOS"`)
   - `SolutionProcessor.ProcessSolution` accepts `IEnumerable<string>? excludeProjects = null`
   - Filters `solution.Projects` before discovery
   - Progress reporting denominator excludes filtered projects

5. **User receives clear feedback on filtering**
   - CLI logs: `"Excluding 3 projects matching pattern *.Android: [list]"`
   - Progress bar denominator reflects only included projects (e.g., "Processing 5,000 / 5,000 files" not "5,000 / 13,056")
   - MSBuild warnings on excluded projects are suppressed (not logged to user)

### Constraints

6. **Default behavior is unchanged**
   - If no `--target-framework` or `--exclude-project` flags are passed, all projects and target frameworks are processed (current behavior)
   - Opt-in filtering — no breaking change to existing automation

7. **API surface is backward-compatible**
   - `IWorkspaceFactory.Create()` optional parameter (additive, default `null`)
   - `SolutionProcessor.ProcessSolution()` optional parameter (additive, default `null`)
   - Existing callers unaffected

## Test Plan

- **Unit test**: Mock `IWorkspaceFactory.Create()` call with `additionalProperties = { "TargetFramework": "net9.0" }`. Verify MSBuild properties are passed through to workspace creation.
- **Integration test**: Run ingestion on MAUI with `--target-framework net9.0`. Verify:
  - Only net9.0 head is evaluated (no net9.0-android, net9.0-ios, etc.)
  - MSBuild warnings drop from 150+ to near-zero
  - Graph output contains only net9.0 symbols (no Android/iOS types)
- **CLI test**: Run with `--exclude-project "*.Android" --exclude-project "*.iOS"`. Verify excluded projects do not appear in progress denominator or logs.
- **Regression test**: Run ingestion on a small multi-target solution with NO flags. Verify all projects are processed (default behavior unchanged).

## Architecture Reference

See `architect.assessment.md` **Finding 2 — Wasted Work on Unbuildable Multi-Platform Target Heads (High Priority)**, lines 80-107.

Proposed design:
- **A**: `MsBuildWorkspaceFactory.Create()` accepts `IDictionary<string, string> additionalProperties`. Caller passes `TargetFramework=net9.0` to constrain evaluation.
- **B**: CLI `--target-framework` / `--exclude-project` options. `SolutionProcessor.ProcessSolution` accepts `IEnumerable<string> excludeProjects`, filters `solution.Projects` before discovery.

## Dependencies

- None. Self-contained CLI and workspace factory changes.

## Effort Estimate

**Medium** — CLI plumbing + two signature additions + factory delegation. No structural refactor. `TargetFramework` injection alone likely recovers most of the 150+ warning load.

## Out of Scope

- Automatic detection of buildable target frameworks (e.g., probing for Android SDK presence before evaluating Android heads). User must explicitly specify `--target-framework` or `--exclude-project`.
- MSBuild diagnostic filtering logic (warnings are already logged via workspace handler; this issue reduces warning count by not evaluating unbuildable heads, not by suppressing logs).
- Progress bar styling or UX improvements unrelated to denominator calculation.

## Risks

- **Multi-targeting semantics**: If a project has conditional compilation symbols or #if directives that vary by target framework, excluding Android/iOS heads will exclude symbols/types that only exist in those heads. This is expected behavior — user explicitly opted in to single-target processing. Document in CLI help text.
- **Glob pattern complexity**: If `--exclude-projects` supports globs, pattern matching must be well-defined (case-sensitive? path vs. project name?). Recommend: exact project name match for initial implementation, defer glob to future enhancement.

## Rollback Plan

If filtering logic breaks valid multi-target scenarios:
1. Revert CLI flags and workspace factory signature changes
2. Remove filtering logic from `SolutionProcessor.ProcessSolution`
3. Re-run integration tests to confirm all projects are processed
4. Root-cause the filtering predicate error (e.g., project-name match vs. path match) before re-attempting

## Success Metrics

- **Primary**: Microsoft.Maui.sln ingestion with `--target-framework net9.0` completes without Android/iOS/Tizen/Gtk warnings (0 unbuildable-head warnings, down from 150+)
- **Secondary**: Progress reporting denominator reflects only buildable files (e.g., 5,000 instead of 13,056)
- **Verification**: Graph output for filtered run contains only net9.0 symbols (no Android/iOS types in Neo4j)

## Gaps Flagged During Grooming

1. **No acceptance criterion for "what was skipped" reporting**: Architect doc mentions "User must receive clear feedback on what was skipped and why" — this is stated under constraints but not formalized as a testable AC. Added as AC #5 above.
2. **No clarification on "automatically detect and skip" vs. "user must specify"**: Original issue-description.md AC says "OR: Automatically detect and skip target frameworks/projects that fail MSBuild restore/load before entering the per-file loop." Architect design does NOT implement auto-detection — only user-specified filtering. This is a gap between requirements and design. **RECOMMEND**: Defer auto-detection to future enhancement, keep scope to user-specified filtering for this issue. Document the decision.
3. **Glob pattern semantics undefined**: If `--exclude-projects <pattern>` supports globs, what is the matching rule? Case-sensitive? Full path or just project name? File extension included? **RECOMMEND**: Start with exact name match, defer glob to follow-up issue. Document in CLI help text.
4. **No test strategy for "MSBuild warnings are surfaced to the user but do not block ingestion of buildable projects"**: Architect design logs warnings but does not block. No AC verifies this behavior. Add integration test: solution with 1 broken project + 1 buildable project → ingestion completes, broken project logged as warning, buildable project ingested successfully.
