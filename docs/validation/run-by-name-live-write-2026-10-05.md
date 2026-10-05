# Named write recipe and populated-read live validation

Date: 2026-10-05 (Europe/Budapest). Tested Node source: `73b7fa3d71a54c0a46e5d5a105679b4bb218bd93`. This commit corrects the CRLF test; `src/` and `plugin/` are unchanged from `9324388`. The live host was Civil 3D 2025 Hungary. Its permanently installed plugin remained the `f3cc05c` build, whose plugin source is identical to the tested branch.

This supplements [the earlier retest](run-by-name-9324388-2026-10-05.md) with a real named-write execution, save, independent reopen, and additional named-read checks.

## Existing read-only drawing

**Proven:** named `drawing_info` and parameterized `data_reference_audit` calls succeeded on an existing read-only road-plan drawing containing nonempty ModelSpace CAD geometry and xref references. The full drawing identity was guarded. A separate final client confirmed the same filename and fingerprint, `Document.IsReadOnly=true`, and unchanged `DBMOD=0`. No write or save was directed to this drawing.

**Unverified:** the drawing had no directly owned Civil objects in the inspected categories. Its CAD geometry and xrefs therefore do not establish populated native Civil inventory coverage. Xref contents were not recursively audited. Client identifiers, counts, drawing names, and paths are omitted from this public report.

## Named write on a saved disposable fixture

**Proven:** a separate process was launched with the user's Civil 3D 2025 Hungary shortcut and a named disposable DWG copied from the stock Hungary template. Before each write, the full filename, fingerprint, and `DBMOD=0` were checked. Unchanged filesystem backups were verified by size and SHA-256 before both writes.

1. A preparation write created an open four-vertex 2D polyline with synthetic coordinates `(0,0)`, `(100,0)`, `(150,50)`, `(250,50)`. Its length was `270.71067811865476`. The preparation write used `saveDrawing:true` and was independently read back.
2. The tested `civil3d_execute` request supplied `skill: "create_alignment_from_polyline"`, `params` containing the source handle and a unique synthetic alignment name, and `saveDrawing:true`. **It supplied no `code` field.** The request also included the freshly observed drawing guard, selected instance, and a new idempotency key.
3. The recipe returned success. It created one siteless alignment with three straight entities, length `270.71067811865476`, and the required alignment style. The source polyline was preserved.
4. A new client and separate read transaction confirmed the alignment count, geometry, source preservation, all four unchanged source coordinates, and `DBMOD=0`.
5. After a normal close, the saved DWG was reopened in a fresh Hungary process. A new client confirmed the same alignment and source handles, fingerprint, three entities, length, and `DBMOD=0`.
6. The test process closed normally. The saved file's SHA-256 was unchanged by the reopen and read checks; the permanently installed plugin hash also remained unchanged.

This is targeted live evidence for named write execution, persistence, and independent reopen. It is not mock-plugin evidence.

## Named reads with actual native Civil data

**Proven on the saved synthetic Civil fixture**, both immediately after writing and after independent reopen:

- `drawing_info` returned one alignment and `DBMOD=0`.
- `list_alignments` returned the created alignment with the expected handle and length.
- `alignment_geometry_audit`, with explicit `alignmentHandle` and `limit:10`, returned the complete three-line geometry, zero arcs and spirals, no truncation, and `DBMOD=0`.

**Unverified:** broad coverage of a complex populated production Civil model, other named write recipes, and creation of curved or spiral alignments through this mechanism. These results establish the specific recipe and read scenarios above.

## Windows CRLF regression check

**Proven:** `npm run test:skills` passed on the ordinary Windows CRLF checkout after `73b7fa3`: 32 Node tests, Civil 3D 2025 metadata compilation of 32 C# templates, and 83 host-free validation cases. The earlier newline assertion failure is resolved. NuGet emitted `NU1900` because advisory retrieval was unavailable; there were no compilation or test failures.

Raw local results, client drawing identity, backups, DWGs, filesystem hashes, and logs remain private. No benchmark timings are included.
