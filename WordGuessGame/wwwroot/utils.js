export function formatThousands(numStr) {
    const match = String(numStr).match(/^(-?)(\d+)(\..*)?$/);
    if (!match) return numStr;
    const [, sign, intPart, dec = ""] = match;
    return sign + intPart.replace(/\B(?=(\d{3})+(?!\d))/g, "\u00A0") + dec;
}

export function formatThousandsInput(str) {
    const match = str.match(/^(-?)(\d*)(\.?\d*)$/);
    if (!match) return str;
    const [, sign, intPart, dec] = match;
    return sign + intPart.replace(/\B(?=(\d{3})+(?!\d))/g, " ") + dec;
}

export function applyThousandsFormatting(input) {
    input.addEventListener("input", () => {
        const cursorPos = input.selectionStart;
        const raw = input.value;
        const rawCharsBeforeCursor = raw.slice(0, cursorPos).replace(/ /g, "").length;
        const formatted = formatThousandsInput(raw.replace(/ /g, ""));
        if (formatted === raw) return;
        input.value = formatted;
        let count = 0;
        let newPos = 0;
        for (let i = 0; i < formatted.length; i++) {
            if (formatted[i] !== " ") count++;
            if (count === rawCharsBeforeCursor) { newPos = i + 1; break; }
        }
        input.setSelectionRange(newPos, newPos);
    });
}

export function escapeHtml(str) {
    return String(str)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
}
