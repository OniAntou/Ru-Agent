from src.framing import JsonFrameDecoder

payload = b'{"message":"xin chao"}'
frame = len(payload).to_bytes(4, "big") + payload
decoder = JsonFrameDecoder()
assert decoder.feed(frame[:3]) == []
result = decoder.feed(frame[3:])[0]
assert result.value == {"message": "xin chao"} and result.error is None
assert decoder.finish() == []
print("smoke passed")
