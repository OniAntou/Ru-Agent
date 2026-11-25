---
status: accepted
date: 2026-11-26
---

# Draw diagrams, formulas and SVG in a locked-down renderer

## Context

The desktop showed Mermaid fences and TeX as raw source, and a workspace `.svg` only as
XML text. The file-preview rule was "HTML and SVG are source text, never active browser
content". The owner asked for real diagrams, typeset math and SVG pictures.

Two constraints decided the design:

- **Model and workspace text grants no authority.** A diagram, a formula or an SVG file is
  untrusted content. Drawing it must not run its scripts, reach the network, open windows
  or touch the Broker. It also needs no new Host route or grant.
- **Native drawing falls short.** WinUI `SvgImageSource` uses Direct2D's SVG subset, which
  drops `<text>`, so labels in an SVG disappear without a word. RichTextBlock has no math
  layout, and nothing native lays out Mermaid.

## Decision

1. **One hidden renderer.** `SandboxRenderer` owns one off-screen `CoreWebView2Controller`
   (`IsVisible = false`) under the main window, in its own WebView2 profile
   (`%LOCALAPPDATA%\Ru\WebView2Renderer`). The profile turns off Chromium's background
   throttling, which otherwise slowed the never-shown page until a first render timed
   out. The page says it is ready only after MathJax has started and both libraries have
   drawn once. It loads `Assets/render/render.html` from
   the virtual host `https://ru-render.local/` and returns PNG pictures. The transcript and
   the Preview tab show those pictures with native `Image` controls, so virtualization,
   overlays and selection work as before. No WebView2 is created per block.
2. **Vendored libraries, no network.** The page loads Mermaid 12.3.0
   (`dist/mermaid.min.js`, MIT) and MathJax 3.2.2 (`es5/tex-svg.js`, Apache-2.0). Both come
   from the npm registry, were checked against its sha512 integrity, and ship with their
   licenses. MathJax replaced KaTeX because it emits self-contained SVG, with glyphs as
   paths, which rasterizes offline. KaTeX emits HTML plus font files.
3. **Lockdown.** The renderer is locked down in three places:
   - **Page.** A CSP of `default-src 'none'` with `script-src 'self'` and no
     `unsafe-eval`; only `data:` images.
   - **Web view settings.** Dev tools, context menus, host objects, script dialogs,
     autofill, accelerator keys and zoom are off.
   - **Web view events.** A `WebResourceRequested` filter on `*` answers 403 to anything
     outside the renderer host and the in-memory `data:` images the page draws through. Navigation outside `render.html` is cancelled, and every
     frame navigation is cancelled. New windows are handled and never open, downloads are
     cancelled, and permissions are denied.
4. **Content stays data.** The host sends `{id, kind, source, dark, ink, fontSize, display}`
   as a JSON web message. The page never inserts the source as markup:
   - **Mermaid** runs with `securityLevel: 'strict'` and `htmlLabels: false`. The keys a
     diagram's `%%{init}%%` must not loosen are in Mermaid's `secure` list.
   - **MathJax** has `require` and `autoload` removed.
   - **Every SVG**, including an SVG file, reaches the canvas through an `<img>`, where an
     SVG runs no script and loads nothing. A workspace SVG is also made inert first: its
     `script`, `foreignObject` and embedded-media elements, event-handler attributes,
     `@import` rules and every reference that leaves the file are removed. An image that
     never loads answers `render_image_timeout` after 5 s instead of leaving the request
     waiting.
5. **Replies are untrusted.** `SandboxRenderPolicy.ReadResult` accepts a reply only when it
   comes from the renderer page and names the pending request id. Its `png` must be a
   `data:image/png;base64,` URL of at most 24 MiB of base64 that decodes to a PNG
   signature, and its sizes must be finite and at most 4096 px.
6. **Bounds and failure.** Sources are capped: Mermaid at 50,000 characters, TeX at 4,000
   and SVG at 1 MiB. Renders are serialized:
   - The page has 20 s to say it is ready.
   - A render has 10 s; on a timeout or a crashed process, the web view is replaced.
   - With no WebView2 runtime, the renderer stays off for the session.

   Every failure leaves the source on screen. The last 64 pictures are cached by kind,
   source and style.
7. **When it draws.** In chat:
   - **Mermaid:** a closed ```` ```mermaid ```` fence.
   - **Display math:** a closed `$$` block.
   - **Inline math:** `$…$`, drawn on the text baseline, except inside table cells,
     which cannot host a picture.

   The block a reply is still streaming into keeps showing its source. It is rebuilt
   once a later block arrives or the reply ends. A theme change redraws the pictures. In Preview, a `.svg` (still read under the
   text cap and checks) shows as a picture, with a Source/Picture toggle. HTML stays
   source text.

## Consequences

- The installer grows by about 7.6 MB of vendored JavaScript. The runtime-boundary
  allowlist (`scripts/check_python_runtime_boundary.py`) names the four renderer scripts
  by exact path; any other product `.js` still fails.
- Updating a vendored library means replacing the file, rechecking its integrity and
  license, and keeping `SandboxRendererContractTests` green.
- The renderer is presentation only. It adds no Host route, grant, tool or Broker path,
  and the model cannot see or call it.
- Copy on a picture card copies the source, never the picture.
