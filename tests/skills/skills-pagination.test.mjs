import assert from "node:assert/strict";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerSkillsTool } from "../../build/tools/skillsTool.js";

async function withSkillsClient(run) {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = new McpServer({ name: "skills-test", version: "1.0.0" });
  registerSkillsTool(server);
  const client = new Client({ name: "skills-test-client", version: "1.0.0" });

  try {
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    await run(client);
  } finally {
    await client.close();
    await server.close();
  }
}

async function callSkills(client, args) {
  const result = await client.callTool({
    name: "civil3d_skills",
    arguments: args,
  });

  assert.notEqual(result.isError, true, result.content?.[0]?.text);
  assert.equal(result.content?.[0]?.type, "text");
  return JSON.parse(result.content[0].text);
}

async function callSkillsError(client, args, expectedMessage) {
  const result = await client.callTool({
    name: "civil3d_skills",
    arguments: args,
  });

  assert.equal(result.isError, true);
  assert.equal(result.content?.[0]?.type, "text");
  assert.match(result.content[0].text, expectedMessage);
}

function assertPageContract(payload, resultField) {
  assert.equal(payload.count, payload[resultField].length);
  assert.equal(payload.returned, payload[resultField].length);
  assert.equal(typeof payload.total, "number");
  assert.equal(typeof payload.truncated, "boolean");
  assert.ok(payload.nextCursor === null || typeof payload.nextCursor === "string");
  assert.equal(payload.truncated, payload.nextCursor !== null);
}

test("list/search keep compatible fields, deterministic order, and metadata-only results", async () => {
  await withSkillsClient(async (client) => {
    const { tools } = await client.listTools();
    assert.equal(tools.length, 1);
    const tool = tools[0];
    assert.equal(tool.name, "civil3d_skills");
    assert.equal(tool.inputSchema.properties.limit.type, "number");
    assert.equal(tool.inputSchema.properties.cursor.type, "string");

    const first = await callSkills(client, { action: "list", limit: 50 });
    const repeated = await callSkills(client, { action: "list", limit: 50 });
    assert.deepEqual(repeated, first);
    assertPageContract(first, "skills");
    assert.equal(first.truncated, false);

    const names = first.skills.map((skill) => skill.name);
    assert.deepEqual(names, [...names].sort());
    for (const skill of first.skills) {
      assert.deepEqual(Object.keys(skill).sort(), [
        "category",
        "description",
        "name",
        "parameters",
        "requires_write",
      ]);
    }

    const search = await callSkills(client, {
      action: "search",
      query: "surface",
      limit: 50,
    });
    assertPageContract(search, "results");
    for (const result of search.results) {
      assert.deepEqual(Object.keys(result).sort(), ["category", "description", "name"]);
    }
  });
});

test("list cursor covers first, middle, and last pages without overlap or omission", async () => {
  await withSkillsClient(async (client) => {
    const inventory = await callSkills(client, { action: "list", limit: 50 });
    assert.ok(inventory.total >= 6, "pagination fixture requires at least six skills");
    const limit = Math.ceil(inventory.total / 3);

    const first = await callSkills(client, { action: "list", limit });
    const middle = await callSkills(client, {
      action: "list",
      limit,
      cursor: first.nextCursor,
    });
    const last = await callSkills(client, {
      action: "list",
      limit,
      cursor: middle.nextCursor,
    });

    for (const page of [first, middle, last]) {
      assertPageContract(page, "skills");
      assert.equal(page.total, inventory.total);
    }
    assert.equal(first.truncated, true);
    assert.equal(middle.truncated, true);
    assert.equal(last.truncated, false);
    assert.equal(last.nextCursor, null);

    const pagedNames = [first, middle, last].flatMap((page) =>
      page.skills.map((skill) => skill.name)
    );
    assert.equal(new Set(pagedNames).size, inventory.total);
    assert.deepEqual(
      pagedNames,
      inventory.skills.map((skill) => skill.name)
    );

    const repeatedMiddle = await callSkills(client, {
      action: "list",
      limit,
      cursor: first.nextCursor,
    });
    assert.deepEqual(repeatedMiddle, middle);
  });
});

