// Loaded synchronously in <head> so the theme is applied before first paint.
//
// The theme itself is saved to the user's profile on the server (see
// AppearanceService). This script keeps a copy in localStorage purely as a
// cache, so the right colors show while the app is still loading; the
// server's copy replaces it once the user is signed in.
//
// data-theme on <html> always holds the resolved base theme ("light" or
// "dark"): for "system" that's the OS preference, followed live. A custom
// theme sets its colors as inline custom properties on <html>, which override
// the stylesheet's tokens; shades that follow from them (hover colors, the
// brand gradient) are derived here. Its custom CSS, if any, goes in a <style>
// element after the app's own stylesheets.
//
// Safe mode: custom CSS can hide everything, including the way to undo it, so
// opening any page with ?safe-mode leaves it off until the tab is closed (or
// it's turned back on from the banner).
(function () {
    var cacheKey = "fossegrim_appearance_cache";
    var legacyKey = "fossegrim_theme";
    var safeModeKey = "fossegrim_safe_mode";
    var root = document.documentElement;
    var media = window.matchMedia("(prefers-color-scheme: dark)");
    var appliedProperties = [];
    var state = readCache();
    var safeMode = startSafeMode();
    var styleElement = null;

    function startSafeMode() {
        try {
            if (new URLSearchParams(window.location.search).has("safe-mode")) {
                sessionStorage.setItem(safeModeKey, "1");
            }
            return sessionStorage.getItem(safeModeKey) === "1";
        } catch (e) {
            return new URLSearchParams(window.location.search).has("safe-mode");
        }
    }

    // Text only, so the CSS can't add markup to the page.
    function applyCss() {
        var css = safeMode ? "" : (state.css || "");
        if (!css) {
            if (styleElement) {
                styleElement.remove();
                styleElement = null;
            }
            return;
        }
        if (!styleElement) {
            styleElement = document.createElement("style");
            styleElement.id = "fossegrim-theme-css";
        }
        styleElement.textContent = css;
        // Last in <head>, so it wins over the app's styles at equal specificity.
        document.head.appendChild(styleElement);
    }

    function readCache() {
        try {
            var cached = JSON.parse(localStorage.getItem(cacheKey) || "null");
            if (cached && typeof cached.choice === "string") {
                return cached;
            }
            // Before themes were saved to the profile, the choice lived here.
            var legacy = localStorage.getItem(legacyKey);
            if (legacy === "light" || legacy === "dark") {
                return { choice: legacy, base: legacy, colors: {}, css: "" };
            }
        } catch (e) {
            // Storage blocked or corrupt: fall through to the default.
        }
        return { choice: "system", base: null, colors: {}, css: "" };
    }

    function writeCache() {
        try {
            localStorage.setItem(cacheKey, JSON.stringify(state));
            localStorage.removeItem(legacyKey);
        } catch (e) {
            // Storage blocked: the theme still applies for this page load.
        }
    }

    function derived(colors, base) {
        var extra = {};
        var shade = base === "dark" ? "white" : "black";
        if (colors["--brand-violet"]) {
            extra["--brand-violet-hover"] = "color-mix(in srgb, " + colors["--brand-violet"] + " 85%, black)";
        }
        if (colors["--accent-fg"]) {
            extra["--accent-fg-hover"] = "color-mix(in srgb, " + colors["--accent-fg"] + " 80%, " + shade + ")";
        }
        if (colors["--brand-indigo"] || colors["--brand-violet"]) {
            extra["--gradient-brand"] = "linear-gradient(135deg, var(--brand-indigo) 0%, var(--brand-violet) 100%)";
        }
        if (colors["--danger"]) {
            extra["--danger-fg"] = base === "dark"
                ? "color-mix(in srgb, " + colors["--danger"] + " 55%, white)"
                : colors["--danger"];
        }
        return extra;
    }

    function apply() {
        var base = state.choice === "system" || !state.base
            ? (media.matches ? "dark" : "light")
            : state.base;
        root.setAttribute("data-theme", base);

        appliedProperties.forEach(function (name) { root.style.removeProperty(name); });
        appliedProperties = [];

        var colors = state.colors || {};
        var all = Object.assign({}, colors, derived(colors, base));
        Object.keys(all).forEach(function (name) {
            // Names and values are checked by the app before they get here;
            // this is a last guard so only custom properties are ever set.
            if (name.indexOf("--") === 0) {
                root.style.setProperty(name, all[name]);
                appliedProperties.push(name);
            }
        });

        applyCss();
    }

    // Follow OS changes live; only matters while the choice is "system".
    media.addEventListener("change", apply);
    apply();

    window.fossegrimTheme = {
        // { choice: "system" | "light" | "dark" | "custom:<id>", base: "light" | "dark" | null, colors: { "--token": "#rrggbb" }, css: "" }
        apply: function (next) {
            state = {
                choice: next.choice || "system",
                base: next.base || null,
                colors: next.colors || {},
                css: next.css || ""
            };
            writeCache();
            apply();
        },
        // Whether custom CSS is being left off for this tab (see above).
        isSafeMode: function () {
            return safeMode;
        },
        // Whether the theme in use has custom CSS (applied or not).
        hasCustomCss: function () {
            return !!state.css;
        },
        leaveSafeMode: function () {
            safeMode = false;
            try {
                sessionStorage.removeItem(safeModeKey);
            } catch (e) {
                // Storage blocked: safe mode only lasted this page anyway.
            }
            applyCss();
        },
        // What the cache holds, so the app can show it before the profile loads.
        getCached: function () {
            return state;
        }
    };
})();
