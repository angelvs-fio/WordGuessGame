import { state, STROKE_SEND_INTERVAL_MS, STROKE_MIN_DISTANCE } from "./state.js";
import { paintCanvas, paintColor, paintSize, paintTool, paintClearBtn } from "./dom.js";

export const ctx = paintCanvas ? paintCanvas.getContext("2d") : null;

let _connection = null;
let _getUser = null;

export function initCanvas(connection, getUser) {
    _connection = connection;
    _getUser = getUser;
    if (!paintCanvas || !ctx) return;

    paintCanvas.addEventListener("mousedown", beginDraw);
    paintCanvas.addEventListener("mousemove", draw);
    window.addEventListener("mouseup", endDraw);
    paintCanvas.addEventListener("touchstart", (e) => { e.preventDefault(); beginDraw(e.touches[0]); }, { passive: false });
    paintCanvas.addEventListener("touchmove", (e) => { e.preventDefault(); draw(e.touches[0]); }, { passive: false });
    paintCanvas.addEventListener("touchend", (e) => { e.preventDefault(); const t = e.changedTouches && e.changedTouches[0]; endDraw(t ? t : undefined); }, { passive: false });
    paintCanvas.addEventListener("touchcancel", (e) => { e.preventDefault(); const t = e.changedTouches && e.changedTouches[0]; endDraw(t ? t : undefined); }, { passive: false });

    paintClearBtn.addEventListener("click", async () => {
        if (!ctx) return;
        ctx.clearRect(0, 0, paintCanvas.width, paintCanvas.height);
        state.baseImage = null;
        try { await _connection.invoke("ClearCanvas", _getUser()); } catch (e) { console.error(e); }
    });
}

function getCanvasPos(ev) {
    const rect = paintCanvas.getBoundingClientRect();
    const clientX = ev.clientX ?? (ev.pageX - window.scrollX);
    const clientY = ev.clientY ?? (ev.pageY - window.scrollY);
    const x = (clientX - rect.left) * (paintCanvas.width / rect.width);
    const y = (clientY - rect.top) * (paintCanvas.height / rect.height);
    return { x, y };
}

function beginDraw(ev) {
    if (!ctx || !state.isPainter || state.isGameOver || !state.hasAnswer) return;
    state.drawing = true;
    const { x, y } = getCanvasPos(ev);
    state.lastX = x; state.lastY = y;
    state.startX = x; state.startY = y;
    state.baseImage = ctx.getImageData(0, 0, paintCanvas.width, paintCanvas.height);
    state.lastStrokeSentTs = performance.now();
}

async function draw(ev) {
    if (!ctx || !state.isPainter || !state.drawing || state.isGameOver || !state.hasAnswer) return;
    const { x, y } = getCanvasPos(ev);
    const color = paintColor.value || "#000";
    const size = Number(paintSize.value) || 4;
    const tool = paintTool ? paintTool.value : state.currentTool;
    if (tool === "freehand") {
        ctx.strokeStyle = color;
        ctx.lineWidth = size;
        ctx.lineCap = "round";
        ctx.beginPath();
        ctx.moveTo(state.lastX, state.lastY);
        ctx.lineTo(x, y);
        ctx.stroke();
        const now = performance.now();
        const dx = x - state.lastX;
        const dy = y - state.lastY;
        const dist2 = dx * dx + dy * dy;
        if (now - state.lastStrokeSentTs >= STROKE_SEND_INTERVAL_MS && dist2 >= STROKE_MIN_DISTANCE * STROKE_MIN_DISTANCE) {
            try {
                _connection.send("DrawStroke", _getUser(), state.lastX, state.lastY, x, y, color, size).catch(console.error);
                state.lastStrokeSentTs = now;
            } catch (e) { console.error(e); }
        }
        state.lastX = x; state.lastY = y;
    } else {
        if (state.baseImage) ctx.putImageData(state.baseImage, 0, 0);
        ctx.strokeStyle = color;
        ctx.lineWidth = size;
        if (tool === "line") {
            ctx.lineCap = "round";
            ctx.beginPath();
            ctx.moveTo(state.startX, state.startY);
            ctx.lineTo(x, y);
            ctx.stroke();
        } else if (tool === "rect") {
            ctx.strokeRect(state.startX, state.startY, x - state.startX, y - state.startY);
        } else if (tool === "circle") {
            const dx = x - state.startX;
            const dy = y - state.startY;
            const r = Math.sqrt(dx * dx + dy * dy);
            ctx.beginPath();
            ctx.arc(state.startX, state.startY, r, 0, Math.PI * 2);
            ctx.stroke();
        }
    }
}

