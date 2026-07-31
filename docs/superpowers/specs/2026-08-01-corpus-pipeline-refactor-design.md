# Design — Corpus Pipeline Refactor (Phase 1)

- Ngày: 2026-08-01
- Trạng thái: Approved (chủ nhân duyệt phần 1 + phần 2)
- Phạm vi: `src/mini_gpt/corpus*`, `scripts/build_*`, README/docs liên quan

## 1. Bối cảnh

Dự án có 5 builder dữ liệu huấn luyện (`ru_v6_dialogues.py`, `ru_v6_1_dialogues.py`, `ru_v6_2_dialogues.py`, `ru_v6_2_fresh_dialogues.py`, `ru_v6_3_dialogues.py`, tổng ~2.500 dòng) cùng các validator (`ru_v6_1_corpus.py`, `ru_v6_2_corpus.py`, `ru_v6_2_fresh_corpus.py`, `ru_v6_3_corpus.py`) và 6 script build (`scripts/build_ru_llama_v6*_data.py`) trùng lặp logic nặng:

- `ru_v6_2_dialogues.py` là bản gần như nguyên văn của `ru_v6_1_dialogues.py` (chỉ khác ~5 vị trí chuỗi + `_MIXED_*` + `_response` variant).
- Cặp validator v6.1/v6.2 trùng ~90%.
- `build_ru_llama_v6_2_data.py` copy nguyên bản script v6.1.

## 2. Mục tiêu & ràng buộc

**Mục tiêu:** giảm ~1.500-1.800 dòng trùng lặp, một CLI build duy nhất, một validator tham số hóa, mỗi version vẫn là module độc lập đọc được.

**Ràng buộc bắt buộc (byte-for-byte):**
- Output JSONL phải GIỮ NGUYÊN từng byte so với `data/conversations/<version>/` hiện có: cùng seed (42/62/63), cùng thứ tự sinh, cùng dict order → SHA256 của train/val/held_out/manifest không đổi.
- Data đã train trên đó; không được thay đổi.

## 3. Phạm vi

### 3.1 File map

| File | Hành động | Nội dung |
|---|---|---|
| `src/mini_gpt/corpus_common.py` | Mới | Shared machinery trích từ v6.1/v6.2 (giống hệt): `Scene`+`_SCENES` (48 scenes), `_VI/_EN/_MIXED_TIMES`, `_STANCES`, quota constants (`EXPECTED_LANGUAGE_COUNTS`, `EXPECTED_MODE_COUNTS`, `EXPECTED_INTENSITY_COUNTS`, `EXPECTED_TURN_BAND_COUNTS`, `_LANGUAGE_MODE_COUNTS`), `_record`, `_turn_count`, `_build_slots`, `_build_rows`, `_history_user`, `_history_assistant`, `_final_prompt`, `pair_key` re-export |
| `src/mini_gpt/ru_v6_1_dialogues.py` | Giảm ~400 dòng | Giữ nội dung prompt riêng (ACKS/DETAILS/QUESTIONS, FRAMES/TAILS, `_MIXED_*` v6.1, `_response_anchor`/`_context_prefix`/`_finish_response`), import machinery từ `corpus_common` |
| `src/mini_gpt/ru_v6_2_dialogues.py` | Giảm ~400 dòng | Giữ diff v6.2 (frames/tails "tự nhiên/natural", `_MIXED_*` code-switching, `_response` ngắn không anchor, regex tự xưng không lookbehind) + machinery chung |
| `src/mini_gpt/ru_v6_dialogues.py` | Không đụng | Deprecated (NSFW, 6 mode cũ, không intensity/turn_band) — chỉ đi qua CLI mới |
| `src/mini_gpt/corpus_validate.py` | Mới | `normalize_row(...)` + `build_corpus(...)` tham số hóa (flags bảng dưới) |
| `src/mini_gpt/ru_v6_1_corpus.py` | Thin wrapper | Gọi `build_corpus(records, variant="v6.1", ...)`; giữ tên hàm `build_ru_v6_1_corpus` |
| `src/mini_gpt/ru_v6_2_corpus.py` | Thin wrapper | Tương tự variant="v6.2" |
| `src/mini_gpt/ru_v6_2_fresh_corpus.py` | Thin wrapper hoặc giữ riêng | Xem §4.3 điều khoản dự phòng |
| `src/mini_gpt/ru_v6_3_corpus.py` | Thin wrapper hoặc giữ riêng | Xem §4.3 điều khoản dự phòng |
| `scripts/build_corpus.py` | Mới | CLI `--version {v6,v6_1,v6_2,v6_2_fresh,v6_3}` + giữ nguyên flag cũ |
| 6 script `scripts/build_ru_llama_v6*_data.py` | Xóa | Thay bằng `build_corpus.py`; cập nhật README/docs tham chiếu |
| `scripts/verify_corpus_byte_identical.py` | Mới | Gate SHA256 (xem §5) |
| `tests/test_corpus_byte_identical.py` | Mới | Chạy gate; skip-if-missing khi thiếu data |

