// Saves text as a file download (used for exporting settings and themes).
export function downloadText(fileName, text, contentType) {
    const blob = new Blob([text], { type: contentType || "application/json" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
}

// Saves a streamed .NET response as a file download (used for backups).
export async function downloadStream(fileName, streamRef, contentType) {
    const buffer = await streamRef.arrayBuffer();
    const blob = new Blob([buffer], { type: contentType || "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
}
