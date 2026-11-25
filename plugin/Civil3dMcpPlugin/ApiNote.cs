namespace Civil3DMcpPlugin;

/// <summary>
/// One note on an API member a script uses (see <see cref="ApiNotes"/>):
/// which note, the member as the compiler resolved it, its first script line,
/// where the knowledge comes from, the text and the guide topic with details.
/// </summary>
internal sealed record ApiNote(string Id, string Member, int Line, string Source, string Text, string? GuideTopic);
