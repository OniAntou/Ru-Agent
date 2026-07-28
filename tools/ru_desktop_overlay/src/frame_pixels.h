#pragma once

#include <cstddef>

void PremultiplyBgra(unsigned char* pixels, std::size_t pixel_count);
void ConvertRgbaToBgra(unsigned char* pixels, std::size_t pixel_count);
void RemoveGreenMatteRgba(unsigned char* pixels, std::size_t pixel_count);
void ApplyGreenScreenAlpha(unsigned char* pixels, std::size_t pixel_count);
