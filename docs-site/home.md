---
title: Ru Agent
template: home.html
hide:
  - navigation
  - toc
---

<section class="hero" aria-labelledby="hero-title">
<div class="hero__copy">
<p class="hero__kicker"><span class="dot" aria-hidden="true"></span> Preview &middot; Windows x64 &middot; MIT</p>
<h1 id="hero-title" class="hero__title">The coding agent that lives <em>on your desktop.</em></h1>
<p class="hero__lede">A native Windows app that reads your project, edits files and runs your checks while you watch every step. Your model, your API key, no account.</p>
<div class="hero__actions">
<a class="btn btn--go" href="https://github.com/OniAntou/Ru-Agent/releases">Download for Windows</a>
<a class="hero__alt" href="#install">or install the CLI</a>
</div>
</div>
<div class="window window--hero ra-host" data-ru-app="final" role="img" aria-label="The Ru desktop app on Windows after a finished task: the request, a summary of the change and how it was verified, the edited files, and a session panel showing two files changed.">
<noscript><img src="_manual/shots/desktop-steps.png" width="1584" height="992" alt="The Ru desktop app working on a task."></noscript>
</div>
</section>

<section class="replay" aria-labelledby="replay-title">
<h2 id="replay-title" class="visually-hidden">One real task, start to finish</h2>
<div class="replay__stage">
<figure class="window window--stage">
<div class="ra-host" data-ru-app="play" role="img" aria-label="The Ru desktop app replaying a real run: the prompt is typed and sent, steps stream into the activity feed, and the finished, checked reply lands."></div>
<div class="replay__progress" aria-hidden="true"><span></span></div>
</figure>
</div>
<ol class="replay__steps">
<li class="beat" id="ask" data-phase="0">
<p class="beat__label">Ask</p>
<h3>Say what you want, in plain words.</h3>
<p>Open a project folder, type the change and press Enter. The model is the one you picked from your own provider: OpenAI, Anthropic, any Models.dev provider such as OpenRouter or OpenCode, or an OpenAI-compatible endpoint you run yourself.</p>
</li>
<li class="beat" id="watch" data-phase="1">
<p class="beat__label">Watch</p>
<h3>Every step happens in the open.</h3>
<p>Ru reads the files it needs, makes the edit, writes a scratch check and runs it. Each read, edit and command lands in the activity feed as it happens, under the permission mode you chose for the session.</p>
</li>
<li class="beat" id="done" data-phase="2">
<p class="beat__label">Done</p>
<h3>Finished, checked, and accounted for.</h3>
<p>The reply says what changed and how it was verified, the edited files are listed under the turn, and the session panel keeps the totals. You review the work, not a wall of text.</p>
</li>
<li class="replay__caption">Rebuilt in HTML from a real Ru 0.1.3 Preview run on a demo project: the prompt, steps and reply are that run's own, sped up. The real task took about a minute.</li>
</ol>
</section>

