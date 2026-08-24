import assert from "node:assert/strict";
import test from "node:test";

import {
  ApplicationClientConnection,
  MAX_JSON_BODY_BYTES,
} from "../../build/utils/SocketClient.js";
import {
  Civil3dMcpError,
  civil3dErrorFromPlugin,
} from "../../build/errors/structuredError.js";

const REQUEST_ID = "transport-test-id";

function createStubConnection(onWrite = () => true) {
  const connection = new ApplicationClientConnection("127.0.0.1", 65535);
  connection.isConnected = true;
  connection.generateRequestId = () => REQUEST_ID;
  connection.socket.write = onWrite;
  return connection;
}

function jsonRpcResult(result) {
  return JSON.stringify({ jsonrpc: "2.0", id: REQUEST_ID, result });
}

function errorFrom(promise) {
  return promise.then(
    () => assert.fail("command must reject"),
    (error) => error
  );
}

function codeForExactRequestBytes(targetBytes) {
  const emptyRequest = JSON.stringify({
    jsonrpc: "2.0",
    method: "executeCode",
    params: { code: "" },
    id: REQUEST_ID,
  });
  const remainingBytes = targetBytes - Buffer.byteLength(emptyRequest, "utf8");
  assert.ok(remainingBytes > 0);
  return "é".repeat(Math.floor(remainingBytes / 2)) + "a".repeat(remainingBytes % 2);
}

function exactResponseBody(targetBytes) {
  const prefix = `{"jsonrpc":"2.0","id":"${REQUEST_ID}","result":"`;
  const suffix = `"}`;
  const remainingBytes =
    targetBytes - Buffer.byteLength(prefix, "utf8") - Buffer.byteLength(suffix, "utf8");
  assert.ok(remainingBytes > 0);
  const result =
    "é".repeat(Math.floor(remainingBytes / 2)) + "a".repeat(remainingBytes % 2);
  const body = Buffer.from(`${prefix}${result}${suffix}`, "utf8");
  assert.equal(body.length, targetBytes);
  return { body, result, prefixBytes: Buffer.byteLength(prefix, "utf8") };
}

test("request framing uses LF and an exact multibyte UTF-8 byte limit", async () => {
  let writtenFrame;
  let writeCalls = 0;
  const connection = createStubConnection((frame) => {
    writeCalls += 1;
    writtenFrame = Buffer.from(frame);
    queueMicrotask(() => {
      connection.socket.emit("data", Buffer.from(`${jsonRpcResult("ok")}\n`, "utf8"));
    });
    return true;
  });

  try {
    const code = codeForExactRequestBytes(MAX_JSON_BODY_BYTES);
    assert.ok(code.length < MAX_JSON_BODY_BYTES);
    assert.equal(await connection.sendCommand("executeCode", { code }), "ok");
    assert.equal(writeCalls, 1);
    assert.equal(writtenFrame.at(-1), 0x0a);
    assert.equal(writtenFrame.length - 1, MAX_JSON_BODY_BYTES);
  } finally {
    connection.socket.destroy();
  }

  let oversizedWriteCalls = 0;
  const oversizedConnection = createStubConnection(() => {
    oversizedWriteCalls += 1;
    return true;
  });

  try {
    const oversizedCode = `${codeForExactRequestBytes(MAX_JSON_BODY_BYTES)}a`;
    const error = await errorFrom(
      oversizedConnection.sendCommand("executeCode", { code: oversizedCode })
    );
    assert.ok(error instanceof Civil3dMcpError);
    assert.equal(error.code, "CIVIL3D.REQUEST_TOO_LARGE");
    assert.equal(error.category, "transport");
    assert.equal(error.source, "node");
    assert.equal(error.outcome, "not_started");
    assert.equal(error.toStructuredError().retryable, false);
    assert.equal(oversizedWriteCalls, 0);
  } finally {
    oversizedConnection.socket.destroy();
  }
});

