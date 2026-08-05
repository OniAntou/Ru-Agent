import os
import requests

api_key = os.environ.get("GEMINI_KEY")
url = f'https://generativelanguage.googleapis.com/v1beta/models?key={api_key}'
r = requests.get(url)
print('Status:', r.status_code)
print('Response:', r.text[:2000])