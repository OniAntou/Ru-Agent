from huggingface_hub import snapshot_download
import os

model_id = "NousResearch/Meta-Llama-3.1-8B-Instruct"
local_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "models", "llama3.1-8b-instruct-hf"))

print(f"Downloading {model_id} to {local_dir}...")
snapshot_download(
    repo_id=model_id,
    local_dir=local_dir,
    local_dir_use_symlinks=False,
    ignore_patterns=["*.msgpack", "*.h5", "*.ot", "onnx/*"]
)
print("Download completed successfully!")