test("category and search filters are applied before pagination", async () => {
  await withSkillsClient(async (client) => {
    const filtered = await callSkills(client, {
      action: "list",
      category: "surfaces",
      limit: 50,
    });
    assert.equal(filtered.truncated, false);
    assert.ok(filtered.total >= 2, "pagination fixture requires at least two surface skills");
    const expectedTotal = filtered.total;
    const pageLimit = Math.ceil(expectedTotal / 2);

    const first = await callSkills(client, {
      action: "list",
      category: "SURFACES",
      limit: pageLimit,
    });
    assert.equal(first.total, expectedTotal);
    assert.equal(first.returned, pageLimit);
    assert.equal(first.truncated, true);

    const last = await callSkills(client, {
      action: "list",
      category: "surfaces",
      limit: pageLimit,
      cursor: first.nextCursor,
    });
    assert.equal(last.total, expectedTotal);
    assert.equal(last.returned, expectedTotal - pageLimit);
    assert.equal(last.truncated, false);
    assert.ok([...first.skills, ...last.skills].every((skill) => skill.category === "surfaces"));

    const searchFirst = await callSkills(client, {
      action: "search",
      category: "SURFACES",
      query: "SURFACE",
      limit: pageLimit,
    });
    const searchLast = await callSkills(client, {
      action: "search",
      category: "surfaces",
      query: "surface",
      limit: pageLimit,
      cursor: searchFirst.nextCursor,
    });
    assert.equal(searchFirst.total, expectedTotal);
    assert.equal(searchLast.total, expectedTotal);
    assert.equal(searchLast.truncated, false);
    assert.deepEqual(
      [...searchFirst.results, ...searchLast.results].map((result) => result.name),
      [...first.skills, ...last.skills].map((skill) => skill.name)
    );

    const intersection = await callSkills(client, {
      action: "search",
      category: "surfaces",
      query: "elevation",
    });
    assert.equal(intersection.total, 1);
    assert.equal(intersection.results[0].name, "surface_elevation");

    const emptyIntersection = await callSkills(client, {
      action: "search",
      category: "points",
      query: "surface",
    });
    assert.equal(emptyIntersection.total, 0);
    assert.equal(emptyIntersection.returned, 0);
    assert.equal(emptyIntersection.nextCursor, null);
  });
});

test("invalid limits and cursors return controlled errors", async () => {
  await withSkillsClient(async (client) => {
    for (const limit of [0, 1.5, 51]) {
      await callSkillsError(
        client,
        { action: "list", limit },
        /Parameter 'limit' must be an integer between 1 and 50\./
      );
    }

    await callSkillsError(
      client,
      { action: "list", cursor: "not-a-valid-cursor!" },
      /Parameter 'cursor' is invalid\./
    );

    const page = await callSkills(client, {
      action: "list",
      category: "surfaces",
      limit: 1,
    });
    await callSkillsError(
      client,
      { action: "list", category: "points", cursor: page.nextCursor },
      /Parameter 'cursor' does not match the current action and filters\./
    );
    await callSkillsError(
      client,
      { action: "search", category: "surfaces", query: "surface", cursor: page.nextCursor },
      /Parameter 'cursor' does not match the current action and filters\./
    );

    const decoded = JSON.parse(Buffer.from(page.nextCursor, "base64url").toString("utf8"));
    decoded.offset = 999;
    const outsideCursor = Buffer.from(JSON.stringify(decoded), "utf8").toString("base64url");
    await callSkillsError(
      client,
      { action: "list", category: "surfaces", cursor: outsideCursor },
      /Parameter 'cursor' is outside the filtered result set\./
    );

    decoded.offset = 1;
    decoded.extra = true;
    const extraFieldCursor = Buffer.from(JSON.stringify(decoded), "utf8").toString("base64url");
    await callSkillsError(
      client,
      { action: "list", category: "surfaces", cursor: extraFieldCursor },
      /Parameter 'cursor' is invalid\./
    );
  });
});

test("get response remains unpaginated and unchanged in shape", async () => {
  await withSkillsClient(async (client) => {
    const skill = await callSkills(client, {
      action: "get",
      skillName: "drawing_info",
    });

    assert.deepEqual(Object.keys(skill).sort(), [
      "category",
      "content",
      "description",
      "name",
      "parameters",
      "requires_write",
    ]);
    assert.equal(skill.name, "drawing_info");
    assert.equal(typeof skill.content, "string");
    assert.ok(skill.content.length > 0);
  });
});