### 3.2 Không đụng

- `ru_v6_2_fresh_import.py`, `approved_v6_1_import.py`, `scripts/merge_approved_into_ru_v6_1.py` (test neo tên hàm).
- `configs/ru_llama3_1_hard_eval.json` (config adapters v6/v6.1/v6.2).
- `data/`, nội dung prompt/seed của mọi version.
- GUI/VTS/LLM runtime, `ru_style.py`, `_base_corpus.py`, `_text_utils.py`, `_corpus_quality.py`, `_safety_patterns.py` (chỉ re-export nếu cần).

## 4. Chi tiết thiết kế

### 4.1 `corpus_validate.py` — tham số hóa

`build_corpus(records, *, variant, system_prompt_fn, allow_approved=False, naturalness_gate=False, casefold_prompts=False, profanity_counts=False, splits, seed, write=True, ...)`

| Version | system_prompt_fn | allow_approved | naturalness_gate | casefold_prompts | profanity_counts | splits | seed |
|---|---|---|---|---|---|---|---|
| v6.1 | hằng `RU_SYSTEM_PROMPT` | True | True | True | False | (1680,360,360) | 42 |
| v6.2 | hằng `RU_SYSTEM_PROMPT` | False | False | False | False | (1680,360,360) | 42 |
| v6.2_fresh | `ru_system_prompt_for` | False | False | False | False | (720,240,240) | 62 |
| v6.3 | `ru_system_prompt_for` | False | False | False | True | (108,36,36) | 63 |

- `normalize_row` nhận `system_prompt_fn` để giữ hành vi prompt theo ngôn ngữ (fresh/v6.3) vs cố định (v6.1/v6.2).
- Report keys giữ nguyên tên: `authored_total_examples`, `quality_gates.*` (gồm `training_started`), `profanity_counts`, `sha256` — mọi test hiện có không vỡ.
- Phần khác nhau còn lại của mỗi version (v6.1: approved row + naturalness audit + casefold check; v6.2: không casefold; fresh: mode import; v6.3: profanity determinism) đều được mã hóa bằng flags — không đổi logic hiện tại.

### 4.2 CLI `build_corpus.py`

```
python scripts/build_corpus.py --version v6_1
python scripts/build_corpus.py --version v6_2 --audit
python scripts/build_corpus.py --version v6_2_fresh --approved-file <f> --memory-file <f>
python scripts/build_corpus.py --version v6_3 --audit-only
```

- Map version → builder function; không có `--version` → in danh sách + exit 2.
- `verify_corpus_byte_identical.py` dùng chung code path (builder với `write=False`/temp dir) tránh drift.

### 4.3 Điều khoản dự phòng (fresh/v6.3)

Nếu trong lúc implement, fresh hoặc v6.3 lệch logic khỏi core quá 30% (khác cơ chế quota/import/field), thì GIỮ NGUYÊN 2 file đó như hiện tại — chỉ trích constants chung. Không bắt buộc gộp bằng mọi giá. Tiêu chí "giảm dòng" ưu tiên thấp hơn "đúng byte-for-byte + đọc được".

## 5. Gate bảo toàn (tiêu chí thành công chính)

`scripts/verify_corpus_byte_identical.py`:
- Với mỗi version có data trong `data/conversations/<version>/`: build vào temp, so SHA256 của `train.jsonl`, `val.jsonl`, `held_out.jsonl`, `manifest.json` với data hiện có.
- Mọi file khớp → PASS; bất kỳ lệch nào → FAIL + in chi tiết (version, file, hash cũ/mới).
- Exit code 0/1 để dùng trong CI và gate thủ công trước mỗi commit.

