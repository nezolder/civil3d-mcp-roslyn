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

  /// <summary>
  /// Hands a PNG or JPEG file back with the result, so that the assistant
  /// sees it: <c>return new { sheet = name, picture = ImageFile(path) };</c>.
  /// At most four images per result, two megabytes each.
  /// </summary>
  public ScriptImage ImageFile(string path) => new(path);

  /// <summary>
  /// A new file path in the plugin's own image folder, for a picture or PDF
  /// that only has to live until it is returned. Files there are removed
  /// after a day. Extension: png, jpg, jpeg or pdf.
  /// </summary>
  public string NewImagePath(string extension = "png") => ScriptImages.NewPath(extension, DateTime.Now);
}
