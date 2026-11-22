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

    internal static string TextOf(JsonObject? source, string name) =>
        source?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    internal static bool BoolOf(JsonObject? source, string name) =>
        source?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string Index(int oneBased) =>
        (oneBased - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

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
