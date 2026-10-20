// Loaded synchronously in <head> so the theme is applied before first paint.
// data-theme on <html> always holds the resolved theme ("light" or "dark"): the
// user's saved choice, or the OS preference when they haven't chosen. ThemeService
// reads and writes the choice through window.fossegrimTheme.
(function () {
    var storageKey = "fossegrim_theme";
    var media = window.matchMedia("(prefers-color-scheme: dark)");

    function savedChoice() {
        try {
            var value = localStorage.getItem(storageKey);
            return value === "light" || value === "dark" ? value : null;
        } catch (e) {
            return null;
        }
    }

    function apply() {
        var theme = savedChoice() || (media.matches ? "dark" : "light");
        document.documentElement.setAttribute("data-theme", theme);
    }

    // Follow OS changes live; apply() ignores them while a choice is saved.
    media.addEventListener("change", apply);
    apply();

    window.fossegrimTheme = {
        // "light", "dark" or "system"
        getChoice: function () {
            return savedChoice() || "system";
        },
        setChoice: function (choice) {
            try {
                if (choice === "light" || choice === "dark") {
                    localStorage.setItem(storageKey, choice);
                } else {
                    localStorage.removeItem(storageKey);
                }
            } catch (e) {
                // Storage blocked: still apply for this page load.
            }
            apply();
        }
    };
})();
