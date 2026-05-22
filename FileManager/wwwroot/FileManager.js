// FileManager.js
// ES module for the FileManager Blazor component.
// Loaded via JS module import in FileManagerComponent.razor.
// Handles:
//   1. Browser file-save (download) from a byte array.
//   2. Splitter drag behaviour for the two-panel layout.

// ── Download ──────────────────────────────────────────────────────────────────

export function downloadFile(fileName, mimeType, data) {
    const blob = new Blob([data], { type: mimeType || 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    setTimeout(() => URL.revokeObjectURL(url), 10000);
}

// ── Splitter ──────────────────────────────────────────────────────────────────

export function initSplitter(containerId, leftPanelId, splitterId, dotNetRef) {
    const container = document.getElementById(containerId);
    const leftPanel = document.getElementById(leftPanelId);
    const splitter  = document.getElementById(splitterId);

    if (!container || !leftPanel || !splitter) return;

    let dragging   = false;
    let startX     = 0;
    let startWidth = 0;

    splitter.addEventListener('mousedown', function (e) {
        dragging   = true;
        startX     = e.clientX;
        startWidth = leftPanel.getBoundingClientRect().width;
        document.body.style.cursor     = 'col-resize';
        document.body.style.userSelect = 'none';
        e.preventDefault();
    });

    document.addEventListener('mousemove', function (e) {
        if (!dragging) return;
        const containerWidth = container.getBoundingClientRect().width;
        const delta    = e.clientX - startX;
        const newWidth = Math.min(
            Math.max(startWidth + delta, 120),
            containerWidth * 0.6
        );
        const pct = (newWidth / containerWidth) * 100;
        leftPanel.style.width = pct + '%';
        dotNetRef.invokeMethodAsync('OnSplitterMoved', pct);
    });

    document.addEventListener('mouseup', function () {
        if (dragging) {
            dragging = false;
            document.body.style.cursor     = '';
            document.body.style.userSelect = '';
        }
    });
}
