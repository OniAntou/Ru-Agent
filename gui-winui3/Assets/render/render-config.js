// MathJax reads this before it loads. Every extension it could fetch is removed:
// the page has no network, and a TeX command never decides what code runs.
window.MathJax = {
    loader: { load: [] },
    startup: { typeset: false },
    tex: { packages: { '[-]': ['require', 'autoload'] } },
    svg: { fontCache: 'local' },
    options: { enableMenu: false },
};
