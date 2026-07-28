#include "spout_overlay.h"

#include "frame_pixels.h"

#include <SpoutLibrary/SpoutLibrary.h>

#include <utility>

namespace {
constexpr GLenum kGlBgra = 0x80E1;
}

class SpoutOverlay::Impl {
public:
    SPOUTHANDLE receiver{nullptr};

    ~Impl() {
        if (receiver) {
            receiver->ReleaseReceiver();
        }
    }
};

SpoutOverlay::SpoutOverlay(std::string sender_name) : sender_name_(std::move(sender_name)) {}

SpoutOverlay::~SpoutOverlay() {
    Stop();
}

bool SpoutOverlay::Start() {
    Stop();
    impl_ = std::make_unique<Impl>();
    impl_->receiver = GetSpout();
    if (!impl_->receiver) {
        status_ = "SpoutLibrary is unavailable";
        return false;
    }

    impl_->receiver->SetReceiverName(sender_name_.c_str());
    status_ = "Waiting for " + sender_name_;
    return true;
}

bool SpoutOverlay::Tick() {
    if (!impl_ || !impl_->receiver) {
        status_ = "SpoutLibrary is unavailable";
        return false;
    }

    unsigned int sender_width = 0;
    unsigned int sender_height = 0;
    HANDLE sender_handle = nullptr;
    DWORD sender_format = 0;
    if (!impl_->receiver->GetSenderInfo(sender_name_.c_str(), sender_width, sender_height, sender_handle, sender_format) ||
        sender_width == 0 || sender_height == 0) {
        status_ = "Waiting for " + sender_name_;
        return false;
    }

    if (sender_width != width_ || sender_height != height_) {
        width_ = sender_width;
        height_ = sender_height;
        pixels_.assign(static_cast<std::size_t>(width_) * height_ * 4, 0);
    }

    if (!impl_->receiver->ReceiveImage(pixels_.data(), kGlBgra, true)) {
        status_ = "Waiting for " + sender_name_;
        return false;
    }

    PremultiplyBgra(pixels_.data(), static_cast<std::size_t>(width_) * height_);
    status_ = "Connected: " + std::string(impl_->receiver->GetSenderName());
    return true;
}

bool SpoutOverlay::IsConnected() const {
    return impl_ && impl_->receiver && impl_->receiver->IsConnected();
}

const std::string& SpoutOverlay::Status() const {
    return status_;
}

unsigned int SpoutOverlay::Width() const {
    return width_;
}

unsigned int SpoutOverlay::Height() const {
    return height_;
}

const std::vector<unsigned char>& SpoutOverlay::Pixels() const {
    return pixels_;
}

void SpoutOverlay::Stop() {
    impl_.reset();
    pixels_.clear();
    width_ = 0;
    height_ = 0;
}
