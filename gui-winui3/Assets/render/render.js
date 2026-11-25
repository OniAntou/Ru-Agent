// Ru's picture renderer. The host posts { id, kind, source, dark, ink, fontSize };
// the page answers { type: 'result', id, ok, png, width, height, depth } or
// { type: 'result', id, ok: false, error }. The source is data only: it is never
// inserted as markup into this page, and the drawn SVG reaches the canvas as an
// <img>, where an SVG can run no script and load nothing.
(function () {
    'use strict';

    var LIMITS = { mermaid: 50000, math: 4000, svg: 1048576 };
    var MAX_SIDE = 4096;
    var PIXEL_SCALE = 2;
    var IMAGE_TIMEOUT = 5000;
    var counter = 0;

    function fail(code) {
        var error = new Error(code);
        error.code = code;
        return error;
    }

    function number(value) {
        var parsed = parseFloat(value);
        return isFinite(parsed) && parsed > 0 ? parsed : 0;
    }

    function viewBoxSize(svg) {
        var box = (svg.getAttribute('viewBox') || '').trim().split(/[\s,]+/).map(parseFloat);
        return box.length === 4 && box[2] > 0 && box[3] > 0 ? { width: box[2], height: box[3] } : null;
    }

    function rasterize(markup, width, height) {
        var scale = Math.min(PIXEL_SCALE, MAX_SIDE / width, MAX_SIDE / height);
        if (!(scale > 0)) return Promise.reject(fail('render_too_large'));
        return new Promise(function (resolve, reject) {
            var image = new Image();
            // An image that never loads answers with a named error instead of silence.
            var timer = setTimeout(function () { reject(fail('render_image_timeout')); }, IMAGE_TIMEOUT);
            image.onload = function () {
                clearTimeout(timer);
                try {
                    var canvas = document.createElement('canvas');
                    canvas.width = Math.max(1, Math.ceil(width * scale));
                    canvas.height = Math.max(1, Math.ceil(height * scale));
                    var context = canvas.getContext('2d');
                    context.scale(scale, scale);
                    context.drawImage(image, 0, 0, width, height);
                    resolve({ png: canvas.toDataURL('image/png'), width: width, height: height });
                } catch (error) {
                    reject(fail('render_raster_failed'));
                }
            };
            image.onerror = function () { clearTimeout(timer); reject(fail('render_svg_invalid')); };
            image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(markup);
        });
    }

    function sized(svg, width, height) {
        svg.setAttribute('width', String(width));
        svg.setAttribute('height', String(height));
        svg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
        return new XMLSerializer().serializeToString(svg);
    }

    function parseSvg(markup) {
        var parsed = new DOMParser().parseFromString(markup, 'image/svg+xml');
        var root = parsed.documentElement;
        if (!root || root.nodeName.toLowerCase() !== 'svg' || parsed.getElementsByTagName('parsererror').length) {
            throw fail('render_svg_invalid');
        }

        return root;
    }

    var mermaidTheme = null;

    function renderMermaid(request) {
        var theme = request.dark ? 'dark' : 'default';
        if (mermaidTheme !== theme) {
            window.mermaid.initialize({
                startOnLoad: false,
                securityLevel: 'strict',
                theme: theme,
                htmlLabels: false,
                flowchart: { htmlLabels: false },
                fontFamily: 'Segoe UI, sans-serif',
                maxTextSize: LIMITS.mermaid,
                suppressErrorRendering: true,
                // A diagram's own %%{init}%% may restyle it but never loosen these.
                secure: ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'suppressErrorRendering',
                    'maxEdges', 'htmlLabels', 'flowchart', 'fontFamily'],
            });
            mermaidTheme = theme;
        }

        counter += 1;
        return window.mermaid.render('ru-diagram-' + counter, request.source).then(function (result) {
            var svg = parseSvg(result.svg);
            var size = viewBoxSize(svg);
            if (!size) throw fail('render_svg_invalid');
            return rasterize(sized(svg, size.width, size.height), size.width, size.height);
        });
    }

    function renderMath(request) {
        var fontSize = Math.min(Math.max(number(request.fontSize) || 14, 8), 48);
        var node = window.MathJax.tex2svg(request.source, { display: !!request.display });
        var svg = node.querySelector('svg');
        if (!svg) throw fail('render_math_invalid');
        if (svg.querySelector('[data-mjx-error], merror')) throw fail('render_math_invalid');
        var size = viewBoxSize(svg);
        if (!size) throw fail('render_math_invalid');
        // The view box counts thousandths of an em; vertical-align counts ex (half an em here).
        var width = size.width / 1000 * fontSize;
        var height = size.height / 1000 * fontSize;
        var align = /vertical-align:\s*(-?[\d.]+)ex/.exec(svg.getAttribute('style') || '');
        var depth = align ? -parseFloat(align[1]) * 0.5 * fontSize : 0;
        svg.setAttribute('style', 'color:' + (/^#[0-9a-fA-F]{6}$/.test(request.ink) ? request.ink : '#000000'));
        return rasterize(sized(svg, width, height), width, height).then(function (result) {
            result.depth = depth;
            return result;
        });
    }

    // A workspace SVG is drawn as a picture only: scripts, embedded HTML, event
    // handlers and every reference that leaves the file are removed before it is
    // rasterized, so nothing in it can run or ask for a resource.
    function inert(svg) {
        var drop = svg.querySelectorAll('script, foreignObject, iframe, object, embed, audio, video');
        for (var i = 0; i < drop.length; i++) drop[i].parentNode.removeChild(drop[i]);
        var all = [svg].concat(Array.prototype.slice.call(svg.getElementsByTagName('*')));
        all.forEach(function (element) {
            Array.prototype.slice.call(element.attributes).forEach(function (attribute) {
                var name = attribute.name.toLowerCase();
                var value = (attribute.value || '').trim();
                if (name.indexOf('on') === 0) element.removeAttribute(attribute.name);
                else if ((name === 'href' || name === 'xlink:href' || name === 'src')
                    && value.charAt(0) !== '#' && value.toLowerCase().indexOf('data:image/') !== 0) {
                    element.removeAttribute(attribute.name);
                } else if (/url\(\s*['"]?(?!#)/i.test(value)) {
                    element.removeAttribute(attribute.name);
                }
            });
        });
        var styles = svg.getElementsByTagName('style');
        for (var j = 0; j < styles.length; j++) {
            styles[j].textContent = styles[j].textContent.replace(/@import[^;]*;?/gi, '').replace(/url\(\s*['"]?(?!#)[^)]*\)/gi, 'none');
        }
        return svg;
    }

    function renderSvg(request) {
        var svg = inert(parseSvg(request.source));
        var box = viewBoxSize(svg);
        var width = number(svg.getAttribute('width')) || (box && box.width) || 512;
        var height = number(svg.getAttribute('height')) || (box && box.height) || 512;
        // Percent sizes mean nothing outside a page; the view box keeps the proportions.
        if (/%/.test(svg.getAttribute('width') || '') && box) width = box.width;
        if (/%/.test(svg.getAttribute('height') || '') && box) height = box.height;
        return rasterize(sized(svg, width, height), width, height);
    }

    function render(request) {
        return new Promise(function (resolve) {
            var limit = request && LIMITS[request.kind];
            if (!limit || typeof request.source !== 'string') throw fail('render_request_invalid');
            if (request.source.length > limit) throw fail('render_source_too_large');
            var job = request.kind === 'mermaid' ? renderMermaid(request)
                : request.kind === 'math' ? renderMath(request)
                    : renderSvg(request);
            resolve(job);
        }).then(function (result) {
            return { ok: true, png: result.png, width: result.width, height: result.height, depth: result.depth || 0 };
        }, function (error) {
            return { ok: false, error: error && error.code ? error.code : 'render_failed' };
        });
    }

    // A plain browser has no host bridge; tests call this directly.
    window.ruRender = render;

    var bridge = window.chrome && window.chrome.webview;
    if (bridge) {
        bridge.addEventListener('message', function (event) {
            var request = event.data || {};
            render(request).then(function (reply) {
                reply.type = 'result';
                reply.id = request.id;
                bridge.postMessage(reply);
            });
        });
        bridge.postMessage({ type: 'ready' });
    }
})();
