#include "overlay_options.h"
#include "spout_overlay.h"
#include "window_styles.h"

#include <shellapi.h>
#include <windows.h>

#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
constexpr wchar_t kWindowClass[] = L"RuDesktopOverlayWindow";
constexpr UINT_PTR kRenderTimer = 1;

std::string ToUtf8(const wchar_t* value) {
    if (!value) return {};
    const int required = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (required <= 1) return {};
    std::string result(static_cast<std::size_t>(required), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), required, nullptr, nullptr);
    result.pop_back();
    return result;
}

class LayeredFrame {
public:
    ~LayeredFrame() {
        if (memory_dc_ && original_bitmap_) SelectObject(memory_dc_, original_bitmap_);
        if (bitmap_) DeleteObject(bitmap_);
        if (memory_dc_) DeleteDC(memory_dc_);
        if (screen_dc_) ReleaseDC(nullptr, screen_dc_);
    }

    bool Present(HWND window, const std::vector<unsigned char>& pixels, unsigned int width, unsigned int height) {
        if (pixels.empty() || width == 0 || height == 0) return false;
        if (!EnsureBitmap(width, height)) return false;
        std::memcpy(bits_, pixels.data(), pixels.size());

        RECT window_rect{};
        GetWindowRect(window, &window_rect);
        POINT position{window_rect.left, window_rect.top};
        SIZE size{static_cast<LONG>(width), static_cast<LONG>(height)};
        POINT source{0, 0};
        BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
        return UpdateLayeredWindow(window, screen_dc_, &position, &size, memory_dc_, &source, 0, &blend, ULW_ALPHA) != FALSE;
    }

    bool PresentStatus(HWND window, const std::string& status) {
        constexpr unsigned int status_width = 420;
        constexpr unsigned int status_height = 96;
        if (!EnsureBitmap(status_width, status_height)) return false;

        auto* pixels = static_cast<unsigned char*>(bits_);
        for (std::size_t index = 0; index < static_cast<std::size_t>(status_width) * status_height; ++index) {
            const auto offset = index * 4;
            pixels[offset] = 32;
            pixels[offset + 1] = 32;
            pixels[offset + 2] = 32;
            pixels[offset + 3] = 255;
        }
        SetBkMode(memory_dc_, TRANSPARENT);
        SetTextColor(memory_dc_, RGB(245, 245, 245));
        RECT text_rect{20, 20, static_cast<LONG>(status_width - 20), static_cast<LONG>(status_height - 20)};
        DrawTextA(memory_dc_, status.c_str(), -1, &text_rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);

        RECT window_rect{};
        GetWindowRect(window, &window_rect);
        POINT position{window_rect.left, window_rect.top};
        SIZE size{static_cast<LONG>(status_width), static_cast<LONG>(status_height)};
        POINT source{0, 0};
        BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
        return UpdateLayeredWindow(window, screen_dc_, &position, &size, memory_dc_, &source, 0, &blend, ULW_ALPHA) != FALSE;
    }

private:
    bool EnsureBitmap(unsigned int width, unsigned int height) {
        if (width == width_ && height == height_ && bitmap_) return true;
        if (!screen_dc_) screen_dc_ = GetDC(nullptr);
        if (!memory_dc_) memory_dc_ = CreateCompatibleDC(screen_dc_);
        if (!screen_dc_ || !memory_dc_) return false;

        if (bitmap_) {
            SelectObject(memory_dc_, original_bitmap_);
            DeleteObject(bitmap_);
            bitmap_ = nullptr;
        }

        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = static_cast<LONG>(width);
        info.bmiHeader.biHeight = static_cast<LONG>(height);
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;
        bitmap_ = CreateDIBSection(screen_dc_, &info, DIB_RGB_COLORS, &bits_, nullptr, 0);
        if (!bitmap_) return false;
        original_bitmap_ = SelectObject(memory_dc_, bitmap_);
        width_ = width;
        height_ = height;
        return true;
    }

