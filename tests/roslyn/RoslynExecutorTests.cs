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

static void Assert(bool condition, string message)
{
  if (!condition) throw new InvalidOperationException(message);
}
