import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const scriptPath = process.argv[2] ?? new URL("../../src/WebViewHostBridge/js/webviewhostbridge.js", import.meta.url);
const source = fs.readFileSync(scriptPath, "utf8");

function load() {
  const sandbox = { setTimeout, clearTimeout, crypto: globalThis.crypto, Promise, Map, Date, Math, JSON, Error, TypeError, Object, Array };
  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(source, sandbox);
  return sandbox.WebViewHostBridge;
}

// Asynchronous in-memory pipe, like postMessage.
function pipe() {
  const listeners = { a: null, b: null };
  const side = (me, other) => ({
    sent: [],
    send(json) { this.sent.push(json); setTimeout(() => listeners[other] && listeners[other](json), 0); },
    subscribe(fn) { listeners[me] = fn; return () => { listeners[me] = null; }; }
  });
  return { a: side('a', 'b'), b: side('b', 'a') };
}

const tick = (ms = 10) => new Promise(r => setTimeout(r, ms));
const results = [];
async function test(name, fn) {
  try { await fn(); results.push(['ok', name]); }
  catch (e) { results.push(['FAIL', name, e && e.stack || e]); }
}

await test('handshake connects both, request/reply, typed error', async () => {
  const B = load();
  const p = pipe();
  const host = B.create({ transport: p.a, announce: false });
  const page = B.create({ transport: p.b });            // announces
  await tick();
  assert.equal(host.isConnected, true);
  assert.equal(page.isConnected, true);

  host.on('getContext', () => ({ caseId: 42 }));
  assert.deepEqual(await page.request('getContext'), { caseId: 42 });

  host.on('save', () => { throw new B.BridgeError('Fill in the number'); });
  await assert.rejects(page.request('save'), e => e.name === 'BridgeRequestError' && e.error === 'Fill in the number');

  host.on('crash', () => { throw new Error('secret internals'); });
  await assert.rejects(page.request('crash'), e => e.error === "Request 'crash' failed.");

  await assert.rejects(page.request('nothing'), e => /No handler/.test(e.error));
});

await test('notification with async handler and onError', async () => {
  const B = load();
  const p = pipe();
  const errors = [];
  const host = B.create({ transport: p.a, onError: e => errors.push(e) });
  const page = B.create({ transport: p.b });
  let got = null;
  host.on('orderCompleted', async payload => { got = payload; });
  host.on('broken', () => { throw new Error('x'); });
  await page.post('orderCompleted', { orderId: 7 });
  await page.post('broken');
  await tick();
  assert.deepEqual(got, { orderId: 7 });
  assert.equal(errors.length, 1);
});

await test('timeout and awaitConnection', async () => {
  const B = load();
  const p = pipe();
  const page = B.create({ transport: p.b, requestTimeout: 50, awaitConnection: true });
  await assert.rejects(page.request('x'), /did not connect/);
  const host = B.create({ transport: p.a });   // host appears, announces
  await tick();
  assert.equal(page.isConnected, true);
  await assert.rejects(page.request('silent'), /No handler/);
});

await test('restart of the other side fails pending requests', async () => {
  const B = load();
  const p = pipe();
  const page = B.create({ transport: p.b });
  const host1 = B.create({ transport: p.a });
  await tick();
  host1.on('slow', () => new Promise(() => {}));
  const pending = assert.rejects(page.request('slow'), /restarted/);
  let reconnects = 0;
  page.onConnected(() => reconnects++);
  host1.dispose();
  const host2 = B.create({ transport: p.a });   // new session
  await tick();
  await pending;
  assert.equal(reconnects, 1);
  assert.equal(host2.isConnected, true);
});

await test('wire format matches the C# side', async () => {
  const B = load();
  const sent = [];
  let deliver;
  const page = B.create({
    transport: { send: j => sent.push(JSON.parse(j)), subscribe: fn => { deliver = fn; return () => {}; } }
  });
  // hello from the page
  assert.equal(sent[0].type, '$bridge.hello');
  assert.equal(typeof sent[0].payload.session, 'string');

  // C# BridgeProtocol.Serialize(Request("getContext")) — and the same wrapped in a JSON string (PostWebMessageAsJson of a string)
  page.on('getContext', () => 5);
  deliver('{"type":"getContext","id":"abc"}');
  deliver(JSON.stringify('{"type":"getContext","id":"def"}'));
  deliver({ type: 'getContext', id: 'ghi' });          // PostWebMessageAsJson of an object
  await tick();
  assert.deepEqual(sent.slice(1), [
    { type: 'getContext', replyTo: 'abc', payload: 5 },
    { type: 'getContext', replyTo: 'def', payload: 5 },
    { type: 'getContext', replyTo: 'ghi', payload: 5 }
  ]);

  // C# welcome → connected; C# error reply → BridgeRequestError
  deliver('{"type":"$bridge.welcome","payload":{"session":"hostsession"}}');
  assert.equal(page.isConnected, true);
  const req = page.request('save', { id: 1 });
  await tick();
  const out = sent[sent.length - 1];
  assert.deepEqual({ type: out.type, payload: out.payload }, { type: 'save', payload: { id: 1 } });
  deliver(JSON.stringify({ type: 'save', replyTo: out.id, error: 'Validation failed' }));
  await assert.rejects(req, e => e.error === 'Validation failed');

  assert.equal(page.receive('not json'), false);
  assert.equal(page.receive('{"type":"openForm","formName":"F"}'), false);
  assert.throws(() => page.on('$bridge.x', () => {}), /reserved/);
});

await test('blazor forward/send helpers', async () => {
  const sandboxListeners = [];
  const posted = [];
  const sandbox = {
    setTimeout, clearTimeout, crypto: globalThis.crypto, Promise, Map, Date, Math, JSON, Error, TypeError, Object, Array,
    chrome: { webview: {
      postMessage: m => posted.push(m),
      addEventListener: (_, fn) => sandboxListeners.push(fn),
      removeEventListener: (_, fn) => sandboxListeners.splice(sandboxListeners.indexOf(fn), 1)
    } }
  };
  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(source, sandbox);
  const calls = [];
  const dotnet = { invokeMethodAsync: (name, json) => calls.push([name, json]) };
  assert.equal(sandbox.WebViewHostBridge.forward(dotnet), true);
  assert.equal(sandbox.WebViewHostBridge.forward(dotnet, 'OnHostMessage'), true);   // replaces
  assert.equal(sandboxListeners.length, 1);
  sandboxListeners[0]({ data: { type: 'x' } });
  assert.deepEqual(calls, [['OnHostMessage', '{"type":"x"}']]);
  sandbox.WebViewHostBridge.send('{"type":"y"}');
  assert.deepEqual(posted, ['{"type":"y"}']);
});

for (const r of results) console.log(r[0], r[1], r[2] ? '\n' + r[2] : '');
process.exit(results.some(r => r[0] === 'FAIL') ? 1 : 0);