## 6. Sequence thực hiện

1. Tạo `corpus_common.py` từ v6.1 → gate v6.1 PASS.
2. Chuyển v6.2 sang common → gate v6.2 PASS.
3. Gộp validator → gate v6.1 + v6.2 PASS.
4. Fresh + v6.3 dùng core (hoặc dự phòng §4.3) → gate từng version PASS.
5. `build_corpus.py` + xóa 6 script cũ + cập nhật README/docs.
6. `verify_corpus_byte_identical.py` + `tests/test_corpus_byte_identical.py`.
7. Full 152 test xanh + gate toàn bộ 5 version + báo cáo số dòng giảm.

Mỗi bước commit riêng (conventional: `refactor(corpus):`); gate phải PASS trước khi sang bước kế.

## 7. Testing strategy

- Gate byte-identical (SHA256 4 file × 5 version) = tiêu chí thành công chính.
- 152 test hiện có giữ nguyên xanh (wrapper giữ tên hàm/key report).
- `tests/test_corpus_byte_identical.py`: chạy gate với data có sẵn; `skipif` khi thiếu data (máy khác/CI).
- Test bổ sung: CLI exit codes + danh sách version (`--help`, thiếu `--version` → exit 2).

## 8. Non-goals (không làm trong phase này)

- Không đổi nội dung prompt/data/seed của bất kỳ version nào.
- Không refactor `ru_v6_dialogues.py` (deprecated), import helpers, configs.
- Không đụng GUI/VTS/LLM runtime.
- Không gộp các version khác nhau về nội dung (fresh, v6.3) vào một builder.

## 9. Rủi ro & giảm thiểu

| Rủi ro | Giảm thiểu |
|---|---|
| Thay đổi thứ tự sinh → phá byte-for-byte | Gate SHA256 sau MỖI bước; seed/thứ tự vòng lặp không đổi; dict order giữ nguyên (không đổi sang sorted/insertion khác) |
| Python version/rounding thay đổi output | Gate so với data hiện có ngay trên máy dev — bắt lệch tức thì |
| Gộp validator làm mất hành vi riêng v6.1 (approved/naturalness) | Flags tường minh + giữ test v6.1 hiện có (không có test riêng cho v6.1/v6.2 — bổ sung test snapshot qua gate) |
| Điều khoản dự phòng bị lạm dụng | Áp dụng đúng tiêu chí §4.3, ghi rõ lý do trong commit |

## 10. Verification results (2026-08-01)

- **Gate byte-identical:** 4× `[PASS]` (v6_1, v6_2, v6_2_fresh, v6_3) + `REFACTOR-NEUTRAL-OK`, exit 0. v6 KHÔNG nằm trong gate: data v6 được build trước khi thêm check "default em/anh voice" (có sẵn tại 43b023f, `ru_v6_corpus.py:67`), rebuild fail pre-existing — không phải lỗi refactor, giữ nguyên deprecated.
- **Test suite:** 161 passed (152 gốc + 4 gate + 5 CLI), 2 deprecation warnings từ thư viện (aifc, pkg_resources).
- **compileall:** `src/mini_gpt` + `scripts` — exit 0.
- **Smoke CLI mới:** v6_1/v6_2 (`--audit`), v6_2_fresh/v6_3 (`--audit-only`) — exit 0. v6 `--audit` fail vì lý do trên.
- **Giảm dòng (43b023f..HEAD, 14 file pipeline):** +1052/−1629 → net **−577 dòng** (khớp ước tính ~600 trong plan Task 7 Step 3; ước 1.500-1.800 trong §2 không đạt vì fresh/v6.3 giữ nguyên theo §4.3 — đúng quy trình, ưu tiên byte-for-byte).
- **Commits:** a4657fb (gate), b00e7c8 (corpus_common), 4863c57 (v6.2 dialogues), f46c263 (corpus_validate), 5fb99de (v6.2 wrapper), 582ed8c (CLI + xóa 5 script), 00eaadf (Round 3 fixes đang chờ — commit riêng theo yêu cầu chủ nhân).
