# .NET Method Dispatch Benchmark, Before/After

Benchmark: `assets/language/repl/bench/bench_methodcall.slc`
Configuration: `dotnet publish -c Release -r win-x64 /p:PublishAot=true` (NativeAOT)
Test: 30,000 iterations × 3 reps per test case

## Results

| Test | Before (ms) | After (ms) | Change |
|------|------------|-----------|--------|
| 1. Environment.TickCount (0-arg) | 119 | 121 | +2% (noise) |
| 2. Convert.ToString(int) (1-arg) | 372 | 328 | **-12%** |
| 3. string.Concat(str,str) (2-arg) | 525 | 445 | **-15%** |
| 4. string.Format(3 args) (params) | 595 | 539 | **-9%** |
| 5. string.EndsWith (instance) | 348 | 311 | **-11%** |
| 6. string.Format(2 args) (2-arg) | 552 | 502 | **-9%** |

## Startup benchmark

Benchmark: `assets/language/repl/bench/bench_startup.slc`
Configuration: AOT binary (`dotnet publish -c Release -r win-x64 /p:PublishAot=true`)
Test: 10 iterations × 3 reps of `RunScript("bench_foreach.slc", true)`

| File | Per run |
|------|--------|
| bench_foreach.slc | 9 ms |
| crash_generator.slc | 9 ms |

**Note:** The REPL `.bench` command runs under **JIT** (not AOT). For the same script, `.bench` shows ~855ms per run vs ~9ms via AOT binary, a ~95× difference. This is expected: the interpreter's switch-heavy dispatch benefits massively from NativeAOT's whole-program optimization and native instruction set selection. Always use the AOT binary (`publish-aot/ScriptLangCore.exe`) for production benchmarks. The REPL `.bench` is useful for relative comparisons within the same session.

## Summary

9-15% improvement across all .NET method call scenarios. The zero-arg test shows no change (no allocation to eliminate). All other tests benefit from:

### Applied optimizations

| Optimization | Commits | Impact |
|-------------|---------|--------|
| `ArrayPool` for argument arrays | `c54f30b`, `5faebdd`, `4c10cf2`, `1e92100` | Eliminates `new object?[n]` per call |
| `string.Join` + interpolation → single StringBuilder | `bbfb200` | Reduces 3 allocations → 1 |
| `DictionaryPool` for scope copies | `099fab1` | Reduces closure allocation |
| `.Where().ToList()` → early-exit `foreach` | `b82e9bf` | Eliminates intermediate `List` allocations |

### Pending / deferred

| Item | Reason |
|------|--------|
| Pure function cache key | `args.ToArray()` required for cache correctness; negligible impact |
| `ReadOnlySpan<char>` migration | Needs profiling baseline first |
