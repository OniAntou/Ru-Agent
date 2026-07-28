from __future__ import annotations

import sys
from pathlib import Path
from flask import Flask, jsonify, render_template_string, request

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.llama3_1_runtime import generate_llama_response_hf
from mini_gpt.ru_style import RU_SYSTEM_PROMPT

app = Flask(__name__)

BASE_MODEL = str(PROJECT_ROOT / "models" / "llama3.1-8b-instruct-hf")
ADAPTER_DIR = str(PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6_3")

HTML_TEMPLATE = """
<!DOCTYPE html>
<html lang="vi">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Ru Llama 3.1 8B GUI</title>
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;600;700&display=swap" rel="stylesheet">
    <style>
        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
        }
        body {
            font-family: 'Outfit', sans-serif;
            background: #090a0f;
            color: #e2e8f0;
            height: 100vh;
            display: flex;
            justify-content: center;
            align-items: center;
            overflow: hidden;
        }
        .bg-glow {
            position: absolute;
            width: 600px;
            height: 600px;
            background: radial-gradient(circle, rgba(16, 185, 129, 0.15) 0%, rgba(139, 92, 246, 0.1) 50%, rgba(0, 0, 0, 0) 70%);
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            z-index: 0;
            pointer-events: none;
        }
        .chat-container {
            position: relative;
            z-index: 1;
            width: 100%;
            max-width: 900px;
            height: 90vh;
            background: rgba(15, 23, 42, 0.75);
            backdrop-filter: blur(16px);
            border: 1px solid rgba(255, 255, 255, 0.1);
            border-radius: 24px;
            display: flex;
            flex-direction: column;
            box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5), 0 0 30px rgba(16, 185, 129, 0.2);
        }
        .chat-header {
            padding: 20px 28px;
            background: rgba(30, 41, 59, 0.5);
            border-bottom: 1px solid rgba(255, 255, 255, 0.08);
            display: flex;
            align-items: center;
            gap: 16px;
            border-top-left-radius: 24px;
            border-top-right-radius: 24px;
        }
        .avatar {
            width: 48px;
            height: 48px;
            border-radius: 50%;
            background: linear-gradient(135deg, #10b981, #8b5cf6);
            display: flex;
            align-items: center;
            justify-content: center;
            font-weight: 700;
            font-size: 20px;
            color: #fff;
            box-shadow: 0 0 15px rgba(16, 185, 129, 0.5);
        }
        .header-info h2 {
            font-size: 18px;
            font-weight: 600;
            color: #f8fafc;
            display: flex;
            align-items: center;
            gap: 8px;
        }
        .header-info span.badge {
            font-size: 11px;
            padding: 3px 8px;
            border-radius: 12px;
            background: rgba(16, 185, 129, 0.2);
            color: #34d399;
            border: 1px solid rgba(52, 211, 153, 0.3);
            text-transform: uppercase;
            letter-spacing: 0.5px;
        }
        .header-info p {
            font-size: 13px;
            color: #94a3b8;
            margin-top: 2px;
        }
        .chat-messages {
            flex: 1;
            padding: 24px;
            overflow-y: auto;
            display: flex;
            flex-direction: column;
            gap: 18px;
        }
        .chat-messages::-webkit-scrollbar {
            width: 6px;
        }
        .chat-messages::-webkit-scrollbar-thumb {
            background: rgba(255, 255, 255, 0.1);
            border-radius: 3px;
        }
        .message {
            display: flex;
            gap: 12px;
            max-width: 80%;
            animation: fadeIn 0.3s ease;
        }
        @keyframes fadeIn {
            from { opacity: 0; transform: translateY(10px); }
            to { opacity: 1; transform: translateY(0); }
        }
        .message.user {
            align-self: flex-end;
            flex-direction: row-reverse;
        }
        .message.assistant {
            align-self: flex-start;
        }
        .msg-bubble {
            padding: 14px 18px;
            border-radius: 18px;
            font-size: 15px;
            line-height: 1.5;
            word-wrap: break-word;
        }
        .message.user .msg-bubble {
            background: linear-gradient(135deg, #10b981 0%, #059669 100%);
            color: #ffffff;
            border-bottom-right-radius: 4px;
            box-shadow: 0 4px 12px rgba(16, 185, 129, 0.2);
        }
        .message.assistant .msg-bubble {
            background: rgba(30, 41, 59, 0.8);
            color: #f1f5f9;
            border: 1px solid rgba(255, 255, 255, 0.08);
            border-bottom-left-radius: 4px;
            box-shadow: 0 4px 12px rgba(0, 0, 0, 0.2);
        }
        .chat-input-area {
            padding: 20px 24px;
            background: rgba(30, 41, 59, 0.5);
            border-top: 1px solid rgba(255, 255, 255, 0.08);
            border-bottom-left-radius: 24px;
            border-bottom-right-radius: 24px;
            display: flex;
            gap: 12px;
        }
        input[type="text"] {
            flex: 1;
            background: rgba(15, 23, 42, 0.8);
            border: 1px solid rgba(255, 255, 255, 0.12);
            border-radius: 14px;
            padding: 14px 20px;
            color: #f8fafc;
            font-family: inherit;
            font-size: 15px;
            outline: none;
            transition: border-color 0.2s, box-shadow 0.2s;
        }
        input[type="text"]:focus {
            border-color: #10b981;
            box-shadow: 0 0 10px rgba(16, 185, 129, 0.3);
        }
        button.send-btn {
            background: linear-gradient(135deg, #10b981, #059669);
            color: white;
            border: none;
            border-radius: 14px;
            padding: 0 24px;
            font-weight: 600;
            font-size: 15px;
            cursor: pointer;
            transition: transform 0.1s, box-shadow 0.2s;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        button.send-btn:hover {
            transform: translateY(-1px);
            box-shadow: 0 4px 15px rgba(16, 185, 129, 0.4);
        }
        button.send-btn:disabled {
            opacity: 0.5;
            cursor: not-allowed;
        }
        .typing {
            display: none;
            align-self: flex-start;
            gap: 6px;
            padding: 14px 18px;
            background: rgba(30, 41, 59, 0.8);
            border-radius: 18px;
            border: 1px solid rgba(255, 255, 255, 0.08);
        }
        .dot {
            width: 8px;
            height: 8px;
            background: #10b981;
            border-radius: 50%;
            animation: pulse 1.2s infinite ease-in-out;
        }
        .dot:nth-child(2) { animation-delay: 0.2s; }
        .dot:nth-child(3) { animation-delay: 0.4s; }
        @keyframes pulse {
            0%, 80%, 100% { transform: scale(0.6); opacity: 0.4; }
            40% { transform: scale(1); opacity: 1; }
        }
    </style>
</head>
<body>
    <div class="bg-glow"></div>
    <div class="chat-container">
        <div class="chat-header">
            <div class="avatar">Ru</div>
            <div class="header-info">
                <h2>Ru <span class="badge">Llama 3.1 8B v6</span></h2>
                <p>Bilingual AI Assistant</p>
            </div>
        </div>
        <div class="chat-messages" id="messages">
            <div class="message assistant">
                <div class="msg-bubble">
                    Xin chào! Ru sẵn sàng trò chuyện cùng bạn.
                </div>
            </div>
        </div>
        <div class="typing" id="typing">
            <div class="dot"></div>
            <div class="dot"></div>
            <div class="dot"></div>
        </div>
        <div class="chat-input-area">
            <input type="text" id="userInput" placeholder="Nhập tin nhắn..." autocomplete="off">
            <button class="send-btn" id="sendBtn" onclick="sendMessage()">Gửi</button>
        </div>
    </div>

    <script>
        const history = [
            ["system", `{{ system_prompt }}`]
        ];

        const messagesContainer = document.getElementById('messages');
        const userInput = document.getElementById('userInput');
        const sendBtn = document.getElementById('sendBtn');
        const typing = document.getElementById('typing');

        userInput.addEventListener('keypress', function(e) {
            if (e.key === 'Enter') {
                sendMessage();
            }
        });

        async function sendMessage() {
            const text = userInput.value.trim();
            if (!text) return;

            // Add user message
            appendMessage('user', text);
            history.push(["user", text]);
            userInput.value = '';

            userInput.disabled = true;
            sendBtn.disabled = true;
            typing.style.display = 'flex';
            messagesContainer.scrollTop = messagesContainer.scrollHeight;

            try {
                const response = await fetch('/api/chat', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ history: history })
                });

                const data = await response.json();
                if (data.reply) {
                    appendMessage('assistant', data.reply);
                    history.push(["assistant", data.reply]);
                } else {
                    appendMessage('assistant', 'Lỗi: Không nhận được phản hồi từ model.');
                }
            } catch (err) {
                appendMessage('assistant', 'Lỗi kết nối: ' + err.message);
            } finally {
                userInput.disabled = false;
                sendBtn.disabled = false;
                typing.style.display = 'none';
                userInput.focus();
                messagesContainer.scrollTop = messagesContainer.scrollHeight;
            }
        }

        function appendMessage(role, text) {
            const msgDiv = document.createElement('div');
            msgDiv.className = `message ${role}`;

            const bubble = document.createElement('div');
            bubble.className = 'msg-bubble';
            bubble.innerText = text;

            msgDiv.appendChild(bubble);
            messagesContainer.appendChild(msgDiv);
            messagesContainer.scrollTop = messagesContainer.scrollHeight;
        }
    </script>
</body>
</html>
"""

@app.route("/")
def index():
    return render_template_string(HTML_TEMPLATE, system_prompt=RU_SYSTEM_PROMPT.replace("\n", " "))

@app.route("/api/chat", methods=["POST"])
def api_chat():
    data = request.get_json() or {}
    raw_history = data.get("history", [])
    history = [tuple(item) for item in raw_history]

    if not history or history[-1][0] != "user":
        return jsonify({"error": "Invalid history"}), 400

    response = generate_llama_response_hf(BASE_MODEL, tuple(history), ADAPTER_DIR)
    return jsonify({"reply": response})

if __name__ == "__main__":
    print("=== Launching Ru Llama 3.1 8B Web GUI at http://127.0.0.1:5000 ===")
    app.run(host="127.0.0.1", port=5000, debug=False)
