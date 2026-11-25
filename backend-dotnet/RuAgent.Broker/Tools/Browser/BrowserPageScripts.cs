using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Browser;

/// <summary>
/// Page-side scripts and result parsing shared by every browser engine (ADR 0055). The Chromium
/// CDP engine and the Firefox WebDriver BiDi engine inject the same JavaScript and therefore speak
/// one snapshot/ref/rect format, so the Agent sees the same contract regardless of the browser.
/// </summary>
internal static class BrowserPageScripts
{
    internal static string Snapshot(int maxElements, int maxTextChars) => SnapshotTemplate
        .Replace("__MAX_ELEMENTS__", maxElements.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("__MAX_TEXT__", maxTextChars.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    internal static string ElementRect(int index) =>
        "(function(){const el=window.__ruRefs&&window.__ruRefs[" + Index(index)
        + "];if(!el||!el.isConnected)return null;el.scrollIntoView({block:'center',inline:'center'});"
        + "const r=el.getBoundingClientRect();if(r.width<1||r.height<1)return null;"
        + "return {x:r.left+r.width/2,y:r.top+r.height/2};})()";

    internal static string Focus(int index, bool replace) =>
        "(function(){const el=window.__ruRefs&&window.__ruRefs[" + Index(index)
        + "];if(!el||!el.isConnected)return false;el.scrollIntoView({block:'center'});el.focus();"
        + (replace ? "if(typeof el.select==='function')el.select();" : string.Empty)
        + "return true;})()";

    internal static string Fill(int index, string text)
    {
        var literal = JsString(text);
        return "(function(){const el=window.__ruRefs&&window.__ruRefs[" + Index(index)
            + "];if(!el||!el.isConnected)return false;"
            + "if(el instanceof HTMLInputElement||el instanceof HTMLTextAreaElement){"
            + "const proto=el instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;"
            + "const setter=Object.getOwnPropertyDescriptor(proto,'value');if(!setter||!setter.set)return false;"
            + "setter.set.call(el," + literal + ");"
            + "}else if(el.isContentEditable){el.textContent=" + literal + ";}else{return false;}"
            + "el.dispatchEvent(new Event('input',{bubbles:true}));"
            + "el.dispatchEvent(new Event('change',{bubbles:true}));return true;})()";
    }

    /// <summary>The ref's box in viewport pixels after scrolling it into view, for an element screenshot.</summary>
    internal static string ElementBox(int index) =>
        "(function(){const el=window.__ruRefs&&window.__ruRefs[" + Index(index)
        + "];if(!el||!el.isConnected)return null;el.scrollIntoView({block:'center',inline:'center'});"
        + "const r=el.getBoundingClientRect();const x=Math.max(0,r.left),y=Math.max(0,r.top);"
        + "const w=Math.min(innerWidth,r.right)-x,h=Math.min(innerHeight,r.bottom)-y;"
        + "if(w<1||h<1)return null;return {x:x,y:y,width:w,height:h};})()";

    /// <summary>
    /// Scrolls a ref into view, or scrolls the scrollable area under the viewport centre (the page
    /// when none) by whole steps; returns the new offsets and whether that edge was reached.
    /// </summary>
    internal static string Scroll(int? index, string direction, int amount)
    {
        var pixels = (amount * RuAgent.Protocol.BrowserLimits.ScrollStepPixels).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (dx, dy) = direction switch
        {
            "up" => ("0", "-" + pixels),
            "left" => ("-" + pixels, "0"),
            "right" => (pixels, "0"),
            _ => ("0", pixels),
        };
        var body = index is { } refIndex
            ? "const el=window.__ruRefs&&window.__ruRefs[" + Index(refIndex) + "];if(!el||!el.isConnected)return null;"
              + "el.scrollIntoView({block:'center',inline:'center'});const box=document.scrollingElement||document.documentElement;"
              + "return {x:Math.round(box.scrollLeft),y:Math.round(box.scrollTop),at_end:false};"
            : "const page=document.scrollingElement||document.documentElement;"
              + "let box=document.elementFromPoint(innerWidth/2,innerHeight/2);"
              + "const scrollable=(n)=>{if(!n||n===document.body||n===document.documentElement)return false;"
              + "const s=getComputedStyle(n);const y=/(auto|scroll|overlay)/.test(s.overflowY)&&n.scrollHeight>n.clientHeight+1;"
              + "const x=/(auto|scroll|overlay)/.test(s.overflowX)&&n.scrollWidth>n.clientWidth+1;return " + (dy == "0" ? "x" : "y") + ";};"
              + "while(box&&!scrollable(box))box=box.parentElement;if(!box)box=page;"
              + "const beforeX=box.scrollLeft,beforeY=box.scrollTop;"
              + "if(box===page)window.scrollBy(" + dx + "," + dy + ");else box.scrollBy(" + dx + "," + dy + ");"
              + "return {x:Math.round(box.scrollLeft),y:Math.round(box.scrollTop),at_end:box.scrollLeft===beforeX&&box.scrollTop===beforeY};";
        return "(function(){" + body + "})()";
    }

    /// <summary>
    /// Selects an option of a &lt;select&gt; ref by value or visible label (exact first, then
    /// case-insensitive) and dispatches input and change; lists options when none matches.
    /// </summary>
    internal static string Select(int index, string value)
    {
        var literal = JsString(value);
        return "(function(){const el=window.__ruRefs&&window.__ruRefs[" + Index(index)
            + "];if(!el||!el.isConnected)return {ok:false,reason:'missing'};"
            + "if(!(el instanceof HTMLSelectElement))return {ok:false,reason:'not_select'};"
            + "const want=" + literal + ";const opts=Array.from(el.options);const label=(o)=>(o.label||o.text||'').trim();"
            + "let hit=opts.find(o=>o.value===want)||opts.find(o=>label(o)===want);"
            + "if(!hit){const lower=want.toLowerCase();hit=opts.find(o=>o.value.toLowerCase()===lower)||opts.find(o=>label(o).toLowerCase()===lower);}"
            + "if(!hit)return {ok:false,reason:'no_option',options:opts.slice(0,30).map(o=>label(o).slice(0,100))};"
            + "if(hit.disabled)return {ok:false,reason:'disabled',options:[]};"
            + "el.focus();el.value=hit.value;hit.selected=true;"
            + "el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));"
            + "return {ok:true,value:hit.value.slice(0,200),label:label(hit).slice(0,200)};})()";
    }

    /// <summary>
    /// Finds visible elements whose accessible name or own text contains the query, deepest match
    /// first in DOM order, and appends them to the page's refs so earlier snapshot refs stay valid.
    /// </summary>
    internal static string Find(string query, int maxResults) =>
        FindTemplate
            .Replace("__QUERY__", JsString(query), StringComparison.Ordinal)
            .Replace("__MAX__", maxResults.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>Whether a ref is an enabled &lt;input type=file&gt;, and whether it accepts several files.</summary>
    internal static string FileInput(int index) =>
        "(function(){const el=window.__ruRefs&&window.__ruRefs[" + Index(index)
        + "];if(!el||!el.isConnected)return {ok:false,reason:'missing'};"
        + "if(!(el instanceof HTMLInputElement)||el.type!=='file')return {ok:false,reason:'not_file'};"
        + "if(el.disabled)return {ok:false,reason:'disabled'};return {ok:true,multiple:!!el.multiple};})()";

    /// <summary>The element a ref names, as a live object for the engine's file-input command.</summary>
    internal static string RefElement(int index) => "window.__ruRefs&&window.__ruRefs[" + Index(index) + "]";

    /// <summary>
    /// Marks the current document and starts a history move or reload after the script returns,
    /// so the evaluation is not torn down by the unload. Returns the token and the starting url.
    /// </summary>
    internal static string StartNavigation(string move)
    {
        var call = move switch
        {
            "back" => "history.back()",
            "forward" => "history.forward()",
            _ => "location.reload()",
        };
        return "(function(){const token='ru'+Math.random().toString(36).slice(2)+Date.now().toString(36);"
            + "window.__ruNavToken=token;const href=location.href;setTimeout(function(){" + call + ";},0);"
            + "return {token:token,href:href};})()";
    }

    /// <summary>Done once the page is loaded and is another document, or the same document at another url.</summary>
    internal static string NavigationSettled(string token, string href) =>
        "(function(){return {done:document.readyState==='complete'&&(window.__ruNavToken!==" + JsString(token)
        + "||location.href!==" + JsString(href) + "),url:location.href,title:document.title||''};})()";

    /// <summary>Validates a snapshot ref such as <c>e3</c> and returns its 1-based index.</summary>
    internal static int RefIndex(string elementRef)
    {
        if (elementRef.Length is < 2 or > 8
            || elementRef[0] != 'e'
            || !int.TryParse(elementRef.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            || index is < 1 or > 1_000_000)
        {
            throw new BrowserEngineException("browser_ref_invalid", elementRef + " is not a snapshot ref such as e3.");
        }

        return index;
    }

    /// <summary>A JavaScript string literal that is safe to interpolate into a script.</summary>
    internal static string JsString(string text)
    {
        var literal = JsonSerializer.Serialize(text);
        return literal.Replace("\u2028", "\\u2028", StringComparison.Ordinal).Replace("\u2029", "\\u2029", StringComparison.Ordinal);
    }

    internal static BrowserSnapshot ParseSnapshot(JsonObject payload, int maxElements, int maxTextChars)
    {
        var elements = new List<BrowserSnapshotElement>();
        if (payload["elements"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (elements.Count >= maxElements) break;
                if (item is not JsonObject element) continue;
                elements.Add(new BrowserSnapshotElement(
                    TextOf(element, "ref"),
                    TextOf(element, "role"),
                    TextOf(element, "name"),
                    TextOf(element, "value"),
                    BoolOf(element, "checked"),
                    BoolOf(element, "disabled")));
            }
        }

        var text = TextOf(payload, "text");
        if (text.Length > maxTextChars) text = text[..maxTextChars];
        return new BrowserSnapshot(
            TextOf(payload, "url"),
            TextOf(payload, "title"),
            elements,
            text,
            BoolOf(payload, "text_truncated"));
    }

    internal static (double X, double Y)? ParsePoint(JsonObject? value) =>
        value is not null
        && value["x"] is JsonValue xValue && xValue.TryGetValue<double>(out var x)
        && value["y"] is JsonValue yValue && yValue.TryGetValue<double>(out var y)
            ? (x, y)
            : null;

    internal static (double X, double Y, double Width, double Height)? ParseBox(JsonObject? value) =>
        value is not null
        && value["x"] is JsonValue xValue && xValue.TryGetValue<double>(out var x)
        && value["y"] is JsonValue yValue && yValue.TryGetValue<double>(out var y)
        && value["width"] is JsonValue wValue && wValue.TryGetValue<double>(out var width)
        && value["height"] is JsonValue hValue && hValue.TryGetValue<double>(out var height)
        && width >= 1 && height >= 1
            ? (x, y, width, height)
            : null;

    /// <summary>Fails by name unless the FileInput check found an enabled file input that takes this many files.</summary>
    internal static void RequireFileInput(JsonObject? check, string elementRef, int fileCount)
    {
        if (BoolOf(check, "ok"))
        {
            if (fileCount > 1 && !BoolOf(check, "multiple"))
            {
                throw new BrowserEngineException("browser_arguments_invalid", elementRef + " accepts one file; pass one path.");
            }

            return;
        }

        throw TextOf(check, "reason") switch
        {
            "not_file" => new BrowserEngineException("browser_upload_target_invalid", elementRef + " is not an <input type=file>; find the file input in a snapshot."),
            "disabled" => new BrowserEngineException("browser_upload_target_invalid", elementRef + " is disabled."),
            _ => new BrowserEngineException("browser_element_not_found", elementRef + " is not on the page; take a new snapshot."),
        };
    }

    internal static string TextOf(JsonObject? source, string name) =>
        source?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    internal static bool BoolOf(JsonObject? source, string name) =>
        source?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string Index(int oneBased) =>
        (oneBased - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Find runs in the page like the snapshot. It matches the query case-insensitively against each
    // visible element's accessible name and own text and keeps the deepest match, so a container
    // that only wraps a matching child is not listed. New refs extend window.__ruRefs, so the refs
    // of the latest snapshot stay valid; a later snapshot starts the numbering again.
    private const string FindTemplate = """
(function () {
  const query = __QUERY__.toLowerCase();
  const max = __MAX__;
  if (!Array.isArray(window.__ruRefs)) window.__ruRefs = [];
  const refs = window.__ruRefs;
  const trim = (value) => (value || '').replace(/\s+/g, ' ').trim();
  const visible = (el) => {
    const style = getComputedStyle(el);
    if (!style || style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;
    const rect = el.getBoundingClientRect();
    return rect.width > 1 && rect.height > 1;
  };
  const nameOf = (el) => trim(el.getAttribute('aria-label') || el.getAttribute('placeholder') || el.getAttribute('title') || el.getAttribute('alt') || '');
  const textOf = (el) => trim(el.innerText || '');
  const matches = (el) => nameOf(el).toLowerCase().includes(query) || textOf(el).toLowerCase().includes(query);
  const results = [];
  let total = 0;
  const nodes = document.querySelectorAll('body *');
  for (let index = 0; index < nodes.length; index++) {
    const el = nodes[index];
    if (!(el instanceof HTMLElement) || !visible(el) || !matches(el)) continue;
    if (Array.from(el.children).some((child) => child instanceof HTMLElement && visible(child) && matches(child))) continue;
    total++;
    if (results.length >= max) continue;
    let at = refs.indexOf(el);
    if (at < 0) { refs.push(el); at = refs.length - 1; }
    const role = trim(el.getAttribute('role')) || el.tagName.toLowerCase();
    results.push({ ref: 'e' + (at + 1), role: role, name: (nameOf(el) || textOf(el)).slice(0, 200) });
  }
  return { matches: results, total: total };
})()
""";

    // The snapshot runs in the page: it lists visible, interactive elements in DOM order and keeps
    // the element references only in the page's own window, so no model text is interpolated. The
    // same script is injected by both engines; only the transport differs.
    private const string SnapshotTemplate = """
(function () {
  const maxElements = __MAX_ELEMENTS__;
  const maxText = __MAX_TEXT__;
  const refs = [];
  const elements = [];
  const trim = (value) => (value || '').replace(/\s+/g, ' ').trim();
  const styleVisible = (el) => {
    const style = getComputedStyle(el);
    if (!style || style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
    if (style.opacity === '0') return false;
    const rect = el.getBoundingClientRect();
    return rect.width > 1 && rect.height > 1;
  };
  const accessibleName = (el) => {
    const aria = trim(el.getAttribute('aria-label'));
    if (aria) return aria;
    const ids = trim(el.getAttribute('aria-labelledby'));
    if (ids) {
      const text = ids.split(/\s+/).map((id) => {
        const node = document.getElementById(id);
        return node ? trim(node.innerText || node.textContent) : '';
      }).join(' ');
      if (text) return text;
    }
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) {
      const placeholder = trim(el.getAttribute('placeholder'));
      if (placeholder) return placeholder;
      if (el.labels) {
        const labels = trim(Array.from(el.labels).map((label) => label.innerText || '').join(' '));
        if (labels) return labels;
      }
    }
    if (el.tagName === 'IMG') return trim(el.getAttribute('alt'));
    return trim(el.innerText || el.textContent).slice(0, 200);
  };
  const roleOf = (el) => {
    const explicit = trim(el.getAttribute('role'));
    if (explicit) return explicit;
    const tag = el.tagName.toLowerCase();
    if (tag === 'a') return el.hasAttribute('href') ? 'link' : '';
    if (tag === 'button') return 'button';
    if (tag === 'input') {
      const type = (el.getAttribute('type') || 'text').toLowerCase();
      if (type === 'checkbox') return 'checkbox';
      if (type === 'radio') return 'radio';
      if (type === 'submit' || type === 'button' || type === 'reset') return 'button';
      if (type === 'range') return 'slider';
      return 'textbox';
    }
    if (tag === 'textarea') return 'textbox';
    if (tag === 'select') return 'combobox';
    if (tag === 'summary') return 'button';
    if (el.isContentEditable) return 'textbox';
    return '';
  };
  const nodes = document.querySelectorAll('body *');
  for (let index = 0; index < nodes.length && elements.length < maxElements; index++) {
    const el = nodes[index];
    if (!(el instanceof Element) || !styleVisible(el)) continue;
    const role = roleOf(el);
    const clickable = !!role || el.hasAttribute('onclick') || el.getAttribute('tabindex') === '0';
    if (!clickable) continue;
    const name = accessibleName(el).slice(0, 200);
    if (!role && !name) continue;
    refs.push(el);
    const entry = { ref: 'e' + refs.length, role: role || 'generic', name: name };
    if ('value' in el && typeof el.value === 'string' && el.value.length > 0) entry.value = el.value.slice(0, 200);
    if (el.checked) entry.checked = true;
    if (el.disabled) entry.disabled = true;
    elements.push(entry);
  }
  window.__ruRefs = refs;
  const body = document.body ? document.body.innerText || '' : '';
  const text = body.replace(/[ \t]+/g, ' ').replace(/\n{3,}/g, '\n\n').trim();
  return {
    url: location.href,
    title: document.title || '',
    elements: elements,
    text: text.slice(0, maxText),
    text_truncated: text.length > maxText
  };
})()
""";
}
