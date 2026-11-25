# Live smoke test

One command that checks an installed plugin and this checkout's MCP server together in a running
Civil 3D. It builds a small generated test drawing through the MCP tools and runs a fixed list of
read and write cases on it. No work drawing and no copy of one is needed.

```powershell
npm run build
npm run smoke:live -- --start-civil
```

The run writes `smoke-report.md` and `smoke-report.json` into its output folder and ends with exit
code `0` when every case passed, `1` when a case failed and `2` when it could not start.

## What it does

1. **Start** (`--start-civil`, Windows): a first `acad.exe` start makes a new drawing from the
   template, saves it as `<out>\mcp_smoke.dwg` and quits. A second start opens that file, the way a
   drawing is normally opened, and the run waits until the plugin of that process registers (by
   process id) with that drawing active. (In the first live run a drawing named by SAVEAS in the
   same session failed its second saved write with `eFileAccessErr`.) Click into the new window
   once if nothing happens; Civil 3D may wait for that before it runs the first command.
2. **Target check**: `civil3d_skills` `status` must show the instance with the session token
   accepted and nothing running (differing plugin and server builds are a warning). A read must then show that the active drawing is
   this run's `mcp_smoke*.dwg`, still holds no surface, alignment, corridor or assembly, and has no
   unsaved changes. Otherwise the run stops before its first write.
3. **Test drawing**, each step one write with `expectedDrawing` and `saveDrawing`:
   a planar TIN surface from a 21 x 9 grid, a centerline polyline, an alignment made from it, a
   ground profile with a profile view, a design profile from three PVIs, sample lines every 20
   units, and an assembly with the stock BasicLane on both sides with a corridor along the design
   profile. Style names are taken from the drawing (the first of each kind), so any template works.
   The corridor step is optional: when the stock subassembly cannot be loaded it is reported as a
   warning and the corridor cases are left out.
   These steps do not set `requireClean`: Civil 3D can mark the drawing modified again right after
   a save while it updates a new profile view or corridor by itself (seen live: `DBMOD` 1 some
   40 ms after a saved write had reported 0). Before the write cases, and after the corridor
   rebuild (and after the real write, see `W2b`), a settle step saves, waits 1.5 s and reads `DBMOD`, at most three times, until the
   drawing stays clean.
4. **Reading**: catalog recipes by name (`drawing_info`, `list_alignments`, `list_surfaces`,
   `list_profiles`, `section_inventory`, `surface_elevation`, `profile_elevation_at_station`,
   `station_offset`, `corridor_summary`), with values checked against the generated geometry.
5. **Writing**: a preview leaves nothing and keeps the drawing clean; the same write for real with
   backup and save lists exactly the new line and the changed alignment and the backup file
   exists; a script that fails after a change is rolled back; a write without save leaves the
   drawing modified and `requireClean` then refuses; a save makes it clean again; the same
   `idempotencyKey` a second time is refused; the corridor is rebuilt and saved.
   The real write changes the alignment, which has profiles, a profile view, sample lines and the
   corridor hanging on it; case `W2b` reports as a warning when Civil 3D marks the drawing modified
   again right after that save (seen in every live run so far), and a settle step follows before
   the next write that asks for a clean drawing.
   Last, a preview creates a new layer with three 3D polylines (case `W10`): the receipt must say
   `rolledBack: true`, and the layer, the polylines and the modified mark must be gone.
6. **Guards**: a wrong `expectedDrawing` and a wrong session token are both refused with nothing
   run.
7. **Final state**: still the same drawing, saved, with the expected object counts.

The C# bodies it sends are in `scripts/live-smoke/csharp/`. `npm run test:skills` compiles each of
them like the plugin does, and `npm run test:smoke` runs the whole sequence against a simulated
Civil 3D (order, checks and stop rules only; that is not evidence of Civil 3D behaviour).

## Safety

- It writes only to its own drawing: a file named `mcp_smoke*.dwg` that held no Civil 3D objects
  when the run started (with `--start-civil`, exactly the file it created).
- A call without reply or with an unknown outcome stops the run. Nothing is sent again, except the
  one idempotency case, which repeats a save on purpose to see it refused.
- It installs nothing and never closes Civil 3D. The test drawing, its backups (in the plugin's
  backup folder) and the output folder can be deleted by hand afterwards.
- It talks to one instance only: its MCP connections are pinned to that instance's port.

## Options

| Option | Meaning |
| --- | --- |
| `--start-civil` | Start Civil 3D for the run (Windows). |
| `--template <dwt>` | Template of the new drawing. Default: the stock `_Autodesk Civil 3D (Metric) NCS.dwt` (older name `_AutoCAD Civil 3D (Metric) NCS.dwt`) from `%LOCALAPPDATA%\Autodesk\C3D 2025\<language>\Template`. |
| `--acad <path>` | `acad.exe`. Default: `CIVIL3D_ACAD_EXE`, else `C:\Program Files\Autodesk\AutoCAD 2025\acad.exe`. |
| `--profile <name>` | AutoCAD profile (`/p`) as the Civil 3D shortcut gives it, a profile name or an `.arg` file path. |
| `--language <code>` | Product language (`/language`), when the shortcut sets one. |
| `--port <port>` | Instead of starting Civil 3D: use the running instance on this port. Its active drawing must be a new, saved `mcp_smoke*.dwg`. |
| `--drawing <path>` | With `--port`: the exact full path that drawing must have. |
| `--out <folder>` | Output folder. Default: `%TEMP%\c3dmcp-smoke\<time stamp>`. |
| `--wait-minutes <n>` | How long to wait for Civil 3D to start (default 10). |

The run uses the server in this checkout's `build\`, so build the commit to be tested first.
When the installed plugin comes from another build, the run goes on and reports a warning (case
`S1b`); that is expected while testing a branch that does not change the plugin.
