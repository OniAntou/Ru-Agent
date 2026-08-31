#include "overlay_options.h"
#include "frame_pixels.h"
#include "status_presentation.h"
#include "window_styles.h"

#include "test_check.h"

#include <string_view>

int main(int argc, const char* argv[]) {
    if (argc == 2 && std::string_view(argv[1]) == "--self-test-failure") {
        CHECK(false);
    }

    const char* defaults[] = {"RuDesktopOverlay"};
    const auto normal = ParseOptions(1, defaults);
    CHECK(normal.sender_name == "VTubeStudioSpout");
    CHECK(!normal.click_through);

    const char* custom[] = {"RuDesktopOverlay", "--sender", "RuPreview", "--click-through"};
    const auto selected = ParseOptions(4, custom);
    CHECK(selected.sender_name == "RuPreview");
    CHECK(selected.click_through);

    CHECK((OverlayExStyle(false) & WS_EX_TRANSPARENT) == 0);
    CHECK((OverlayExStyle(true) & WS_EX_TRANSPARENT) != 0);

    unsigned char pixel[] = {100, 200, 50, 128};
    PremultiplyBgra(pixel, 1);
    CHECK(pixel[0] == 50);
    CHECK(pixel[1] == 100);
    CHECK(pixel[2] == 25);
    CHECK(pixel[3] == 128);

    unsigned char keyed_pixels[] = {0, 255, 0, 0, 255, 160, 40, 0};
    ApplyGreenScreenAlpha(keyed_pixels, 2);
    CHECK(keyed_pixels[3] == 0);
    CHECK(keyed_pixels[7] == 255);

    unsigned char rgba_pixel[] = {11, 22, 33, 44};
    ConvertRgbaToBgra(rgba_pixel, 1);
    CHECK(rgba_pixel[0] == 33);
    CHECK(rgba_pixel[1] == 22);
    CHECK(rgba_pixel[2] == 11);
    CHECK(rgba_pixel[3] == 44);

    unsigned char matted_edge[] = {4, 254, 4, 5};
    RemoveGreenMatteRgba(matted_edge, 1);
    CHECK(matted_edge[0] == 0);
    CHECK(matted_edge[1] == 0);
    CHECK(matted_edge[2] == 0);
    CHECK(matted_edge[3] == 0);

    unsigned char pixel2[] = {100, 200, 50, 128};
    PrepareRgbaForOverlay(pixel2, 1);
    CHECK(pixel2[0] == 0 && pixel2[1] == 0 && pixel2[2] == 0 && pixel2[3] == 0);
    unsigned char colored[] = {100, 110, 50, 128};
    PrepareRgbaForOverlay(colored, 1);
    CHECK(colored[0] == 25 && colored[1] == 55 && colored[2] == 50 && colored[3] == 128);
    unsigned char transparent[] = {10, 20, 30, 0};
    PrepareRgbaForOverlay(transparent, 1);
    CHECK(transparent[0] == 0 && transparent[1] == 0 && transparent[2] == 0 && transparent[3] == 0);
    unsigned char opaque[] = {10, 20, 30, 255};
    PrepareRgbaForOverlay(opaque, 1);
    CHECK(opaque[0] == 30 && opaque[1] == 20 && opaque[2] == 10 && opaque[3] == 255);

    StatusPresentationState presentation{};
    CHECK(ShouldPresentStatus(presentation, "Waiting for Ru", false, 0, 0));
    RecordStatusPresented(presentation, "Waiting for Ru", false, 0, 0);
    CHECK(!ShouldPresentStatus(presentation, "Waiting for Ru", false, 0, 0));
    CHECK(ShouldPresentStatus(presentation, "Error", false, 0, 0));
    RecordStatusPresented(presentation, "Error", false, 0, 0);

    InvalidateStatusGeometry(presentation);
    CHECK(ShouldPresentStatus(presentation, "Error", false, 1, 0));
    RecordStatusPresented(presentation, "Error", false, 1, 0);
    InvalidateStatusVisibility(presentation);
    CHECK(ShouldPresentStatus(presentation, "Error", false, 1, 1));
    RecordStatusPresented(presentation, "Error", false, 1, 1);

    CHECK(ShouldPresentStatus(presentation, "Error", true, 1, 1));
    RecordStatusPresented(presentation, "Error", true, 1, 1);
    RecordFramePresented(presentation);
    CHECK(ShouldPresentStatus(presentation, "Error", true, 1, 1));
    // A failed PresentStatus call must not commit suppression state.
    CHECK(ShouldPresentStatus(presentation, "Error", true, 1, 1));
    RecordStatusPresented(presentation, "Error", true, 1, 1);
    CHECK(!ShouldPresentStatus(presentation, "Error", true, 1, 1));

    RecordFramePresented(presentation);
    CHECK(ShouldPresentStatus(presentation, "Error", true, 1, 1));
    InvalidateStatusGeometry(presentation);
    RecordStatusPresented(presentation, "Error", true,
                          presentation.geometry_generation,
                          presentation.visibility_generation);
    CHECK(!ShouldPresentStatus(presentation, "Error", true,
                               presentation.geometry_generation,
                               presentation.visibility_generation));

    return 0;
}
