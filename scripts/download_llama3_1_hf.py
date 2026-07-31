from huggingface_hub import snapshot_download
import os
import sys

model_id = "NousResearch/Meta-Llama-3.1-8B-Instruct"
local_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "models", "llama3.1-8b-instruct-hf"))

print(f"Downloading {model_id} to {local_dir}...")
try:
    downloaded_dir = snapshot_download(
        repo_id=model_id,
        local_dir=local_dir,
        ignore_patterns=["*.msgpack", "*.h5", "*.ot", "onnx/*"],
    )
except Exception as error:
    print(f"Download failed: {type(error).__name__}: {error}", file=sys.stderr)
    raise SystemExit(1) from error
if not downloaded_dir or not os.path.isdir(downloaded_dir):
    print(f"Download did not produce a model directory: {downloaded_dir!r}", file=sys.stderr)
    raise SystemExit(1)
print(f"Download completed successfully: {downloaded_dir}")
