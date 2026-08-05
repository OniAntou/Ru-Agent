import sys
sys.path.append(r'D:\LLM\src\mini_gpt')
from voice_runtime import _SELECTIONS_BY_LABEL
for key in _SELECTIONS_BY_LABEL.keys():
    print(key.encode('utf-8'))