test("fragmented responses preserve split UTF-8 characters at the exact byte limit", async () => {
  const connection = createStubConnection();

  try {
    const pending = connection.sendCommand("executeCode", { code: "return 1;" });
    const { body, result, prefixBytes } = exactResponseBody(MAX_JSON_BODY_BYTES);

    connection.socket.emit("data", body.subarray(0, prefixBytes + 1));
    connection.socket.emit("data", body.subarray(prefixBytes + 1, body.length - 7));
    connection.socket.emit("data", body.subarray(body.length - 7));
    connection.socket.emit("data", Buffer.from("\n"));

    assert.equal(await pending, result);
  } finally {
    connection.socket.destroy();
  }
});

test("a response one byte above the limit fails immediately and is never retried", async () => {
  let writeCalls = 0;
  const connection = createStubConnection(() => {
    writeCalls += 1;
    return true;
  });

  try {
    const pending = connection.sendCommand("executeCode", { code: "return 1;" });
    const { body } = exactResponseBody(MAX_JSON_BODY_BYTES);
    connection.socket.emit("data", body);
    connection.socket.emit("data", Buffer.from("x"));

    const error = await errorFrom(pending);
    assert.ok(error instanceof Civil3dMcpError);
    assert.equal(error.code, "CIVIL3D.RESPONSE_TOO_LARGE");
    assert.equal(error.category, "transport");
    assert.equal(error.outcome, "unknown");
    assert.equal(error.toStructuredError().retryable, false);
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(writeCalls, 1);
  } finally {
    connection.socket.destroy();
  }
});

test("malformed framed JSON fails immediately and is never retried", async () => {
  let writeCalls = 0;
  const connection = createStubConnection(() => {
    writeCalls += 1;
    return true;
  });

  try {
    const pending = connection.sendCommand("executeCode", { code: "return 1;" });
    connection.socket.emit("data", Buffer.from('{"jsonrpc":"2.0","result":\n'));

    const error = await errorFrom(pending);
    assert.ok(error instanceof Civil3dMcpError);
    assert.equal(error.code, "CIVIL3D.TRANSPORT_ERROR");
    assert.equal(error.category, "transport");
    assert.equal(error.outcome, "unknown");
    assert.equal(error.toStructuredError().retryable, false);
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(writeCalls, 1);
  } finally {
    connection.socket.destroy();
  }
});

test("connection interruption rejects immediately and is never retried", async () => {
  let writeCalls = 0;
  const connection = createStubConnection(() => {
    writeCalls += 1;
    return true;
  });

  try {
    const pending = connection.sendCommand("executeCode", { code: "return 1;" });
    connection.socket.emit("close");

    const error = await errorFrom(pending);
    assert.ok(error instanceof Civil3dMcpError);
    assert.equal(error.code, "CIVIL3D.TRANSPORT_ERROR");
    assert.equal(error.category, "transport");
    assert.equal(error.outcome, "unknown");
    assert.equal(error.toStructuredError().retryable, false);
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(writeCalls, 1);
  } finally {
    connection.socket.destroy();
  }
});

test("legacy unframed plugin JSON is accepted only after an orderly EOF", async () => {
  const connection = createStubConnection();

  try {
    let settled = false;
    const pending = connection.sendCommand("executeCode", { code: "return 1;" });
    pending.then(
      () => {
        settled = true;
      },
      () => {
        settled = true;
      }
    );

    connection.socket.emit("data", Buffer.from(jsonRpcResult("legacy"), "utf8"));
    await Promise.resolve();
    assert.equal(settled, false);

    connection.socket.emit("end");
    assert.equal(await pending, "legacy");
  } finally {
    connection.socket.destroy();
  }
});

test("plugin response-size errors retain unknown non-retryable transport semantics", () => {
  const error = civil3dErrorFromPlugin({
    code: "CIVIL3D.RESPONSE_TOO_LARGE",
    message: "Response exceeded the limit after execution.",
  });

  assert.equal(error.category, "transport");
  assert.equal(error.source, "plugin");
  assert.equal(error.outcome, "unknown");
  assert.equal(error.toStructuredError().retryable, false);
});
