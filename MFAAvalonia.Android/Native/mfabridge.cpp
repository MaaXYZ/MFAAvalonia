#include <android/log.h>
#include <atomic>
#include <cstdint>
#include <arpa/inet.h>
#include <sys/socket.h>
#include <unistd.h>
#include <cstring>
#include <dlfcn.h>
#include <exception>
#include <signal.h>
#include <thread>
#include <mutex>

#include "frame_store.h"

#define MFA_EXPORT extern "C" __attribute__((visibility("default")))

namespace {
constexpr const char* kTag = "MfaBridge";

struct Position { int x; int y; };
struct StartGameArgs { const char* package_name; int force_stop; };
struct StopGameArgs { const char* client_type; };
struct InputArgs { const char* text; };
struct TouchArgs { Position p; };
struct KeyArgs { int key_code; };
union ArgUnion {
    StartGameArgs start_game;
    StopGameArgs stop_game;
    InputArgs input;
    TouchArgs touch;
    KeyArgs key;
};
struct MethodParam { int display_id; int method; ArgUnion args; };

std::atomic<std::uint16_t> g_input_port { 0 };
std::mutex g_input_mutex;
int g_input_socket = -1;

using MaaAgentClientConnectFunction = std::uint8_t (*)(void*);

MaaAgentClientConnectFunction resolve_agent_client_connect() {
    auto* symbol = dlsym(RTLD_DEFAULT, "MaaAgentClientConnect");
    if (!symbol) {
        auto* library = dlopen("libMaaAgentClient.so", RTLD_NOW | RTLD_LOCAL);
        if (library) symbol = dlsym(library, "MaaAgentClientConnect");
    }
    return reinterpret_cast<MaaAgentClientConnectFunction>(symbol);
}

MaaAgentClientConnectFunction resolve_agent_client_disconnect() {
    auto* symbol = dlsym(RTLD_DEFAULT, "MaaAgentClientDisconnect");
    if (!symbol) {
        auto* library = dlopen("libMaaAgentClient.so", RTLD_NOW | RTLD_LOCAL);
        if (library) symbol = dlsym(library, "MaaAgentClientDisconnect");
    }
    return reinterpret_cast<MaaAgentClientConnectFunction>(symbol);
}

void fill_safe_async_signal_mask(sigset_t& blocked) {
    sigfillset(&blocked);
    // Never hide synchronous process faults. They must still reach Android's
    // crash reporter instead of leaving MaaFramework in a corrupted state.
    sigdelset(&blocked, SIGABRT);
    sigdelset(&blocked, SIGBUS);
    sigdelset(&blocked, SIGFPE);
    sigdelset(&blocked, SIGILL);
    sigdelset(&blocked, SIGSEGV);
    sigdelset(&blocked, SIGTRAP);
}

bool send_all(int socket_fd, const void* data, std::size_t size) {
    const auto* bytes = static_cast<const std::uint8_t*>(data);
    while (size > 0) {
        const auto sent = send(socket_fd, bytes, size, MSG_NOSIGNAL);
        if (sent <= 0) return false;
        bytes += sent;
        size -= static_cast<std::size_t>(sent);
    }
    return true;
}

bool receive_all(int socket_fd, void* data, std::size_t size) {
    auto* bytes = static_cast<std::uint8_t*>(data);
    while (size > 0) {
        const auto received = recv(socket_fd, bytes, size, 0);
        if (received <= 0) return false;
        bytes += received;
        size -= static_cast<std::size_t>(received);
    }
    return true;
}

int connect_input_socket() {
    if (g_input_socket >= 0) return g_input_socket;
    const auto input_port = g_input_port.load(std::memory_order_relaxed);
    if (input_port == 0) return -9;
    const int socket_fd = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (socket_fd < 0) return -10;
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(input_port);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(socket_fd, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0) {
        close(socket_fd);
        return -11;
    }
    g_input_socket = socket_fd;
    return socket_fd;
}

int send_input(const MethodParam& param, int x, int y, int key, const char* text) {
    std::lock_guard<std::mutex> lock(g_input_mutex);
    const int socket_fd = connect_input_socket();
    if (socket_fd < 0) return socket_fd;
    const int text_length = text ? static_cast<int>(std::strlen(text)) : 0;
    const std::uint32_t header[] = {
        htonl(0x4d464142), htonl(param.display_id), htonl(param.method),
        htonl(x), htonl(y), htonl(key), htonl(text_length)
    };
    const bool sent = send_all(socket_fd, header, sizeof(header))
        && (text_length == 0 || send_all(socket_fd, text, text_length));
    std::uint32_t network_result = 0;
    const bool received = sent && receive_all(socket_fd, &network_result, sizeof(network_result));
    if (!sent || !received) {
        close(g_input_socket);
        g_input_socket = -1;
    }
    if (!sent) return -12;
    if (!received) return -13;
    return static_cast<std::int32_t>(ntohl(network_result));
}
}

