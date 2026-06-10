import { state, TRIVIA_NUMBER_RE } from "./state.js";
import { applyThousandsFormatting, escapeHtml, formatThousands } from "./utils.js";
import { ctx, initCanvas, renderStroke, renderShape } from "./canvas.js";
import {
    applyCanvasEnablement, applyGameMode, applyTriviaGuessState, updateWordCount,
    populateNames, loadAndRenderResultsFromFile, loadTopic,
    hasSelectedName, getUser, updatePainterUI, applyNameRowVisibility,
    applyGlobalPainterVisibility, setInputsEnabled, renderTopic,
    updateStatus, initPaletteAndTools
} from "./ui.js";
import * as dom from "./dom.js";

// Connection
const connection = new signalR.HubConnectionBuilder()
    .withUrl("hub/guess")
    .withAutomaticReconnect({
        // Retry indefinitely: 500 ms, 1 s, 2 s, 4 s … capped at 30 s
        nextRetryDelayInMilliseconds: retryContext =>
            Math.min(500 * Math.pow(2, retryContext.previousRetryCount), 30000)
    })
    .build();

// Initial DOM state
dom.resetSection.style.display = "none";

// Init subsystems
initCanvas(connection, getUser);
initPaletteAndTools();
applyThousandsFormatting(dom.triviaAnswerInput);
applyThousandsFormatting(dom.triviaGuessInput);

// --- Manage players ---
async function managePlayer(action) {
    const name = (dom.managePlayerName.value || "").trim();
    if (!name) return;
    try {
        const res = await fetch(`/players/manage/${action}?name=${encodeURIComponent(name)}`, { method: "POST" });
        if (!res.ok) throw new Error(`${action} ${res.status}`);
        dom.managePlayerName.value = "";
        await populateNames();
        await loadAndRenderResultsFromFile();
    } catch (e) {
        console.error(`Failed to ${action} player:`, e);
    }
}
dom.addPlayerBtn.addEventListener("click", () => managePlayer("add"));
dom.deletePlayerBtn.addEventListener("click", () => managePlayer("remove"));

// --- Painter button ---
dom.painterBtn.addEventListener("click", async () => {
    state.isPainter = !state.isPainter;
    updatePainterUI();
    applyNameRowVisibility();
    try {
        const me = getUser();
        await connection.invoke("SelectPainter", state.isPainter ? me : null);
        if (state.isPainter) {
            await connection.invoke("SetUserName", "");
        } else if (hasSelectedName()) {
            await connection.invoke("SetUserName", getUser());
        }
    } catch (e) { console.error(e); }
    if (state.isPainter && !state.hasAnswer && !state.isGameOver) {
        dom.statusText.textContent = "Waiting for answer...";
    }
    setInputsEnabled(!state.isGameOver);
    applyCanvasEnablement();
});

// --- Username select ---
dom.userNameInput.addEventListener("change", async () => {
    setInputsEnabled(!state.isGameOver);
    if (hasSelectedName()) {
        try { await connection.invoke("SetUserName", getUser()); } catch (e) { console.error(e); }
    }
    await loadAndRenderResultsFromFile();
    await loadTopic();
    applyCanvasEnablement();
});

// --- Answer ---
dom.setAnswerBtn.addEventListener("click", async () => {
    const answer = (dom.answerInput.value || "").trim();
    if (!answer) return;
    try { await connection.invoke("SetAnswer", getUser(), answer); } catch (e) { console.error(e); }
});

// --- Guess ---
dom.guessBtn.addEventListener("click", async () => {
    const guess = (dom.guessInput.value || "").trim();
    if (!guess) return;
    try { await connection.invoke("Guess", getUser(), guess); dom.guessInput.value = ""; dom.guessInput.focus(); } catch (e) { console.error(e); }
});

dom.guessInput.addEventListener("keydown", async (ev) => {
    if (ev.key === "Enter" && !dom.guessBtn.disabled) { ev.preventDefault(); dom.guessBtn.click(); }
});

// --- Topic ---
dom.setTopicBtn.addEventListener("click", async () => {
    const t = (dom.topicInput.value || "").trim();
    if (!t) return;
    try { await connection.invoke("SetTopic", getUser(), t); } catch (e) { console.error(e); }
});

// --- Game mode ---
dom.gameModeBtn.addEventListener("click", async () => {
    const newMode = state.triviaMode ? "drawing" : "trivia";
    try { await connection.invoke("SwitchGameMode", newMode); } catch (e) { console.error(e); }
});

