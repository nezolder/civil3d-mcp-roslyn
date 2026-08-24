using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Globals exposed to Roslyn scripts. Every property here is accessible
/// directly by name in the C# code sent by the AI.
/// </summary>
public class ScriptContext
{
  public Document Document { get; }
  public CivilDocument CivilDoc { get; }
  public Database Database { get; }
  public Transaction Transaction { get; }
  public Editor Editor { get; }

  public ScriptContext(
    Document document,
    CivilDocument civilDoc,
    Database database,
    Transaction transaction)
  {
    Document = document;
    CivilDoc = civilDoc;
    Database = database;
    Transaction = transaction;
    Editor = document.Editor;
  }
}
