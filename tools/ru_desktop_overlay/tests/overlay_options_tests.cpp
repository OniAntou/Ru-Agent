#include "overlay_options.h"
#include "frame_pixels.h"
#include "spout_overlay.h"
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

    unsigned char pixel[] = {100, 200, 50, 128};
    PremultiplyBgra(pixel, 1);
    assert(pixel[0] == 50);
    assert(pixel[1] == 100);
    assert(pixel[2] == 25);
    assert(pixel[3] == 128);

    unsigned char keyed_pixels[] = {0, 255, 0, 0, 255, 160, 40, 0};
    ApplyGreenScreenAlpha(keyed_pixels, 2);
    assert(keyed_pixels[3] == 0);
    assert(keyed_pixels[7] == 255);

    unsigned char rgba_pixel[] = {11, 22, 33, 44};
    ConvertRgbaToBgra(rgba_pixel, 1);
    assert(rgba_pixel[0] == 33);
    assert(rgba_pixel[1] == 22);
    assert(rgba_pixel[2] == 11);
    assert(rgba_pixel[3] == 44);

    unsigned char matted_edge[] = {4, 254, 4, 5};
    RemoveGreenMatteRgba(matted_edge, 1);
    assert(matted_edge[0] == 0);
    assert(matted_edge[1] == 0);
    assert(matted_edge[2] == 0);
    assert(matted_edge[3] == 0);

    SpoutOverlay unavailable{"RuDesktopOverlayTestMissingSender"};
    assert(unavailable.Start());
    assert(!unavailable.Tick());
    assert(unavailable.Status() == "Waiting for RuDesktopOverlayTestMissingSender");
    return 0;
}
