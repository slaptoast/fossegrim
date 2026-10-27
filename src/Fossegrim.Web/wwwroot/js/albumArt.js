// Helpers for AlbumArtEditor.razor.

const JPEG_QUALITY = 0.9;

/**
 * Reads the image chosen in a file input, shrunk to at most `maxSide` pixels
 * on its longest side (re-encoded as JPEG when it had to shrink), and clears
 * the input so the same file can be chosen again. Returns the bytes, or null
 * when nothing was chosen. Throws with a readable message for anything that
 * isn't a JPEG or PNG image.
 */
export async function prepareImage(input, maxSide) {
    const file = input.files?.[0];
    input.value = "";
    if (!file) {
        return null;
    }
    if (file.type !== "image/jpeg" && file.type !== "image/png") {
        throw new Error("Choose a JPEG or PNG image.");
    }

    let bitmap;
    try {
        bitmap = await createImageBitmap(file);
    } catch {
        throw new Error("That file couldn't be read as an image.");
    }

    try {
        const scale = Math.min(1, maxSide / Math.max(bitmap.width, bitmap.height));
        if (scale === 1) {
            return new Uint8Array(await file.arrayBuffer());
        }

        const canvas = document.createElement("canvas");
        canvas.width = Math.round(bitmap.width * scale);
        canvas.height = Math.round(bitmap.height * scale);
        const context = canvas.getContext("2d");
        // JPEG has no transparency; give see-through PNGs a white background rather than black.
        context.fillStyle = "#ffffff";
        context.fillRect(0, 0, canvas.width, canvas.height);
        context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        const blob = await new Promise(resolve => canvas.toBlob(resolve, "image/jpeg", JPEG_QUALITY));
        if (!blob) {
            throw new Error("The image couldn't be resized.");
        }
        return new Uint8Array(await blob.arrayBuffer());
    } finally {
        bitmap.close();
    }
}

/** Opens the hidden file input's picker. */
export function choose(input) {
    input.click();
}

/** Scrolls the editor into view when it opens off screen. */
export function reveal(element) {
    element?.scrollIntoView({ block: "nearest", behavior: "smooth" });
}
