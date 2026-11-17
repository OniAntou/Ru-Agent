/*
 * The cover's behaviour: the rebuilt Ru window (ru-app.js) as a still in the hero and as a playing run in
 * the replay, whose phases drive the Ask, Watch and Done beats; the headline's word-by-word entrance; the
 * one-time reveal of sections on scroll; the bar's scrolled state; and the install copy button. With
 * reduced motion everything is shown at rest.
 */
(function () {
  "use strict";

  var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

  // The hero holds a still of the finished run.
  function initHero() {
    var host = document.querySelector('[data-ru-app="final"]');
    if (host && window.RuApp) window.RuApp.mount(host, { mode: "final" });
  }

  // The replay plays the run while it is in view; the beats follow its phases and can jump to them.
  function initReplay() {
    var replay = document.querySelector(".replay");
    var host = replay && replay.querySelector('[data-ru-app="play"]');
    if (!host || !window.RuApp) return;
    if (reduceMotion.matches || !("IntersectionObserver" in window)) {
      window.RuApp.mount(host, { mode: "final" });
      return;
    }
    var bar = replay.querySelector(".replay__progress span");
    var beats = Array.prototype.slice.call(replay.querySelectorAll(".beat"));
    replay.classList.add("is-live");
    var player = window.RuApp.mount(host, {
      mode: "play",
      onPhase: function (phase, within, overall) {
        if (bar) bar.style.width = (overall * 100).toFixed(2) + "%";
        beats.forEach(function (beat) {
          var p = Number(beat.dataset.phase);
          beat.classList.toggle("is-on", p === phase);
          beat.style.setProperty("--fill", p < phase ? "1" : p === phase ? within.toFixed(3) : "0");
        });
      }
    });

    new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) player.play();
          else player.pause();
        });
      },
      { threshold: 0.35 }
    ).observe(host);

    beats.forEach(function (beat) {
      beat.tabIndex = 0;
      beat.setAttribute("role", "button");
      beat.setAttribute("aria-label", "Show: " + beat.querySelector("h3").textContent);
      function go() {
        player.seek(Number(beat.dataset.phase));
        player.play();
      }
      beat.addEventListener("click", go);
      beat.addEventListener("keydown", function (event) {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          go();
        }
      });
    });
  }

  // Sections and their parts rise into place once as they enter the view.
  function initReveal() {
    if (reduceMotion.matches || !("IntersectionObserver" in window)) return;
    var targets = document.querySelectorAll(
      ".replay__stage, .replay__steps, .band-title, .native__list > div, .modes__lede, .modes__dial li, .limits, " +
      ".terminal__copy, .window--term, .install__card, .install__next, .bench__head, .bench__chart, .bench__idle, .closer__inner"
    );
    document.documentElement.classList.add("has-reveal");
    var io = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (!entry.isIntersecting) return;
          var el = entry.target;
          var siblings = el.parentElement ? Array.prototype.indexOf.call(el.parentElement.children, el) : 0;
          el.style.transitionDelay = Math.min(siblings, 5) * 70 + "ms";
          el.classList.add("is-in");
          io.unobserve(el);
        });
      },
      { rootMargin: "0px 0px -12% 0px" }
    );
    targets.forEach(function (el) {
      el.classList.add("reveal");
      io.observe(el);
    });
  }

  // The headline arrives word by word on load.
  function initHeadline() {
    var title = document.querySelector(".hero__title");
    if (!title || reduceMotion.matches) return;
    var index = 0;
    function wrap(node) {
      Array.prototype.slice.call(node.childNodes).forEach(function (child) {
        if (child.nodeType === 3) {
          var frag = document.createDocumentFragment();
          child.textContent.split(/(\s+)/).forEach(function (part) {
            if (!part) return;
            if (/^\s+$/.test(part)) {
              frag.appendChild(document.createTextNode(part));
            } else {
              var span = document.createElement("span");
              span.className = "word";
              span.style.setProperty("--i", index++);
              span.textContent = part;
              frag.appendChild(span);
            }
          });
          child.parentNode.replaceChild(frag, child);
        } else if (child.nodeType === 1) {
          wrap(child);
        }
      });
    }
    wrap(title);
    title.classList.add("is-split");
  }

  // The bar gains a hairline once the page has scrolled.
  function initBar() {
    var bar = document.querySelector(".desk-bar");
    if (!bar) return;
    function sync() {
      bar.classList.toggle("is-scrolled", window.scrollY > 8);
    }
    window.addEventListener("scroll", sync, { passive: true });
    sync();
  }

  function initCopy() {
    document.querySelectorAll(".copy[data-copy]").forEach(function (button) {
      var source = document.querySelector(button.dataset.copy);
      if (!source || !navigator.clipboard) {
        button.hidden = true;
        return;
      }
      var label = button.textContent;
      button.addEventListener("click", function () {
        navigator.clipboard.writeText(source.textContent).then(
          function () {
            button.textContent = "Copied";
            button.classList.add("is-done");
            window.setTimeout(function () {
              button.textContent = label;
              button.classList.remove("is-done");
            }, 1800);
          },
          function () {
            button.textContent = "Select and copy the command";
          }
        );
      });
    });
  }

  function init() {
    initHeadline();
    initHero();
    initReplay();
    initReveal();
    initBar();
    initCopy();
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
