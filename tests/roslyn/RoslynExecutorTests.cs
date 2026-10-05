using Civil3DMcpPlugin;

// Runs the real RoslynExecutor against stub globals. Host-independent: no
// Autodesk assemblies or Civil 3D process are needed.
var tests = new (string Name, Func<Task> Run)[]
{
  ("repeated script is served from the cache", RepeatedScriptIsCachedAsync),
  ("failed compilation is reported every time and never cached", FailedCompilationIsNotCachedAsync),
  ("cache stays bounded and keeps recent scripts", CacheIsBoundedAsync),
  ("runtime failure reports exception type and script line", RuntimeFailureLineAsync),
  ("failure inside a script method reports that line", RuntimeFailureInMethodAsync),
  ("failure inside framework code reports the calling script line", RuntimeFailureInFrameworkAsync),
  ("reflection invocation reports the inner exception", RuntimeFailureThroughReflectionAsync),
  ("misspelled instance member lists similar members", MissingInstanceMemberAsync),
  ("similar members include inherited ones and adjacent swaps", MissingInheritedMemberAsync),
  ("misspelled static member lists similar members", MissingStaticMemberAsync),
  ("missing type names the namespace to import", MissingTypeAsync),
  ("missing static class names the namespace to import", MissingStaticClassAsync),
  ("unknown name without a match keeps the previous message exactly", UnknownNameAsync),
  ("hints are capped", HintsAreCappedAsync),
  ("instrumentation keeps every line break and leaves loop-free code alone", InstrumentationKeepsLinesAsync),
  ("loops still compute their normal results", LoopResultsAsync),
  ("runaway while, for, foreach and do loops stop at the timeout", RunawayLoopsStopAtTimeoutAsync),
  ("caller cancellation stops a running loop", CallerCancellationStopsLoopAsync),
  ("runtime line numbers are unchanged around loops", LoopRuntimeLineAsync),
  ("compile errors inside loops report the caller's column", LoopCompileErrorColumnAsync),
  ("a declaration as loop body stays a compile error", DeclarationLoopBodyAsync),
};

foreach (var (name, run) in tests)
{
  await run();
  Console.WriteLine($"PASS {name}");
}

static ScriptContext Context() => new();

static async Task<JsonRpcDispatchException> FailAsync(string code)
{
  try
  {
    await RoslynExecutor.ExecuteAsync(code, Context());
  }
  catch (JsonRpcDispatchException ex)
  {
    return ex;
  }
  throw new InvalidOperationException($"script was expected to fail: {code}");
}

static async Task RepeatedScriptIsCachedAsync()
{
  RoslynExecutor.ClearCache();
  Assert(Equals(await RoslynExecutor.ExecuteAsync("return Value + 1;", Context()), 42), "script must read globals");
  Assert(Equals(await RoslynExecutor.ExecuteAsync("return Value + 1;", Context()), 42), "repeat must return the same value");
  Assert(RoslynExecutor.CachedScriptCount == 1, "repeat must reuse the cached script");
}

static async Task FailedCompilationIsNotCachedAsync()
{
  RoslynExecutor.ClearCache();
  var first = await FailAsync("return NoSuchName_Cache;");
  var second = await FailAsync("return NoSuchName_Cache;");
  Assert(first.Code == "CIVIL3D.COMPILATION_ERROR" && first.Message == second.Message,
    "a repeated failing script must report the same compilation error");
  Assert(RoslynExecutor.CachedScriptCount == 0, "failed compilations must not be cached");
  Assert(Equals(await RoslynExecutor.ExecuteAsync("return Value + 2;", Context()), 43),
    "a valid script must compile after a failure");
}

static async Task CacheIsBoundedAsync()
{
  RoslynExecutor.ClearCache();
  for (var i = 0; i < RoslynExecutor.ScriptCacheCapacity + 6; i++)
  {
    await RoslynExecutor.ExecuteAsync($"return {i};", Context());
  }
  Assert(RoslynExecutor.CachedScriptCount == RoslynExecutor.ScriptCacheCapacity, "cache must stay at capacity");
  var last = RoslynExecutor.ScriptCacheCapacity + 5;
  Assert(Equals(await RoslynExecutor.ExecuteAsync($"return {last};", Context()), last),
    "a recent script must return its own value");
}

