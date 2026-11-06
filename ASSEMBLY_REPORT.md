# IntelliSearch: Assembly Report

## 1. Summary

The assembly succeeded. Five nanoservices (ValidateRequest, TraverseDirectory, SplitPathsIntoChunks, PatternMatching and SearchContent) were each compiled into a Release class-library DLL and copied into `src/IntelliSearch/lib/`. Their source code was not changed. The big service `src/IntelliSearch` (a class library) references those DLLs through `<Reference HintPath>` and builds in Release with **0 errors and 0 warnings**. It implements the `pseudocode.txt` orchestration behind `IIntelliSearchService`. The unchanged test project ran **45 tests: 45 passed, 0 failed, 0 skipped**. Three further runs all passed, so no flakiness was observed. Line coverage is **89.0% for the IntelliSearch assembly** and **73.0% across all instrumented assemblies**.

**Target framework: `net9.0`.** The nanoservices declare `net10.0`, but the test project and the `src` stub target `net9.0`, and the only SDK installed is .NET SDK 9.0.320. A `net9.0` test assembly cannot reference `net10.0` DLLs, and the test project may not be changed. The nanoservices were therefore compiled for `net9.0` with a command-line override (`-p:TargetFramework=net9.0`), and none of their files were edited.

## 2. Small services → DLLs

| Service | DLL | Declared TFM → built TFM | Structural changes made | Build status |
|---|---|---|---|---|
| ValidateRequestService | `ValidateRequest.dll` (+ `.pdb`) | net10.0 → **net9.0** | None to source or csproj. TFM overridden on the command line. | Succeeded, 0 warnings, 0 errors |
| TraversalDirectoryService | `TraverseDirectory.dll` (+ `.pdb`) | net10.0 → **net9.0** | None (same override) | Succeeded, 0 warnings, 0 errors |
| SplitIntoChunksService | `SplitPathsIntoChunks.dll` (+ `.pdb`) | net10.0 → **net9.0** | None (same override) | Succeeded, 0 warnings, 0 errors |
| PatternMatchingService | `PatternMatching.dll` (+ `.pdb`) | net10.0 → **net9.0** | None (same override) | Succeeded, 0 warnings, 0 errors |
| SearchContentService | `SearchContent.dll` (+ `.pdb`) | net10.0 → **net9.0** | None (same override) | Succeeded, 0 warnings, 0 errors |

Discovery facts that apply to all five services:

* **Project type:** each one was already a class library. None has an entry point, ASP.NET Core usage, or DI registrations, so none needed an entry point removed or an `Add<Service>()` extension.
* **NuGet dependencies:** none. As a result the big service needs no `PackageReference`, and there were no version conflicts to resolve.
* **Configuration:** none (no appsettings, environment variables or connection strings), so there was no configuration to merge or rename.
* **XML docs:** none of the services sets `GenerateDocumentationFile`, so there are no `.xml` files to copy.
* **Public surface used by the orchestrator:**
  * `ValidateRequest.ValidateRequestService.ValidateSearchRequest(SearchRequest, CancellationToken) → (bool, SearchError?)`
  * `TraverseDirectoryService.TraverseDirectory(string, int, bool, CancellationToken) → (List<string>, List<SearchError>)`. This type and its `SearchError` are in the **global namespace**.
  * `SplitPathsIntoChunks.SplitPathsIntoChunksService.SplitIntoChunks(List<string>, int, CancellationToken)`. Static method.
  * `PatternMatching.PatternMatchingService.IsPatternMatching(string, SearchRequest, CancellationToken) → bool`
  * `SearchContent.SearchContentService.FindMatches(string, SearchRequest, CancellationToken) → (IEnumerable<SearchResult>, List<SearchError>)`. The results are lazy, and the position fields are strings.
  * Each service defines its own `SearchRequest` and `SearchError` types. The internal `NanoserviceMapper.cs` maps between them and the public contract.

Build notes:

* NuGet restore ignores a global `TargetFramework` property. As a result, `dotnet build -p:TargetFramework=net9.0` failed with `NETSDK1045`.
* `build-libs.sh` therefore runs `dotnet restore -p:TargetFramework=net9.0` and then `dotnet build --no-restore -c Release -p:TargetFramework=net9.0` for each project.
* These steps regenerate build artifacts (`bin/`, `obj/`) inside each nanoservice folder. Running the nanoservices' own `net10.0` test projects will restore them again; that requires a .NET 10 SDK, which this machine does not have.

## 3. Pseudocode mapping

