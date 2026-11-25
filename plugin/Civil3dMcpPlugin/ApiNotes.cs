using Microsoft.CodeAnalysis;

namespace Civil3DMcpPlugin;

/// <summary>
/// Warnings about API members whose behaviour has surprised earlier work:
/// a crash in field use, a value that is not what its name suggests, a
/// documented limit. Unlike the script guard they refuse nothing. A script
/// that uses such a member gets a short note next to its result, found from
/// what the compiler resolved, so the note reaches the assistant exactly when
/// it uses the member and not only when it has read the guide.
///
/// Each note says where it comes from: "live" when this repository saw it in
/// Civil 3D 2025, "field" when it was reported from project work and not
/// reproduced here, "api" when it follows from how the API is documented. Keep the texts short; the guide topic named in a note
/// holds the details.
/// </summary>
internal static class ApiNotes
{
  internal sealed record Note(string Id, string Source, string Text, string? Topic);

  internal const int MaxNotesPerScript = 8;

  private static readonly Dictionary<(string Type, string Member), Note> Notes = new()
  {
    [("Autodesk.Civil.DatabaseServices.AppliedSubassembly", "Parameters")] = new(
      "applied-parameters-override",
      "field",
      "Reading AppliedSubassembly.Parameters where a parameter is overridden for a station range has crashed " +
        "Civil 3D. Read the subassembly's own ParamsDouble/ParamsLong/... (assembly defaults) or the built geometry " +
        "(recipe corridor_applied_section) instead, or ask the user to read the value in Civil 3D.",
      "csharp"),
    [("Autodesk.Civil.DatabaseServices.SectionViewGroupCollection", "Add")] = new(
      "section-view-group-end-stations",
      "live",
      "SectionViewGroups.Add with a station range can leave out the sample line exactly at its start station. " +
        "Pass a range slightly wider than the wanted lines, but not before the alignment start (Add throws), " +
        "and read back which sample lines got a view; recipe create_section_view_group does this.",
      "civil"),
    [("Autodesk.Civil.DatabaseServices.Surface", "GetBoundedVolumes")] = new(
      "bounded-volumes-raw",
      "live",
      "GetBoundedVolumes on a volume surface returns raw cut and fill, without the surface's cut and fill factors. " +
        "Apply TinVolumeSurface.CutFactor and FillFactor yourself (recipe bounded_volumes does).",
      null),
    [("Autodesk.Civil.DatabaseServices.SampleLineGroup", "GetSectionSources")] = new(
      "section-sources-for-write",
      "live",
      "GetSectionSources needs the sample line group opened ForWrite; on a group opened for read Civil 3D 2025 has " +
        "closed with a fatal eNotOpenForWrite.",
      "civil"),
    [("Autodesk.Civil.DatabaseServices.Corridor", "Rebuild")] = new(
      "corridor-rebuild-long",
      "field",
      "A corridor rebuild can take long and Civil 3D updates its dependents after the command. Rebuild one corridor " +
        "per call, nothing else heavy in the same call; afterwards check targets (recipe corridor_target_audit).",
      "civil"),
    [("Autodesk.Civil.DatabaseServices.Assembly", "AddSubassembly")] = new(
      "assembly-add-subassembly",
      "field",
      "Assembly.AddSubassembly does not re-attach a subassembly that is already in an assembly. Check the new " +
        "object, its attachment and the rebuilt corridor separately.",
      "csharp"),
    [("Autodesk.AutoCAD.DatabaseServices.Database", "SaveAs")] = new(
      "save-inside-script",
      "live",
      "Do not save from the script: inside the script's transaction a save can fail with eFilerError, and the " +
        "receipt cannot report it. Use saveDrawing: true on civil3d_execute.",
      "writing"),
    [("Autodesk.AutoCAD.ApplicationServices.Document", "SendStringToExecute")] = new(
      "send-string-after-call",
      "api",
      "A command queued with SendStringToExecute runs after this call has ended: outside its transaction, " +
        "receipt and Undo step, and possibly after the next call. Use the API in the script, or saveDrawing for a save.",
      "writing"),
    [("Autodesk.AutoCAD.ApplicationServices.DocumentCollectionExtension", "Open")] = new(
      "document-switch",
      "field",
      "A script must not open, close or switch drawings: the call is bound to the active drawing and its " +
        "transaction. Read another file in a side database (recipe saved_drawing_readback) or ask the user.",
      "drawing"),
    [("Autodesk.AutoCAD.ApplicationServices.DocumentCollectionExtension", "Add")] = new(
      "document-switch",
      "field",
      "A script must not open, close or switch drawings: the call is bound to the active drawing and its " +
        "transaction. Read another file in a side database (recipe saved_drawing_readback) or ask the user.",
      "drawing"),
    [("Autodesk.AutoCAD.ApplicationServices.DocumentExtension", "CloseAndSave")] = new(
      "document-switch",
      "field",
      "A script must not open, close or switch drawings: the call is bound to the active drawing and its " +
        "transaction. Read another file in a side database (recipe saved_drawing_readback) or ask the user.",
      "drawing"),
    [("Autodesk.AutoCAD.ApplicationServices.DocumentExtension", "CloseAndDiscard")] = new(
      "document-switch",
      "field",
      "A script must not open, close or switch drawings: the call is bound to the active drawing and its " +
        "transaction. Read another file in a side database (recipe saved_drawing_readback) or ask the user.",
      "drawing"),
  };

  /// <summary>Every noted member, for the check against the Civil 3D 2025 metadata.</summary>
  internal static IEnumerable<(string Type, string Member)> NotedMembers => Notes.Keys;

  /// <summary>The member names, for a cheap text check before the semantic walk.</summary>
  private static readonly string[] MemberNames = Notes.Keys.Select(key => key.Member).Distinct().ToArray();

  /// <summary>
  /// The notes for one compiled script, one per note id, in reading order.
  /// Never throws: a note is a help, and a failure to find one must not stop
  /// the script.
  /// </summary>
  internal static IReadOnlyList<ApiNote> Find(string code, Compilation compilation)
  {
    try
    {
      if (!MemberNames.Any(name => code.Contains(name, StringComparison.Ordinal))) return Array.Empty<ApiNote>();
      var found = new List<ApiNote>();
      var seen = new HashSet<string>(StringComparer.Ordinal);
      foreach (var (symbol, line) in ScriptSandbox.ResolvedSymbols(compilation))
      {
        if (symbol is not (IMethodSymbol or IPropertySymbol) || symbol.ContainingType is null) continue;
        // An extension method called as doc.Method() resolves to its reduced form.
        var member = symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : symbol;
        for (var owner = member.ContainingType; owner is not null; owner = owner.BaseType)
        {
          var ownerName = ScriptSandbox.FullName(owner);
          if (!Notes.TryGetValue((ownerName, member.Name), out var note)) continue;
          if (seen.Add(note.Id)) found.Add(new ApiNote(note.Id, ownerName + "." + member.Name, line, note.Source, note.Text, note.Topic));
          break;
        }
        if (found.Count >= MaxNotesPerScript) break;
      }
      return found;
    }
    catch (Exception)
    {
      return Array.Empty<ApiNote>();
    }
  }
}
