import requests

payload = {
    "voice_label": "Ái Vy (OmniVoice)",
    "voice_speed": 1.2,
    "auto_pace_enabled": False,
    "auto_read_replies": False
}

try:
    response = requests.post("http://127.0.0.1:8000/settings", json=payload)
    print("Status code:", response.status_code)
    print("Response body:", response.text)
except Exception as e:
    print(e)
