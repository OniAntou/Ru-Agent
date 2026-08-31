#include "spout_overlay.h"

#include "frame_pixels.h"

#include <SpoutDX/SpoutDX.h>

#include <utility>

class SpoutOverlay::Impl {
public:
    spoutDX receiver;

    ~Impl() {
        receiver.ReleaseReceiver();
        receiver.CloseDirectX11();
    }
};

SpoutOverlay::SpoutOverlay(std::string sender_name) : sender_name_(std::move(sender_name)) {}

SpoutOverlay::~SpoutOverlay() {
    Stop();
}

bool SpoutOverlay::Start() {
    Stop();
    impl_ = std::make_unique<Impl>();
    if (!impl_->receiver.OpenDirectX11()) {
        status_ = "SpoutDX could not create a DirectX 11 device";
        return false;
    }

    impl_->receiver.SetReceiverName(sender_name_.c_str());
    status_ = "Waiting for " + sender_name_;
    return true;
}

bool SpoutOverlay::Tick() {
    if (!impl_) {
        status_ = "SpoutDX is unavailable";
        return false;
    }

    bool received = false;
    if (pixels_.empty()) {
        impl_->receiver.ReceiveImage(nullptr, 0, 0, false, true);
    } else {
        received = impl_->receiver.ReceiveImage(pixels_.data(), width_, height_, false, true);
    }

    if (impl_->receiver.IsUpdated()) {
        width_ = impl_->receiver.GetSenderWidth();
        height_ = impl_->receiver.GetSenderHeight();
        pixels_.assign(static_cast<std::size_t>(width_) * height_ * 4, 0);
        status_ = "Connected: " + std::string(impl_->receiver.GetSenderName());
        return false;
    }

    if (!received) {
        status_ = "Waiting for " + sender_name_;
        return false;
    }

    const auto pixel_count = static_cast<std::size_t>(width_) * height_;
    if (impl_->receiver.GetSenderFormat() != DXGI_FORMAT_B8G8R8A8_UNORM &&
        impl_->receiver.GetSenderFormat() != DXGI_FORMAT_B8G8R8X8_UNORM) {
        PrepareRgbaForOverlay(pixels_.data(), pixel_count);
    } else {
        PremultiplyBgra(pixels_.data(), pixel_count);
    }
    status_ = "Connected: " + std::string(impl_->receiver.GetSenderName());
    return true;
}

bool SpoutOverlay::IsConnected() const {
    return impl_ && impl_->receiver.IsConnected();
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
