#pragma once

#include <memory>
#include <string>
#include <vector>

class SpoutOverlay {
public:
    explicit SpoutOverlay(std::string sender_name);
    ~SpoutOverlay();

    bool Start();
    bool Tick();
    bool IsConnected() const;
    const std::string& Status() const;
    unsigned int Width() const;
    unsigned int Height() const;
    const std::vector<unsigned char>& Pixels() const;
    void Stop();

private:
    class Impl;

    std::string sender_name_;
    std::string status_{"Waiting for VTubeStudioSpout"};
    std::unique_ptr<Impl> impl_;
    std::vector<unsigned char> pixels_;
    unsigned int width_{0};
    unsigned int height_{0};
};
