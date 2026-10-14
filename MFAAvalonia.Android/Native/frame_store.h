#pragma once

#include <android/hardware_buffer.h>
#include <cstdint>

struct FrameInfo {
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride;
    std::uint32_t length;
    void* data;
    void* frame_ref;
};

int ConfigureFrameStore(std::uint32_t width, std::uint32_t height);
int UpdateFrameStore(const std::uint8_t* data, std::uint32_t width,
                     std::uint32_t height, std::uint32_t stride);
bool UpdateFrameStore(AHardwareBuffer* buffer, int acquire_fence_fd = -1);
FrameInfo LockCurrentFrame();
int UnlockCurrentFrame(FrameInfo info);
std::uint64_t CurrentFrameVersion();
bool WaitForFrameAfter(std::uint64_t version, int timeout_milliseconds);
