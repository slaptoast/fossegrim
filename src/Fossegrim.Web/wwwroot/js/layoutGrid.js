// A thin wrapper around GridStack (wwwroot/lib/gridstack) for ModuleGrid.razor.
// Blazor renders the .grid-stack-item elements with their saved gs-x/y/w/h;
// this turns them into a draggable, resizable grid and reports changes back.

const COLUMNS = 24;
// Below this width the grid becomes a single column. Layouts are only saved
// from the full 24-column grid, so a phone never overwrites the desktop layout.
const ONE_COLUMN_BELOW = 720;

export function create(element, dotnet, editing) {
    const grid = window.GridStack.init({
        column: COLUMNS,
        cellHeight: 40,
        margin: 4,
        float: false,
        animate: true,
        handle: ".layout-module-overlay",
        disableDrag: !editing,
        disableResize: !editing,
        resizable: { handles: "e,se,s,sw,w" },
        // columnMax matters: with breakpoints, GridStack caps the column count
        // at columnMax (default 12) whatever `column` says.
        columnOpts: { columnMax: COLUMNS, breakpoints: [{ w: ONE_COLUMN_BELOW, c: 1 }] },
    }, element);

    grid.on("change", () => {
        if (grid.getColumn() !== COLUMNS) {
            return;
        }
        const items = grid.save(false).map(n => ({ id: String(n.id), x: n.x, y: n.y, w: n.w, h: n.h }));
        dotnet.invokeMethodAsync("OnLayoutChanged", items);
    });

    return {
        setEditing(on) {
            grid.enableMove(on);
            grid.enableResize(on);
        },
        // False on narrow screens, where the grid is one column and can't be edited.
        isFullWidth() {
            return grid.getColumn() === COLUMNS;
        },
        destroy() {
            grid.offAll();
            grid.destroy(false); // keep the DOM; Blazor owns it
        },
    };
}
