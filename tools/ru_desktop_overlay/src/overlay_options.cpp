#include "overlay_options.h"

#include <stdexcept>

OverlayOptions ParseOptions(int argc, const char* const argv[]) {
    OverlayOptions result;

    for (int index = 1; index < argc; ++index) {
        const std::string argument{argv[index]};
        if (argument == "--click-through") {
            result.click_through = true;
        } else if (argument == "--sender" && index + 1 < argc) {
            result.sender_name = argv[++index];
        } else {
            throw std::invalid_argument("Usage: RuDesktopOverlay [--sender NAME] [--click-through]");
        }
    }

    return result;
}
