import requests

response = requests.post("http://127.0.0.1:8000/voice/tts", json={
    "text": "Alo 1 2 3",
    "voice_label": "Ái Vy (OmniVoice)",
    "speed": 1.0
})
print("Ái Vy status:", response.status_code)
try:
    print(response.json())
except:
    pass

response2 = requests.post("http://127.0.0.1:8000/voice/tts", json={
    "text": "Alo 1 2 3",
    "voice_label": "Ru (OmniVoice)",
    "speed": 1.0
})
print("Ru status:", response2.status_code)
try:
    print(response2.json())
except:
    pass
