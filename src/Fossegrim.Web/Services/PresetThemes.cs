namespace Fossegrim.Web.Services;

/// <summary>
/// Themes that ship with the app: picked like Light and Dark, and built like
/// a custom theme (colors plus CSS). They can't be edited, but the Appearance
/// page can make an editable copy. Their CSS follows the same rules as a
/// user's: it can only load images and fonts from this server.
/// </summary>
public static class PresetThemes
{
    public const string ChoicePrefix = "builtin:";

    public static readonly IReadOnlyList<CustomTheme> All = [RetroAmp()];

    public static string ChoiceFor(CustomTheme theme) => ChoicePrefix + theme.Id;

    public static CustomTheme? Find(string? choice) =>
        choice is not null && choice.StartsWith(ChoicePrefix, StringComparison.Ordinal)
            ? All.FirstOrDefault(t => ChoiceFor(t) == choice)
            : null;

    // A late-90s desktop media player: slate-blue bevelled frames, black
    // displays with green text, gold slider accents, grey 3D buttons.
    private static CustomTheme RetroAmp() => new()
    {
        Id = "retro-amp",
        Name = "Retro Amp",
        Base = ThemeChoices.Dark,
        Colors = new()
        {
            ["--brand-violet"] = "#c9a53a",
            ["--brand-indigo"] = "#5d6cc8",
            ["--accent-fg"] = "#7cff7c",
            ["--canvas"] = "#1b1b29",
            ["--surface"] = "#000000",
            ["--surface-muted"] = "#2b2b44",
            ["--ink"] = "#00e000",
            ["--text-secondary"] = "#00c000",
            ["--text-muted"] = "#4f9f4f",
            ["--border"] = "#1a1a2a",
            ["--border-strong"] = "#5c5c80",
            ["--chrome-bg"] = "#2b2b44",
            ["--chrome-fg"] = "#d6d6e6",
            ["--chrome-muted"] = "#9a9abb",
            ["--selection-bg"] = "#0000c6",
            ["--selection-fg"] = "#ffffff",
            ["--danger"] = "#d03030",
        },
        Css = """
            /* Retro Amp: square corners, small system fonts, bevelled frames. */
            :root { --radius-sm: 0; --radius-md: 0; --radius-lg: 0; --font-sans: Tahoma, Verdana, "Segoe UI", Arial, sans-serif; --on-brand: #10101a; }
            * { scrollbar-color: #6c6c90 #15151f; }
            body { font-size: 0.8125rem; }

            /* Window frames: light top-left edge, dark bottom-right. */
            .artist-list-component, .album-list-component, .track-list-component, .container,
            .visualizer-module, .player-panel-module, .search-results-column, .search-results-tracks {
                border: 2px solid; border-color: #74749a #101018 #101018 #74749a;
                box-shadow: inset 0 0 0 1px #2b2b44;
            }

            /* Title bars: small spaced capitals between gold stripes. */
            .header, .artist-list-header, .album-list-header, .track-list-component .controls,
            .up-next-module-header, .visualizer-header, .up-next-header {
                background: #2b2b44; border-bottom: 1px solid #101018;
            }
            .header h1, .artist-list-header h2, .album-list-header h2, .track-list-component .controls h2,
            .up-next-module-header h2, .visualizer-header h2 {
                color: #d6d6e6; font-size: 0.6875rem; font-weight: 700; letter-spacing: 0.18em; text-transform: uppercase;
                display: flex; align-items: center; gap: 6px;
            }
            .header h1::before, .header h1::after, .artist-list-header h2::before, .artist-list-header h2::after,
            .album-list-header h2::before, .album-list-header h2::after, .track-list-component .controls h2::before,
            .track-list-component .controls h2::after, .up-next-module-header h2::before, .up-next-module-header h2::after,
            .visualizer-header h2::before, .visualizer-header h2::after {
                content: ""; width: 14px; height: 7px; flex-shrink: 0;
                background: repeating-linear-gradient(to bottom, #c9a53a 0 1px, transparent 1px 3px);
            }
            .header h1::after, .artist-list-header h2::after, .album-list-header h2::after,
            .track-list-component .controls h2::after, .up-next-module-header h2::after, .visualizer-header h2::after { width: 36px; }
            .header p { color: #9a9abb; }

            /* Column headers: pale lavender, dark text. */
            thead, th { background: #bcc0da; color: #10101a; }
            th { border-bottom: 1px solid #5c5c80; letter-spacing: 0.04em; }
            th:hover { color: #000000; }
            .title-cell { font-weight: normal; }
            .artist-cell, .album-cell { color: var(--ink); }
            tbody tr { border-bottom-color: transparent; }
            tbody tr:hover, .artist-item:hover, .album-item:hover { background: #101030; }
            tbody tr.selected { box-shadow: none; }

            /* Grey 3D buttons that press in. */
            .button, .button-secondary, .button-danger, .player-button {
                background: #bdbdcf; color: #10101a; font-weight: normal;
                border: 2px solid; border-color: #f2f2ff #50506a #50506a #f2f2ff;
            }
            .button:hover, .button-secondary:hover, .player-button:hover:not(:disabled), .player-button.active { background: #d0d0e0; }
            .button:active, .button-secondary:active, .button-danger:active, .player-button:active:not(:disabled) { border-color: #50506a #f2f2ff #f2f2ff #50506a; }
            .button-danger { color: #8a0000; }
            .button-secondary[aria-pressed="true"], .theme-switch button.active { background: #9a9ab8; color: #000000; }

            /* Fields: black wells with green text. */
            input[type="text"], input[type="search"], input[type="email"], input[type="password"], input[type="tel"],
            input[type="number"], select, textarea {
                background: #000000; color: #00e000; border: 2px solid; border-color: #101018 #74749a #74749a #101018;
            }

            /* Top bar and player: the main window. */
            .topbar, .player-bar { border-bottom: 2px solid #101018; box-shadow: inset 0 1px 0 #74749a; }
            .player-bar { border-top: 2px solid #74749a; }
            .player-track-info, .player-panel .player-track-info {
                background: #000000; padding: 2px 8px; border: 2px solid; border-color: #101018 #74749a #74749a #101018;
            }
            .player-title { color: #00e000; font-weight: normal; }
            .player-artist { color: #00a000; }
            .transport-time {
                color: #00e000; background: #000000; padding: 1px 4px; font-family: "Courier New", monospace; font-size: 0.875rem;
                border: 1px solid; border-color: #101018 #74749a #74749a #101018;
            }
            .transport input[type="range"], input[type="range"] { accent-color: #c9a53a; }
            .player-panel-module { background: #2b2b44; }
            .album-art { background: #101018; }
            """,
    };
}
