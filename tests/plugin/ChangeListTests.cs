using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

internal static class ChangeListTests
{
  public static void RunAll()
  {
    var tests = new (string Name, Action Run)[]
    {
      ("change list counts by type and lists the first objects of each kind", CountsAndItems),
      ("change list keeps containers apart and a zero item limit leaves only counts", ContainersAndCountsOnly),
      ("change list says when it is not complete, and why", GapsAreNamed),
      ("change list of a preview reports whether everything was taken back", PreviewRollback),
      ("an unavailable change list says so without numbers", Unavailable),
    };

    foreach (var (name, run) in tests)
    {
      run();
      Console.WriteLine($"PASS {name}");
    }
  }

  private static void CountsAndItems()
  {
    var list = new ChangeList(2);
    Assert(list.WantsItem(ChangeKind.Added), "an empty group still wants items");
    list.Add(ChangeKind.Added, new ChangedObject("Polyline", "1A", null, "C-ROAD"));
    list.Add(ChangeKind.Added, new ChangedObject("Alignment", "1B", "Tengely\r\n 1", "C-ROAD"));
    Assert(!list.WantsItem(ChangeKind.Added), "a full group wants no more items");
    list.Add(ChangeKind.Added, new ChangedObject("Polyline", "1C"));
    list.Add(ChangeKind.Modified, new ChangedObject("Profile", "F0", new string('x', 200)));
    var json = list.ToJson();

    var added = json["added"]!.AsObject();
    Assert(added["count"]!.GetValue<int>() == 3 && added["more"]!.GetValue<int>() == 1,
      "the count covers every object, also the ones not listed");
    Assert(added["byType"]!.AsObject().Select(pair => pair.Key).SequenceEqual(new[] { "Polyline", "Alignment" })
      && added["byType"]!["Polyline"]!.GetValue<int>() == 2,
      "types are counted, the most frequent first");
    var items = added["items"]!.AsArray();
    Assert(items.Count == 2, "the item limit bounds the listed objects");
    Assert(items[0]!["handle"]!.GetValue<string>() == "1A" && items[0]!.AsObject().ContainsKey("name") == false
      && items[0]!["layer"]!.GetValue<string>() == "C-ROAD",
      "an item carries handle and layer, and no empty name");
    Assert(items[1]!["name"]!.GetValue<string>() == "Tengely 1", "a name is kept as one line");

    var modified = json["modified"]!.AsObject();
    Assert(modified["count"]!.GetValue<int>() == 1 && !modified.ContainsKey("more"),
      "a group that lists everything has no remainder");
    Assert(modified["items"]![0]!["name"]!.GetValue<string>().Length == 80, "a long name is cut");
    Assert(json["erased"]!.AsObject().Count == 1 && json["erased"]!["count"]!.GetValue<int>() == 0,
      "an empty group is only its zero count");
    Assert(json["complete"]!.GetValue<bool>() && !json.ContainsKey("note") && !json.ContainsKey("containers")
      && !json.ContainsKey("rolledBack"),
      "a complete list of a committed write needs no remark");
    Assert(list.AddedCount == 3 && list.ModifiedCount == 1 && list.ErasedCount == 0, "counts are readable for the journal");
  }

  private static void ContainersAndCountsOnly()
  {
    var list = new ChangeList(0);
    list.Add(ChangeKind.Erased, new ChangedObject("Line", "2A", null, "0"));
    list.AddContainer("BlockTableRecord");
    list.AddContainer("DBDictionary");
    list.AddContainer("DBDictionary");
    var json = list.ToJson();
    var erased = json["erased"]!.AsObject();
    Assert(erased["count"]!.GetValue<int>() == 1 && erased["byType"]!["Line"]!.GetValue<int>() == 1
      && !erased.ContainsKey("items") && !erased.ContainsKey("more"),
      "with a zero item limit only the counts remain");
    Assert(json["containers"]!["DBDictionary"]!.GetValue<int>() == 2
      && json["containers"]!["BlockTableRecord"]!.GetValue<int>() == 1
      && json["modified"]!["count"]!.GetValue<int>() == 0,
      "tables and dictionaries are counted apart from the changed objects");
    Assert(new ChangeList(5000).WantsItem(ChangeKind.Added) && !new ChangeList(-3).WantsItem(ChangeKind.Added),
      "an item limit outside the range is clamped");

    var many = new ChangeList(0);
    for (var index = 0; index < 45; index++) many.Add(ChangeKind.Added, new ChangedObject($"Type{index:00}", null));
    var byType = many.ToJson()["added"]!["byType"]!.AsObject();
    Assert(byType.Count == 41 && byType["other"]!.GetValue<int>() == 5, "a long tail of types is summed up");
  }

  private static void GapsAreNamed()
  {
    var list = new ChangeList(3) { AddedScanTruncated = true, TouchedOverflow = true, InternalChangesUnobserved = true };
    var json = list.ToJson();
    var note = json["note"]!.GetValue<string>();
    Assert(!json["complete"]!.GetValue<bool>(), "a list with gaps must not claim to be complete");
    Assert(note.Contains("on its own", StringComparison.Ordinal)
      && note.Contains("added is a lower bound", StringComparison.Ordinal)
      && note.Contains("modified and erased are lower bounds", StringComparison.Ordinal),
      "every gap is named");
  }

  private static void PreviewRollback()
  {
    var clean = new ChangeList(3) { Preview = true, LeftAfterPreview = 0 };
    clean.Add(ChangeKind.Added, new ChangedObject("Alignment", "3A", "T-1"));
    var cleanJson = clean.ToJson();
    Assert(cleanJson["rolledBack"]!.GetValue<bool>() && !cleanJson.ContainsKey("leftAfterPreview"),
      "a preview that left nothing behind says so");
    Assert(!cleanJson["complete"]!.GetValue<bool>()
      && cleanJson["note"]!.GetValue<string>().Contains("preview", StringComparison.Ordinal),
      "a preview never claims to show what a commit would trigger");

    var dirty = new ChangeList(3) { Preview = true, LeftAfterPreview = 2 }.ToJson();
    Assert(!dirty["rolledBack"]!.GetValue<bool>() && dirty["leftAfterPreview"]!.GetValue<int>() == 2,
      "objects that stayed behind are reported");

    Assert(!new ChangeList(3) { Preview = true }.ToJson().ContainsKey("rolledBack"),
      "without the check nothing is claimed either way");
  }

  private static void Unavailable()
  {
    var list = ChangeList.Unavailable("InvalidOperationException:\r\n eWasOpenForWrite");
    var json = list.ToJson();
    Assert(!list.IsAvailable && json["available"]!.GetValue<bool>() == false
      && json["reason"]!.GetValue<string>() == "InvalidOperationException: eWasOpenForWrite"
      && json.Count == 2,
      "an unavailable list carries its reason and nothing that looks like a result");
  }

  private static void Assert(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }
}
