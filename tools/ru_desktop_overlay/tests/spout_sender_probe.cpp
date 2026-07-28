#include <SpoutLibrary/SpoutLibrary.h>

#include "frame_pixels.h"

#include <Windows.h>

#include <iostream>
#include <algorithm>
#include <vector>

int main() {
    const SPOUTHANDLE receiver = GetSpout();
    if (!receiver) {
        std::cerr << "SpoutLibrary factory failed.\n";
        return 1;
    }

    const int count = receiver->GetSenderCount();
    std::cout << "Sender count: " << count << "\n";
    for (int index = 0; index < count; ++index) {
        char sender_name[256]{};
        if (receiver->GetSender(index, sender_name, static_cast<int>(sizeof(sender_name)))) {
            std::cout << "Sender[" << index << "]: " << sender_name << "\n";
        }
    }
    if (count == 0) return 2;

    if (!receiver->CreateOpenGL()) {
        std::cerr << "SpoutLibrary could not create an OpenGL context.\n";
        return 3;
    }
    receiver->SetCPUmode(true);
    receiver->SetReceiverName("VTubeStudioSpout");
    unsigned int width = 0;
    unsigned int height = 0;
    HANDLE shared_texture = nullptr;
    DWORD format = 0;
    if (!receiver->GetSenderInfo("VTubeStudioSpout", width, height, shared_texture, format)) {
        std::cerr << "VTubeStudioSpout details are unavailable.\n";
        return 4;
    }

    std::vector<unsigned char> pixels(static_cast<std::size_t>(width) * height * 4);
    bool received = false;
    int fresh_frames = 0;
    for (int attempt = 0; attempt < 120; ++attempt) {
        received = receiver->ReceiveImage(pixels.data(), GL_RGBA, true) || received;
        if (receiver->IsFrameNew()) ++fresh_frames;
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
    std::cout << "Source frame: " << width << "x" << height << ", format=" << format
              << ", received=" << (received ? "true" : "false")
              << ", fresh-frames=" << fresh_frames
              << ", sender-cpu=" << (receiver->GetSenderCPU() ? "true" : "false")
              << ", sender-gldx=" << (receiver->GetSenderGLDX() ? "true" : "false")
              << ", alpha-range=" << static_cast<int>(minimum_alpha) << "-" << static_cast<int>(maximum_alpha)
              << ", nonzero-alpha=" << nonzero_alpha << "\n";
    const auto print_pixel = [&pixels, width](const char* label, unsigned int x, unsigned int y) {
        const std::size_t offset = (static_cast<std::size_t>(y) * width + x) * 4;
        std::cout << label << " BGRA=" << static_cast<int>(pixels[offset]) << ","
                  << static_cast<int>(pixels[offset + 1]) << ","
                  << static_cast<int>(pixels[offset + 2]) << ","
                  << static_cast<int>(pixels[offset + 3]) << "\n";
    };
    print_pixel("Top-left", 0, 0);
    print_pixel("Centre", width / 2, height / 2);
    ApplyGreenScreenAlpha(pixels.data(), static_cast<std::size_t>(width) * height);
    minimum_alpha = 255;
    maximum_alpha = 0;
    nonzero_alpha = 0;
    for (std::size_t offset = 3; offset < pixels.size(); offset += 4) {
        minimum_alpha = (std::min)(minimum_alpha, pixels[offset]);
        maximum_alpha = (std::max)(maximum_alpha, pixels[offset]);
        if (pixels[offset] != 0) ++nonzero_alpha;
    }
    std::cout << "After green key: alpha-range=" << static_cast<int>(minimum_alpha) << "-"
              << static_cast<int>(maximum_alpha) << ", nonzero-alpha=" << nonzero_alpha << "\n";
    receiver->ReleaseReceiver();
    receiver->CloseOpenGL();
    return received ? 0 : 5;
}
