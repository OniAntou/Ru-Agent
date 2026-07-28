#include "frame_pixels.h"

void PremultiplyBgra(unsigned char* pixels, std::size_t pixel_count) {
    for (std::size_t index = 0; index < pixel_count; ++index) {
        const auto offset = index * 4;
        const unsigned int alpha = pixels[offset + 3];
        pixels[offset] = static_cast<unsigned char>((pixels[offset] * alpha) / 255);
        pixels[offset + 1] = static_cast<unsigned char>((pixels[offset + 1] * alpha) / 255);
        pixels[offset + 2] = static_cast<unsigned char>((pixels[offset + 2] * alpha) / 255);
    }
}
