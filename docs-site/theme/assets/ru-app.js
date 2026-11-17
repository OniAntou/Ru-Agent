/*
 * A faithful HTML rebuild of the Ru desktop window (dark theme tokens from gui-winui3/App.xaml), driven
 * by the content of one real run on a demo project: the prompt, the activity feed and the final reply.
 * It renders crisp at any size and can play the run: the prompt is typed and sent, steps stream into the
 * feed, the counters move, the feed folds into "Worked" and the reply lands.
 *
 *   RuApp.mount(element, { mode: "final" })        a still of the finished run (the hero)
 *   RuApp.mount(element, { mode: "play", onPhase }) the run as a loop; returns { play, pause, seek, at }
 */
(function () {
  "use strict";

  var PROMPT =
    "Implement sanitize(value) in src/sanitize.py so our structured logs never leak secrets. Redact the values " +
    "of password, secret, token, api_key and access_token keys (case-insensitive, ignore surrounding spaces) " +
    "with \"[REDACTED]\". Recurse through nested dicts, lists and tuples, keep keys, order, tuple shape and " +
    "scalar types, and never mutate the input. Verify it with a quick scratch check, then remove the scratch file.";

  // The activity feed of the run, in order. Kinds: think, say, find, list, read, edit, write, run, delete.
  var STEPS = [
    { k: "think", t: "Thought", d: "6.4s" },
    { k: "say", t: "I'll inspect the workspace and target file first." },
    { k: "find", t: "Found files", d: "1 item" },
    { k: "list", t: "Listed", d: "3 items" },
    { k: "think", t: "Thought", d: "3.3s" },
    { k: "read", t: "Read", d: "sanitize.py" },
    { k: "read", t: "Read", d: "README.md" },
    { k: "think", t: "Thought", d: "17.9s" },
    { k: "say", t: "Now I'll implement the function with recursive redaction." },
    { k: "edit", t: "Edited", d: "sanitize.py", file: 1 },
    { k: "think", t: "Thought", d: "6.5s" },
    { k: "say", t: "I'll verify with a scratch script covering all stated rules, then remove it." },
    { k: "write", t: "Wrote", d: "_scratch_check.py", add: 58, file: 2 },
    { k: "think", t: "Thought", d: "4.8s" },
    { k: "say", t: "I'll run the scratch check." },
    { k: "run", t: "Ran", d: "python _scratch_check.py", ms: "73ms" },
    { k: "think", t: "Thought", d: "3.9s" },
    { k: "delete", t: "Deleted", d: "_scratch_check.py" }
  ];

  var REPLY =
    '<p>Implemented <code>sanitize(value)</code> in <code>src/sanitize.py</code>.</p>' +
    '<p><b>What it does:</b></p>' +
    '<ul><li>Redacts values for <code>password</code>, <code>secret</code>, <code>token</code>, <code>api_key</code>, ' +
    '<code>access_token</code> keys — matched case-insensitively with surrounding spaces ignored.</li>' +
    '<li>Recurses through nested dicts, lists, and tuples.</li>' +
    '<li>Preserves keys, insertion order, tuple shape, and scalar types; returns a fresh detached structure so the input is never mutated.</li></ul>' +
    '<p><b>Verification:</b> A scratch script exercised all five keys (case/space variations), nested structures, tuple shape, ' +
    'scalar-type preservation, non-string keys, and input immutability — all checks passed. The scratch file was then removed, confirmed absent.</p>';

  var TITLE = "Implement sanitize(value) in src/sanitize.py so our structured logs never leak secrets";

  var ICON = {
    think: '<path d="M8 2.5a4 4 0 0 0-2.4 7.2c.4.3.6.7.6 1.2V12h3.6v-1.1c0-.5.2-.9.6-1.2A4 4 0 0 0 8 2.5Z"/><path d="M6.3 13.8h3.4"/>',
    find: '<path d="M2.5 4.5h4l1.2 1.4h5.8v6.6h-11z"/>',
    list: '<path d="M2.5 4.5h4l1.2 1.4h5.8v6.6h-11z"/>',
    read: '<path d="M4 2.5h5l3 3v8H4z"/><path d="M9 2.5v3h3"/>',
    edit: '<path d="M3 13l.6-2.6 7-7 2 2-7 7z"/><path d="M9.6 4.4l2 2"/>',
    write: '<path d="M3 13l.6-2.6 7-7 2 2-7 7z"/><path d="M9.6 4.4l2 2"/>',
    run: '<path d="M2.8 4.5 6 7.5l-3.2 3"/><path d="M7.5 11h5.5"/>',
    delete: '<path d="M3.5 4.5h9"/><path d="M6 4.5V3h4v1.5"/><path d="M4.8 4.5l.6 9h5.2l.6-9"/>',
    folder: '<path d="M2.5 4.5h4l1.2 1.4h5.8v6.6h-11z"/>',
    pencil: '<path d="M3 13l.6-2.6 7-7 2 2-7 7z"/>',
    chev: '<path d="M4.5 6.5 8 10l3.5-3.5"/>',
    up: '<path d="M4.5 9.5 8 6l3.5 3.5"/>',
    plus: '<path d="M8 3.5v9M3.5 8h9"/>',
    gear: '<circle cx="8" cy="8" r="2"/><path d="M8 2.5v1.6M8 11.9v1.6M2.5 8h1.6M11.9 8h1.6M4.1 4.1l1.1 1.1M10.8 10.8l1.1 1.1M4.1 11.9l1.1-1.1M10.8 5.2l1.1-1.1"/>',
    clock: '<circle cx="8" cy="8" r="5.5"/><path d="M8 5v3.2l2 1.3"/>',
    grid: '<path d="M3 3h4v4H3zM9 3h4v4H9zM3 9h4v4H3zM9 9h4v4H9z"/>',
    people: '<circle cx="6" cy="6" r="2"/><path d="M2.5 12.5c.5-2 1.8-3 3.5-3s3 1 3.5 3"/><circle cx="11" cy="6.5" r="1.6"/><path d="M10.5 9.6c1.6 0 2.6.9 3 2.9"/>',
    tasks: '<path d="M3 3.5h7v7H3z"/><path d="M6 13h7V6"/>',
    side: '<path d="M2.5 3.5h11v9h-11z"/><path d="M6 3.5v9"/>',
    arrow: '<path d="M3 8h9M8.5 4.5 12 8l-3.5 3.5"/>',
    send: '<path d="M3.5 3.5 13 8l-9.5 4.5L5.5 8z"/>',
    right: '<path d="M6.5 4.5 10 8l-3.5 3.5"/>',
    ring: '<circle cx="8" cy="8" r="5.5"/>',
    term: '<path d="M2.8 5 5.6 7.6 2.8 10.2"/><path d="M7 10.5h4"/>'
  };

  function icon(name, cls) {
    return '<svg class="ra-i' + (cls ? " " + cls : "") + '" viewBox="0 0 16 16" aria-hidden="true">' + ICON[name] + "</svg>";
  }

  function esc(text) {
    return String(text).replace(/[&<>"]/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c];
    });
  }

  function stepHtml(step) {
    if (step.k === "say") return '<div class="ra-say">' + esc(step.t) + "</div>";
    var tail = "";
    if (step.k === "think") tail = '<span class="ra-mono">' + esc(step.d) + "</span>";
    else tail = '<span class="ra-target">' + esc(step.d) + "</span>";
    if (step.add) tail += ' <span class="ra-add">+' + step.add + '</span> <span class="ra-del">&minus;0</span>';
    if (step.ms) tail += ' <span class="ra-mono ra-dim">' + esc(step.ms) + "</span>";
    return '<div class="ra-step">' + icon(step.k) + "<span>" + esc(step.t) + '</span><span class="ra-dot">&middot;</span>' + tail + "</div>";
  }

  var SHELL =
    '<div class="ra" data-state="empty">' +
      '<div class="ra-titlebar">' +
        '<span class="ra-tb-left">' + icon("side") + '<svg class="ra-i ra-nav-i" viewBox="0 0 16 16"><path d="M10 3.5 5.5 8l4.5 4.5"/></svg><svg class="ra-i ra-nav-i ra-off" viewBox="0 0 16 16"><path d="M6 3.5 10.5 8 6 12.5"/></svg></span>' +
        '<span class="ra-caps"><i></i><i></i><i></i></span>' +
      "</div>" +
      '<aside class="ra-side">' +
        '<div class="ra-new">' + icon("plus") + "New Chat</div>" +
        '<div class="ra-nav">' + icon("pencil") + "Customize</div>" +
        '<div class="ra-sec">' + icon("grid") + "<span>Workspaces</span>" + icon("plus", "ra-r") + "</div>" +
        '<div class="ra-ws">' + icon("folder") + "<span>log-redactor</span></div>" +
        '<div class="ra-sess"><span class="ra-sess-t">New Session</span><span class="ra-mono ra-dim">22:54</span></div>' +
        '<div class="ra-sec">' + icon("clock") + "<span>Recents</span>" + icon("chev", "ra-r") + icon("plus") + "</div>" +
        '<div class="ra-empty">No recent sessions</div>' +
        '<div class="ra-sec">' + icon("term") + "<span>CLI sessions</span>" + icon("chev", "ra-r") + "</div>" +
        '<div class="ra-empty">No CLI sessions</div>' +
        '<div class="ra-settings">' + icon("gear") + "Settings</div>" +
      "</aside>" +
      '<main class="ra-main">' +
        '<div class="ra-crumb">' + icon("folder") + '<span class="ra-dim">log-redactor</span><span class="ra-dim">&rsaquo;</span>' +
          '<b class="ra-title">' + esc(TITLE) + '</b><span class="ra-mode"><i></i>Standard</span>' +
          '<span class="ra-working"><i></i>Working</span>' + icon("side", "ra-crumb-r") + "</div>" +
        '<div class="ra-hello"><div class="ra-path"><i></i>D:\\projects\\log-redactor</div>Hello. Where should we start today?</div>' +
        '<div class="ra-scroll"><div class="ra-thread">' +
          '<div class="ra-user"></div>' +
          '<div class="ra-turn">' +
            '<div class="ra-worked">' + icon("up", "ra-open") + icon("think", "ra-shut") + '<span class="ra-worked-t">Working&hellip;</span><span class="ra-dim">&middot; <b class="ra-count">0</b> steps</span></div>' +
            '<div class="ra-steps"></div>' +
            '<div class="ra-reply">' + REPLY +
              '<div class="ra-files"><div class="ra-files-h"><span class="ra-files-i">{&nbsp;}</span><b>Edited 2 files</b><span class="ra-chip">View changes</span></div>' +
              '<div class="ra-mono">sanitize.py</div><div class="ra-mono">_scratch_check.py</div></div>' +
            "</div>" +
          "</div>" +
        "</div></div>" +
        '<div class="ra-composer"><div class="ra-input"><span class="ra-typed"></span><span class="ra-caret"></span><span class="ra-ph">Message Ru&hellip;</span></div>' +
          '<div class="ra-bar">' + icon("plus") + '<span class="ra-pick">hy4-flash-free ' + icon("chev") + "</span>" +
          '<span class="ra-sep"></span><span class="ra-pick">default ' + icon("chev") + '</span>' + icon("ring", "ra-ring") + '<span class="ra-send">' + icon("send") + "</span></div></div>" +
      "</main>" +
      '<div class="ra-panel">' +
        '<div class="ra-panel-h"><b>' + esc(TITLE) + "</b>" + icon("right") + "</div>" +
        '<div class="ra-prow">' + icon("pencil") + '<span class="ra-files-n">No file changes</span><span class="ra-diff"><span class="ra-add">+<b class="ra-adds">0</b></span> <span class="ra-del">&minus;0</span></span></div>' +
        '<div class="ra-prow">' + icon("people") + "<span>Subagents<small>No subagents yet</small></span></div>" +
        '<div class="ra-prow">' + icon("tasks") + "<span>Background tasks<small>Tasks appear here while a run is active.</small></span></div>" +
      "</div>" +
    "</div>";

  function q(root, sel) {
    return root.querySelector(sel);
  }

  function mount(host, options) {
    host.innerHTML = SHELL;
    var root = host.firstChild;
    var stepsEl = q(root, ".ra-steps");
    var scroll = q(root, ".ra-scroll");
    var typed = q(root, ".ra-typed");
    var count = q(root, ".ra-count");
    var adds = q(root, ".ra-adds");
    var filesN = q(root, ".ra-files-n");
    q(root, ".ra-user").textContent = PROMPT;

    function setFiles(n, add) {
      filesN.textContent = n === 0 ? "No file changes" : n === 1 ? "1 file changed" : n + " files changed";
      root.classList.toggle("has-diff", n > 0);
      adds.textContent = String(add);
    }

    var rendered = 0;
    function render(state, shown) {
      root.dataset.state = state;
      // Append only the new steps so each one animates in once; rebuild when the loop restarts.
      if (shown < rendered) {
        stepsEl.innerHTML = "";
        rendered = 0;
      }
      if (shown > rendered) {
        stepsEl.insertAdjacentHTML("beforeend", STEPS.slice(rendered, shown).map(stepHtml).join(""));
        rendered = shown;
      }
      var tools = STEPS.slice(0, shown).filter(function (s) { return s.k !== "say"; }).length;
      count.textContent = String(state === "done" ? 18 : tools);
      var files = 0, add = 0;
      STEPS.slice(0, shown).forEach(function (s) {
        if (s.file) files = Math.max(files, s.file);
        if (s.add) add += s.add;
      });
      setFiles(files, add);
      q(root, ".ra-worked-t").textContent = state === "done" ? "Worked" : "Working\u2026";
    }

    function bottom() {
      scroll.scrollTop = scroll.scrollHeight;
    }

    if (!options || options.mode !== "play") {
      typed.textContent = "";
      render("done", STEPS.length);
      return { play: function () {}, pause: function () {}, seek: function () {} };
    }

    // The run as a timeline (ms). Phases: 0 ask, 1 watch, 2 done.
    var T_TYPE = 4200, T_SEND = 4900, T_STEP0 = 5600, STEP_GAP = 620;
    var T_FOLD = T_STEP0 + STEPS.length * STEP_GAP + 500;
    var T_END = T_FOLD + 6500;
    var PHASES = [0, T_SEND, T_FOLD];
    var start = 0, offset = 0, raf = 0, running = false, lastShown = -1, lastState = "";
    var onPhase = options.onPhase || function () {};
    var lastPhase = -1;

    function frame(now) {
      if (!running) return;
      var t = offset + (now - start);
      if (t > T_END) {
        offset = 0;
        start = now;
        t = 0;
      }
      draw(t);
      raf = window.requestAnimationFrame(frame);
    }

    function draw(t) {
      var state, shown = 0;
      root.classList.toggle("is-sending", t >= T_SEND - 260 && t < T_SEND + 120);
      if (t < T_SEND) {
        state = "typing";
        var n = Math.round(PROMPT.length * Math.min(1, Math.max(0, (t - 500) / T_TYPE)));
        if (typed.textContent.length !== n) typed.textContent = PROMPT.slice(0, n);
      } else if (t < T_FOLD) {
        state = "working";
        shown = Math.min(STEPS.length, Math.floor((t - T_STEP0) / STEP_GAP) + 1);
        if (t < T_STEP0) shown = 0;
      } else {
        state = "done";
        shown = STEPS.length;
      }
      if (state !== "typing" && typed.textContent) typed.textContent = "";
      if (state !== lastState || shown !== lastShown) {
        render(state, shown);
        if (state === "done") scroll.scrollTop = 0;
        else bottom();
        lastState = state;
        lastShown = shown;
      }
      var p = t < PHASES[1] ? 0 : t < PHASES[2] ? 1 : 2;
      var span = p === 0 ? [0, PHASES[1]] : p === 1 ? [PHASES[1], PHASES[2]] : [PHASES[2], T_END];
      onPhase(p, (t - span[0]) / (span[1] - span[0]), t / T_END);
      lastPhase = p;
    }

    var api = {
      play: function () {
        if (running) return;
        running = true;
        start = performance.now();
        raf = window.requestAnimationFrame(frame);
      },
      pause: function () {
        if (!running) return;
        offset += performance.now() - start;
        running = false;
        window.cancelAnimationFrame(raf);
      },
      // Draws the run as it stands t ms in, without playing (used to check frames).
      at: function (t) {
        lastState = "";
        draw(t);
      },
      seek: function (phase) {
        offset = PHASES[phase] + (phase === 0 ? 0 : 1);
        start = performance.now();
        lastState = "";
        draw(offset);
      }
    };
    root.classList.add("is-playing");
    draw(0);
    return api;
  }

  window.RuApp = { mount: mount };
})();
