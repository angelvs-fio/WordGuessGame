(() => {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("hub/guess")
        .withAutomaticReconnect()
        .build();

    // Elements
    const userNameRow = document.getElementById("userNameRow");
    const userNameInput = document.getElementById("userName");
    const painterBtn = document.getElementById("painterBtn");
    const painterSection = document.getElementById("painterSection");
    const guessSection = document.getElementById("guessSection");

    const secretInput = document.getElementById("secretInput");
    const setSecretBtn = document.getElementById("setSecretBtn");
    const guessInput = document.getElementById("guessInput");
    const guessBtn = document.getElementById("guessBtn");

    const resetSection = document.getElementById("resetSection");
    resetSection.style.display = "none";
    const resetWithResultsBtn = document.getElementById("resetWithResultsBtn");
    const resetKeepResultsBtn = document.getElementById("resetKeepResultsBtn");

    const statusText = document.getElementById("statusText");
    const historyList = document.getElementById("historyList");
    const resultsBody = document.getElementById("resultsBody");

    // Paint control
    const paintSection = document.getElementById("paintSection");
    const paintCanvas = document.getElementById("paintCanvas");
    const paintControls = document.getElementById("paintControls");
    const paintTool = document.getElementById("paintTool");
    const paintColor = document.getElementById("paintColor");
    const paintSize = document.getElementById("paintSize");
    const paintClearBtn = document.getElementById("paintClearBtn");
    const ctx = paintCanvas ? paintCanvas.getContext("2d") : null;

    // State
    let isGameOver = false;
    let isPainter = false;
    let currentPainter = "";
    let cachedUser = "";
    let drawing = false;
    let lastX = 0, lastY = 0;
    let startX = 0, startY = 0; // for shapes
    let baseImage = null; // ImageData for preview

    // Throttling for freehand stroke sending
    let lastStrokeSentTs = 0;
    const STROKE_SEND_INTERVAL_MS = 16; // ~60fps
    const STROKE_MIN_DISTANCE = 0.5; // ignore tiny moves

    // Load players into dropdown
    async function populateNames() {
        try {
            const res = await fetch("players");
            if (!res.ok) throw new Error(`players ${res.status}`);
            const names = await res.json();
            userNameInput.innerHTML = "";
            const placeholder = document.createElement("option");
            placeholder.value = "";
            placeholder.disabled = true;
            placeholder.selected = true;
            placeholder.textContent = "Select your name";
            userNameInput.appendChild(placeholder);
            names.forEach(n => {
                const opt = document.createElement("option");
                opt.value = n;
                opt.textContent = n;
                userNameInput.appendChild(opt);
            });
        } catch (e) {
            console.error("Failed to load players:", e);
            statusText.textContent = "Failed to load players.";
        }
    }

    function escapeHtml(str) {
        return String(str)
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#39;");
    }

    // Initial load from results.json for Name/Points only
    async function loadAndRenderResultsFromFile() {
        try {
            const res = await fetch("results");
            if (!res.ok) throw new Error(`results ${res.status}`);
            const items = await res.json();

            resultsBody.innerHTML = "";

            if (Array.isArray(items) && items.length > 0) {
                items.forEach(x => {
                    const tr = document.createElement("tr");
                    tr.innerHTML = `<td>${escapeHtml(x.name)}</td><td>${x.points}</td>`;
                    resultsBody.appendChild(tr);
                });
                return;
            }

            const playersRes = await fetch("players");
            if (playersRes.ok) {
                const players = await playersRes.json();
                players.forEach(p => {
                    const tr = document.createElement("tr");
                    tr.innerHTML = `<td>${escapeHtml(p)}</td><td>0</td>`;
                    resultsBody.appendChild(tr);
                });
            }
        } catch (e) {
            console.error("Failed to load results:", e);
            statusText.textContent = "Failed to load results.";
        }
    }

    function hasSelectedName() {
        return !!(userNameInput.value && userNameInput.value.trim().length);
    }

    function getUser() {
        const name = (userNameInput.value || "").trim();
        if (name.length) {
            cachedUser = name;
            return name;
        }
        if (!cachedUser) {
            cachedUser = `User-${Math.floor(Math.random() * 10000)}`;
        }
        return cachedUser;
    }

    function updatePainterUI() {
        painterBtn.classList.toggle("active", isPainter);
        painterBtn.setAttribute("aria-pressed", String(isPainter));
        painterBtn.textContent = isPainter ? "I am the painter (on)" : "I am the painter";

        if (isPainter && statusText.textContent === "Select your name to start guessing.") {
            statusText.textContent = "Waiting for secret...";
        }
    }

    function applyNameRowVisibility() {
        userNameRow.style.display = isPainter ? "none" : "flex";
    }

    function applyGlobalPainterVisibility() {
        const me = getUser();
        const someoneIsPainter = !!currentPainter;
        const iAmGlobalPainter = someoneIsPainter && currentPainter === me;

        // Show reset section only to the painter
        resetSection.style.display = iAmGlobalPainter ? "block" : "none";
        // Canvas always visible, controls only to painter
        if (paintSection) paintSection.style.display = "block";
        if (paintControls) paintControls.style.display = iAmGlobalPainter ? "flex" : "none";
    }

    function setInputsEnabled(enabled) {
        const nameSelected = hasSelectedName();

        painterSection.style.display = (!isGameOver && isPainter) ? "block" : "none";
        guessSection.style.display = isPainter ? "none" : "block";
        applyNameRowVisibility();

        secretInput.disabled = !enabled || isGameOver || !isPainter;
        setSecretBtn.disabled = !enabled || isGameOver || !isPainter;

        const canGuess = enabled && !isGameOver && !isPainter && nameSelected;
        guessInput.disabled = !canGuess;
        guessBtn.disabled = !canGuess;

        userNameInput.disabled = !enabled || isGameOver;

        if (!isPainter && !nameSelected && !isGameOver && enabled) {
            statusText.textContent = "Select your name to start guessing.";
        }

        painterBtn.style.display = nameSelected ? "none" : "inline-block";

        updatePainterUI();
        applyGlobalPainterVisibility();
    }

    // Painter canvas events
    function getCanvasPos(ev) {
        const rect = paintCanvas.getBoundingClientRect();
        const clientX = ev.clientX ?? (ev.pageX - window.scrollX);
        const clientY = ev.clientY ?? (ev.pageY - window.scrollY);
        const x = (clientX - rect.left) * (paintCanvas.width / rect.width);
        const y = (clientY - rect.top) * (paintCanvas.height / rect.height);
        return { x, y };
    }

    function beginDraw(ev) {
        if (!ctx || !isPainter) return;
        drawing = true;
        const { x, y } = getCanvasPos(ev);
        lastX = x; lastY = y;
        startX = x; startY = y;
        // save base image for shape preview
        baseImage = ctx.getImageData(0, 0, paintCanvas.width, paintCanvas.height);
        // reset stroke throttling
        lastStrokeSentTs = performance.now();
    }

    async function draw(ev) {
        if (!ctx || !isPainter || !drawing) return;
        const { x, y } = getCanvasPos(ev);
        const color = paintColor.value || "#000";
        const size = Number(paintSize.value) || 4;

        const tool = paintTool ? paintTool.value : "freehand";
        if (tool === "freehand") {
            // Draw locally for the painter
            ctx.strokeStyle = color;
            ctx.lineWidth = size;
            ctx.lineCap = "round";
            ctx.beginPath();
            ctx.moveTo(lastX, lastY);
            ctx.lineTo(x, y);
            ctx.stroke();

            // Throttle broadcasting stroke to others
            const now = performance.now();
            const dx = x - lastX;
            const dy = y - lastY;
            const dist2 = dx * dx + dy * dy;
            if (now - lastStrokeSentTs >= STROKE_SEND_INTERVAL_MS && dist2 >= STROKE_MIN_DISTANCE * STROKE_MIN_DISTANCE) {
                try {
                    // Fire-and-forget to avoid backpressure
                    connection.send("DrawStroke", getUser(), lastX, lastY, x, y, color, size).catch(console.error);
                    lastStrokeSentTs = now;
                } catch (e) {
                    console.error(e);
                }
            }

            lastX = x; lastY = y;
        } else {
            // Preview shapes while dragging
            if (baseImage) ctx.putImageData(baseImage, 0, 0);
            ctx.strokeStyle = color;
            ctx.lineWidth = size;
            if (tool === "line") {
                ctx.lineCap = "round";
                ctx.beginPath();
                ctx.moveTo(startX, startY);
                ctx.lineTo(x, y);
                ctx.stroke();
            } else if (tool === "rect") {
                ctx.strokeRect(startX, startY, x - startX, y - startY);
            } else if (tool === "circle") {
                const dx = x - startX;
                const dy = y - startY;
                const r = Math.sqrt(dx*dx + dy*dy);
                ctx.beginPath();
                ctx.arc(startX, startY, r, 0, Math.PI * 2);
                ctx.stroke();
            }
        }
    }

    async function endDraw(ev) {
        if (!ctx || !isPainter || !drawing) { drawing = false; baseImage = null; return; }
        drawing = false;
        const pointEv = (ev && (ev.clientX !== undefined || ev.pageX !== undefined)) ? ev : { clientX: lastX, clientY: lastY };
        const { x, y } = getCanvasPos(pointEv);
        const color = paintColor.value || "#000";
        const size = Number(paintSize.value) || 4;
        const tool = paintTool ? paintTool.value : "freehand";

        if (tool === "line") {
            // local (ensure final render based on saved base)
            if (baseImage) ctx.putImageData(baseImage, 0, 0);
            ctx.strokeStyle = color;
            ctx.lineWidth = size;
            ctx.lineCap = "round";
            ctx.beginPath();
            ctx.moveTo(startX, startY);
            ctx.lineTo(x, y);
            ctx.stroke();
            // broadcast
            try {
                await connection.invoke("DrawShape", getUser(), "line", { x1: startX, y1: startY, x2: x, y2: y, color, size });
            } catch (e) { console.error(e); }
        } else if (tool === "rect") {
            if (baseImage) ctx.putImageData(baseImage, 0, 0);
            const w = x - startX;
            const h = y - startY;
            ctx.strokeStyle = color;
            ctx.lineWidth = size;
            ctx.strokeRect(startX, startY, w, h);
            try {
                await connection.invoke("DrawShape", getUser(), "rect", { x: startX, y: startY, w, h, color, size });
            } catch (e) { console.error(e); }
        } else if (tool === "circle") {
            if (baseImage) ctx.putImageData(baseImage, 0, 0);
            const dx = x - startX;
            const dy = y - startY;
            const r = Math.sqrt(dx*dx + dy*dy);
            ctx.strokeStyle = color;
            ctx.lineWidth = size;
            ctx.beginPath();
            ctx.arc(startX, startY, r, 0, Math.PI * 2);
            ctx.stroke();
            try {
                await connection.invoke("DrawShape", getUser(), "circle", { cx: startX, cy: startY, r, color, size });
            } catch (e) { console.error(e); }
        }
        baseImage = null;
    }

    function renderStroke(seg) {
        if (!ctx || !seg) return;
        ctx.strokeStyle = seg.color || "#000";
        ctx.lineWidth = Number(seg.size) || 4;
        ctx.lineCap = "round";
        ctx.beginPath();
        ctx.moveTo(seg.x1, seg.y1);
        ctx.lineTo(seg.x2, seg.y2);
        ctx.stroke();
    }

    function renderShape(shape) {
        if (!ctx || !shape) return;
        const type = shape.type;
        const p = shape.payload || {};
        const color = p.color || "#000";
        const size = Number(p.size) || 4;
        ctx.strokeStyle = color;
        ctx.lineWidth = size;
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

    if (paintCanvas && ctx) {
        paintCanvas.addEventListener("mousedown", beginDraw);
        paintCanvas.addEventListener("mousemove", draw);
        window.addEventListener("mouseup", endDraw);
        // Touch support
        paintCanvas.addEventListener("touchstart", (e) => { e.preventDefault(); beginDraw(e.touches[0]); }, { passive: false });
        paintCanvas.addEventListener("touchmove", (e) => { e.preventDefault(); draw(e.touches[0]); }, { passive: false });
        paintCanvas.addEventListener("touchend", (e) => { e.preventDefault(); const t = e.changedTouches && e.changedTouches[0]; endDraw(t ? t : undefined); }, { passive: false });
        paintCanvas.addEventListener("touchcancel", (e) => { e.preventDefault(); const t = e.changedTouches && e.changedTouches[0]; endDraw(t ? t : undefined); }, { passive: false });

        paintClearBtn.addEventListener("click", async () => {
            if (!ctx) return;
            ctx.clearRect(0, 0, paintCanvas.width, paintCanvas.height);
            baseImage = null;
            try {
                await connection.invoke("ClearCanvas", getUser());
            } catch (e) { console.error(e); }
        });
    }

    // Events
    painterBtn.addEventListener("click", async () => {
        isPainter = !isPainter;
        updatePainterUI();
        applyNameRowVisibility();

        try {
            const me = getUser();
            await connection.invoke("SelectPainter", isPainter ? me : null);
        } catch (e) {
            console.error(e);
        }

        setInputsEnabled(!isGameOver);
    });

    userNameInput.addEventListener("change", () => {
        setInputsEnabled(!isGameOver);
    });

    setSecretBtn.addEventListener("click", async () => {
        const secret = (secretInput.value || "").trim();
        if (!secret) return;
        try {
            await connection.invoke("SetSecret", getUser(), secret);
            secretInput.value = "";
        } catch (e) {
            console.error(e);
        }
    });

    // Submit guess and let server broadcast GuessAdded
    guessBtn.addEventListener("click", async () => {
        const guess = (guessInput.value || "").trim();
        if (!guess) return;
        try {
            await connection.invoke("Guess", getUser(), guess);
            guessInput.value = "";
            guessInput.focus();
        } catch (e) {
            console.error(e);
        }
    });

    // Allow Enter key to submit guess
    guessInput.addEventListener("keydown", async (ev) => {
        if (ev.key === "Enter" && !guessBtn.disabled) {
            ev.preventDefault();
            guessBtn.click();
        }
    });

    function setResetStatus(resetMsg) {
        isGameOver = false;
        historyList.innerHTML = "";
        statusText.textContent = resetMsg || "Game reset. Waiting for secret...";
        setInputsEnabled(true);
        // Clear painter canvas on reset
        if (ctx) ctx.clearRect(0, 0, paintCanvas.width, paintCanvas.height);
        baseImage = null;
    }

    resetWithResultsBtn.addEventListener("click", async () => {
        const ok = window.confirm("Are you sure you want to reset the whole game and points?");
        if (!ok) return;
        try {
            await connection.invoke("ResetWithResults");
            setResetStatus("Game reset. Results cleared.");
            await populateNames();
            await loadAndRenderResultsFromFile();
        } catch (e) { console.error(e); }
    });

    resetKeepResultsBtn.addEventListener("click", async () => {
        try {
            await connection.invoke("ResetKeepResults");
            setResetStatus("Game reset. Results kept.");
            await populateNames();
            await loadAndRenderResultsFromFile();
        } catch (e) { console.error(e); }
    });

    // Hub events
    connection.on("PainterSelected", payload => {
        const announced = (payload && payload.painter) ? payload.painter : "";
        currentPainter = announced;

        if (announced && hasSelectedName() && announced === getUser()) {
            isPainter = true;
            updatePainterUI();
            applyNameRowVisibility();
        } else if (!announced || (hasSelectedName() && announced !== getUser())) {
            isPainter = false;
            updatePainterUI();
            applyNameRowVisibility();
        }

        applyGlobalPainterVisibility();
    });

    connection.on("Error", msg => { statusText.textContent = `Error: ${msg}`; });

    connection.on("SecretSet", async payload => {
        statusText.textContent = `Secret set by ${payload.by}. Start guessing!`;
        await loadAndRenderResultsFromFile();
    });

    connection.on("GuessAdded", msg => {
        const li = document.createElement("li");
        li.className = msg.isCorrect ? "correct" : "";
        li.innerHTML = `<span class="user">${escapeHtml(msg.user)}</span>: <span class="guess">${escapeHtml(msg.guess)}</span>${msg.isCorrect ? " ✅" : ""}`;
        // Insert newest guesses at the top
        if (historyList.firstChild) {
            historyList.insertBefore(li, historyList.firstChild);
        } else {
            historyList.appendChild(li);
        }
    });

    connection.on("GameOver", async payload => {
        isGameOver = true;
        statusText.textContent = `Game over! Winner: ${payload.winner}`;
        setInputsEnabled(false);
        await loadAndRenderResultsFromFile();
    });

    connection.on("GameState", async state => {
        updateStatus(state);
    });

    // Drawing broadcasts
    connection.on("Stroke", seg => {
        renderStroke(seg);
    });

    connection.on("Shape", shape => {
        renderShape(shape);
    });

    connection.on("CanvasCleared", () => {
        if (ctx) ctx.clearRect(0, 0, paintCanvas.width, paintCanvas.height);
        baseImage = null;
    });

    function updateStatus(state) {
        isGameOver = !!state.isGameOver;
        if (!state.hasSecret && !isGameOver) statusText.textContent = "Waiting for secret...";
        else if (state.hasSecret && !isGameOver) statusText.textContent = "Secret set. Keep guessing!";
        else statusText.textContent = "Game over. Use Reset buttons.";
        setInputsEnabled(!isGameOver);
    }

    // Startup
    (async function start() {
        await populateNames();
        await loadAndRenderResultsFromFile();
        try {
            await connection.start();
            statusText.textContent = "Connected. Waiting for secret...";
        } catch (err) {
            console.error("Connection failed:", err);
            statusText.textContent = "Disconnected. Retrying...";
            setTimeout(start, 2000);
            return;
        }
        setInputsEnabled(true);
    })();
})();