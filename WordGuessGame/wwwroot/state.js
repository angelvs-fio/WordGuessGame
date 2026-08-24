export const state = {
    isGameOver: false,
    hasAnswer: false,
    isPainter: false,
    currentPainter: "",
    lastWinner: "",
    cachedUser: "",
    drawing: false,
    lastX: 0,
    lastY: 0,
    startX: 0,
    startY: 0,
    baseImage: null,
    currentTool: "freehand",
    lastStrokeSentTs: 0,
    activePlayers: [],
    triviaMode: false,
    triviaQuestionText: "",
    myTriviaAnswerSubmitted: false,
};

export const STROKE_SEND_INTERVAL_MS = 10;
export const STROKE_MIN_DISTANCE = 0.3;
export const TRIVIA_NUMBER_RE = /^-?(\d+(\.\d+)?|\.(\d+))$/;
