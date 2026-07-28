#pragma once

#include <string>

struct OverlayOptions {
    std::string sender_name{"VTubeStudioSpout"};
    bool click_through{false};
};

OverlayOptions ParseOptions(int argc, const char* const argv[]);