| Pseudocode step | Implemented by | Notes |
|---|---|---|
| Set up searchStatistics, SearchResponse, SearchProgress | `IntelliSearchService.SearchState` | Thread-safe holder. Results are stored by traversal index so the output order is deterministic. |
| Start timer | `IntelliSearchService.SearchAsync` (`Stopwatch`) | |
| Validate searchRequest | `ValidateRequestService.ValidateSearchRequest` | `MaximumDepth: int?` is mapped as follows: `null` becomes 0 (accepted) and `-1` stays −1 (rejected). The validator's `OperationCanceled` result is reported as `Cancelled = true`. |
| *(gap)* Reject an invalid regex before any file is read | `IntelliSearchService.IsValidRegex` | No nanoservice validates the pattern up front: PatternMatching silently returns `false`, and SearchContent would report the error once per file. Required by VAL-01 / AT-04. |
| *(gap)* Request timeout | `IntelliSearchService.RunAsync` (linked CTS with `CancelAfter`) | Required by "Timeout expiration". Produces a `Timeout` SearchError. |
| Traverse directory | `TraverseDirectoryService.TraverseDirectory` | Non-recursive: depth 0. Recursive with `MaximumDepth` unset: `int.MaxValue`. Runs on the thread pool. |
| Update response errors with traversal errors | `SearchTreeAsync` | An error with `Operation == "Validate"` (the root itself is unusable) is fatal, so `Succeeded = false`. |
| Split paths into chunks | `SplitPathsIntoChunksService.SplitIntoChunks` | Chunk size is set to give about 4 chunks per core. |
| Process chunks concurrently | `Parallel.ForEachAsync` in `SearchTreeAsync` | Degree of parallelism = processor count. Uses the request-scoped token. |
| If directory → pattern match → update stats, response, progress | `ProcessPath` → `PassesFilters` + `NameMatches` → `PatternMatchingService.IsPatternMatching` | Applied only when `SearchFolders = true` (see conflict C3). |
| If file → pattern match → update stats, progress | `ProcessPath` → `PassesFilters` → `IsPatternMatching` | Include/exclude gate for both file and content results. A file-name match when `SearchFiles = true` is also added to the response (conflict C1). |
| If searchContent → search content → update stats, response, progress | `SearchContents` → `SearchContentService.FindMatches` | Results are streamed. Errors raised while enumerating are collected afterwards. Progress is reported per match. |
| "In case of error update the searchResponse error" | `Match` / `SearchContents` catch blocks | Unexpected nanoservice exceptions become `UnexpectedError` SearchErrors, and the search continues. |
| Stop timer, set ElapsedTime, return response | `SearchState.Complete` | `Succeeded = !Cancelled && !fatal`. |

### Conflicts and gaps (requirements were followed in each case)

1. **C1: file-name matches.** In the pseudocode's file branch, a pattern match updates only statistics and progress, never the response. The requirements (File search, FR-01) need file results, so they are added to the response.
2. **C2: what gates content search.** `IsPatternMatching` tests the *search text* against the *path*. If the pseudocode were applied literally, `"world"` inside `a.txt` would never be searched. The orchestrator therefore makes two kinds of calls:
   * **Filter call:** include/exclude only, with the search text set to null. This gates every entry.
   * **Name call:** the search text against the entry name. This decides file and folder results.

   The requirement AT-05 ("search file names for…") confirms that name matching is intended.
3. **C3: folder results.** The pseudocode's directory branch adds every matching directory. The requirements and FR-02 return folders only when `SearchFolders = true`, so that flag is honoured.
4. **C4: glob semantics.** PatternMatching collapses `**` into `*`, and its `*` crosses `/`. Because of this, `**/*.md` needs a literal `/`, while `bin/*` needs the path relative to the root. Each rule set is therefore checked against both `rel` and `/rel`, which lets both requirement examples work. A side effect is that `bin/*` excludes only `bin` directly under the root.
5. **C5: PreviewSnippet.** No nanoservice returns line context, so `PreviewSnippet` is set to the matched text (or to the entry name for name matches). Reading the line back from the file would re-implement SearchContent's reader.
6. **C6: SearchStatistics placement.** The requirements define SearchStatistics but do not list it on SearchResponse. It is exposed through an additive `SearchResponse.Statistics` property, so no existing member changes.

## 4. Requirements coverage

All implementation is in `src/IntelliSearch/`. Unless another file is named, the methods below are in `IntelliSearchService.cs`.

