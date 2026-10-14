#include "frame_store.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <vector>

#if defined(__ARM_NEON)
#include <arm_neon.h>
#endif

namespace {
constexpr int kBufferCount = 3;

struct FrameBuffer {
    std::vector<std::uint8_t> pixels;
    std::atomic<int> readers{0};
    std::atomic<bool> writing{false};
    std::uint32_t width = 0;
    std::uint32_t height = 0;
};

std::array<FrameBuffer, kBufferCount> g_buffers;
std::atomic<int> g_current{-1};
std::atomic<std::uint64_t> g_version{0};
std::atomic<std::uint32_t> g_configured_width{0};
std::atomic<std::uint32_t> g_configured_height{0};
std::mutex g_config_mutex;
std::mutex g_frame_wait_mutex;
std::condition_variable g_frame_changed;

FrameBuffer* AcquireWriteBuffer(std::uint32_t width, std::uint32_t height) {
    const int current = g_current.load(std::memory_order_acquire);
    for (int i = 0; i < kBufferCount; ++i) {
        auto& candidate = g_buffers[i];
        if (i == current || candidate.readers.load(std::memory_order_acquire) != 0) {
            continue;
        }
        bool expected = false;
        if (!candidate.writing.compare_exchange_strong(
                expected, true, std::memory_order_acq_rel)) {
            continue;
        }
        if (candidate.readers.load(std::memory_order_acquire) != 0 ||
            g_current.load(std::memory_order_acquire) == i) {
            candidate.writing.store(false, std::memory_order_release);
            continue;
        }
        const auto required = static_cast<std::size_t>(width) * height * 3;
        if (candidate.pixels.size() != required) {
            candidate.pixels.resize(required);
        }
        candidate.width = width;
        candidate.height = height;
        return &candidate;
    }
    return nullptr;
}

void CommitWriteBuffer(FrameBuffer* buffer) {
    const int index = static_cast<int>(buffer - g_buffers.data());
    buffer->writing.store(false, std::memory_order_release);
    g_current.store(index, std::memory_order_release);
    g_version.fetch_add(1, std::memory_order_acq_rel);
    g_frame_changed.notify_all();
}

void ConvertRgbaToBgr(const std::uint8_t* source, std::uint8_t* target,
                      int width, int height, int source_stride) {
    for (int y = 0; y < height; ++y) {
        const auto* src = source + static_cast<std::size_t>(y) * source_stride;
        auto* dst = target + static_cast<std::size_t>(y) * width * 3;
        int x = 0;
#if defined(__ARM_NEON)
        for (; x <= width - 16; x += 16) {
            const uint8x16x4_t rgba = vld4q_u8(src);
            src += 64;
            const uint8x16x3_t bgr{rgba.val[2], rgba.val[1], rgba.val[0]};
            vst3q_u8(dst, bgr);
            dst += 48;
        }
#endif
        for (; x < width; ++x) {
            dst[0] = src[2];
            dst[1] = src[1];
            dst[2] = src[0];
            src += 4;
            dst += 3;
        }
    }
}
}

int ConfigureFrameStore(std::uint32_t width, std::uint32_t height) {
    if (width == 0 || height == 0) {
        return -1;
    }
    std::scoped_lock lock(g_config_mutex);
    const auto required = static_cast<std::size_t>(width) * height * 3;

    // A controller can be recreated immediately after a cancelled task while the
    // previous Maa controller is still releasing its last locked frame. Reconfiguring
    // an already-compatible store would only clear/resize the same vectors and used to
    // fail with -2 in that short window. Keep the existing buffers and published frame
    // intact instead. Readers and the capture copy thread can safely continue using the
    // same allocation while ownership moves to the new controller.
    const bool already_configured =
        g_configured_width.load(std::memory_order_acquire) == width &&
        g_configured_height.load(std::memory_order_acquire) == height;
    if (already_configured) {
        return 0;
    }

    // A real size change must never invalidate storage referenced by an old reader or
    // written by the capture thread. The caller may retry after those users drain.
    for (auto& buffer : g_buffers) {
        if (buffer.readers.load(std::memory_order_acquire) != 0 ||
            buffer.writing.load(std::memory_order_acquire)) {
            return -2;
        }
        buffer.pixels.resize(required);
        std::fill(buffer.pixels.begin(), buffer.pixels.end(), 0);
        buffer.width = width;
        buffer.height = height;
    }
    // MaaTasker requests a screenshot before every recognition, including DirectHit.
    // A newly-created virtual display does not submit a buffer until an activity draws
    // on it, so exposing no current frame deadlocks tasks which are meant to launch that
    // first activity. Publish a valid black bootstrap frame; the first real display
    // buffer atomically replaces it as soon as Android renders one.
    g_current.store(0, std::memory_order_release);
    g_version.store(1, std::memory_order_release);
    g_configured_width.store(width, std::memory_order_release);
    g_configured_height.store(height, std::memory_order_release);
    return 0;
}

