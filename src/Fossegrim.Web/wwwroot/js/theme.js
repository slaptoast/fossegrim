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
// brand gradient) are derived here.
(function () {
    var cacheKey = "fossegrim_appearance_cache";
    var legacyKey = "fossegrim_theme";
    var root = document.documentElement;
    var media = window.matchMedia("(prefers-color-scheme: dark)");
    var appliedProperties = [];
    var state = readCache();

    function readCache() {
        try {
            var cached = JSON.parse(localStorage.getItem(cacheKey) || "null");
            if (cached && typeof cached.choice === "string") {
                return cached;
            }
            // Before themes were saved to the profile, the choice lived here.
            var legacy = localStorage.getItem(legacyKey);
            if (legacy === "light" || legacy === "dark") {
                return { choice: legacy, base: legacy, colors: {} };
            }
        } catch (e) {
            // Storage blocked or corrupt: fall through to the default.
        }
        return { choice: "system", base: null, colors: {} };
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
    }

    // Follow OS changes live; only matters while the choice is "system".
    media.addEventListener("change", apply);
    apply();

    window.fossegrimTheme = {
        // { choice: "system" | "light" | "dark" | "custom:<id>", base: "light" | "dark" | null, colors: { "--token": "#rrggbb" } }
        apply: function (next) {
            state = {
                choice: next.choice || "system",
                base: next.base || null,
                colors: next.colors || {}
            };
            writeCache();
            apply();
        },
        // What the cache holds, so the app can show it before the profile loads.
        getCached: function () {
            return state;
        }
    };
})();