<section class="bench" aria-labelledby="bench-title">
<div class="bench__head">
<h2 id="bench-title" class="band-title">Light on your machine.</h2>
<p class="bench__lede">Ru&rsquo;s terminal app has the smallest install of the four by a wide margin and idles in as little memory as the leanest of them, measured side by side on one Windows laptop.</p>
</div>
<div class="bench__charts">
<figure class="bench__chart" aria-labelledby="bench-disk">
<figcaption id="bench-disk"><span class="bench__metric">Install size</span><span class="bench__unit">MB on disk, lower is lighter</span></figcaption>
<ol class="bench__rows">
<li class="is-ru" style="--v:.332" title="Ru: 163 MB, terminal app and Host"><span class="bench__name">Ru</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">163</span></li>
<li style="--v:.569" title="Claude Code: 280 MB"><span class="bench__name">Claude Code</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">280</span></li>
<li style="--v:.875" title="Codex: 430 MB, plus Node.js"><span class="bench__name">Codex</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">430</span></li>
<li style="--v:1" title="OpenCode: 491 MB"><span class="bench__name">OpenCode</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">491</span></li>
</ol>
</figure>
<figure class="bench__chart" aria-labelledby="bench-ram">
<figcaption id="bench-ram"><span class="bench__metric">Memory while idle</span><span class="bench__unit">MB in use, lower is lighter</span></figcaption>
<ol class="bench__rows">
<li class="is-ru" style="--v:.395" title="Ru: 119 MB"><span class="bench__name">Ru</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">119</span></li>
<li style="--v:.551" title="Claude Code: 166 MB"><span class="bench__name">Claude Code</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">166</span></li>
<li style="--v:.429" title="Codex: 129 MB"><span class="bench__name">Codex</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">129</span></li>
<li style="--v:1" title="OpenCode: 301 MB"><span class="bench__name">OpenCode</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">301</span></li>
</ol>
</figure>
<figure class="bench__chart" aria-labelledby="bench-start">
<figcaption id="bench-start"><span class="bench__metric">Time to the input box</span><span class="bench__unit">seconds from launch, lower is faster</span></figcaption>
<ol class="bench__rows">
<li class="is-ru" style="--v:.241" title="Ru: 1.7 s"><span class="bench__name">Ru</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">1.7</span></li>
<li style="--v:.179" title="Claude Code: 1.3 s"><span class="bench__name">Claude Code</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">1.3</span></li>
<li style="--v:.084" title="Codex: 0.6 s"><span class="bench__name">Codex</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">0.6</span></li>
<li style="--v:1" title="OpenCode: 7.0 s"><span class="bench__name">OpenCode</span><span class="bench__track"><span class="bench__fill"></span></span><span class="bench__val">7.0</span></li>
</ol>
</figure>
</div>
<p class="bench__idle">Left alone, all four sit near idle: between 1 and 3% of one CPU core.</p>
<details class="bench__table">
<summary>See every number</summary>
<table>
<thead><tr><th scope="col">Agent</th><th scope="col">Version</th><th scope="col">Install (MB)</th><th scope="col">Idle memory (MB)</th><th scope="col">Input box (s)</th><th scope="col">CPU to start (s)</th><th scope="col">Idle CPU (% of a core)</th></tr></thead>
<tbody>
<tr><th scope="row">Ru</th><td>0.1.3 Preview + fixes, terminal app</td><td>163</td><td>119</td><td>1.70</td><td>1.45</td><td>2.5</td></tr>
<tr><th scope="row">Claude Code</th><td>2.1.304</td><td>280</td><td>166</td><td>1.26</td><td>1.25</td><td>1.2</td></tr>
<tr><th scope="row">Codex</th><td>0.160.0</td><td>430</td><td>129</td><td>0.59</td><td>0.59</td><td>2.5</td></tr>
<tr><th scope="row">OpenCode</th><td>2.3.0</td><td>491</td><td>301</td><td>7.04</td><td>2.98</td><td>1.6</td></tr>
</tbody>
</table>
</details>
<p class="bench__note">Measured on 18 November 2026 on an Intel Core i7-13620H with 16 GB of RAM running Windows 11: median of five launches each, after one warm-up launch, in a fresh two-file project with no prompt sent. Ru is its terminal app from the current development build, which stops the welcome animation after ten seconds untouched; the released 0.1.3 Preview keeps about 10% of one core busy instead. Install counts the files each install puts on disk: for Ru, the terminal app and the Host it bundles; Node.js, which Codex also needs, is not counted. Memory is the private working set of every process the agent starts, 15 seconds after its input box appears. MB here means 1,048,576 bytes. Time to the input box runs from launch until the box is drawn.</p>
</section>

<section class="native" aria-labelledby="native-title">
<h2 id="native-title" class="band-title">Built for Windows. <span>Not ported to it.</span></h2>
<dl class="native__list">
<div><dt>A native WinUI 3 interface</dt><dd>A real Windows app with the system's own controls, keyboard, theme and window behaviour. Not an Electron shell around a web page.</dd></div>
<div><dt>Keys in Credential Manager</dt><dd>Your API keys are stored by Windows, not in a config file. No Ru account and no hosted Ru service between you and your provider.</dd></div>
<div><dt>A terminal that knows PowerShell</dt><dd>Commands run as you, in your workspace, with output streamed into the conversation and long jobs moved to the background.</dd></div>
<div><dt>Your history stays home</dt><dd>Sessions, run history and workspace state live on your machine. Prompts and tool results go to the provider you select; web search and fetch make their own requests when you use them.</dd></div>
</dl>
</section>

