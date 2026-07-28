#include "window_styles.h"

LONG_PTR OverlayExStyle(bool click_through) {
    LONG_PTR value = WS_EX_LAYERED | WS_EX_TOOLWINDOW;
    if (click_through) {
        value |= WS_EX_TRANSPARENT;
    }
    return value;
}
