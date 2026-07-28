#include <SpoutDX/SpoutDX.h>

#include <Windows.h>

#include <algorithm>
#include <iostream>
#include <vector>

int main() {
    spoutDX receiver;
    if (!receiver.OpenDirectX11()) {
        std::cerr << "SpoutDX could not create a DirectX 11 device.\n";
        return 1;
    }

    receiver.SetReceiverName("VTubeStudioSpout");
    unsigned int width = 0;
    unsigned int height = 0;
    std::vector<unsigned char> pixels;
    bool received = false;
    int fresh_frames = 0;

    for (int attempt = 0; attempt < 120; ++attempt) {
        if (width != 0 && height != 0) {
            received = receiver.ReceiveImage(pixels.data(), width, height, false, true) || received;
        } else {
            receiver.ReceiveImage(nullptr, 0, 0, false, true);
        }
        if (receiver.IsUpdated()) {
            width = receiver.GetSenderWidth();
            height = receiver.GetSenderHeight();
            pixels.assign(static_cast<std::size_t>(width) * height * 4, 0);
        }
        if (receiver.IsFrameNew()) ++fresh_frames;
        Sleep(16);
    }

    unsigned char minimum_alpha = 255;
    unsigned char maximum_alpha = 0;
    std::size_t nonzero_alpha = 0;
    for (std::size_t offset = 3; offset < pixels.size(); offset += 4) {
        minimum_alpha = (std::min)(minimum_alpha, pixels[offset]);
        maximum_alpha = (std::max)(maximum_alpha, pixels[offset]);
        if (pixels[offset] != 0) ++nonzero_alpha;
    }
    bool printed_partial_green = false;
    int greatest_green_excess = -1;
    std::size_t greatest_green_offset = 0;
    int greatest_visible_green_excess = -1;
    std::size_t greatest_visible_green_offset = 0;
    for (std::size_t offset = 0; offset < pixels.size(); offset += 4) {
        const auto alpha = pixels[offset + 3];
        const auto strongest_non_green = (std::max)(pixels[offset], pixels[offset + 2]);
        const int green_excess = static_cast<int>(pixels[offset + 1]) - static_cast<int>(strongest_non_green);
        if (green_excess > greatest_green_excess) {
            greatest_green_excess = green_excess;
            greatest_green_offset = offset;
        }
        if (alpha > 0 && green_excess > greatest_visible_green_excess) {
            greatest_visible_green_excess = green_excess;
            greatest_visible_green_offset = offset;
        }
        if (!printed_partial_green && alpha > 0 && alpha < 255 && pixels[offset + 1] > strongest_non_green + 24) {
            std::cout << "Partial green RGBA=" << static_cast<int>(pixels[offset]) << ","
                      << static_cast<int>(pixels[offset + 1]) << ","
                      << static_cast<int>(pixels[offset + 2]) << ","
                      << static_cast<int>(alpha) << "\n";
            printed_partial_green = true;
        }
    }
    std::cout << "Strongest green RGBA=" << static_cast<int>(pixels[greatest_green_offset]) << ","
              << static_cast<int>(pixels[greatest_green_offset + 1]) << ","
              << static_cast<int>(pixels[greatest_green_offset + 2]) << ","
              << static_cast<int>(pixels[greatest_green_offset + 3]) << "\n";
    std::cout << "Strongest visible green RGBA=" << static_cast<int>(pixels[greatest_visible_green_offset]) << ","
              << static_cast<int>(pixels[greatest_visible_green_offset + 1]) << ","
              << static_cast<int>(pixels[greatest_visible_green_offset + 2]) << ","
              << static_cast<int>(pixels[greatest_visible_green_offset + 3]) << "\n";
    std::cout << "Frame: " << width << "x" << height << ", format=" << receiver.GetSenderFormat()
              << ", received=" << (received ? "true" : "false")
              << ", fresh-frames=" << fresh_frames << ", alpha-range=" << static_cast<int>(minimum_alpha)
              << "-" << static_cast<int>(maximum_alpha) << ", nonzero-alpha=" << nonzero_alpha << "\n";

    receiver.ReleaseReceiver();
    receiver.CloseDirectX11();
    return received ? 0 : 2;
}