static async Task RuntimeFailureLineAsync()
{
  var error = await FailAsync("string s = null;\nvar x = 1;\nreturn s.Length + x;");
  Assert(error.Code == "CIVIL3D.TRANSACTION_FAILED", "runtime failures keep their existing code");
  Assert(error.Message == "Script threw System.NullReferenceException at script line 3: " +
    "Object reference not set to an instance of an object.", $"unexpected message: {error.Message}");
}

static async Task RuntimeFailureInMethodAsync()
{
  var error = await FailAsync("int F(int[] a) => a[5];\nreturn F(new int[1]);");
  Assert(error.Message.StartsWith("Script threw System.IndexOutOfRangeException at script line 1:", StringComparison.Ordinal),
    $"unexpected message: {error.Message}");
}

static async Task RuntimeFailureInFrameworkAsync()
{
  var error = await FailAsync("var t = \"x\";\n\nreturn int.Parse(t);");
  Assert(error.Message.StartsWith("Script threw System.FormatException at script line 3:", StringComparison.Ordinal),
    $"unexpected message: {error.Message}");
}

static async Task RuntimeFailureThroughReflectionAsync()
{
  var error = await FailAsync(
    "var parse = typeof(int).GetMethod(\"Parse\", new[] { typeof(string) });\n" +
    "return parse.Invoke(null, new object[] { \"x\" });");
  Assert(error.Message.StartsWith("Script threw System.FormatException at script line 2:", StringComparison.Ordinal),
    $"unexpected message: {error.Message}");
}

static async Task MissingInstanceMemberAsync()
{
  var error = await FailAsync("return new Alignment().Lenght;");
  Assert(error.Code == "CIVIL3D.COMPILATION_ERROR", "compile failures keep their existing code");
  Assert(error.Message.StartsWith("C# compilation failed:\n(1,24): error CS1061:", StringComparison.Ordinal),
    "the compiler error must stay first and unchanged");
  Assert(error.Message.EndsWith("\nHints:\n- Autodesk.Civil.DatabaseServices.Alignment has no member 'Lenght'. " +
    "Similar members, including inherited: Length.", StringComparison.Ordinal), $"unexpected message: {error.Message}");
}

static async Task MissingInheritedMemberAsync()
{
  var error = await FailAsync("return new Alignment().Nmae;");
  Assert(error.Message.Contains("Similar members, including inherited: Name.", StringComparison.Ordinal),
    $"unexpected message: {error.Message}");
}

static async Task MissingStaticMemberAsync()
{
  var error = await FailAsync("return Math.Sqroot(4.0);");
  Assert(error.Message.Contains("error CS0117", StringComparison.Ordinal)
    && error.Message.Contains("System.Math has no member 'Sqroot'. Similar members, including inherited: Sqrt.", StringComparison.Ordinal),
    $"unexpected message: {error.Message}");
}

static async Task MissingTypeAsync()
{
  var error = await FailAsync("return typeof(SurfaceStyle).Name;");
  Assert(error.Message.Contains("error CS0246", StringComparison.Ordinal)
    && error.Message.Contains("'SurfaceStyle' is a type in Autodesk.Civil.DatabaseServices.Styles. " +
      "Add a using line such as `using Autodesk.Civil.DatabaseServices.Styles;` or write the full name.", StringComparison.Ordinal),
    $"unexpected message: {error.Message}");
}

static async Task MissingStaticClassAsync()
{
  var error = await FailAsync("return Path.Combine(\"a\", \"b\");");
  Assert(error.Message.Contains("error CS0103", StringComparison.Ordinal)
    && error.Message.Contains("`using System.IO;`", StringComparison.Ordinal), $"unexpected message: {error.Message}");
}

static async Task UnknownNameAsync()
{
  var error = await FailAsync("return CacheProbeMissingSymbol_20261005;");
  Assert(error.Message == "C# compilation failed:\n(1,8): error CS0103: " +
    "The name 'CacheProbeMissingSymbol_20261005' does not exist in the current context", $"unexpected message: {error.Message}");
}

static async Task HintsAreCappedAsync()
{
  var lines = Enumerable.Range(0, ScriptDiagnostics.MaximumHints + 5)
    .Select(i => $"var v{i} = new Alignment().Lenght{i};");
  var error = await FailAsync(string.Join("\n", lines) + "\nreturn 0;");
  var hintCount = error.Message.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));
  Assert(hintCount == ScriptDiagnostics.MaximumHints, $"expected {ScriptDiagnostics.MaximumHints} hints, got {hintCount}");
}

