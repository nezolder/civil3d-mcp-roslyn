using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Civil3DMcpPlugin;

/// <summary>
/// Adds a cancellation checkpoint to the top of every loop body. Roslyn can
/// only cancel a script between awaits, and .NET cannot abort a thread, so a
/// synchronous loop would otherwise ignore the timeout on the Civil 3D main
/// thread.
/// </summary>
internal static class ScriptInstrumentation
{
  private static readonly CSharpParseOptions ParseOptions =
    new(LanguageVersion.Latest, kind: SourceCodeKind.Script);

  private static readonly StatementSyntax Checkpoint = SyntaxFactory.ParseStatement(
    "global::Civil3DMcpPlugin.ScriptCancellation.ThrowIfCancellationRequested();");

  /// <summary>
  /// Returns the code with checkpoints, or the original code when it does not
  /// parse. Inserted text never contains a line break, so script line numbers
  /// stay the same.
  /// </summary>
  public static string AddCancellationCheckpoints(string code)
  {
    var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
    if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) return code;

    var rewriter = new CheckpointRewriter();
    var instrumented = rewriter.Visit(tree.GetRoot()).ToFullString();
    if (rewriter.LoopCount == 0 || CountLineBreaks(instrumented) != CountLineBreaks(code)) return code;
    return instrumented;
  }

  /// <summary>
  /// One-based line and column of the first <c>await</c> keyword, or null.
  /// Scripts run synchronously on the Civil 3D main thread, so an await there
  /// can wait forever for a continuation queued to that same thread; unlike a
  /// loop, no checkpoint ever runs to stop it. A variable named "await" in a
  /// synchronous method is an identifier, not this keyword, and is allowed.
  /// </summary>
  public static (int Line, int Column)? FindAwait(string code)
  {
    var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
    var keyword = tree.GetRoot().DescendantTokens().FirstOrDefault(token => token.IsKind(SyntaxKind.AwaitKeyword));
    if (keyword == default) return null;
    var start = keyword.GetLocation().GetLineSpan().StartLinePosition;
    return (start.Line + 1, start.Character + 1);
  }

  private static int CountLineBreaks(string text) => text.Count(character => character == '\n');

  private sealed class CheckpointRewriter : CSharpSyntaxRewriter
  {
    public int LoopCount { get; private set; }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
      var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
      return visited.WithStatement(AddCheckpoint(visited.Statement));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
      var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
      return visited.WithStatement(AddCheckpoint(visited.Statement));
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
      var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
      return visited.WithStatement(AddCheckpoint(visited.Statement));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
      var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
      return visited.WithStatement(AddCheckpoint(visited.Statement));
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
      var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
      return visited.WithStatement(AddCheckpoint(visited.Statement));
    }

    private StatementSyntax AddCheckpoint(StatementSyntax body)
    {
      // A declaration is not allowed as a loop body; wrapping it in a block
      // would turn that compile error into valid code.
      if (body is LocalDeclarationStatementSyntax or LabeledStatementSyntax or LocalFunctionStatementSyntax)
      {
        return body;
      }

      LoopCount++;
      // Keep the body's surrounding trivia outside the new braces so every
      // original token stays on its line.
      return SyntaxFactory.Block(Checkpoint, body.WithoutLeadingTrivia().WithoutTrailingTrivia())
        .WithLeadingTrivia(body.GetLeadingTrivia())
        .WithTrailingTrivia(body.GetTrailingTrivia());
    }
  }
}