MFA_EXPORT int MfaBridgeConfigure(std::uint32_t width, std::uint32_t height) {
    const int result = ConfigureFrameStore(width, height);
    if (result == 0) {
        __android_log_print(ANDROID_LOG_INFO, kTag, "configured %ux%u", width, height);
    } else {
        __android_log_print(ANDROID_LOG_ERROR, kTag,
                            "configuration failed: %ux%u, result=%d",
                            width, height, result);
    }
    return result;
}

MFA_EXPORT int MfaBridgeSetInputPort(std::uint32_t port) {
    if (port == 0 || port > 65535) return -1;
    {
        std::lock_guard<std::mutex> lock(g_input_mutex);
        if (g_input_socket >= 0) {
            close(g_input_socket);
            g_input_socket = -1;
        }
    }
    g_input_port.store(static_cast<std::uint16_t>(port), std::memory_order_relaxed);
    __android_log_print(ANDROID_LOG_INFO, kTag, "input port configured: %u", port);
    return 0;
}

MFA_EXPORT int MfaBridgeSafeAgentClientConnect(void* client) {
    if (!client) return -1;
    const auto connect = resolve_agent_client_connect();
    if (!connect) {
        __android_log_print(ANDROID_LOG_ERROR, kTag,
                            "MaaAgentClientConnect symbol is unavailable: %s", dlerror());
        return -2;
    }

    // MaaAgentClient's public C entry point currently lets a zmq::error_t escape when
    // vendor kernels return EINTR from poll(). MuMu regularly delivers runtime signals
    // to .NET worker threads while this blocking call is active. Run the call on a native
    // thread which is not managed by .NET and block asynchronous signals there. Fatal
    // synchronous signals remain unblocked so genuine native faults are still reported.
    int result = 0;
    std::thread worker([&] {
        sigset_t blocked{};
        fill_safe_async_signal_mask(blocked);
        pthread_sigmask(SIG_SETMASK, &blocked, nullptr);

        try {
            result = connect(client) ? 1 : 0;
        } catch (const std::exception& error) {
            __android_log_print(ANDROID_LOG_WARN, kTag,
                                "MaaAgentClientConnect threw: %s", error.what());
            result = -3;
        } catch (...) {
            __android_log_print(ANDROID_LOG_WARN, kTag,
                                "MaaAgentClientConnect threw an unknown native exception");
            result = -4;
        }
    });
    worker.join();
    return result;
}

MFA_EXPORT int MfaBridgeSafeAgentClientDisconnect(void* client) {
    if (!client) return -1;
    const auto disconnect = resolve_agent_client_disconnect();
    if (!disconnect) {
        __android_log_print(ANDROID_LOG_ERROR, kTag,
                            "MaaAgentClientDisconnect symbol is unavailable: %s", dlerror());
        return -2;
    }

    int result = 0;
    std::thread worker([&] {
        sigset_t blocked{};
        fill_safe_async_signal_mask(blocked);
        pthread_sigmask(SIG_SETMASK, &blocked, nullptr);
        try {
            result = disconnect(client) ? 1 : 0;
        } catch (const std::exception& error) {
            __android_log_print(ANDROID_LOG_WARN, kTag,
                                "MaaAgentClientDisconnect threw: %s", error.what());
            result = -3;
        } catch (...) {
            __android_log_print(ANDROID_LOG_WARN, kTag,
                                "MaaAgentClientDisconnect threw an unknown native exception");
            result = -4;
        }
    });
    worker.join();
    return result;
}

MFA_EXPORT int MfaBridgeUpdateFrame(const std::uint8_t* data, std::uint32_t width,
                                     std::uint32_t height, std::uint32_t stride) {
    return UpdateFrameStore(data, width, height, stride);
}

MFA_EXPORT FrameInfo GetLockedPixels() {
    return LockCurrentFrame();
}

MFA_EXPORT int UnlockPixels(FrameInfo info) {
    return UnlockCurrentFrame(info);
}

MFA_EXPORT int DispatchInputMessage(MethodParam param) {
    __android_log_print(ANDROID_LOG_DEBUG, kTag, "input method=%d display=%d", param.method, param.display_id);
    switch (param.method) {
        case 1:
        {
            const auto baseline = CurrentFrameVersion();
            const auto result = send_input(param, 0, 0, param.args.start_game.force_stop,
                                            param.args.start_game.package_name);
            if (result != 0) return result;

            // Capture the baseline before launching. Otherwise a fast first frame can be
            // missed while `am start -W` is still returning, causing a needless 5s wait.
            if (!WaitForFrameAfter(baseline, 5000)) {
                __android_log_print(ANDROID_LOG_WARN, kTag,
                                    "StartApp timed out waiting for a new virtual-display frame");
            }
            return 0;
        }
        case 2: return send_input(param, 0, 0, 0, param.args.stop_game.client_type);
        case 4: return send_input(param, 0, 0, 0, param.args.input.text);
        case 6:
        case 7:
        case 8: return send_input(param, param.args.touch.p.x, param.args.touch.p.y, 0, nullptr);
        case 9:
        case 10: return send_input(param, 0, 0, param.args.key.key_code, nullptr);
        default: return -2;
    }
}
