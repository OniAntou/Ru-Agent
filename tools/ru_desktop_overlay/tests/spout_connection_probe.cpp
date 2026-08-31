#include "spout_overlay.h"
#include "test_check.h"

int main() {
    SpoutOverlay unavailable{"RuDesktopOverlayTestMissingSender"};
    CHECK(unavailable.Start());
    CHECK(!unavailable.Tick());
    CHECK(unavailable.Status() == "Waiting for RuDesktopOverlayTestMissingSender");
    return 0;
}