static Task InstrumentationKeepsLinesAsync()
{
  var code = "var total = 0;\nfor (var i = 0; i < 3; i++)\n{\n  total += i;\n}\nwhile (total > 100) total--;\nreturn total;";
  var instrumented = ScriptInstrumentation.AddCancellationCheckpoints(code);
  Assert(instrumented != code, "loops must receive checkpoints");
  Assert(instrumented.Split('\n').Length == code.Split('\n').Length, "line count must not change");
  Assert(instrumented.Split("ScriptCancellation.ThrowIfCancellationRequested()").Length == 3,
    $"each loop must receive one checkpoint: {instrumented}");
  Assert(ScriptInstrumentation.AddCancellationCheckpoints("return 1;") == "return 1;", "loop-free code must be unchanged");
  Assert(ScriptInstrumentation.AddCancellationCheckpoints("while (true) {") == "while (true) {",
    "code that does not parse must be unchanged");
  return Task.CompletedTask;
}

static async Task LoopResultsAsync()
{
  var result = await RoslynExecutor.ExecuteAsync(
    "var s = 0;\n" +
    "for (var i = 0; i < 10; i++) s += i;\n" +
    "foreach (var (a, b) in new[] { (1, 2), (3, 4) }) s += a * b;\n" +
    "var n = 0; do n++; while (n < 5);\n" +
    "while (n < 8) { n++; if (n == 7) continue; }\n" +
    "IEnumerable<int> Three() { for (var k = 0; k < 3; k++) yield return k; }\n" +
    "return s + n + Three().Sum();", Context());
  Assert(Equals(result, 45 + 14 + 8 + 3), $"unexpected loop result: {result}");
}

static async Task RunawayLoopsStopAtTimeoutAsync()
{
  var previous = RoslynExecutor.Timeout;
  RoslynExecutor.Timeout = TimeSpan.FromMilliseconds(200);
  try
  {
    foreach (var code in new[]
    {
      "while (true) { }",
      "for (;;) ;",
      "IEnumerable<int> Forever() { while (true) yield return 1; }\nforeach (var x in Forever()) { }",
      "var i = 0;\ndo { i++; } while (true);",
      "void Spin() { while (true) { } }\nSpin();",
    })
    {
      var watch = System.Diagnostics.Stopwatch.StartNew();
      var error = await FailAsync(code).WaitAsync(TimeSpan.FromSeconds(10));
      Assert(error.Code == "CIVIL3D.TIMEOUT", $"runaway loop must time out: {code}");
      Assert(watch.Elapsed < TimeSpan.FromSeconds(5), $"timeout must stop the loop promptly: {code}");
    }
  }
  finally
  {
    RoslynExecutor.Timeout = previous;
  }
}

static async Task CallerCancellationStopsLoopAsync()
{
  using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
  try
  {
    await RoslynExecutor.ExecuteAsync("while (true) { }", Context(), null, null, caller.Token)
      .WaitAsync(TimeSpan.FromSeconds(10));
  }
  catch (OperationCanceledException) when (caller.IsCancellationRequested)
  {
    Assert(ScriptCancellation.Token == default, "the checkpoint token must be reset after the run");
    return;
  }
  throw new InvalidOperationException("caller cancellation must surface as cancellation, not a reply");
}

static async Task LoopRuntimeLineAsync()
{
  var error = await FailAsync(
    "var items = new[] { 1, 2, 3 };\n" +
    "foreach (var item in items)\n" +
    "{\n" +
    "  if (item == 3) throw new InvalidOperationException(\"third\");\n" +
    "}\n" +
    "return 0;");
  Assert(error.Message == "Script threw System.InvalidOperationException at script line 4: third",
    $"unexpected message: {error.Message}");
}

static async Task LoopCompileErrorColumnAsync()
{
  var code = "for (var i = 0; i < 2; i++) { var y = Nope; }";
  var error = await FailAsync(code);
  var column = code.IndexOf("Nope", StringComparison.Ordinal) + 1;
  Assert(error.Message == $"C# compilation failed:\n(1,{column}): error CS0103: The name 'Nope' does not exist in the current context",
    $"unexpected message: {error.Message}");
}

static async Task DeclarationLoopBodyAsync()
{
  var error = await FailAsync("while (false) var z = 1;\nreturn 0;");
  Assert(error.Code == "CIVIL3D.COMPILATION_ERROR" && error.Message.Contains("CS1023", StringComparison.Ordinal),
    $"unexpected message: {error.Message}");
}

static void Assert(bool condition, string message)
{
  if (!condition) throw new InvalidOperationException(message);
}
