import os
import requests

api_key = os.environ.get("GEMINI_KEY")
url = f'https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={api_key}'
r = requests.post(url, json={'contents': [{'parts': [{'text': 'Hello'}]}]})
print('Status:', r.status_code)
print('Response:', r.text[:500])