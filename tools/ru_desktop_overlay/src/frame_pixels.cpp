#include "frame_pixels.h"

#include <algorithm>
#include <utility>

void PremultiplyBgra(unsigned char* pixels, std::size_t pixel_count) {
    for (std::size_t index = 0; index < pixel_count; ++index) {
        const auto offset = index * 4;
        const unsigned int alpha = pixels[offset + 3];
        pixels[offset] = static_cast<unsigned char>((pixels[offset] * alpha) / 255);
        pixels[offset + 1] = static_cast<unsigned char>((pixels[offset + 1] * alpha) / 255);
        pixels[offset + 2] = static_cast<unsigned char>((pixels[offset + 2] * alpha) / 255);
    }
}

void ConvertRgbaToBgra(unsigned char* pixels, std::size_t pixel_count) {
    for (std::size_t index = 0; index < pixel_count; ++index) {
        auto* pixel = pixels + index * 4;
        std::swap(pixel[0], pixel[2]);
    }
}

void RemoveGreenMatteRgba(unsigned char* pixels, std::size_t pixel_count) {
    for (std::size_t index = 0; index < pixel_count; ++index) {
        auto* pixel = pixels + index * 4;
        const int strongest_non_green = (std::max)(static_cast<int>(pixel[0]), static_cast<int>(pixel[2]));
        if (static_cast<int>(pixel[1]) <= strongest_non_green + 24) continue;

        pixel[0] = 0;
        pixel[1] = 0;
        pixel[2] = 0;
        pixel[3] = 0;
    }
}

void ApplyGreenScreenAlpha(unsigned char* pixels, std::size_t pixel_count) {
    for (std::size_t index = 0; index < pixel_count; ++index) {
        const auto offset = index * 4;
        const int blue = pixels[offset];
        const int green = pixels[offset + 1];
        const int red = pixels[offset + 2];
        const int green_dominance = green - (blue > red ? blue : red);
        pixels[offset + 3] = (green >= 160 && green_dominance >= 80) ? 0 : 255;
    }
}