test("known Civil 3D 2025 skill regressions stay corrected", async () => {
  await withSkillsClient(async (client) => {
    const drawingInfo = await callSkills(client, {
      action: "get",
      skillName: "drawing_info",
    });
    assert.match(drawingInfo.content, /CivilDoc\.CorridorCollection\.Count/);
    assert.doesNotMatch(drawingInfo.content, /GetCorridorIds\s*\(/);

    const cogoPoints = await callSkills(client, {
      action: "get",
      skillName: "list_cogo_points",
    });
    assert.equal(cogoPoints.description, "List COGO points in the drawing with a configurable result limit");
    assert.deepEqual(cogoPoints.parameters.map((parameter) => parameter.name), ["limit"]);
    assert.match(cogoPoints.content, /Point-group filtering is not included in this template/);

    const surfaces = await callSkills(client, {
      action: "get",
      skillName: "list_surfaces",
    });
    assert.match(surfaces.content, /surface is TinVolumeSurface \? "TINVolume"/);

    const surfaceVolume = await callSkills(client, {
      action: "get",
      skillName: "surface_volume",
    });
    assert.equal(surfaceVolume.requires_write, false);
    assert.equal(surfaceVolume.description, "Read cut/fill volumes from an existing TIN volume surface");
    assert.deepEqual(surfaceVolume.parameters, [
      {
        name: "surfaceName",
        type: "string",
        required: true,
        description: "Name of an existing TIN volume surface",
      },
    ]);
    assert.match(
      surfaceVolume.content,
      /Transaction\.GetObject\(id, OpenMode\.ForRead\) as TinVolumeSurface/
    );
    assert.match(surfaceVolume.content, /volumeSurface\.GetVolumeProperties\(\)/);
    assert.match(surfaceVolume.content, /netVolume = volumeProps\.UnadjustedNetVolume/);
    assert.match(surfaceVolume.content, /only reads an existing `TinVolumeSurface`/);
    assert.match(surfaceVolume.content, /changed `DBMOD` from 0 to 1/);
    assert.match(surfaceVolume.content, /surface count and `DBMOD` unchanged/);
    assert.doesNotMatch(surfaceVolume.content, /TinVolumeSurface\.Create\s*\(/);
    assert.doesNotMatch(surfaceVolume.content, /baseSurf\.GetVolumeProperties\s*\(/);
    assert.doesNotMatch(surfaceVolume.content, /Unadjusted(?:Cut|Fill)Area/);

    const earthwork = await callSkills(client, {
      action: "get",
      skillName: "earthwork_report",
    });
    assert.equal(
      earthwork.description,
      "Summarize an existing ground surface, alignments, and existing profiles in a read-only report"
    );
    assert.match(earthwork.content, /does not create profiles or compute cut\/fill volumes/);
  });
});

test("phase 3A.1 read-only inventory skills stay discoverable and bounded", async () => {
  await withSkillsClient(async (client) => {
    const inventory = await callSkills(client, { action: "list", limit: 50 });
    assert.equal(inventory.total, 22);

    const expectedParameters = {
      selected_objects_summary: ["limit"],
      list_profiles: ["alignmentName", "limit"],
      profile_elevation_at_station: ["alignmentName", "profileName", "station"],
      corridor_summary: ["corridorName", "limit", "baselineLimit"],
      section_inventory: ["alignmentName", "limit"],
      pipe_network_qc: ["networkName", "limit", "partLimit", "issueLimit"],
      surface_definition_summary: ["surfaceName", "limit"],
    };

    const names = new Set(inventory.skills.map((skill) => skill.name));
    for (const name of Object.keys(expectedParameters)) {
      assert.ok(names.has(name), `${name} must be discoverable`);
      const skill = await callSkills(client, { action: "get", skillName: name });
      assert.equal(skill.requires_write, false, `${name} must remain read-only`);
      assert.deepEqual(
        skill.parameters.map((parameter) => parameter.name),
        expectedParameters[name]
      );
      assert.doesNotMatch(
        skill.content,
        /OpenMode\.ForWrite|UpgradeOpen\s*\(|Transaction\.Commit\s*\(/,
        `${name} must not contain a write operation`
      );
    }

    for (const name of [
      "selected_objects_summary",
      "list_profiles",
      "corridor_summary",
      "section_inventory",
      "pipe_network_qc",
      "surface_definition_summary",
    ]) {
      const skill = await callSkills(client, { action: "get", skillName: name });
      for (const field of ["total", "returned", "truncated", "limit"]) {
        assert.match(skill.content, new RegExp(`\\b${field}\\b`));
      }
    }

    const drawingInfo = await callSkills(client, {
      action: "get",
      skillName: "drawing_info",
    });
    assert.match(drawingInfo.content, /Database\.FingerprintGuid\.ToString\(\)/);
    assert.match(drawingInfo.content, /GetSystemVariable\("DBMOD"\)/);
    assert.match(drawingInfo.content, /GetSystemVariable\("DWGTITLED"\)/);
    assert.match(drawingInfo.content, /hasUnsavedChanges = dbmod != 0/);
    assert.match(drawingInfo.content, /isSavedAndClean = isNamed && dbmod == 0/);

  });
});

test("phase 3D.3 fixed-primitive recipe is discoverable as an explicitly write-capable skill", async () => {
  await withSkillsClient(async (client) => {
    const skill = await callSkills(client, {
      action: "get",
      skillName: "replace_alignment_with_fixed_primitives",
    });
    assert.equal(skill.category, "alignments");
    assert.equal(skill.requires_write, true);
    assert.deepEqual(
      skill.parameters.map(({ name, type, required }) => ({ name, type, required })),
      [
        { name: "alignmentHandle", type: "string", required: true },
        { name: "expectedBaseline", type: "object", required: true },
        { name: "plannedPrimitives", type: "array", required: true },
        { name: "tolerance", type: "number", required: false },
      ]
    );
    assert.doesNotMatch(skill.content, /Database\.SaveAs\s*\(|SendStringToExecute\s*\(|Transaction\.Commit\s*\(/);
  });
});

test("phase 3D.2 alignment geometry audit remains bounded, read-only, and path-safe", async () => {
  await withSkillsClient(async (client) => {
    const skill = await callSkills(client, {
      action: "get",
      skillName: "alignment_geometry_audit",
    });

    assert.equal(skill.category, "alignments");
    assert.equal(skill.requires_write, false);
    assert.deepEqual(skill.parameters, [
      {
        name: "alignmentHandle",
        type: "string",
        required: true,
        description: "Hex handle of an existing Alignment",
      },
      {
        name: "limit",
        type: "number",
        required: false,
        description: "Maximum top-level alignment entities to return (default 100; 1-200)",
      },
    ]);
    for (const requiredSnippet of [
      /\^\[0-9A-Fa-f\]\{1,16\}\$/,
      /limit < 1 \|\| limit > 200/,
      /as Alignment/,
      /alignment\.Entities\.Count/,
      /alignment\.Entities\.GetEntityByOrder\(chainIndex\)/,
      /AlignmentCurve curve/,
      /AlignmentLine line/,
      /AlignmentArc arc/,
      /AlignmentSpiral spiral/,
      /var subentity = entity\[subentityIndex\]/,
      /AlignmentSubEntityLine subentityLine/,
      /AlignmentSubEntityArc subentityArc/,
      /AlignmentSubEntitySpiral subentitySpiral/,
      /parentEntityIndex/,
      /geometryTotal/,
      /geometryItems\.Count >= limit/,
      /connectedChainOrder/,
      /parentEntityIndexes\[entity\.EntityId\]/,
      /if \(geometryUsesConnectedOrder && warnings\.Count < 20\)/,
      /Double\.IsInfinity\(spiral\.RadiusIn\)/,
      /Double\.IsInfinity\(subentitySpiral\.RadiusOut\)/,
      /startIsTangent/,
      /endRadiusUnknown/,
      /standardCompliance = "not_evaluated"/,
      /curve\.HighestDesignSpeed/,
      /entityHighestDesignSpeed > 0\.0/,
      /string criteriaFileName = null/,
      /GetSystemVariable\("DBMOD"\)/,
    ]) {
      assert.match(skill.content, requiredSnippet);
    }
    assert.doesNotMatch(
      skill.content,
      /OpenMode\.ForWrite|UpgradeOpen\s*\(|Transaction\.Commit\s*\(|SaveAs\s*\(|QSAVE|DesignChecks|ValidateDesignCheck|\.DesignCriteriaFile\s*=/,
      "3D.2 must not author, validate, or save an alignment"
    );
    assert.doesNotMatch(skill.content, /item\["startRadius"\] = spiral\.RadiusIn;/);
    assert.doesNotMatch(skill.content, /geometryItem\["endRadius"\] = subentitySpiral\.RadiusOut;/);
    for (const geometryField of ["total", "returned", "truncated", "limit", "counts", "items"]) {
      assert.match(skill.content, new RegExp(`geometry[\\s\\S]*?${geometryField}`));
    }
    assert.doesNotMatch(skill.content, /criteriaFilePath|fullPath|DirectoryName/, "3D.2 must not expose a criteria-file path");
  });
});

test("phase 3D.1 alignment-from-polyline skill remains narrow and rollback-safe", async () => {
  await withSkillsClient(async (client) => {
    const createAlignment = await callSkills(client, {
      action: "get",
      skillName: "create_alignment_from_polyline",
    });
    assert.equal(createAlignment.category, "alignments");
    assert.equal(createAlignment.requires_write, true);
    assert.deepEqual(
      createAlignment.parameters,
      [
        {
          name: "sourceHandle",
          type: "string",
          required: true,
          description: "Hex handle of an existing open 2D model-space Polyline",
        },
        {
          name: "alignmentName",
          type: "string",
          required: true,
          description: "New unique alignment name",
        },
      ]
    );
    for (const requiredSnippet of [
      /existing\.Name\.Equals\(alignmentName, StringComparison\.OrdinalIgnoreCase\)/,
      /source\.OwnerId != modelSpaceId/,
      /source\.Closed \|\| source\.NumberOfVertices < 2 \|\| source\.Length <= 1e-9/,
      /EraseExistingEntities = false/,
      /AddCurvesBetweenTangents = false/,
      /CivilDoc\.Styles\.AlignmentStyles\["Tervező"\]/,
      /AlignmentLabelSetStyles\["Út szelvény és geometriai pontok"\]/,
      /Alignment\.Create\(\s*CivilDoc,\s*options,\s*alignmentName,\s*ObjectId\.Null,\s*source\.LayerId,\s*styleId,\s*labelSetId/s,
      /if \(alignmentId\.IsNull\)[\s\S]*throw new InvalidOperationException/,
      /if \(!sourcePreserved\)[\s\S]*throw new InvalidOperationException/,
      /sourcePreserved/,
    ]) {
      assert.match(createAlignment.content, requiredSnippet);
    }
    assert.doesNotMatch(
      createAlignment.content,
      /SaveAs\s*\(|QSAVE|AddFixedSpiral|Superelevation|Profile\.Create|Corridor\.Create|SampleLineGroup\.Create/,
      "3D.1 must stay limited to source-polyline alignment creation"
    );
  });
});

test("road-design readiness skill remains a bounded read-only baseline check", async () => {
  await withSkillsClient(async (client) => {
    const skill = await callSkills(client, {
      action: "get",
      skillName: "road_design_readiness",
    });

    assert.equal(skill.category, "workflows");
    assert.equal(skill.requires_write, false);
    assert.deepEqual(skill.parameters, []);
    for (const field of ["overallReady", "dbmod", "total", "required", "missing", "ready", "sample", "truncated"]) {
      assert.match(skill.content, new RegExp(`\\b${field}\\b`));
    }
    assert.match(skill.content, /styles\.AlignmentStyles\[name\]/);
    assert.match(skill.content, /styles\.PartsListSet\[name\]/);
    assert.match(skill.content, /styles\.CorridorStyles\.Count/);
    assert.match(skill.content, /styles\.SuperelevationViewStyles\.Count/);
    for (const baselineName of [
      "Tervező",
      "Terep",
      "Út",
      "Szabványos",
      "Nyomterv tervező",
      "Nyomterv KSZ nyomtatás",
      "Út mintavonal",
      "Meglévő terep",
      "Tervezett pálya",
      "Út keresztszelvény rajz",
    ]) {
      assert.ok(skill.content.includes(baselineName), `${baselineName} must remain in the baseline`);
    }
    assert.doesNotMatch(
      skill.content,
      /OpenMode\.ForWrite|UpgradeOpen\s*\(|Transaction\.Commit\s*\(|SaveAs\s*\(|QSAVE/,
      "readiness skill must not contain a write operation"
    );
  });
});
