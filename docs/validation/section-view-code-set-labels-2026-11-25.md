# Code set labels on section views made through the API (2026-11-25)

**Question:** a field project reported that section views created through the .NET API did not
show the code set style labels of the corridor sections, while views placed with the Civil 3D
dialog did, with the same view style and band set. The project's code set style gives some point
codes a label style tuned to land in the bottom bands (design elevation and offset rows).

**Setting:** Civil 3D 2025 (Hungarian), throwaway copies of that project's drawings (cross-section
drawing with 12 sample line groups and 81 dialog-made views; corridors, alignments, profiles and the
ground surface as data references to copied source drawings). Plugin and server at `7d6b2d0`.

## Results

| Check | Dialog view | API view |
| --- | --- | --- |
| Code set labels in the bands (seen on screen by the owner) | shown | **not shown** |
| Objects created with the view (handle range read before and after) | 1 `SectionView`, 4 `SectionDataBandLabelGroup` | the same |
| View style, band set, band items and their sources, gaps, weeding | the same | the same |
| `GetSectionCorridorPointLabelGroupIds()` | 0 | 0 |
| Section overrides (40), `GetSectionLabelGroupIds()` per section | 0 groups, all drawn | the same |
| Reflection over about 300 readable values (properties, band items, band label groups, extension dictionaries, xrecords, XData, persistent reactors) | | only location, station, group name, the automatic range flags and band label counts differ |

- Re-applying the code set style to the corridor section (by hand on one API view, through a
  section override on another), `REGENALL` and updating the section view layout did not add the
  labels.
- A new view placed with the dialog at the time of the test showed the labels, so this is not an
  old state of the earlier views.
- `SectionCorridorPointLabelGroup.Create` works, but adds separate corridor point labels with
  their own label style inside the view, not the code set style labels.
- The .NET API (Civil 3D 2025 metadata) has no member for the corridor point code labeling of
  section views and no method that resets or updates code set labels.

**Conclusion (likely, not provable through .NET):** the dialog switches the code set labels on in
data the .NET API does not reach. The guide (`civil` topic) and the `create_section_view_group`
recipe now say so: when sheets rely on such labels, those views are placed with the dialog.

## Also seen

- `SectionViewGroups.Add(point, start, end, ...)` in a direct script left out the sample lines at
  exactly 100 and 200 (only 150 was created); 99 to 201 created all three.
- Opening the copy made Civil 3D read the data reference sources through the profile's data
  shortcut working folder, which pointed at the original project folder. While the copy was open,
  creating section views left `.dwl` lock files next to two original source drawings; they went
  away when that Civil 3D closed. No original file was changed (snapshot of size and time before
  and after).

## Not tried

- Creating views by running the Civil 3D command from a script (`SendStringToExecute`), and the
  COM API.