int UpdateFrameStore(const std::uint8_t* data, std::uint32_t width,
                     std::uint32_t height, std::uint32_t stride) {
    if (!data || width == 0 || height == 0 || stride < width * 3) {
        return -1;
    }
    auto* target = AcquireWriteBuffer(width, height);
    if (!target) {
        return -2;
    }
    for (std::uint32_t row = 0; row < height; ++row) {
        std::memcpy(target->pixels.data() + static_cast<std::size_t>(row) * width * 3,
                    data + static_cast<std::size_t>(row) * stride,
                    static_cast<std::size_t>(width) * 3);
    }
    CommitWriteBuffer(target);
    return 0;
}

bool UpdateFrameStore(AHardwareBuffer* buffer, int acquire_fence_fd) {
    if (!buffer) {
        return false;
    }
    AHardwareBuffer_Desc description{};
    AHardwareBuffer_describe(buffer, &description);
    auto* target = AcquireWriteBuffer(description.width, description.height);
    if (!target) {
        return false;
    }
    void* source = nullptr;
    if (AHardwareBuffer_lock(buffer, AHARDWAREBUFFER_USAGE_CPU_READ_OFTEN,
                             acquire_fence_fd, nullptr, &source) != 0 || !source) {
        target->writing.store(false, std::memory_order_release);
        return false;
    }
    ConvertRgbaToBgr(static_cast<const std::uint8_t*>(source), target->pixels.data(),
                     static_cast<int>(description.width), static_cast<int>(description.height),
                     static_cast<int>(description.stride) * 4);
    AHardwareBuffer_unlock(buffer, nullptr);
    CommitWriteBuffer(target);
    return true;
}

FrameInfo LockCurrentFrame() {
    for (int attempt = 0; attempt < 3; ++attempt) {
        const int index = g_current.load(std::memory_order_acquire);
        if (index < 0 || index >= kBufferCount) {
            return {};
        }
        auto& buffer = g_buffers[index];
        buffer.readers.fetch_add(1, std::memory_order_acquire);
        if (g_current.load(std::memory_order_acquire) != index ||
            buffer.writing.load(std::memory_order_acquire)) {
            buffer.readers.fetch_sub(1, std::memory_order_release);
            continue;
        }
        return {buffer.width, buffer.height, buffer.width * 3,
                static_cast<std::uint32_t>(buffer.pixels.size()),
                buffer.pixels.data(), &buffer};
    }
    return {};
}

int UnlockCurrentFrame(FrameInfo info) {
    if (!info.frame_ref) {
        return -1;
    }
    static_cast<FrameBuffer*>(info.frame_ref)->readers.fetch_sub(1, std::memory_order_release);
    return 0;
}

std::uint64_t CurrentFrameVersion() {
    return g_version.load(std::memory_order_acquire);
}

bool WaitForFrameAfter(std::uint64_t version, int timeout_milliseconds) {
    if (CurrentFrameVersion() > version) {
        return true;
    }
    std::unique_lock lock(g_frame_wait_mutex);
    return g_frame_changed.wait_for(lock, std::chrono::milliseconds(timeout_milliseconds),
                                    [version] { return CurrentFrameVersion() > version; });
}
