# Design — TUI: `/reasoning` + provider động + bộ lọc picker

Ngày: 2026-09-25
Nhánh: `feat/tui-provider-and-reasoning`
Trạng thái: đã duyệt, chưa implement

## Bối cảnh

TUI (`backend-dotnet/RuAgent.Cli`) đã có `/key`, `/backend`, `/model`. So với GUI
(`gui-winui3`) còn thiếu ba thứ, trong đó hai thứ đã chốt làm:

1. **Đặt reasoning effort.** GUI có dropdown chọn mức suy luận theo từng model
   (`gui-winui3/Views/ChatView.Models.cs:327`). TUI chỉ *đọc* `reasoning_effort` để
   hiện ở `backend-dotnet/RuAgent.Cli/AgentCommands.cs:56`, không đặt được.
2. **Danh sách provider động.** TUI hardcode 7 dòng tại
   `backend-dotnet/RuAgent.Cli/Tui/TuiSurfaces.cs:111`. GUI gọi `GET /models-dev` nên
   thấy hết provider. Máy đo được **190 provider**.
3. **Bộ lọc picker.** Không nằm trong danh sách ban đầu nhưng bắt buộc: picker chỉ có
   ↑↓ + phím số, không lọc, nên 190 dòng là vô dùng. Picker model đã dính đúng bệnh này
   (458 model OpenRouter).

**Không làm:** provider tuỳ chỉnh (`custom_url` / `custom_model`) — đã thốt làm sau.

## Ràng buộc

- Không sửa file nào trong `gui-winui3/`. Thêm method mới vào `RuAgent.Client` được
  phép vì đó là thư viện dùng chung, không phải mã GUI.
- Không đổi Public Contract, không thêm route. Hai route dùng ở đây
  (`GET /models-dev`, `POST /settings`) đã có sẵn trong
  `backend-dotnet/public-route-contract.json` và không bị test nào ràng buộc danh sách
  `winui_callers`.
- Fail-closed: provider list lấy lỗi thì có fallback, không để người dùng mất đường
  vào, nhưng không được bịa thêm provider ngoài danh sách đã biết.

---

## Phần 1 — `/reasoning`

### Dữ liệu (đã có sẵn, không cần endpoint mới)

`GET /models?backend=<backend hiện tại>` trả về:

| Field | Ý nghĩa |
|---|---|
| `identity` | mã định danh của snapshot catalog, dùng làm optimistic token |
| `selected_model` | model đang chạy |
| `selected_effort` | mức đang dùng, `"default"` nếu chưa đặt |
| `catalog[]`, `models[]` | danh sách model, mỗi phần tử kèm `reasoning.state` và `reasoning.options` |

Đo trên máy thật: `catalog` và `models` cùng 458 phần tử nhưng model đang chọn
chỉ nằm trong `catalog`. Nên **dò cả hai mảng**, lấy phần tử đầu tiên tìm thấy theo
`selected_model` — không được chỉ tin một mảng.

`reasoning.state` có 4 giá trị: `levels` (có mức để chọn), `toggle` (bật/tắt),
`unsupported`, `unknown`.

### Hành vi

Quy ước bám đúng `/backend` (`AgentSlashCommands.cs:191-219`): **dạng có đối số thì ghi
được không cần terminal**, chỉ dạng không có đối số mới cần picker.

| Gọi | Không tương tác | Có tương tác |
|---|---|---|
| `/reasoning` | in mức đang dùng + danh sách hợp lệ (dạng panel) | mở picker nếu model có option, ngược lại in giải thích |
| `/reasoning <mức>` | đặt luôn, in kết quả | đặt luôn |

- Picker gồm `default` **luôn** (contract yêu cầu effort khác `default` phải resolve
  được trong catalog, nên không thêm mức lạ), cộng đúng các `reasoning.options` của
  model hiện tại. Mức đang dùng được mark.
- `state` là `toggle` thì options thường là `enabled` / `disabled` — vẫn hiện bình
  thường.
- `state` là `unknown` / `unsupported`, hoặc `options` rỗng: in
  "model này không có mức suy luận để chọn" kèm tên model, **không** mở picker.
- Gõ sai tên mức: in danh sách hợp lệ, không ghi gì.

### Lưu

`POST /settings` với **đúng 3 field**, không thêm không bớt — `SettingsTransitionService.cs:59-70`
ném `SettingsPayloadException` nếu `payload.Count != 3`:

```json
{ "reasoning_effort": "<mức>", "reasoning_model": "<selected_model>", "reasoning_identity": "<identity>" }
```

`reasoning_model` + `reasoning_identity` phải khớp đúng snapshot đã dùng để vẽ picker.
Backend tự resolve và từ chối mức mà model không hỗ trợ, nên TUI không cần tự kiểm tra
lần hai — nhưng phải gửi đúng token, nếu không mọi lần lưu đều fail.

Sau khi lưu thành công thì cập nhật lại mức đang hiện. Không tự refetch catalog: giữ
nguyên hành vi mà GUI cũng giữ, tránh picker tụt về trạng thái chỉ-có-`default`.

### Lỗi

| Tình huống | Hiển thị |
|---|---|
| `GET /models` fail | một dòng `reasoning_unavailable: …`, không mở picker |
| lưu bị từ chối (token lệch) | in mức đang dùng thật + một dòng giải thích, không mất cấu hình cũ |
| `Esc` ở picker | im lặng, giữ nguyên |

---

## Phần 2 — provider động

### API mới trên client

`SettingsApiClient.GetModelsDevAsync(ct)` → `IReadOnlyList<(string Id, string Name)>`
từ `GET /models-dev`, response:

