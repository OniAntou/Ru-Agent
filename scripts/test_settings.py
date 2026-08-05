import sys, json
sys.path.append(r'D:\LLM\src\mini_gpt')
from server import load_settings
settings = load_settings()
print(repr(settings.get("voice_label")))
print(settings.get("voice_label").encode('utf-8'))