<section class="modes" aria-labelledby="modes-title">
<h2 id="modes-title" class="band-title">You decide how far it reaches.</h2>
<p class="modes__lede">Every session runs under a permission mode. Workspace trust and explicit approvals sit on top; instructions and skills guide Ru but never grant it more.</p>
<ol class="modes__dial">
<li><code>plan</code><p>Explore the project and plan the change with read-only tools.</p></li>
<li class="is-default"><code>standard</code><p>Work in the workspace. Requests beyond the standard policy can ask you first.</p></li>
<li><code>full_access</code><p>Broader tool access for work you trust, including paths outside the workspace.</p></li>
</ol>
<p class="limits"><strong>Know the limits.</strong> Terminal commands run as your Windows user without an OS filesystem or network sandbox, in every mode that allows them. Ru is a Preview and its installers are unsigned. <a href="docs/operations/agent-tools/">Read the tool policy</a>.</p>
</section>

<section class="terminal" aria-labelledby="terminal-title">
<div class="terminal__copy">
<h2 id="terminal-title" class="band-title">Prefer the terminal? <span>It is there too.</span></h2>
<p>The same agent runs as a full-screen terminal UI: <code>ru</code> opens it, with session history, model selection, slash commands, subagents and a multiline composer. A stdio RPC bridge and a TypeScript SDK let you script it.</p>
<a class="btn btn--line" href="backend-dotnet/RuAgent.Cli/">Terminal guide</a>
</div>
<figure class="window window--term">
<div class="term__bar" aria-hidden="true"><span>ru</span></div>
<pre class="term__body"><code>&gt; Delegate a review subagent (agent_spawn, review kind) to review a.py for bugs, wait for it with agent_wait, then summarize in two lines.
<span class="t-dim">◐</span> subagent agent·893a67 started
<span class="t-ok">✓</span> subagent agent·893a67 finished  ·  /subagents agent_893a67bd803a12f38d96893f39197927 result</code></pre>
<figcaption>Subagent lines from a real terminal session.</figcaption>
</figure>
</section>

<section class="install" id="install" aria-labelledby="install-title">
<h2 id="install-title" class="band-title">Get Ru on your machine.</h2>
<div class="install__grid">
<div class="install__card install__card--main">
<p class="install__label">Desktop app</p>
<h3>Download the installer</h3>
<p>Per-user Windows x64 installer with its own backend. No .NET install needed; updates from Settings, About.</p>
<p class="install__note">Preview build. The installer is unsigned, so Windows may warn before it runs.</p>
<a class="btn btn--go" href="https://github.com/OniAntou/Ru-Agent/releases">Open releases</a>
</div>
<div class="install__card">
<p class="install__label">Command line</p>
<h3>One command in Command Prompt</h3>
<pre class="install__cmd"><code>powershell -NoProfile -ExecutionPolicy Bypass -Command "&amp; ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/OniAntou/Ru-Agent/dev/scripts/install.ps1'))) -Channel Preview"</code></pre>
<button class="copy" type="button" data-copy=".install__cmd code">Copy command</button>
<p class="install__after">Then open a new terminal and run <code>ru</code>.</p>
</div>
</div>
<p class="install__next">First time? <a href="docs/guide/install/">Install</a>, <a href="docs/guide/first-task/">connect a provider and run your first task</a>, then read <a href="docs/guide/permissions/">permissions and approvals</a>. Building from source: <a href="overview/#quick-start">Quick start</a>.</p>
</section>

<section class="closer" aria-labelledby="closer-title">
<div class="closer__inner">
<h2 id="closer-title" class="closer__title">Your next change, done on your desktop.</h2>
<p class="closer__lede">Download Ru, point it at a project and watch the work happen.</p>
<div class="closer__actions">
<a class="btn btn--ink" href="https://github.com/OniAntou/Ru-Agent/releases">Download for Windows</a>
<a class="closer__alt" href="#install">or install the CLI</a>
</div>
</div>
</section>