```json
{ "status": "ok", "providers": [ { "id": "openrouter", "name": "OpenRouter", "base_url": "…" } ] }
```

Có bound đọc body giống `GetModelsAsync`; provider không có `id` bị bỏ; `id` phải là
token an toàn vì nó đi thẳng vào giá trị `backend_choice` khi chọn. `base_url` **không**
đưa vào TUI — chỉ cần `id` + `name`.

### `BackendChoices`

Tham số mới: `IReadOnlyList<(string Id, string Name)>? providers`. Thứ tự:

1. Ba provider gốc `openai`, `anthropic`, `custom` (giữ nguyên, không phải models.dev)
2. provider đang chọn (nếu là models.dev)
3. provider đã có key, theo `GET /settings/models-dev-keys`
4. còn lại A-Z theo `id`

Duyệt trùng: một id chỉ xuất hiện một lần, ưu tiên vị trí sớm nhất.

Mảng hardcode 7 dòng hiện tại thành **fallback**. Khi `GET /models-dev` lỗi hoặc rỗng:
dùng lại mảng cũ và in một dòng cảnh báo, không để picker trống.

Footer picker hiện số provider và số đã đăng nhập, theo đúng kiểu footer của picker model.

### `/backend <tên>`

- `models.dev:<id>` và `<id>` rút gọn vẫn nhận như hiện tại.
- Gõ sai id: in tên gần giống nhất, không đụng cấu hình cũ.

---

## Phần 3 — bộ lọc picker

### Hành vi

Trong chế độ chọn (picker), phím chữ in được bắt đầu **lọc** thay vì bỏ qua:

| Phím | Việc |
|---|---|
| chữ cái, `-`, `_`, `.` | nối vào bộ lọc, dựng lại list đã lọc, vẽ lại |
| chữ số `1`–`9` | **giữ nguyên** phím chọn nhanh dòng thứ N — không dùng để lọc |
| `Backspace` | xoá 1 ký tự cuối bộ lọc; bộ lọc rỗng thì xoá như trước |
| `↑` `↓` | di chuyển trong list **đã lọc** |
| `Enter` | chọn dòng đang highlight |
| `Esc` | huỷ, giống trước |

Chữ số **cố ý không lọc**: phím số chọn nhanh là hành vi đã có, đã có test, và trùng với
ký tự trong id model (`llama-3`, `qwen3`). Lọc bằng chữ cái vẫn tới được mọi id có chữ;
id chỉ toàn số thì tì bằng `/backend <tên>` có đối số.

Lọc là substring không phân biệt hoa thường trên `Label` + `Detail` + `Value`.
Bộ lọc rỗng thì hành vi **y hệt hiện tại** — nên không picker nào cũ đổi hành vi.
Bộ lọc áp cho **mọi** picker, gồm cả picker model (458 dòng) vốn đang không lọc.

Footer hiện `filter: groq · 3/190`; khi rỗng thì chỉ hiện tổng số.

### Giới hạn

- Danh sách gốc rỗng (ví dụ `/models-dev` lỗi **và** fallback cũng rỗng): in
  `provider_list_unavailable` kèm gợi ý dùng `/backend <tên>` có đối số.
- Bộ lọc không khớp gì: hiện một dòng "không có dòng nào khớp `<bộ lọc>`" thay vì list
  rỗng, `Enter` khi đó không làm gì.
- Bộ lọc không bền qua lần mở picker sau (mỗi lần mở là mới), để trạng thái không
  dính vào nhau.

---

## Kiểm thử

| Tầng | Nội dung |
|---|---|
| Client | `GetModelsDevAsync` parse đúng; bỏ provider thiếu `id`; bound; lỗi HTTP |
| Surface | `BackendChoices`: thứ tự 4 nhóm, dedupe, giữ 3 provider gốc, mark key, rỗng thì fallback |
| Surface | `ReasoningChoices`: luôn có `default`, chỉ option thật, rỗng thì trả list rỗng |
| Handler | `/reasoning` gửi **đúng 3** field với identity/model của snapshot; `Esc` không POST; dạng không có arg khi không tương tác in panel chứ không mở picker; dạng có arg khi không tương tác vẫn POST; model rỗng option thì không mở picker |
| Handler | `/backend` fallback khi `/models-dev` lỗi |
| Picker | lọc substring; `Backspace`; lọc rỗng thì hành vi cũ; lọc không khớp; con trỏ clamp khi list thu nhỏ |

Chạy `RuAgent.Cli.Tests`, `RuAgent.Client.Tests`, `gui-winui3.Tests` (bảo đảm contract
test đọc source GUI không vỡ). Không sửa `gui-winui3/`.

## Ngoài phạm vi

- Provider tuỳ chỉnh (`custom_url` / `custom_model`).
- Mở rộng `ProviderCatalogParser` để hiểu format reasoning của OpenRouter. Đã đo:
  cả 458 model OpenRouter ra `state=unknown`, 0 option, vì parser (`ProviderCatalogParser.cs:53-55`)
  cần cả `supported_parameters` chứa `reasoning_effort` lẫn mảng `reasoning_efforts` —
  đúng format OpenAI. OpenRouter không có `reasoning_efforts`. Nên với provider hiện
  tại `/reasoning` sẽ luôn báo không có mức để chọn; nó sống được với OpenAI, Anthropic
  và gateway nói đúng format OpenAI.
- Vụ model không chịu gọi tool (native vs markup contract ở
  `WorkerV2StepService.cs:105` và `SystemPromptComposer.cs:98`). Đã biết là hướng sửa
  khả dĩ nhưng nằm ngoài spec này.
