import assert from "node:assert/strict";
import net from "node:net";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

function restoreEnvironment(saved) {
  for (const [name, value] of Object.entries(saved)) {
    if (value === undefined) delete process.env[name];
    else process.env[name] = value;
  }
}

test("logger falls back for invalid and inherited names while honoring valid levels", async () => {
  const saved = { LOG_LEVEL: process.env.LOG_LEVEL };
  const originalError = console.error;
  const cases = [
    [undefined, ["info", "warn", "error"]],
    ["", ["info", "warn", "error"]],
    ["unknown", ["info", "warn", "error"]],
    ["constructor", ["info", "warn", "error"]],
    ["__proto__", ["info", "warn", "error"]],
    ["debug", ["debug", "info", "warn", "error"]],
    [" INFO ", ["info", "warn", "error"]],
    ["Warn", ["warn", "error"]],
    ["error", ["error"]],
  ];
  try {
    for (const [index, [setting, expected]] of cases.entries()) {
      restoreEnvironment({ LOG_LEVEL: setting });
      const lines = [];
      console.error = (line) => lines.push(String(line));
      const { createLogger } = await import(`../../build/utils/logger.js?settings-case=${index}`);
      const logger = createLogger("SettingsTest");
      for (const level of ["debug", "info", "warn", "error"]) logger[level](level);
      assert.deepEqual(lines.map((line) => line.split(" ").at(-1)), expected, String(setting));
    }
  } finally {
    console.error = originalError;
    restoreEnvironment(saved);
  }
});

test("timeouts reject invalid timer delays and omit raw values from warnings", async () => {
  const saved = {
    CIVIL3D_COMMAND_TIMEOUT: process.env.CIVIL3D_COMMAND_TIMEOUT,
    CIVIL3D_TEST_TIMEOUT: process.env.CIVIL3D_TEST_TIMEOUT,
  };
  const originalError = console.error;
  const warnings = [];
  try {
    process.env.CIVIL3D_COMMAND_TIMEOUT = "not-a-number";
    console.error = (line) => warnings.push(String(line));
    const { readTimeoutEnvironment } = await import("../../build/utils/SocketClient.js");
    for (const value of [undefined, "", "  ", "0", "-1", "1.5", "Infinity", "NaN", "2147483648", "123ms", "PRIVATE_TIMEOUT_MARKER"]) {
      restoreEnvironment({ CIVIL3D_TEST_TIMEOUT: value });
      assert.equal(readTimeoutEnvironment("CIVIL3D_TEST_TIMEOUT", 120_000), 120_000, String(value));
    }
    for (const [value, expected] of [["1", 1], [" 600000 ", 600_000], ["2147483647", 2_147_483_647]]) {
      process.env.CIVIL3D_TEST_TIMEOUT = value;
      assert.equal(readTimeoutEnvironment("CIVIL3D_TEST_TIMEOUT", 120_000), expected);
    }
    assert.ok(warnings.some((line) => line.includes("CIVIL3D_TEST_TIMEOUT")));
    assert.ok(warnings.every((line) => !line.includes("PRIVATE_TIMEOUT_MARKER")));
  } finally {
    console.error = originalError;
    restoreEnvironment(saved);
  }
});
