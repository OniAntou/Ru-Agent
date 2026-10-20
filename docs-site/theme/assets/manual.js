/*
 * Procedures can be ticked off. Each numbered step in a procedure plate gets a button over its
 * numeral; the state is remembered per page in localStorage, and everything still reads correctly
 * when storage or script is unavailable.
 */
(function () {
  "use strict";

  var PREFIX = "ru-docs-step:";

  function read(key) {
    try {
      return window.localStorage.getItem(key) === "1";
    } catch (error) {
      return false;
    }
  }

  function write(key, done) {
    try {
      if (done) {
        window.localStorage.setItem(key, "1");
      } else {
        window.localStorage.removeItem(key);
      }
    } catch (error) {
      /* storage unavailable: the tick lasts until the page is left */
    }
  }

  function setup(plate, plateIndex) {
    var steps = plate.querySelectorAll(":scope > ol > li");
    steps.forEach(function (item, stepIndex) {
      var key = PREFIX + location.pathname + ":" + plateIndex + ":" + stepIndex;
      var button = document.createElement("button");
      button.type = "button";
      button.className = "step-toggle";
      button.setAttribute("aria-label", "Mark step " + (stepIndex + 1) + " as done");

      function apply(done) {
        item.dataset.done = done ? "true" : "false";
        button.setAttribute("aria-pressed", done ? "true" : "false");
      }

      apply(read(key));
      button.addEventListener("click", function () {
        var done = item.dataset.done !== "true";
        apply(done);
        write(key, done);
      });
      item.insertBefore(button, item.firstChild);
    });
  }

  function init() {
    document.querySelectorAll(".plate__body").forEach(setup);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