    HDC screen_dc_{nullptr};
    HDC memory_dc_{nullptr};
    HBITMAP bitmap_{nullptr};
    HGDIOBJ original_bitmap_{nullptr};
    void* bits_{nullptr};
    unsigned int width_{0};
    unsigned int height_{0};
};

struct AppState {
    explicit AppState(OverlayOptions overlay_options)
        : options(std::move(overlay_options)), receiver(options.sender_name), click_through(options.click_through) {}

    OverlayOptions options;
    SpoutOverlay receiver;
    LayeredFrame frame;
    bool click_through{false};
};

AppState* StateFor(HWND window) {
    return reinterpret_cast<AppState*>(GetWindowLongPtr(window, GWLP_USERDATA));
}

void SetClickThrough(HWND window, AppState& state, bool enabled) {
    state.click_through = enabled;
    SetWindowLongPtr(window, GWL_EXSTYLE, OverlayExStyle(enabled));
    SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0,
                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_NCCREATE) {
        const auto create = reinterpret_cast<CREATESTRUCT*>(lparam);
        SetWindowLongPtr(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
    }
    AppState* state = StateFor(window);

    switch (message) {
    case WM_NCHITTEST:
        if (state && state->click_through) return HTTRANSPARENT;
        return HTCAPTION;
    case WM_TIMER:
        if (state && state->receiver.Tick()) {
            state->frame.Present(window, state->receiver.Pixels(), state->receiver.Width(), state->receiver.Height());
        } else if (state) {
            state->frame.PresentStatus(window, state->receiver.Status());
        }
        return 0;
    case WM_KEYUP:
        if (!state) return DefWindowProc(window, message, wparam, lparam);
        if (wparam == 'C') SetClickThrough(window, *state, !state->click_through);
        if (wparam == 'T') SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (wparam == VK_ESCAPE) DestroyWindow(window);
        return 0;
    case WM_DESTROY:
        KillTimer(window, kRenderTimer);
        if (state) state->receiver.Stop();
        PostQuitMessage(0);
        return 0;
    default:
        return DefWindowProc(window, message, wparam, lparam);
    }
}

} // namespace

int APIENTRY wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    int argc = 0;
    LPWSTR* wide_args = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!wide_args) return 1;

    std::vector<std::string> storage;
    std::vector<const char*> args;
    storage.reserve(static_cast<std::size_t>(argc));
    args.reserve(static_cast<std::size_t>(argc));
    for (int index = 0; index < argc; ++index) {
        storage.push_back(ToUtf8(wide_args[index]));
        args.push_back(storage.back().c_str());
    }
    LocalFree(wide_args);

    OverlayOptions options;
    try {
        options = ParseOptions(argc, args.data());
    } catch (const std::invalid_argument& error) {
        MessageBoxA(nullptr, error.what(), "RuDesktopOverlay", MB_ICONERROR | MB_OK);
        return 1;
    }

    AppState state{std::move(options)};
    if (!state.receiver.Start()) {
        MessageBoxA(nullptr, state.receiver.Status().c_str(), "RuDesktopOverlay", MB_ICONERROR | MB_OK);
        return 1;
    }

    WNDCLASSW window_class{};
    window_class.hInstance = instance;
    window_class.lpfnWndProc = WindowProc;
    window_class.lpszClassName = kWindowClass;
    window_class.hCursor = LoadCursor(nullptr, IDC_SIZEALL);
    RegisterClassW(&window_class);

    HWND window = CreateWindowExW(
        OverlayExStyle(state.click_through), kWindowClass, L"RuDesktopOverlay",
        WS_POPUP, 200, 200, 640, 360, nullptr, nullptr, instance, &state);
    if (!window) return 1;

    SetWindowPos(window, HWND_TOPMOST, 200, 200, 640, 360, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    SetTimer(window, kRenderTimer, 16, nullptr);

    MSG message{};
    while (GetMessage(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessage(&message);
    }
    return static_cast<int>(message.wParam);
}
