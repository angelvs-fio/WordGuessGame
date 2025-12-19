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

    const answerInput = document.getElementById("answerInput");
    const setAnswerBtn = document.getElementById("setAnswerBtn");
    const guessInput = document.getElementById("guessInput");
    const guessBtn = document.getElementById("guessBtn");

    const resetSection = document.getElementById("resetSection");
    resetSection.style.display = "none";
    const resetWithResultsBtn = document.getElementById("resetWithResultsBtn");
    const resetKeepResultsBtn = document.getElementById("resetKeepResultsBtn");

    const statusText = document.getElementById("statusText");
    const historyList = document.getElementById("historyList");
    const resultsBody = document.getElementById("resultsBody");

    // Topic UI
    const topicLabel = document.getElementById("topicLabel");
    const topicValue = document.getElementById("topicValue");
    const setTopicBtn = document.getElementById("setTopicBtn");
    const topicInput = document.getElementById("topicInput");

    // Painter-only manage players UI
    const managePlayersSection = document.getElementById("managePlayersSection");
    const managePlayerName = document.getElementById("managePlayerName");
    const addPlayerBtn = document.getElementById("addPlayerBtn");
    const deletePlayerBtn = document.getElementById("deletePlayerBtn");

    // Paint control
    const paintSection = document.getElementById("paintSection");
    const paintCanvas = document.getElementById("paintCanvas");
    const paintControls = document.getElementById("paintControls");
    const paintTool = document.getElementById("paintTool"); // may not exist anymore
    const toolButtons = document.getElementById("toolButtons");
    const paintColor = document.getElementById("paintColor");
    const paintSize = document.getElementById("paintSize");
    const paintClearBtn = document.getElementById("paintClearBtn");
    const colorPalette = document.getElementById("colorPalette");
    const ctx = paintCanvas ? paintCanvas.getContext("2d") : null;

    // State
    let isGameOver = false;
    let hasAnswer = false; // new: gate drawing until answer is set
    let isPainter = false;
    let currentPainter = "";
    let cachedUser = "";
    let drawing = false;
    let lastX = 0, lastY = 0;
    let startX = 0, startY = 0; // for shapes
    let baseImage = null; // ImageData for preview
    let currentTool = "freehand"; // new: track selected tool

    // Throttling for freehand stroke sending
    let lastStrokeSentTs = 0;
    const STROKE_SEND_INTERVAL_MS = 10; // ~100fps
    const STROKE_MIN_DISTANCE = 0.3; // px

    // Track active players announced by server
    let activePlayers = [];

    // Helper: enable/disable canvas and painter controls based on state
    function applyCanvasEnablement() {
        const someoneIsPainter = !!currentPainter;
        const iAmGlobalPainter = someoneIsPainter && currentPainter === getUser();
        const disableCanvas = isGameOver || !hasAnswer; // disable when winner or no answer yet
        if (paintCanvas) {
            paintCanvas.classList.toggle('disabled', disableCanvas);
        }
        // Show controls only when I'm the painter AND canvas is enabled
        if (paintControls) {
            paintControls.style.display = (!disableCanvas && iAmGlobalPainter) ? 'flex' : 'none';
        }
    }

    // Attach palette events
    if (colorPalette && paintColor) {
        colorPalette.querySelectorAll('.color-swatch').forEach(btn => {
            btn.addEventListener('click', () => {
                const c = btn.getAttribute('data-color');
                if (c) paintColor.value = c;
            });
        });
    }

    // Tool buttons -> set tool
    if (toolButtons) {
        const buttons = Array.from(toolButtons.querySelectorAll('.icon-btn'));
        const setActive = (tool) => {
            currentTool = tool;
            buttons.forEach(b => b.classList.toggle('active', b.getAttribute('data-tool') === tool));
        };
        // default active tool
        setActive('freehand');
        toolButtons.addEventListener('click', (e) => {
            const target = e.target.closest('.icon-btn');
            if (!target) return;
            const tool = target.getAttribute('data-tool');
            if (!tool) return;
            if (paintTool) paintTool.value = tool; // keep compatibility if select exists
            setActive(tool);
        });
    }

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

    // Fetch and render results
    async function loadAndRenderResultsFromFile() {
        try {
            const res = await fetch("results");
            if (!res.ok) throw new Error(`results ${res.status}`);
            const items = await res.json();
            renderResults(items);
        } catch (e) {
            console.error("Failed to load results:", e);
            statusText.textContent = "Failed to load results.";
        }
    }

    async function loadTopic() {
        try {
            const res = await fetch("topic");
            if (!res.ok) throw new Error(`topic ${res.status}`);
            const payload = await res.json();
            const t = (payload && payload.topic) ? String(payload.topic) : "";
            renderTopic(t);
        } catch (e) {
            console.error("Failed to load topic:", e);
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
        managePlayersSection.style.display = isPainter ? "block" : "none";
        setTopicBtn.style.display = isPainter ? "inline-block" : "none";
        topicInput.style.display = isPainter ? "block" : "none";
        // Removed: do not override status here; rely on server state
    }

    function applyNameRowVisibility() {
        // Keep the row visible; only hide the username selector when painter
        if (userNameRow) userNameRow.style.display = "flex";
        if (userNameInput) userNameInput.style.display = isPainter ? "none" : "block";
        if (painterBtn) painterBtn.style.display = "inline-flex";
    }

    function applyGlobalPainterVisibility() {
        const me = getUser();
        const someoneIsPainter = !!currentPainter;
        const iAmGlobalPainter = someoneIsPainter && currentPainter === me;
        resetSection.style.display = iAmGlobalPainter ? "block" : "none";
        if (paintSection) paintSection.style.display = "block";
        // Only show controls when I am painter AND answer is set AND game not over
        if (paintControls) paintControls.style.display = (iAmGlobalPainter && hasAnswer && !isGameOver) ? "flex" : "none";
        // Update canvas enabled/disabled state
        applyCanvasEnablement();
    }

    function setInputsEnabled(enabled) {
        const nameSelected = hasSelectedName();
        painterSection.style.display = isPainter ? "block" : "none";
        guessSection.style.display = isPainter ? "none" : "block";
        applyNameRowVisibility();
        answerInput.disabled = !enabled || isGameOver || !isPainter;
        setAnswerBtn.disabled = !enabled || isGameOver || !isPainter;
        const canGuess = enabled && !isGameOver && !isPainter && nameSelected;
        guessInput.disabled = !canGuess;
        guessBtn.disabled = !canGuess;
        userNameInput.disabled = !enabled || isGameOver;
        if (!isPainter && !nameSelected && !isGameOver && enabled) {
            statusText.textContent = "Select your name to start guessing.";
        }
        // Painter button must always be visible
        painterBtn.style.display = "inline-flex";
        updatePainterUI();
        applyGlobalPainterVisibility();
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
        if (!ctx || !isPainter || isGameOver || !hasAnswer) return; // gate drawing
        drawing = true;
        const { x, y } = getCanvasPos(ev);
        lastX = x; lastY = y;
        startX = x; startY = y;
        baseImage = ctx.getImageData(0, 0, paintCanvas.width, paintCanvas.height);
        lastStrokeSentTs = performance.now();
    }

    async function draw(ev) {
        if (!ctx || !isPainter || !drawing || isGameOver || !hasAnswer) return; // gate drawing
        const { x, y } = getCanvasPos(ev);
        const color = paintColor.value || "#000";
        const size = Number(paintSize.value) || 4;
        const tool = paintTool ? paintTool.value : currentTool;
        if (tool === "freehand") {
            ctx.strokeStyle = color;
            ctx.lineWidth = size;
            ctx.lineCap = "round";
            ctx.beginPath();
            ctx.moveTo(lastX, lastY);
            ctx.lineTo(x, y);
            ctx.stroke();
            const now = performance.now();
            const dx = x - lastX;
            const dy = y - lastY;
            const dist2 = dx * dx + dy * dy;
            if (now - lastStrokeSentTs >= STROKE_SEND_INTERVAL_MS && dist2 >= STROKE_MIN_DISTANCE * STROKE_MIN_DISTANCE) {
                try {
                    connection.send("DrawStroke", getUser(), lastX, lastY, x, y, color, size).catch(console.error);
                    lastStrokeSentTs = now;
                } catch (e) { console.error(e); }
            }
            lastX = x; lastY = y;
        } else {
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
        if (isGameOver || !hasAnswer) { drawing = false; baseImage = null; return; } // gate drawing
        drawing = false;
        const pointEv = (ev && (ev.clientX !== undefined || ev.pageX !== undefined)) ? ev : { clientX: lastX, clientY: lastY };
        const { x, y } = getCanvasPos(pointEv);
        const color = paintColor.value || "#000";
        const size = Number(paintSize.value) || 4;
        const tool = paintTool ? paintTool.value : currentTool;
        if (tool === "line") {
            if (baseImage) ctx.putImageData(baseImage, 0, 0);
            ctx.strokeStyle = color;
            ctx.lineWidth = size;
            ctx.lineCap = "round";
            ctx.beginPath();
            ctx.moveTo(startX, startY);
            ctx.lineTo(x, y);
            ctx.stroke();
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
        } else {
            try {
                connection.send("DrawStroke", getUser(), lastX, lastY, x, y, color, size).catch(console.error);
            } catch (e) { console.error(e); }
        }
        baseImage = null;
    }

    // Manage players actions
    async function managePlayer(action) {
        const name = (managePlayerName.value || "").trim();
        if (!name) return;
        try {
            const res = await fetch(`/players/manage/${action}?name=${encodeURIComponent(name)}`, { method: "POST" });
            if (!res.ok) throw new Error(`${action} ${res.status}`);
            managePlayerName.value = "";
            await populateNames();
            await loadAndRenderResultsFromFile();
        } catch (e) {
            console.error(`Failed to ${action} player:`, e);
        }
    }
    addPlayerBtn.addEventListener("click", () => managePlayer("add"));
    deletePlayerBtn.addEventListener("click", () => managePlayer("remove"));

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
            try { await connection.invoke("ClearCanvas", getUser()); } catch (e) { console.error(e); }
        });
    }

    painterBtn.addEventListener("click", async () => {
        isPainter = !isPainter;
        updatePainterUI();
        applyNameRowVisibility();
        try {
            const me = getUser();
            await connection.invoke("SelectPainter", isPainter ? me : null);
        } catch (e) { console.error(e); }
        // Show painter-only status when no answer yet
        if (isPainter && !hasAnswer && !isGameOver) {
            statusText.textContent = "Waiting for answer...";
        }
        setInputsEnabled(!isGameOver);
        applyCanvasEnablement();
    });

    userNameInput.addEventListener("change", async () => {
        setInputsEnabled(!isGameOver);
        if (hasSelectedName()) {
            try { await connection.invoke("SetUserName", getUser()); } catch (e) { console.error(e); }
        }
        await loadAndRenderResultsFromFile();
        await loadTopic();
        applyCanvasEnablement();
    });

    setAnswerBtn.addEventListener("click", async () => {
        const answer = (answerInput.value || "").trim();
        if (!answer) return;
        try { await connection.invoke("SetAnswer", getUser(), answer); answerInput.value = ""; } catch (e) { console.error(e); }
    });


    guessBtn.addEventListener("click", async () => {
        const guess = (guessInput.value || "").trim();
        if (!guess) return;
        try { await connection.invoke("Guess", getUser(), guess); guessInput.value = ""; guessInput.focus(); } catch (e) { console.error(e); }
    });

    // Topic button: use input field value
    setTopicBtn.addEventListener("click", async () => {
        const t = (topicInput.value || "").trim();
        if (!t) return;
        try { await connection.invoke("SetTopic", getUser(), t); } catch (e) { console.error(e); }
    });

    guessInput.addEventListener("keydown", async (ev) => {
        if (ev.key === "Enter" && !guessBtn.disabled) { ev.preventDefault(); guessBtn.click(); }
    });

    function setResetStatus(resetMsg) {
        isGameOver = false;
        hasAnswer = false; // reset answer state
        historyList.innerHTML = "";
        statusText.textContent = resetMsg || "Game reset. Waiting for answer...";
        setInputsEnabled(true);
        if (ctx) ctx.clearRect(0, 0, paintCanvas.width, paintCanvas.height);
        baseImage = null;
        activePlayers = [];
        applyCanvasEnablement();
    }

    resetWithResultsBtn.addEventListener("click", async () => {
        const ok = window.confirm("Are you sure you want to reset the whole game and points?");
        if (!ok) return;
        try {
            await connection.invoke("ResetWithResults");
            setResetStatus("Game reset. Results cleared.");
            await populateNames();
            await loadAndRenderResultsFromFile();
            await loadTopic();
        } catch (e) { console.error(e); }
    });

    resetKeepResultsBtn.addEventListener("click", async () => {
        try {
            await connection.invoke("ResetKeepResults");
            setResetStatus("Game reset. Results kept.");
            await populateNames();
            await loadAndRenderResultsFromFile();
            await loadTopic();
        } catch (e) { console.error(e); }
    });

    // SignalR events
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
        // Adjust status based on painter role when answer is set or not set
        const someoneIsPainter = !!currentPainter;
        const iAmGlobalPainter = someoneIsPainter && currentPainter === getUser();
        if (!hasAnswer && !isGameOver && iAmGlobalPainter) {
            statusText.textContent = "Waiting for answer...";
        } else if (hasAnswer && !isGameOver) {
            statusText.textContent = iAmGlobalPainter ? "Answer set. You can start drawing!" : "Answer set. Start guessing!";
        }
        applyGlobalPainterVisibility();
        applyCanvasEnablement();
    });

    connection.on("ActivePlayers", async players => {
        activePlayers = Array.isArray(players) ? players : [];
        await loadAndRenderResultsFromFile();
    });

    connection.on("Error", msg => { statusText.textContent = `Error: ${msg}`; });

    connection.on("AnswerSet", async payload => {
        const someoneIsPainter = !!currentPainter;
        const iAmGlobalPainter = someoneIsPainter && currentPainter === getUser();
        statusText.textContent = iAmGlobalPainter ? "Answer set. You can start drawing!" : `Answer set by ${payload.by}. Start guessing!`;
        hasAnswer = true; // enable canvas now
        applyCanvasEnablement();
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
        statusText.textContent = `Congratulations! The winner is ${payload.winner}!`;
        setInputsEnabled(false);
        applyCanvasEnablement();
        await loadAndRenderResultsFromFile();
    });

    connection.on("GameState", async state => { updateStatus(state); });
    connection.on("Stroke", seg => { renderStroke(seg); });
    connection.on("Shape", shape => { renderShape(shape); });
    connection.on("CanvasCleared", () => { if (ctx) ctx.clearRect(0, 0, paintCanvas.width, paintCanvas.height); baseImage = null; });
    connection.on("ResetWithResults", async () => {
        historyList.innerHTML = "";
        statusText.textContent = "Game reset. Results cleared.";
        isGameOver = false;
        hasAnswer = false;
        setInputsEnabled(true);
        activePlayers = [];
        await populateNames();
        await loadAndRenderResultsFromFile();
        await loadTopic();
        applyCanvasEnablement();
    });
    connection.on("ResetKeepResults", async () => {
        historyList.innerHTML = "";
        statusText.textContent = "Game reset. Results kept.";
        isGameOver = false;
        hasAnswer = false;
        setInputsEnabled(true);
        activePlayers = [];
        await populateNames();
        await loadAndRenderResultsFromFile();
        await loadTopic();
        applyCanvasEnablement();
    });

    connection.on("TopicUpdated", payload => {
        const t = (payload && payload.topic) ? String(payload.topic) : "";
        renderTopic(t);
        topicInput.value = t;
    });

    function updateStatus(state) {
        isGameOver = !!state.isGameOver;
        hasAnswer = !!state.hasAnswer;
        const someoneIsPainter = !!currentPainter;
        const iAmGlobalPainter = someoneIsPainter && currentPainter === getUser();
        if (!state.hasAnswer && !isGameOver) {
            statusText.textContent = "Waiting for answer...";
        } else if (state.hasAnswer && !isGameOver) {
            statusText.textContent = iAmGlobalPainter ? "Answer set. You can start drawing!" : "Answer set. Keep guessing!";
        } else {
            const lw = state.lastWinner ? String(state.lastWinner).trim() : "";
            statusText.textContent = lw ? `Congratulations, ${escapeHtml(lw)}! Please reset the game!` : "Please reset the game!";
        }
        // topic always visible
        const t = state.topic ? String(state.topic) : "";
        renderTopic(t);
        topicInput.value = t;
        setInputsEnabled(!isGameOver);
        applyCanvasEnablement();
    }

    function renderTopic(topic) {
        if (!topicLabel || !topicValue) return;
        topicLabel.textContent = "Current topic:";
        topicLabel.style.color = "white";
        topicValue.textContent = String(topic || "").toUpperCase();
        topicValue.style.color = "#22c55e"; // green
        topicValue.style.fontWeight = "bold";
        topicValue.style.textAlign = "center";
    }

    function renderResults(items) {
        resultsBody.innerHTML = "";
        if (!Array.isArray(items) || items.length === 0) return;
        const activeSet = new Set((activePlayers || []).map(a => a.toLowerCase()));
        items.forEach(x => {
            const crown = x.isLastWinner ? " 👑" : "";
            const isActive = activeSet.has(String(x.name).toLowerCase());
            const nameCell = isActive ? `<span class="active-player">${escapeHtml(x.name)}</span>${crown}` : `${escapeHtml(x.name)}${crown}`;
            const tr = document.createElement("tr");
            tr.innerHTML = `<td>${nameCell}</td><td>${x.points}</td>`;
            resultsBody.appendChild(tr);
        });
    }

    function renderStroke(seg) {
        if (!ctx || !seg) return;
        const color = seg.color || "#000";
        const size = Number(seg.size) || 4;
        ctx.strokeStyle = color;
        ctx.lineWidth = size;
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

    // Startup
    connection.start()
        .then(async () => {
            await populateNames();
            await loadAndRenderResultsFromFile();
            await loadTopic();
            // Removed local status override; GameState from server will drive UI
            if (hasSelectedName()) { try { await connection.invoke("SetUserName", getUser()); } catch {} }
            applyCanvasEnablement();
        })
        .catch(err => { console.error("Connection failed:", err); statusText.textContent = "Disconnected."; });

})();