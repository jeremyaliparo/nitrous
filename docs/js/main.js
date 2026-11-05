/* ==========================================================================
   Nitrous site — progressive enhancement only.
   Everything renders and reads correctly without JavaScript.
   ========================================================================== */

(function () {
  "use strict";

  var REPO = "jeremyaliparo/nitrous";
  var CACHE_KEY = "nitrous:repo:v1";
  var CACHE_TTL = 10 * 60 * 1000;

  var $ = function (selector, root) {
    return (root || document).querySelector(selector);
  };
  var $$ = function (selector, root) {
    return Array.prototype.slice.call((root || document).querySelectorAll(selector));
  };

  /* ------------------------------- Header -------------------------------- */

  var header = $("#site-header");
  var navToggle = $(".nav-toggle");
  var nav = $("#primary-nav");
  var navLinks = $$(".nav a");
  var ticking = false;

  function onScroll() {
    if (ticking) return;
    ticking = true;
    window.requestAnimationFrame(function () {
      header.classList.toggle("is-scrolled", window.scrollY > 8);
      if (window.scrollY < 240) {
        navLinks.forEach(function (link) {
          link.classList.remove("is-active");
        });
      }
      ticking = false;
    });
  }

  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  function setNav(open) {
    document.body.classList.toggle("nav-open", open);
    navToggle.setAttribute("aria-expanded", String(open));
    navToggle.setAttribute("aria-label", open ? "Close menu" : "Open menu");
  }

  if (navToggle) {
    navToggle.addEventListener("click", function () {
      setNav(!document.body.classList.contains("nav-open"));
    });
  }

  if (nav) {
    $$("a", nav).forEach(function (link) {
      link.addEventListener("click", function () {
        setNav(false);
      });
    });
  }

  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape" && document.body.classList.contains("nav-open")) {
      setNav(false);
      navToggle.focus();
    }
  });

  document.addEventListener("click", function (event) {
    if (!document.body.classList.contains("nav-open")) return;
    if (header.contains(event.target)) return;
    setNav(false);
  });

  window.addEventListener("resize", function () {
    if (window.innerWidth > 900) setNav(false);
  });

  /* ---------------------------- Active section --------------------------- */

  var watched = navLinks
    .map(function (link) {
      var id = link.getAttribute("href");
      var section = id && id.length > 1 ? $(id) : null;
      return section ? { link: link, section: section } : null;
    })
    .filter(Boolean);

  if ("IntersectionObserver" in window && watched.length) {
    var navObserver = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (!entry.isIntersecting) return;
          navLinks.forEach(function (link) {
            link.classList.remove("is-active");
          });
          var match = watched.filter(function (item) {
            return item.section === entry.target;
          })[0];
          if (match) match.link.classList.add("is-active");
        });
      },
      { rootMargin: "-42% 0px -52% 0px", threshold: 0 }
    );

    watched.forEach(function (item) {
      navObserver.observe(item.section);
    });
  }

  /* ------------------------------- Reveal -------------------------------- */

  var revealItems = $$(".reveal");
  var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  if (!("IntersectionObserver" in window) || reduceMotion) {
    revealItems.forEach(function (item) {
      item.classList.add("is-visible");
    });
  } else {
    var revealObserver = new IntersectionObserver(
      function (entries, observer) {
        entries.forEach(function (entry) {
          if (!entry.isIntersecting) return;
          entry.target.classList.add("is-visible");
          observer.unobserve(entry.target);
        });
      },
      { threshold: 0.1, rootMargin: "0px 0px -60px 0px" }
    );

    revealItems.forEach(function (item) {
      revealObserver.observe(item);
    });
  }

  /* ------------------------------- Tabs ---------------------------------- */

  var tablist = $(".screen-tabs");
  if (tablist) {
    var tabs = $$("[role='tab']", tablist);
    var panels = tabs.map(function (tab) {
      return document.getElementById(tab.getAttribute("aria-controls"));
    });

    function selectTab(index, focus) {
      tabs.forEach(function (tab, i) {
        var selected = i === index;
        tab.classList.toggle("is-active", selected);
        tab.setAttribute("aria-selected", String(selected));
        tab.tabIndex = selected ? 0 : -1;
        if (panels[i]) panels[i].hidden = !selected;
      });
      if (focus) tabs[index].focus();
    }

    tabs.forEach(function (tab, index) {
      tab.addEventListener("click", function () {
        selectTab(index, false);
      });

      tab.addEventListener("keydown", function (event) {
        var next = null;
        if (event.key === "ArrowDown" || event.key === "ArrowRight") {
          next = (index + 1) % tabs.length;
        } else if (event.key === "ArrowUp" || event.key === "ArrowLeft") {
          next = (index - 1 + tabs.length) % tabs.length;
        } else if (event.key === "Home") {
          next = 0;
        } else if (event.key === "End") {
          next = tabs.length - 1;
        }

        if (next !== null) {
          event.preventDefault();
          selectTab(next, true);
        }
      });
    });
  }

  /* ------------------------------- Copy ---------------------------------- */

  var toast = $(".toast");
  var toastTimer = null;

  function showToast(message) {
    if (!toast) return;
    toast.textContent = message;
    toast.classList.add("is-visible");
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(function () {
      toast.classList.remove("is-visible");
    }, 2200);
  }

  function legacyCopy(text) {
    var area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.top = "-1000px";
    area.style.opacity = "0";
    document.body.appendChild(area);
    area.select();
    area.setSelectionRange(0, area.value.length);
    var ok = false;
    try {
      ok = document.execCommand("copy");
    } catch (error) {
      ok = false;
    }
    document.body.removeChild(area);
    return ok;
  }

  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text).catch(function () {
        if (!legacyCopy(text)) throw new Error("copy failed");
      });
    }
    if (legacyCopy(text)) return Promise.resolve();
    return Promise.reject(new Error("copy failed"));
  }

  $$(".copy-btn").forEach(function (button) {
    var label = $("span", button);
    var original = label ? label.textContent : "Copy";

    button.addEventListener("click", function () {
      var text = button.getAttribute("data-copy") || "";
      copyText(text).then(
        function () {
          button.classList.add("is-copied");
          if (label) label.textContent = "Copied";
          showToast("Checksum command copied to clipboard");
          window.setTimeout(function () {
            button.classList.remove("is-copied");
            if (label) label.textContent = original;
          }, 2000);
        },
        function () {
          showToast("Copy failed — select the command manually");
        }
      );
    });
  });

  /* ------------------------- Feature pointer glow ------------------------ */

  if (window.matchMedia("(hover: hover) and (pointer: fine)").matches) {
    $$(".feature").forEach(function (card) {
      card.addEventListener("pointermove", function (event) {
        var rect = card.getBoundingClientRect();
        var x = ((event.clientX - rect.left) / rect.width) * 100;
        card.style.setProperty("--mx", x + "%");
      });
    });
  }

  /* --------------------------- Live repo stats --------------------------- */

  var els = {
    version: $$("[data-gh-version]"),
    stars: $$("[data-gh-stars]"),
    size: $$("[data-gh-size]"),
    date: $$("[data-gh-date]")
  };

  function setAll(nodes, value) {
    if (!value) return;
    nodes.forEach(function (node) {
      node.textContent = value;
    });
  }

  function formatSize(bytes) {
    if (!bytes || typeof bytes !== "number") return null;
    return (bytes / 1048576).toFixed(1) + " MB";
  }

  function formatDate(value) {
    if (!value) return null;
    var date = new Date(value);
    if (isNaN(date.getTime())) return null;
    return new Intl.DateTimeFormat("en-US", {
      month: "short",
      day: "numeric",
      year: "numeric"
    }).format(date);
  }

  function applyStats(stats) {
    setAll(els.version, stats.version);
    setAll(els.stars, stats.stars ? stats.stars.toLocaleString("en-US") : null);
    setAll(els.size, stats.size);
    setAll(els.date, stats.date);
  }

  function readCache() {
    try {
      var raw = window.sessionStorage.getItem(CACHE_KEY);
      if (!raw) return null;
      var cached = JSON.parse(raw);
      if (!cached || !cached.at || Date.now() - cached.at > CACHE_TTL) return null;
      return cached.stats;
    } catch (error) {
      return null;
    }
  }

  function writeCache(stats) {
    try {
      window.sessionStorage.setItem(CACHE_KEY, JSON.stringify({ at: Date.now(), stats: stats }));
    } catch (error) {
      /* storage may be unavailable — not important */
    }
  }

  function loadStats() {
    if (!window.fetch) return;
    var headers = { Accept: "application/vnd.github+json" };

    Promise.all([
      fetch("https://api.github.com/repos/" + REPO + "/releases/latest", { headers: headers })
        .then(function (response) {
          return response.ok ? response.json() : null;
        })
        .catch(function () {
          return null;
        }),
      fetch("https://api.github.com/repos/" + REPO, { headers: headers })
        .then(function (response) {
          return response.ok ? response.json() : null;
        })
        .catch(function () {
          return null;
        })
    ]).then(function (results) {
      var release = results[0];
      var repo = results[1];
      var stats = {
        version: release && release.tag_name ? String(release.tag_name).replace(/^v/, "") : null,
        stars: repo && typeof repo.stargazers_count === "number" ? repo.stargazers_count : null,
        size: release && release.assets && release.assets[0] ? formatSize(release.assets[0].size) : null,
        date: release && release.published_at ? formatDate(release.published_at) : null
      };

      var hasAny = stats.version || stats.stars !== null;
      if (!hasAny) return;

      applyStats(stats);
      writeCache(stats);
    });
  }

  var cachedStats = readCache();
  if (cachedStats) {
    applyStats(cachedStats);
  } else if (window.fetch) {
    if ("requestIdleCallback" in window) {
      window.requestIdleCallback(loadStats, { timeout: 2500 });
    } else {
      window.addEventListener("load", loadStats, { once: true });
    }
  }

  /* -------------------------------- Year --------------------------------- */

  var year = $("#year");
  if (year) year.textContent = String(new Date().getFullYear());
})();