async function endDraw(ev) {
    if (!ctx || !state.isPainter || !state.drawing) { state.drawing = false; state.baseImage = null; return; }
    if (state.isGameOver || !state.hasAnswer) { state.drawing = false; state.baseImage = null; return; }
    state.drawing = false;
    const pointEv = (ev && (ev.clientX !== undefined || ev.pageX !== undefined)) ? ev : { clientX: state.lastX, clientY: state.lastY };
    const { x, y } = getCanvasPos(pointEv);
    const color = paintColor.value || "#000";
    const size = Number(paintSize.value) || 4;
    const tool = paintTool ? paintTool.value : state.currentTool;
    if (tool === "line") {
        if (state.baseImage) ctx.putImageData(state.baseImage, 0, 0);
        ctx.strokeStyle = color;
        ctx.lineWidth = size;
        ctx.lineCap = "round";
        ctx.beginPath();
        ctx.moveTo(state.startX, state.startY);
        ctx.lineTo(x, y);
        ctx.stroke();
        try { await _connection.invoke("DrawShape", _getUser(), "line", { x1: state.startX, y1: state.startY, x2: x, y2: y, color, size }); } catch (e) { console.error(e); }
    } else if (tool === "rect") {
        if (state.baseImage) ctx.putImageData(state.baseImage, 0, 0);
        const w = x - state.startX;
        const h = y - state.startY;
        ctx.strokeStyle = color;
        ctx.lineWidth = size;
        ctx.strokeRect(state.startX, state.startY, w, h);
        try { await _connection.invoke("DrawShape", _getUser(), "rect", { x: state.startX, y: state.startY, w, h, color, size }); } catch (e) { console.error(e); }
    } else if (tool === "circle") {
        if (state.baseImage) ctx.putImageData(state.baseImage, 0, 0);
        const dx = x - state.startX;
        const dy = y - state.startY;
        const r = Math.sqrt(dx * dx + dy * dy);
        ctx.strokeStyle = color;
        ctx.lineWidth = size;
        ctx.beginPath();
        ctx.arc(state.startX, state.startY, r, 0, Math.PI * 2);
        ctx.stroke();
        try { await _connection.invoke("DrawShape", _getUser(), "circle", { cx: state.startX, cy: state.startY, r, color, size }); } catch (e) { console.error(e); }
    } else {
        try { _connection.send("DrawStroke", _getUser(), state.lastX, state.lastY, x, y, color, size).catch(console.error); } catch (e) { console.error(e); }
    }
    state.baseImage = null;
}

export function renderStroke(seg) {
    if (!ctx || !seg) return;
    ctx.strokeStyle = seg.color || "#000";
    ctx.lineWidth = Number(seg.size) || 4;
    ctx.lineCap = "round";
    ctx.beginPath();
    ctx.moveTo(seg.x1, seg.y1);
    ctx.lineTo(seg.x2, seg.y2);
    ctx.stroke();
}

export function renderShape(shape) {
    if (!ctx || !shape) return;
    const type = shape.type;
    const p = shape.payload || {};
    ctx.strokeStyle = p.color || "#000";
    ctx.lineWidth = Number(p.size) || 4;
    if (type === "line") {
        ctx.lineCap = "round";
        ctx.beginPath();
        ctx.moveTo(p.x1, p.y1);
        ctx.lineTo(p.x2, p.y2);
        ctx.stroke();
    } else if (type === "rect") {
        ctx.strokeRect(p.x, p.y, p.w, p.h);
    } else if (type === "circle") {
        ctx.beginPath();
        ctx.arc(p.cx, p.cy, p.r, 0, Math.PI * 2);
        ctx.stroke();
    }
}
