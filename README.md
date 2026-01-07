# RedSharp
## Loop & Complexity Analyzers for C# (Roslyn)
A growing collection of **free Roslyn analyzers and code fixes for Visual Studio (Windows, not VS Code)**, focused on surfacing potential inefficiencies and encouraging clearer, idiomatic C#.

### Parallel download & deserialization analyzer
The analyzer should replace this:
```
foreach (string? key in filteredKeys) {
  byte[]? data = await DownloadBytesAsync(key, ct);
  T? value = JsonSerializer.Deserialize<T>(data, jsonOptions);
    if (value != null) {
      results.Add(value);
    }
}
```
With this:
```
// Concurrent download and deserialize all matching objects
T?[] values = await Task.WhenAll(
    filteredKeys.Select(async key =>
        JsonSerializer.Deserialize<T>(
            await DownloadBytesAsync(key, ct),
              _jsonOptions()))
);
```

### Current Goals
- Detect nested `foreach` loops as possible performance hotspots
- Provide conservative **code-fix suggestions** (e.g., `SelectMany`/`AddRange`) when semantics can be preserved
- Promote maintainable, modern C# patterns without relying on file names or opinionated style rules

### Platform & Compatibility
- Minimum version is **Visual Studio 2022** using `netstandard2.0` analyzer assemblies
- Will get packaged via VSIX or NuGet for easy reuse in Visual Studio
- Pure C# static analysis — no external services required
- Free foorever.

### Status
🚧 **Work in progress.**  
More analyzers will be added over time to help C# developers spot:
- Inefficient `.Contains()` lookups inside loops
- Join-like nested loops that could become `Enumerable.Join`
- Repeated allocations (`.Where().ToList()` inside loops, etc.)
- Other patterns that hint at avoidable complexity

### Contributions
Ideas, PRs, and new analyzer suggestions are welcome. This project aims to grow steadily into a helpful toolkit for C# developers who care about clarity and performance.

---

**Made with 💙 for C# developers.**