| Requirement | Where implemented | Status |
|---|---|---|
| File search | `ProcessPath` / `NameMatches` → PatternMatching | Met |
| Folder search | `ProcessPath` (directory branch) | Met |
| Combined file + folder search | `ProcessPath` | Met |
| Content search | `SearchContents` → SearchContent | Met |
| Recursive / non-recursive / max depth | `SearchTreeAsync` → TraverseDirectory | Met |
| Include / exclude (glob) filters | `PassesFilters` → PatternMatching | Met. Glob semantics are those of the PatternMatching service (C4). |
| Plain text, Match Case, Whole Word, Regex, and their combinations | PatternMatching (names), SearchContent (contents) | Met |
| Result fields: path, name, line, column, char index, matched text | `NanoserviceMapper.FromContentResult` / `NameResult` | Met. Name matches report line, column and index as 0. |
| Result field: preview snippet | `NanoserviceMapper.FromContentResult` | Partially met (C5) |
| Request-scoped cooperative cancellation (traversal, enumeration, reading, matching, regex, results) | Token passed to every nanoservice call and to `Parallel.ForEachAsync`; `SearchAsync` catches it | Met. Returns `Cancelled = true` and keeps partial results; no exception reaches the caller. |
| Progress reporting | `SearchState.Report` | Met. Reported on every match, under a lock: percentage is monotonic and within 0–100, and no report arrives after return. A reporter that throws is disabled and logged as an error. |
| Result aggregation, statistics | `SearchState` | Met (C6) |
| Error recovery: permission denied, missing, locked files/folders | TraverseDirectory and SearchContent errors, mapped in `SearchTreeAsync` / `SearchContents` | Met |
| Invalid encodings → SearchError | SearchContent decodes with replacement characters and does not raise an error | Partially met. The search continues (ERR-02 passes), but no invalid-encoding SearchError is produced. That is nanoservice behaviour, which was not changed. |
| Unsupported encoding preference | ValidateRequest | Met |
| Invalid paths, null request, empty search text, missing root | ValidateRequest | Met |
| Invalid regular expressions | `IsValidRegex` pre-check | Met |
| Timeout expiration | `RunAsync` linked CTS, plus per-file and per-regex timeouts in SearchContent and PatternMatching | Met |
| Symbolic link cycles | TraverseDirectory does not follow reparse points | Partially met. No loops or duplicates occur, but a cycle is not reported as a SearchError (AT-02). |
| Excessive recursion | TraverseDirectory uses iterative traversal; `MaximumDepth = -1` is rejected | Met |
| I/O exceptions | Nanoservice error mapping plus orchestrator catch blocks | Met |
| Primary interface `IIntelliSearchService` | `IntelliSearchService` | Met |
| `ISearchProgressReporter` | Constructor injection | Met |
| `ISearchEngine`, `IPatternMatcher`, `IContentMatcher` | Declared in `Contracts.cs` | Not met. The nanoservice signatures don't fit: `IContentMatcher` takes a content *string*, but SearchContent takes a file path. `IPatternMatcher.IsMatch(string)` carries no request. |
| Mockable file-system enumeration and content readers | — | Not met. The nanoservices are concrete classes without seams, so AT-01/05/06 cannot be run with mocks as specified. None of these tests is in the supplied suite. |
| JSON serialization of SearchRequest / SearchResponse | POCOs in `Contracts.cs` | Met |
| Future extension without breaking APIs | Additive members only | Met |
| Logging | — | Not applicable. Requirement.pdf specifies no logging; errors are returned as structured data. |

## 5. Test results

Command: Step 4 of the brief, run from `src/IntelliSearch`. Raw output is kept in `./TestResults`:

* `TestResults/test-results.trx`
* `TestResults/<run-id>/coverage.cobertura.xml`

| Total | Passed | Failed | Skipped | Duration |
|---|---|---|---|---|
| 45 | 45 | 0 | 0 | 2.15 s (console "Total time", with coverage) |

| Coverage (coverlet, line / branch) | Line | Branch |
|---|---|---|
| IntelliSearch assembly (big service) | **89.0%** | 84.9% |
| All instrumented assemblies (big service + 5 nanoservice DLLs) | 73.0% | 63.8% |

**Failing tests:** none. No fix-and-rerun cycles were needed; every test passed on the first run, and three more runs without coverage also passed.

**Test-project note:** the test project does not reference `coverlet.collector`, so `--collect:"XPlat Code Coverage"` on its own reports *"Unable to find a datacollector"* and collects nothing. Coverage was collected by adding `--test-adapter-path ~/.nuget/packages/coverlet.collector/6.0.2/build/netstandard2.0`, using the copy already in the local NuGet cache. The test project and the tests were **not modified**. The ProjectReference to `src/IntelliSearch` already pointed at the big service, so no reference path changed either.

**Tests worth flagging (left unchanged):**

* `EC-02` and `CAN-02` accept either outcome, so they do not pin down behaviour.
* `FR-09` filters out its own deep paths before comparing. It is weaker than its name suggests, but still correct.

## 6. How to build and run

The SDK is installed at `~/.dotnet` and is not on the default `PATH`.

```bash
export PATH="$HOME/.dotnet:$PATH"
cd /home/steph/nanoservices/backend/dotnet/IntelliSearch/src/IntelliSearch

# 1. Rebuild the nanoservice DLLs (net9.0, Release) into ./lib
./build-libs.sh

# 2. Build the big service
dotnet build IntelliSearch.csproj -c Release

# 3. Run the unit tests with TRX + coverage (results in ./TestResults)
dotnet test ../../tests/IntelliSearch.Tests/IntelliSearch.Tests.csproj -c Release \
  --logger "trx;LogFileName=test-results.trx" \
  --logger "console;verbosity=normal" \
  --collect:"XPlat Code Coverage" \
  --test-adapter-path ~/.nuget/packages/coverlet.collector/6.0.2/build/netstandard2.0 \
  --results-directory ./TestResults
```

### Files changed or added in `src/IntelliSearch`

| File | Change |
|---|---|
| `IntelliSearch.csproj` | Added five `<Reference HintPath="lib\*.dll">` items |
| `Contracts.cs` | Added the `SearchResponse.Statistics` property |
| `IntelliSearchService.cs` | Replaced the stub with the orchestration |
| `NanoserviceMapper.cs` | New file: type mapping |
| `build-libs.sh` | New file: rebuilds the DLLs |
| `lib/` | New folder: the five DLLs and their PDBs |
