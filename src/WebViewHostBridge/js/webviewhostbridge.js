/*! WebViewHostBridge JS client — wire-compatible with the WebViewHostBridge NuGet package. MIT License. */
(function (global) {
  'use strict';
  if (global.WebViewHostBridge) return;

  var RESERVED = '$bridge.';
  var HELLO = RESERVED + 'hello';
  var WELCOME = RESERVED + 'welcome';

  /** Error whose message is sent to the other side when a request handler throws it. */
  function BridgeError(message) {
    var error = new Error(message);
    Object.setPrototypeOf(error, BridgeError.prototype);
    return error;
  }
  BridgeError.prototype = Object.create(Error.prototype, {
    constructor: { value: BridgeError },
    name: { value: 'BridgeError' }
  });

  /** The other side answered a request with an error. */
  function BridgeRequestError(type, error) {
    var e = BridgeError("Bridge request '" + type + "' failed: " + error);
    Object.setPrototypeOf(e, BridgeRequestError.prototype);
    e.type = type;
    e.error = error;
    return e;
  }
  BridgeRequestError.prototype = Object.create(BridgeError.prototype, {
    constructor: { value: BridgeRequestError },
    name: { value: 'BridgeRequestError' }
  });

  function newId() {
    if (global.crypto && typeof global.crypto.randomUUID === 'function')
      return global.crypto.randomUUID().replace(/-/g, '');
    return Date.now().toString(16) + Math.random().toString(16).slice(2) + Math.random().toString(16).slice(2);
  }

  /** WebView2: page → host via chrome.webview.postMessage, host → page via its 'message' event. */
  function webView2Transport() {
    var webview = global.chrome && global.chrome.webview;
    if (!webview) return null;
    return {
      send: function (json) { webview.postMessage(json); },
      subscribe: function (onMessage) {
        var listener = function (e) { onMessage(e.data); };
        webview.addEventListener('message', listener);
        return function () { webview.removeEventListener('message', listener); };
      }
    };
  }

  function parse(raw) {
    var message = raw;
    try {
      if (typeof message === 'string') message = JSON.parse(message);
      if (typeof message === 'string') message = JSON.parse(message);
    } catch (e) {
      return null;
    }
    if (!message || typeof message !== 'object' || Array.isArray(message)) return null;
    if (typeof message.type !== 'string' && typeof message.replyTo !== 'string') return null;
    return message;
  }

  function withTimeout(promise, ms, text) {
    if (!(ms > 0) || ms === Infinity) return promise;
    return new Promise(function (resolve, reject) {
      var timer = setTimeout(function () { reject(new Error(text)); }, ms);
      promise.then(
        function (value) { clearTimeout(timer); resolve(value); },
        function (error) { clearTimeout(timer); reject(error); });
    });
  }

  function requireType(type) {
    if (typeof type !== 'string' || !type.trim()) throw new TypeError('Message type must not be empty.');
    if (type.indexOf(RESERVED) === 0) throw new TypeError("Message types starting with '" + RESERVED + "' are reserved.");
  }

  /**
   * Creates a bridge endpoint. Options:
   *   transport        — { send(json), subscribe(onMessage) → unsubscribe }; WebView2 by default
   *   requestTimeout   — ms to wait for a reply (and for the connection with awaitConnection); 30000
   *   awaitConnection  — post/request wait until the host has connected; false
   *   announce         — announce this endpoint right away; true
   *   onError(error, message) — a handler threw or a reply could not be sent
   */
  function create(options) {
    options = options || {};
    var transport = options.transport || webView2Transport();
    if (!transport) throw new Error('WebViewHostBridge: no transport (chrome.webview is not available).');

    var timeout = options.requestTimeout > 0 ? options.requestTimeout : 30000;
    var awaitConnection = !!options.awaitConnection;
    var session = newId();
    var peerSession = null;
    var pending = new Map();
    var handlers = new Map();
    var connectedListeners = [];
    var connected = false;
    var disposed = false;
    var resolveConnected;
    var connectedPromise = new Promise(function (resolve) { resolveConnected = resolve; });

    function report(error, message) {
      if (typeof options.onError === 'function') {
        try { options.onError(error, message); } catch (e) { /* a faulty logger must not break the bridge */ }
      }
    }

    function send(message) { transport.send(JSON.stringify(message)); }

    function message(type, payload) {
      var m = { type: type };
      if (payload !== undefined && payload !== null) m.payload = payload;
      return m;
    }

    function trySend(m, cause) {
      try { send(m); } catch (e) { report(e, cause); }
    }

    function failPending(error) {
      pending.forEach(function (p) { p.reject(error); });
      pending.clear();
    }

    function checkAlive() {
      if (disposed) throw new Error('WebViewHostBridge: the bridge is disposed.');
    }

    function whenReady(type) {
      if (!awaitConnection || connected) return Promise.resolve();
      return withTimeout(connectedPromise, timeout,
        "Bridge message '" + type + "': the other side did not connect within " + timeout + " ms.");
    }

    function errorText(request, error) {
      return error instanceof BridgeError ? error.message : "Request '" + request.type + "' failed.";
    }

    function reply(request, payload, error) {
      var m = { type: request.type, replyTo: request.id };
      if (error != null) m.error = error;
      else if (payload !== undefined && payload !== null) m.payload = payload;
      trySend(m, request);
    }

    function handleControl(m) {
      var isHello = m.type === HELLO;
      if (!isHello && m.type !== WELCOME) return;
      var peer = m.payload && typeof m.payload.session === 'string' ? m.payload.session : null;
      var changed = !connected || peerSession !== peer;
      var restarted = connected && changed;
      peerSession = peer;
      if (!connected) {
        connected = true;
        resolveConnected();
      }
      if (restarted) failPending(BridgeError('The other side restarted.'));
      if (changed) {
        connectedListeners.slice().forEach(function (listener) {
          try { listener(); } catch (e) { report(e, m); }
        });
      }
      if (isHello) trySend(message(WELCOME, { session: session }), m);
    }

    function receive(raw) {
      if (disposed) return false;
      var m = parse(raw);
      if (!m) return false;

      if (typeof m.replyTo === 'string') {
        var waiting = pending.get(m.replyTo);
        if (waiting) {
          pending.delete(m.replyTo);
          if (m.error != null) waiting.reject(BridgeRequestError(waiting.type, m.error));
          else waiting.resolve(m.payload === undefined ? null : m.payload);
        }
        return true;
      }

      if (m.type.indexOf(RESERVED) === 0) {
        handleControl(m);
        return true;
      }

      var isRequest = typeof m.id === 'string';
      var handler = handlers.get(m.type);
      if (!handler) {
        if (!isRequest) return false;
        reply(m, null, "No handler for '" + m.type + "'.");
        return true;
      }

      var result;
      try {
        result = Promise.resolve(handler(m.payload === undefined ? null : m.payload, m));
      } catch (e) {
        result = Promise.reject(e);
      }
      result.then(
        function (value) { if (isRequest) reply(m, value); },
        function (error) {
          report(error, m);
          if (isRequest) reply(m, null, errorText(m, error));
        });
      return true;
    }

    var unsubscribe = transport.subscribe(receive);

    var bridge = {
      /** Fire-and-forget notification. */
      post: function (type, payload) {
        checkAlive();
        requireType(type);
        return whenReady(type).then(function () { send(message(type, payload)); });
      },

      /** Request; resolves with the reply payload, rejects with BridgeRequestError or a timeout Error. */
      request: function (type, payload) {
        checkAlive();
        requireType(type);
        var started = Date.now();
        return whenReady(type).then(function () {
          checkAlive();
          return new Promise(function (resolve, reject) {
            var id = newId();
            var left = Math.max(0, timeout - (Date.now() - started));
            var timer = setTimeout(function () {
              pending.delete(id);
              reject(new Error("Bridge request '" + type + "' got no reply within " + timeout + " ms."));
            }, left);
            pending.set(id, {
              type: type,
              resolve: function (value) { clearTimeout(timer); resolve(value); },
              reject: function (error) { clearTimeout(timer); reject(error); }
            });
            try {
              var m = message(type, payload);
              m.id = id;
              send(m);
            } catch (e) {
              pending.delete(id);
              clearTimeout(timer);
              reject(e);
            }
          });
        });
      },

      /**
       * Handles messages of a type: handler(payload, message) → value or Promise. For requests the result
       * is the reply; a thrown BridgeError sends its message, any other error a generic failure.
       * Returns a function that unregisters the handler.
       */
      on: function (type, handler) {
        checkAlive();
        requireType(type);
        if (typeof handler !== 'function') throw new TypeError('Handler must be a function.');
        handlers.set(type, handler);
        return function () { if (handlers.get(type) === handler) handlers.delete(type); };
      },

      /** Calls listener whenever the host connects (again). Returns a function that removes it. */
      onConnected: function (listener) {
        connectedListeners.push(listener);
        return function () {
          var i = connectedListeners.indexOf(listener);
          if (i >= 0) connectedListeners.splice(i, 1);
        };
      },

      /** Resolves once the host has connected. */
      whenConnected: function () { return connectedPromise; },

      get isConnected() { return connected; },

      /** Tells the host this page is ready to receive. Called by create() unless announce: false. */
      announce: function () {
        checkAlive();
        send(message(HELLO, { session: session }));
      },

      /** Feeds a raw message in, for custom transports. Returns false if it was not for the bridge. */
      receive: receive,

      /** Stops listening and fails pending requests. */
      dispose: function () {
        if (disposed) return;
        disposed = true;
        if (typeof unsubscribe === 'function') unsubscribe();
        failPending(new Error('WebViewHostBridge: the bridge is disposed.'));
        handlers.clear();
        connectedListeners.length = 0;
      }
    };

    if (options.announce !== false) bridge.announce();
    return bridge;
  }

  var forwarding = null;

  global.WebViewHostBridge = {
    create: create,
    BridgeError: BridgeError,
    BridgeRequestError: BridgeRequestError,

    /**
     * Blazor: forwards every host message to a .NET method, e.g. a [JSInvokable] Receive(string json)
     * of a BridgeEndpoint subclass. Calling it again replaces the previous forwarding.
     */
    forward: function (dotNetRef, methodName) {
      var transport = webView2Transport();
      if (!transport) return false;
      if (forwarding) forwarding();
      forwarding = transport.subscribe(function (data) {
        dotNetRef.invokeMethodAsync(methodName || 'Receive', typeof data === 'string' ? data : JSON.stringify(data));
      });
      return true;
    },

    /** Blazor: sends a serialized message to the host; the BridgeEndpoint.SendAsync counterpart. */
    send: function (json) {
      var transport = webView2Transport();
      if (!transport) return false;
      transport.send(json);
      return true;
    }
  };
})(typeof window !== 'undefined' ? window : globalThis);
