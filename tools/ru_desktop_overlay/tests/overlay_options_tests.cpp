#include "overlay_options.h"
#include "window_styles.h"

#include <cassert>

int main() {
    const char* defaults[] = {"RuDesktopOverlay"};
    const auto normal = ParseOptions(1, defaults);
    assert(normal.sender_name == "VTubeStudioSpout");
    assert(!normal.click_through);

    const char* custom[] = {"RuDesktopOverlay", "--sender", "RuPreview", "--click-through"};
    const auto selected = ParseOptions(4, custom);
    assert(selected.sender_name == "RuPreview");
    assert(selected.click_through);

    assert((OverlayExStyle(false) & WS_EX_TRANSPARENT) == 0);
    assert((OverlayExStyle(true) & WS_EX_TRANSPARENT) != 0);
    return 0;
}