// --- Trivia: AI question generator ---
dom.generateTriviaBtn.addEventListener("click", async () => {
    const btn = dom.generateTriviaBtn;
    const originalText = btn.textContent;
    btn.textContent = "⏳ Generating...";
    btn.disabled = true;
    try {
        const res = await fetch("/trivia/generate");
        const data = await res.json();
        if (!res.ok) {
            dom.statusText.textContent = data.error || `AI generation failed (${res.status}).`;
            return;
        }
        dom.triviaQuestionInput.value = data.question;
        dom.triviaAnswerInput.value = data.answer;
        dom.triviaAnswerInput.dispatchEvent(new Event("input"));
    } catch (e) {
        console.error("AI generation error:", e);
        dom.statusText.textContent = "AI generation failed. Check your connection.";
    } finally {
        btn.textContent = originalText;
        btn.disabled = false;
    }
});

// --- Trivia: set question ---
dom.setTriviaQuestionBtn.addEventListener("click", async () => {
    const q = (dom.triviaQuestionInput.value || "").trim();
    const a = (dom.triviaAnswerInput.value || "").trim().replace(/\s+/g, "");
    if (q.length <= 5) {
        dom.statusText.textContent = "Question must be longer than 5 characters.";
        dom.triviaQuestionInput.focus();
        return;
    }
    if (!TRIVIA_NUMBER_RE.test(a)) {
        dom.statusText.textContent = "Answer must be a valid real number. Use '.' for decimals.";
        dom.triviaAnswerInput.focus();
        return;
    }
    try {
        await connection.invoke("SetTriviaQuestion", getUser(), q, a);
    } catch (e) { console.error(e); }
});

// --- Trivia: submit guess ---
dom.triviaGuessBtn.addEventListener("click", async () => {
    if (state.myTriviaAnswerSubmitted) return;
    const ans = (dom.triviaGuessInput.value || "").trim().replace(/\s+/g, "");
    if (!ans) return;
    if (!TRIVIA_NUMBER_RE.test(ans)) {
        dom.statusText.textContent = "Please enter a valid real number.";
        dom.triviaGuessInput.focus();
        return;
    }
    try {
        await connection.invoke("SubmitTriviaAnswer", getUser(), ans);
        state.myTriviaAnswerSubmitted = true;
        dom.triviaGuessInput.disabled = true;
        dom.triviaGuessBtn.disabled = true;
    } catch (e) { console.error(e); }
});

dom.triviaGuessInput.addEventListener("keydown", (ev) => {
    if (ev.key === "Enter" && !dom.triviaGuessBtn.disabled) { ev.preventDefault(); dom.triviaGuessBtn.click(); }
});

// --- Reset helpers ---
function setResetStatus(resetMsg) {
    state.isGameOver = false;
    state.hasAnswer = false;
    state.triviaQuestionText = "";
    state.myTriviaAnswerSubmitted = false;
    dom.answerInput.value = "";
    updateWordCount(0);
    if (dom.triviaQuestionDisplay) dom.triviaQuestionDisplay.value = "";
    if (dom.triviaAnswerReveal) { dom.triviaAnswerReveal.style.display = "none"; dom.triviaAnswerReveal.textContent = ""; }
    if (dom.triviaGuessInput) { dom.triviaGuessInput.value = ""; dom.triviaGuessInput.disabled = true; }
    if (dom.triviaGuessBtn) dom.triviaGuessBtn.disabled = true;
    dom.historyList.innerHTML = "";
    dom.statusText.textContent = resetMsg || "Game reset. Waiting for answer...";
    setInputsEnabled(true);
    if (ctx) ctx.clearRect(0, 0, dom.paintCanvas.width, dom.paintCanvas.height);
    state.baseImage = null;
    state.activePlayers = [];
    applyCanvasEnablement();
}

