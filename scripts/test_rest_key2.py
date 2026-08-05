import os
import requests

api_key = os.environ.get("GEMINI_KEY")
# Test with gemini-2.0-flash which is in the list
url = f'https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={api_key}'
r = requests.post(url, json={'contents': [{'parts': [{'text': 'Say hello'}]}]})
print('Status:', r.status_code)
if r.status_code == 200:
    data = r.json()
    text = data['candidates'][0]['content']['parts'][0]['text']
    print('Response:', text)
else:
    print('Response:', r.text[:500])