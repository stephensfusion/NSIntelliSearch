# IntelliSearch

IntelliSearch is a .NET 9 class library for searching a directory tree. It can find **files by name**, **folders by name**, and **text inside files**. It supports include/exclude glob filters, regular expressions, case-sensitive and whole-word matching, a maximum search depth, progress reporting, a timeout, and cooperative cancellation.

It never throws. Every problem comes back as a structured `SearchError` in the `SearchResponse`, and recoverable problems don't stop the search. Examples of recoverable problems are a locked file, a folder you can't read, or a file deleted mid-search.

```csharp
IIntelliSearchService search = new IntelliSearchService();

SearchResponse response = await search.SearchAsync(new SearchRequest
{
    RootPath = "/home/me/project",
    SearchText = "TODO",
    SearchContents = true,
    Recursive = true,
    IncludePatterns = new() { "*.cs" },
    ExcludePatterns = new() { "bin/*", "obj/*" },
});

foreach (var hit in response.SearchResults)
    Console.WriteLine($"{hit.FilePath}:{hit.LineNumber}:{hit.ColumnNumber}  {hit.MatchedText}");
```

---

## Contents

- [How it works](#how-it-works)
- [Getting started](#getting-started)
- [Usage examples](#usage-examples)
- [SearchRequest reference](#searchrequest-reference)
- [SearchResponse reference](#searchresponse-reference)
- [Matching rules](#matching-rules)
- [Cancellation, timeout and progress](#cancellation-timeout-and-progress)
- [Errors](#errors)
- [Known limitations](#known-limitations)
- [Building and testing](#building-and-testing)
- [Project layout](#project-layout)

---

## How it works

IntelliSearch is a thin orchestration layer over five single-purpose *nanoservices*. Each nanoservice is consumed as a compiled DLL from `lib/`:

| Step | Nanoservice (DLL) | What it does |
|---|---|---|
| 1 | `ValidateRequest` | Rejects bad requests before any work is done: null request, bad or missing root path, empty search text, negative depth or timeout, unknown encoding. |
| 2 | `TraverseDirectory` | Lists every file and folder under the root, honouring `Recursive` and `MaximumDepth`. Does not follow symbolic links. |
| 3 | `SplitPathsIntoChunks` | Splits the list of paths into chunks so they can be processed in parallel. |
| 4 | `PatternMatching` | Applies include/exclude globs, and matches the search text against file and folder names. |
| 5 | `SearchContent` | Streams a file line by line and reports every match with its line, column and character index. |

```
SearchAsync(request, token)
  ├─ ValidateRequest ──── invalid? → return Succeeded = false + error
  ├─ TraverseDirectory ── list paths (errors collected, search continues)
  ├─ SplitPathsIntoChunks
  └─ for each chunk, in parallel:
       folder → include/exclude filter → name match?       → folder result
       file   → include/exclude filter → name match?       → file result
                                       → SearchContents?   → content results
```

Results always come back in traversal order, even though the paths are processed in parallel.

---

## Getting started

**Requirements:** .NET 9 SDK.

Reference the project from your application:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/src/IntelliSearch/IntelliSearch.csproj" />
</ItemGroup>
```

The project carries its nanoservice DLLs in `lib/` and has no NuGet dependencies.

Everything lives in the `IntelliSearch` namespace:

```csharp
using IntelliSearch;
```

### Dependency injection (optional)

The library has no DI extension. The service is stateless and thread-safe, so register it yourself as a singleton:

```csharp
services.AddSingleton<ISearchProgressReporter, MyProgressReporter>();   // optional
services.AddSingleton<IIntelliSearchService>(sp =>
    new IntelliSearchService(sp.GetService<ISearchProgressReporter>()));
```

---

## Usage examples

### Find files by name

```csharp
var r = await search.SearchAsync(new SearchRequest
{
    RootPath = root,
    SearchText = "report",
    SearchFiles = true,
    Recursive = true,
});
// → every file whose *name* contains "report" (case-insensitive by default)
```

### Find folders by name

```csharp
var r = await search.SearchAsync(new SearchRequest
{
    RootPath = root,
    SearchText = "reports",
    SearchFolders = true,
});
```

### Files and folders together

```csharp
new SearchRequest { RootPath = root, SearchText = "data", SearchFiles = true, SearchFolders = true };
```

### Search inside files

```csharp
var r = await search.SearchAsync(new SearchRequest
{
    RootPath = root,
    SearchText = "world",
    SearchContents = true,
});

var hit = r.SearchResults[0];
// hit.LineNumber     = 2     (1-based)
// hit.ColumnNumber   = 7     (1-based, within the line)
// hit.CharacterIndex = 17    (0-based, from the start of the file)
// hit.MatchedText    = "world"
```

### Regular expressions, match case and whole word

These options can be combined freely.

```csharp
// Every number
new SearchRequest { RootPath = root, SearchText = @"\d+", UseRegularExpression = true, SearchContents = true };

// Upper-case runs only
new SearchRequest { RootPath = root, SearchText = "[A-Z]+", UseRegularExpression = true, MatchCase = true, SearchContents = true };

// "cat", but not "concatenate"
new SearchRequest { RootPath = root, SearchText = "cat", WholeWord = true, SearchContents = true };
```

Without `UseRegularExpression`, the search text is literal. `a.c` matches only `a.c`, not `abc`.

### Include and exclude filters

```csharp
new SearchRequest
{
    RootPath = root,
    SearchText = "needle",
    SearchContents = true,
    Recursive = true,
    IncludePatterns = new() { "*.txt", "**/*.md" },
    ExcludePatterns = new() { "bin/*", "obj/*", "node_modules/*", "*.tmp" },
};
```

When an entry matches both an include and an exclude pattern, the exclude wins.

### Limit the depth

```csharp
// Only the root's own contents
new SearchRequest { RootPath = root, SearchText = "x", SearchFiles = true, Recursive = false };

// Root contents plus one level of subfolders
new SearchRequest { RootPath = root, SearchText = "x", SearchFiles = true, Recursive = true, MaximumDepth = 2 };
```

### Checking the outcome

```csharp
var r = await search.SearchAsync(request, token);

if (r.Cancelled)        Console.WriteLine("Cancelled; partial results kept.");
else if (!r.Succeeded)  Console.WriteLine("Search failed.");

foreach (var e in r.Errors)   // may be non-empty even when Succeeded == true
    Console.WriteLine($"[{e.ErrorCode}] {e.Operation} {e.Path}: {e.Message}");

Console.WriteLine($"{r.SearchResults.Count} results in {r.ElapsedTime.TotalMilliseconds:F0} ms, " +
                  $"{r.Statistics.FilesVisited} files visited");
```

---

## SearchRequest reference

| Property | Type | Default | Meaning |
|---|---|---|---|
| `RootPath` | `string?` | — | **Required.** The directory to search. Must exist. Must not contain `..` segments or invalid characters. |
| `SearchText` | `string?` | — | **Required.** Must not be empty or whitespace. Literal text, or a regex when `UseRegularExpression` is set. |
| `SearchFiles` | `bool` | `false` | Return files whose **name** matches. |
| `SearchFolders` | `bool` | `false` | Return folders whose **name** matches. |
| `SearchContents` | `bool` | `false` | Return matches found **inside** files. |
| `Recursive` | `bool` | `false` | Descend into subfolders. |
| `MaximumDepth` | `int?` | `null` | Used when `Recursive` is set. Direct children of the root are depth 1. `null` means unlimited. Negative values are rejected. |
| `IncludePatterns` | `List<string>?` | `null` | Glob patterns. If any are set, an entry must match one of them. |
| `ExcludePatterns` | `List<string>?` | `null` | Glob patterns. An entry that matches any of them is skipped. |
| `MatchCase` | `bool` | `false` | Case-sensitive matching. Also makes glob filters case-sensitive. |
| `WholeWord` | `bool` | `false` | A match must not touch a letter, digit or `_` on either side. |
| `UseRegularExpression` | `bool` | `false` | Treat `SearchText` as a .NET regular expression. |
| `EncodingPreference` | `string?` | `null` (UTF-8) | Encoding used for files without a byte-order mark, for example `"utf-8"`, `"utf-16"` or `"latin1"`. Unknown names are rejected. |
| `Timeout` | `TimeSpan` | `Zero` (none) | Limit for the whole search, also used as the per-regex match timeout. Negative values are rejected. |

At least one of `SearchFiles`, `SearchFolders` and `SearchContents` must be `true`; otherwise the search returns nothing.

---

## SearchResponse reference

| Property | Type | Meaning |
|---|---|---|
| `Succeeded` | `bool` | `true` when the search ran to completion. Recoverable per-file errors don't make it `false`. It is `false` after cancellation, a timeout, a validation failure, an invalid regex, or an unusable root. |
| `Cancelled` | `bool` | `true` when the caller's token was cancelled. |
| `ElapsedTime` | `TimeSpan` | Wall-clock duration of the search. |
| `SearchResults` | `List<SearchResult>` | The matches, in traversal order. |
| `Errors` | `List<SearchError>` | Every error collected during the search. |
| `Statistics` | `SearchStatistics` | Counters for the search (see below). |

### SearchResult

| Property | Content match | File or folder name match |
|---|---|---|
| `FilePath` | Full path of the file | Full path of the entry |
| `FileName` | File name | Entry name |
| `LineNumber` | 1-based line | `0` |
| `ColumnNumber` | 1-based column | `0` |
| `CharacterIndex` | 0-based offset in the decoded file, in UTF-16 code units | `0` |
| `MatchedText` | The matched text | The entry name |
| `PreviewSnippet` | The matched text | The entry name |

### SearchStatistics

| Property | Meaning |
|---|---|
| `DirectoriesVisited` | The root plus every folder that was listed. |
| `FilesVisited` | Files that were processed. |
| `FilesMatched` | Files with at least one result (by name or content). Each file counts once. |
| `FoldersMatched` | Folder results. |
| `ContentMatches` | Content results. |
| `ElapsedTime` | Same as `SearchResponse.ElapsedTime`. |

`SearchRequest` and `SearchResponse` are plain classes that round-trip through `System.Text.Json`.

---

## Matching rules

### Name and text matching

- **Name searches** (`SearchFiles`, `SearchFolders`) match `SearchText` against the entry's **name** only, never its folder path. Names are compared exactly: Unicode is not normalised, and invisible characters are kept.
- **Content searches** match line by line. A match never spans a line break. Empty (zero-length) regex matches are ignored, so patterns such as `a*` or `^` cannot flood the results.
- **Overlapping matches:** regex matches never overlap. For example, `a+` on `aaaa` gives one match, `aaaa`.

### Glob filters

Glob patterns are matched against the path relative to `RootPath`, using `/` as the separator on every OS.

| Token | Matches |
|---|---|
| `*` | Any run of characters, **including `/`** |
| `**` | The same as `*` |
| `?` | Exactly one character |

The table below shows what each requirement example matches:

| Pattern | Matches |
|---|---|
| `*.txt` | Every `.txt` file at any depth |
| `**/*.md` | Every `.md` file, including ones directly under the root |
| `src/*` | Everything under `src` at the root |
| `bin/*` | Everything under `bin` at the root. A nested `x/bin/` is **not** excluded. |
| `*.tmp` | Every `.tmp` file at any depth |

### File encoding

- A byte-order mark (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE) is detected automatically.
- Files without a BOM are decoded with `EncodingPreference`, which defaults to UTF-8.
- Invalid bytes are decoded as replacement characters; they don't produce an error.

### Large files

Files are streamed with bounded memory. A large file is never loaded into memory in full.

---

## Cancellation, timeout and progress

### Cancellation

Pass a `CancellationToken`. The token is honoured during:

- validation
- traversal
- pattern matching
- file reading
- regex evaluation

When the token is cancelled, `SearchAsync` **returns** rather than throwing. The response has `Cancelled = true` and `Succeeded = false`, and keeps every result found before cancellation. Each kept result is fully populated.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var r = await search.SearchAsync(request, cts.Token);
```

### Timeout

`SearchRequest.Timeout` limits the whole search, and it is also the per-match limit for regular expressions. When the timeout expires, the response has `Succeeded = false` and `Cancelled = false`, and it contains a `Timeout` error. A runaway regex such as `(a+)+$` is stopped and reported as `RegexTimeout`; the search continues with the other files.

### Progress

Implement `ISearchProgressReporter` and pass it to the constructor:

```csharp
sealed class ConsoleProgress : ISearchProgressReporter
{
    public void Report(SearchProgress p) =>
        Console.WriteLine($"{p.ProgressPercentage,5:F1}%  {p.FilesProcessed} files  {p.CurrentFile}");
}

var search = new IntelliSearchService(new ConsoleProgress());
```

How progress is reported:

- A report is sent **each time a result is produced**.
- Reports are serialised, so they never arrive concurrently.
- `ProgressPercentage` (the share of traversed paths processed) only increases and stays within 0–100.
- No report arrives after `SearchAsync` has returned.
- Each report is a fresh snapshot, so it is safe to keep.
- If your reporter throws, it is not called again, and a `ProgressReporterFailed` error is added to the response.

---

## Errors

Every `SearchError` contains:

| Field | Meaning |
|---|---|
| `Path` | The file or folder concerned |
| `Operation` | The step that failed |
| `ErrorCode` | A stable code (see the tables below) |
| `Message` | A human-readable message |
| `ExceptionType` | The underlying exception type, when there is one |

### Fatal errors

Fatal errors stop the search. They set `Succeeded = false` and return no results.

| ErrorCode | Cause |
|---|---|
| `NullRequest` | `request` is null. |
| `InvalidRootPath` | `RootPath` is null, empty or whitespace. |
| `MalformedPath` | `RootPath` contains invalid characters (for example `\0`). |
| `PathTraversal` | `RootPath` contains a `..` segment. |
| `PathNotFound` | `RootPath` does not exist. |
| `InvalidPathType` | `RootPath` is a device, pipe or socket. |
| `NotADirectory` | `RootPath` is a file. |
| `InvalidSearchText` | `SearchText` is empty or whitespace. |
| `InvalidMaximumDepth` | `MaximumDepth` is negative. |
| `InvalidTimeout` | `Timeout` is negative. |
| `UnsupportedEncoding` | `EncodingPreference` is not a known encoding. |
| `InvalidPattern` | `SearchText` is not a valid regex. This is reported before any file is read. |
| `Timeout` | `SearchRequest.Timeout` expired. Results found so far are kept. |

### Recoverable errors

The search continues after these. They are collected in `Errors`.

| ErrorCode | Cause |
|---|---|
| `AccessDenied` | A folder or file can't be read. |
| `FileLocked` | A file is locked by another process. |
| `FileNotFound`, `DirectoryNotFound`, `PathNotFound` | The item was deleted or moved during the search. |
| `PathTooLong` | The OS rejected the path length. |
| `IOError` | Any other I/O failure. |
| `RegexTimeout` | A regex exceeded its time limit on one file. |
| `UnexpectedError` | An unexpected failure on one entry. |
| `ProgressReporterFailed` | Your progress reporter threw an exception. |

---

## Known limitations

- **Preview snippet:** `PreviewSnippet` holds the matched text, not the surrounding line.
- **Invalid encodings:** a file with invalid bytes is searched using replacement characters. No invalid-encoding error is reported.
- **Symbolic links:** symbolic links and junctions are listed but never followed. Cycles can't cause loops, but they aren't reported as errors.
- **Nested exclude patterns:** a pattern such as `bin/*` applies only directly under the root. To exclude `bin` folders at any depth, use `*/bin/*` together with `bin/*`.
- **Extension interfaces:** `ISearchEngine`, `IPatternMatcher` and `IContentMatcher` are declared but not implemented.
- **Mocking:** the file system and the file readers can't be swapped out for mocks.

---

## Building and testing

The SDK may be installed under `~/.dotnet`. If so, run `export PATH="$HOME/.dotnet:$PATH"` first.

```bash
cd src/IntelliSearch

# Rebuild the nanoservice DLLs into ./lib (only needed when a nanoservice changes)
./build-libs.sh

# Build the library
dotnet build -c Release

# Run the tests with TRX output and coverage
dotnet test ../../tests/IntelliSearch.Tests/IntelliSearch.Tests.csproj -c Release \
  --logger "trx;LogFileName=test-results.trx" \
  --collect:"XPlat Code Coverage" \
  --test-adapter-path ~/.nuget/packages/coverlet.collector/6.0.2/build/netstandard2.0 \
  --results-directory ./TestResults
```

Current result: **45 / 45 tests pass**, with 89% line coverage of the IntelliSearch assembly. See [ASSEMBLY_REPORT.md](ASSEMBLY_REPORT.md) for the full build, mapping and requirements report.

---

## Project layout

```
src/IntelliSearch/
├── IntelliSearch.csproj     # net9.0 library; references lib/*.dll
├── Contracts.cs             # public interfaces and models
├── IntelliSearchService.cs  # orchestration (validate → traverse → chunk → match → search)
├── NanoserviceMapper.cs     # maps between the public types and each nanoservice's types
├── build-libs.sh            # builds the five nanoservices into lib/
├── lib/                     # ValidateRequest, TraverseDirectory, SplitPathsIntoChunks,
│                            # PatternMatching, SearchContent (.dll + .pdb)
├── ASSEMBLY_REPORT.md       # build, mapping and test report
└── TestResults/             # .trx and coverage output
```