dom.resetWithResultsBtn.addEventListener("click", async () => {
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

dom.resetKeepResultsBtn.addEventListener("click", async () => {
    try {
        await connection.invoke("ResetKeepResults");
        setResetStatus("Game reset. Results kept.");
        await populateNames();
        await loadAndRenderResultsFromFile();
        await loadTopic();
    } catch (e) { console.error(e); }
});

// --- SignalR events ---
connection.on("PainterSelected", payload => {
    const announced = (payload && payload.painter) ? payload.painter : "";
    state.currentPainter = announced;
    if (announced && hasSelectedName() && announced === getUser()) {
        state.isPainter = true;
        updatePainterUI();
        applyNameRowVisibility();
    } else if (!announced || (hasSelectedName() && announced !== getUser())) {
        state.isPainter = false;
        updatePainterUI();
        applyNameRowVisibility();
    }
    const someoneIsPainter = !!state.currentPainter;
    const iAmGlobalPainter = someoneIsPainter && state.currentPainter === getUser();
    if (!state.hasAnswer && !state.isGameOver && iAmGlobalPainter) {
        dom.statusText.textContent = "Waiting for answer...";
    } else if (state.hasAnswer && !state.isGameOver) {
        const nameSelected = hasSelectedName();
        dom.statusText.textContent = iAmGlobalPainter
            ? "Answer set. You can start drawing!"
            : (nameSelected ? "Answer set. Start guessing!" : "Answer set. Please select your name and start guessing!");
    }
    applyGlobalPainterVisibility();
    applyCanvasEnablement();
    if (state.triviaMode) applyGameMode();
});

connection.on("ActivePlayers", async players => {
    state.activePlayers = Array.isArray(players) ? players : [];
    await loadAndRenderResultsFromFile();
});

connection.on("Error", msg => { dom.statusText.textContent = `Error: ${msg}`; });

connection.on("AnswerSet", async payload => {
    const someoneIsPainter = !!state.currentPainter;
    const iAmGlobalPainter = someoneIsPainter && state.currentPainter === getUser();
    const nameSelected = hasSelectedName();
    dom.statusText.textContent = iAmGlobalPainter
        ? "Answer set. You can start drawing!"
        : (nameSelected ? `Answer set by ${payload.by}. Start guessing!` : `Answer set by ${payload.by}. Please select your name and start guessing!`);
    state.hasAnswer = true;
    if (payload.wordCount > 0) updateWordCount(payload.wordCount);
    applyCanvasEnablement();
    await loadAndRenderResultsFromFile();
});

connection.on("GuessAdded", msg => {
    const li = document.createElement("li");
    li.className = msg.isCorrect ? "correct" : "";
    li.innerHTML = `<span class="user">${escapeHtml(msg.user)}</span>: <span class="guess">${escapeHtml(msg.guess)}</span>${msg.isCorrect ? " \u2705" : ""}`;
    if (dom.historyList.firstChild) {
        dom.historyList.insertBefore(li, dom.historyList.firstChild);
    } else {
        dom.historyList.appendChild(li);
    }
});

connection.on("GameOver", async payload => {
    state.isGameOver = true;
    dom.statusText.textContent = `Congratulations! The winner is ${payload.winner}!`;
    setInputsEnabled(false);
    applyCanvasEnablement();
    await loadAndRenderResultsFromFile();
});

connection.on("GameState", async serverState => { updateStatus(serverState); });
connection.on("Stroke", seg => { renderStroke(seg); });
connection.on("Shape", shape => { renderShape(shape); });
connection.on("CanvasCleared", () => { if (ctx) ctx.clearRect(0, 0, dom.paintCanvas.width, dom.paintCanvas.height); state.baseImage = null; });

connection.on("ResetWithResults", async () => {
    state.triviaQuestionText = "";
    state.myTriviaAnswerSubmitted = false;
    if (dom.triviaQuestionDisplay) dom.triviaQuestionDisplay.value = "";
    if (dom.triviaAnswerReveal) { dom.triviaAnswerReveal.style.display = "none"; dom.triviaAnswerReveal.textContent = ""; }
    if (dom.triviaGuessInput) { dom.triviaGuessInput.value = ""; dom.triviaGuessInput.disabled = true; }
    if (dom.triviaGuessBtn) dom.triviaGuessBtn.disabled = true;
    dom.historyList.innerHTML = "";
    dom.statusText.textContent = "Game reset. Results cleared.";
    state.isGameOver = false;
    state.hasAnswer = false;
    updateWordCount(0);
    setInputsEnabled(true);
    state.activePlayers = [];
    await populateNames();
    await loadAndRenderResultsFromFile();
    await loadTopic();
    applyCanvasEnablement();
});

connection.on("ResetKeepResults", async () => {
    state.triviaQuestionText = "";
    state.myTriviaAnswerSubmitted = false;
    if (dom.triviaQuestionDisplay) dom.triviaQuestionDisplay.value = "";
    if (dom.triviaAnswerReveal) { dom.triviaAnswerReveal.style.display = "none"; dom.triviaAnswerReveal.textContent = ""; }
    if (dom.triviaGuessInput) { dom.triviaGuessInput.value = ""; dom.triviaGuessInput.disabled = true; }
    if (dom.triviaGuessBtn) dom.triviaGuessBtn.disabled = true;
    dom.historyList.innerHTML = "";
    dom.statusText.textContent = "Game reset. Results kept.";
    state.isGameOver = false;
    state.hasAnswer = false;
    updateWordCount(0);
    setInputsEnabled(true);
    state.activePlayers = [];
    await populateNames();
    await loadAndRenderResultsFromFile();
    await loadTopic();
    applyCanvasEnablement();
});

connection.on("TopicUpdated", payload => {
    const t = (payload && payload.topic) ? String(payload.topic) : "";
    renderTopic(t);
    dom.topicInput.value = t;
});

connection.on("GameModeChanged", payload => {
    state.triviaMode = payload.mode === "trivia";
    state.triviaQuestionText = "";
    state.myTriviaAnswerSubmitted = false;
    if (dom.triviaQuestionInput) dom.triviaQuestionInput.value = "";
    if (dom.triviaAnswerInput) dom.triviaAnswerInput.value = "";
    if (dom.triviaQuestionDisplay) dom.triviaQuestionDisplay.value = "";
    if (dom.triviaAnswerReveal) { dom.triviaAnswerReveal.style.display = "none"; dom.triviaAnswerReveal.textContent = ""; }
    if (dom.triviaGuessInput) { dom.triviaGuessInput.value = ""; dom.triviaGuessInput.disabled = true; }
    if (dom.triviaGuessBtn) dom.triviaGuessBtn.disabled = true;
    applyGameMode();
    dom.statusText.textContent = state.triviaMode
        ? (state.isPainter ? "Trivia mode. Set a question!" : "Trivia mode. Waiting for question...")
        : "Drawing mode active.";
    if (!state.triviaMode) setInputsEnabled(!state.isGameOver);
});

connection.on("TriviaQuestionSet", payload => {
    state.triviaQuestionText = payload.question || "";
    state.myTriviaAnswerSubmitted = false;
    if (dom.triviaQuestionDisplay) dom.triviaQuestionDisplay.value = state.triviaQuestionText;
    if (dom.triviaAnswerReveal) { dom.triviaAnswerReveal.style.display = "none"; dom.triviaAnswerReveal.textContent = ""; }
    if (dom.triviaGuessInput && !state.isPainter) dom.triviaGuessInput.value = "";
    applyTriviaGuessState();
    dom.statusText.textContent = state.isPainter
        ? "Question set. Waiting for players to answer..."
        : (hasSelectedName() ? "Question ready. Enter your answer!" : "Question ready. Select your name to answer!");
});

connection.on("TriviaAnswerSubmitted", msg => {
    const li = document.createElement("li");
    li.innerHTML = `<span class="user">${escapeHtml(msg.user)}</span>: <span class="guess">${escapeHtml(formatThousands(msg.answer))}</span>`;
    if (dom.historyList.firstChild) {
        dom.historyList.insertBefore(li, dom.historyList.firstChild);
    } else {
        dom.historyList.appendChild(li);
    }
});

connection.on("TriviaComplete", async payload => {
    if (dom.triviaAnswerReveal) {
        dom.triviaAnswerReveal.style.display = "block";
        dom.triviaAnswerReveal.textContent = `\u2705 Correct answer: ${formatThousands(payload.correctAnswer)}`;
    }
    if (dom.triviaGuessInput) dom.triviaGuessInput.disabled = true;
    if (dom.triviaGuessBtn) dom.triviaGuessBtn.disabled = true;
    const winnerAnswer = payload.winner && Array.isArray(payload.answers)
        ? (payload.answers.find(a => a.user === payload.winner)?.answer ?? "")
        : "";
    dom.statusText.textContent = payload.winner
        ? `\uD83C\uDFC6 Winner: ${escapeHtml(payload.winner)}! His/Her answer was: ${formatThousands(winnerAnswer)}`
        : `Answer was: ${formatThousands(payload.correctAnswer)}. No winner determined.`;
    await loadAndRenderResultsFromFile();
});

// Startup
connection.start()
    .then(async () => {
        await populateNames();
        await loadAndRenderResultsFromFile();
        await loadTopic();
        if (hasSelectedName()) { try { await connection.invoke("SetUserName", getUser()); } catch { } }
        applyCanvasEnablement();
    })
    .catch(err => { console.error("Connection failed:", err); dom.statusText.textContent = "Disconnected."; });
