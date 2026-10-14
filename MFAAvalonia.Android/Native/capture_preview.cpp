#include "capture_preview.h"

#include "frame_store.h"

#include <android/hardware_buffer.h>
#include <android/log.h>
#include <android/native_window.h>
#include <android/native_window_jni.h>
#include <media/NdkImage.h>
#include <media/NdkImageReader.h>

#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES2/gl2.h>
#include <GLES2/gl2ext.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <queue>
#include <thread>
#include <unistd.h>

namespace {
constexpr const char* kTag = "MfaNativeCapture";

struct Capturer {
    AImageReader* reader = nullptr;
    ANativeWindow* window = nullptr;
    AImageReader_ImageListener listener{};
};

struct EglState {
    EGLDisplay display = EGL_NO_DISPLAY;
    EGLSurface surface = EGL_NO_SURFACE;
    EGLContext context = EGL_NO_CONTEXT;
    GLuint program = 0;
    GLuint texture = 0;
};

Capturer* g_capturer = nullptr;
std::mutex g_capturer_mutex;
std::atomic<long long> g_frame_count{0};
std::atomic<long long> g_render_count{0};
std::atomic<long long> g_last_copy_dispatch_ns{0};
std::atomic<long long> g_last_preview_dispatch_ns{0};
std::chrono::steady_clock::time_point g_capture_report_time;
std::chrono::steady_clock::time_point g_render_report_time;

std::mutex g_preview_mutex;
std::mutex g_queue_mutex;
std::condition_variable g_queue_changed;
std::queue<AHardwareBuffer*> g_queue;
std::thread g_render_thread;
std::atomic<bool> g_rendering{false};
ANativeWindow* g_pending_window = nullptr;

std::mutex g_copy_mutex;
std::condition_variable g_copy_changed;
AHardwareBuffer* g_pending_copy = nullptr;
int g_pending_copy_fence = -1;
std::thread g_copy_thread;
std::atomic<bool> g_copying{false};

PFNEGLGETNATIVECLIENTBUFFERANDROIDPROC g_get_native_client_buffer = nullptr;
PFNEGLCREATEIMAGEKHRPROC g_create_image = nullptr;
PFNEGLDESTROYIMAGEKHRPROC g_destroy_image = nullptr;
PFNGLEGLIMAGETARGETTEXTURE2DOESPROC g_image_target_texture = nullptr;

constexpr const char* kVertexShader =
    "attribute vec2 position;\n"
    "attribute vec2 texCoord;\n"
    "varying vec2 uv;\n"
    "void main() { gl_Position = vec4(position, 0.0, 1.0); uv = texCoord; }\n";

constexpr const char* kFragmentShader =
    "#extension GL_OES_EGL_image_external : require\n"
    "precision mediump float;\n"
    "uniform samplerExternalOES frame;\n"
    "varying vec2 uv;\n"
    "void main() { gl_FragColor = texture2D(frame, uv); }\n";

GLuint CompileShader(GLenum type, const char* source) {
    const GLuint shader = glCreateShader(type);
    glShaderSource(shader, 1, &source, nullptr);
    glCompileShader(shader);
    GLint compiled = GL_FALSE;
    glGetShaderiv(shader, GL_COMPILE_STATUS, &compiled);
    if (compiled != GL_TRUE) {
        glDeleteShader(shader);
        return 0;
    }
    return shader;
}

void DestroyEgl(EglState& state) {
    if (state.display != EGL_NO_DISPLAY) {
        eglMakeCurrent(state.display, state.surface, state.surface, state.context);
        if (state.texture) glDeleteTextures(1, &state.texture);
        if (state.program) glDeleteProgram(state.program);
        eglMakeCurrent(state.display, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
        if (state.surface != EGL_NO_SURFACE) eglDestroySurface(state.display, state.surface);
        if (state.context != EGL_NO_CONTEXT) eglDestroyContext(state.display, state.context);
        eglTerminate(state.display);
    }
    state = {};
}

bool CreateEgl(ANativeWindow* window, EglState& state) {
    state.display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    if (state.display == EGL_NO_DISPLAY ||
        eglInitialize(state.display, nullptr, nullptr) == EGL_FALSE) {
        return false;
    }
    const EGLint attributes[] = {
        EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
        EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8,
        EGL_NONE
    };
    EGLConfig config = nullptr;
    EGLint count = 0;
    if (eglChooseConfig(state.display, attributes, &config, 1, &count) == EGL_FALSE || count == 0) {
        DestroyEgl(state);
        return false;
    }
    state.surface = eglCreateWindowSurface(state.display, config, window, nullptr);
    const EGLint context_attributes[] = {EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE};
    state.context = eglCreateContext(state.display, config, EGL_NO_CONTEXT, context_attributes);
    if (state.surface == EGL_NO_SURFACE || state.context == EGL_NO_CONTEXT ||
        eglMakeCurrent(state.display, state.surface, state.surface, state.context) == EGL_FALSE) {
        DestroyEgl(state);
        return false;
    }

    g_get_native_client_buffer = reinterpret_cast<PFNEGLGETNATIVECLIENTBUFFERANDROIDPROC>(
        eglGetProcAddress("eglGetNativeClientBufferANDROID"));
    g_create_image = reinterpret_cast<PFNEGLCREATEIMAGEKHRPROC>(
        eglGetProcAddress("eglCreateImageKHR"));
    g_destroy_image = reinterpret_cast<PFNEGLDESTROYIMAGEKHRPROC>(
        eglGetProcAddress("eglDestroyImageKHR"));
    g_image_target_texture = reinterpret_cast<PFNGLEGLIMAGETARGETTEXTURE2DOESPROC>(
        eglGetProcAddress("glEGLImageTargetTexture2DOES"));
    if (!g_get_native_client_buffer || !g_create_image || !g_destroy_image ||
        !g_image_target_texture) {
        DestroyEgl(state);
        return false;
    }

    const GLuint vertex = CompileShader(GL_VERTEX_SHADER, kVertexShader);
    const GLuint fragment = CompileShader(GL_FRAGMENT_SHADER, kFragmentShader);
    if (!vertex || !fragment) {
        if (vertex) glDeleteShader(vertex);
        if (fragment) glDeleteShader(fragment);
        DestroyEgl(state);
        return false;
    }
    state.program = glCreateProgram();
    glAttachShader(state.program, vertex);
    glAttachShader(state.program, fragment);
    glLinkProgram(state.program);
    glDeleteShader(vertex);
    glDeleteShader(fragment);
    GLint linked = GL_FALSE;
    glGetProgramiv(state.program, GL_LINK_STATUS, &linked);
    if (linked != GL_TRUE) {
        DestroyEgl(state);
        return false;
    }
    glUseProgram(state.program);
    glGenTextures(1, &state.texture);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, state.texture);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glUniform1i(glGetUniformLocation(state.program, "frame"), 0);
    glViewport(0, 0, ANativeWindow_getWidth(window), ANativeWindow_getHeight(window));
    return true;
}

void RenderFrame(EglState& state, AHardwareBuffer* buffer) {
    if (!buffer || state.display == EGL_NO_DISPLAY ||
        eglMakeCurrent(state.display, state.surface, state.surface, state.context) == EGL_FALSE) {
        return;
    }
    const EGLClientBuffer client = g_get_native_client_buffer(buffer);
    const EGLint attributes[] = {EGL_IMAGE_PRESERVED_KHR, EGL_TRUE, EGL_NONE};
    const EGLImageKHR image = g_create_image(state.display, EGL_NO_CONTEXT,
                                              EGL_NATIVE_BUFFER_ANDROID, client, attributes);
    if (image == EGL_NO_IMAGE_KHR) {
        return;
    }
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, state.texture);
    g_image_target_texture(GL_TEXTURE_EXTERNAL_OES, image);

    static const GLfloat vertices[] = {-1.f, 1.f, -1.f, -1.f, 1.f, 1.f, 1.f, -1.f};
    static const GLfloat coordinates[] = {0.f, 0.f, 0.f, 1.f, 1.f, 0.f, 1.f, 1.f};
    const GLint position = glGetAttribLocation(state.program, "position");
    const GLint tex_coord = glGetAttribLocation(state.program, "texCoord");
    glEnableVertexAttribArray(position);
    glVertexAttribPointer(position, 2, GL_FLOAT, GL_FALSE, 0, vertices);
    glEnableVertexAttribArray(tex_coord);
    glVertexAttribPointer(tex_coord, 2, GL_FLOAT, GL_FALSE, 0, coordinates);
    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
    if (eglSwapBuffers(state.display, state.surface) == EGL_TRUE) {
        const auto count = g_render_count.fetch_add(1, std::memory_order_relaxed) + 1;
        if (count == 1) {
            g_render_report_time = std::chrono::steady_clock::now();
            __android_log_print(ANDROID_LOG_INFO, kTag, "preview first frame rendered");
        } else if (count % 300 == 0) {
            const auto now = std::chrono::steady_clock::now();
            const auto elapsed = std::chrono::duration<double>(now - g_render_report_time).count();
            __android_log_print(ANDROID_LOG_INFO, kTag,
                                "preview frames=%lld, fps=%.1f", count, 299.0 / elapsed);
            g_render_report_time = now;
        }
    }
    g_destroy_image(state.display, image);
}

void DrainQueueLocked() {
    while (!g_queue.empty()) {
        AHardwareBuffer_release(g_queue.front());
        g_queue.pop();
    }
}

void RenderLoop() {
    EglState egl{};
    ANativeWindow* window = nullptr;
    while (g_rendering.load(std::memory_order_acquire)) {
        AHardwareBuffer* buffer = nullptr;
        {
            std::unique_lock lock(g_queue_mutex);
            g_queue_changed.wait(lock, [] {
                return !g_rendering.load(std::memory_order_acquire) ||
                       g_pending_window != nullptr || !g_queue.empty();
            });
            if (!g_rendering.load(std::memory_order_acquire)) break;
            if (g_pending_window) {
                DestroyEgl(egl);
                if (window) ANativeWindow_release(window);
                window = g_pending_window;
                g_pending_window = nullptr;
                if (!CreateEgl(window, egl)) {
                    __android_log_print(ANDROID_LOG_ERROR, kTag, "EGL preview initialization failed");
                }
            }
            if (!g_queue.empty()) {
                buffer = g_queue.front();
                g_queue.pop();
            }
        }
        if (buffer) {
            RenderFrame(egl, buffer);
            AHardwareBuffer_release(buffer);
        }
    }
    DestroyEgl(egl);
    if (window) ANativeWindow_release(window);
    std::scoped_lock lock(g_queue_mutex);
    DrainQueueLocked();
}

void DispatchPreview(AHardwareBuffer* buffer) {
    if (!buffer || !g_rendering.load(std::memory_order_acquire)) return;
    constexpr auto kPreviewInterval = std::chrono::milliseconds(33);
    const auto now_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
    auto previous_ns = g_last_preview_dispatch_ns.load(std::memory_order_relaxed);
    if (now_ns - previous_ns <
        std::chrono::duration_cast<std::chrono::nanoseconds>(kPreviewInterval).count() ||
        !g_last_preview_dispatch_ns.compare_exchange_strong(
            previous_ns, now_ns, std::memory_order_relaxed)) {
        return;
    }
    AHardwareBuffer_acquire(buffer);
    {
        std::scoped_lock lock(g_queue_mutex);
        DrainQueueLocked();
        g_queue.push(buffer);
    }
    g_queue_changed.notify_one();
}

void ReleasePendingCopyLocked() {
    if (g_pending_copy) {
        AHardwareBuffer_release(g_pending_copy);
        g_pending_copy = nullptr;
    }
    if (g_pending_copy_fence >= 0) {
        close(g_pending_copy_fence);
        g_pending_copy_fence = -1;
    }
}

void CopyLoop() {
    while (g_copying.load(std::memory_order_acquire)) {
        AHardwareBuffer* buffer = nullptr;
        int acquire_fence_fd = -1;
        {
            std::unique_lock lock(g_copy_mutex);
            g_copy_changed.wait(lock, [] {
                return !g_copying.load(std::memory_order_acquire) || g_pending_copy != nullptr;
            });
            if (!g_copying.load(std::memory_order_acquire)) break;
            buffer = g_pending_copy;
            acquire_fence_fd = g_pending_copy_fence;
            g_pending_copy = nullptr;
            g_pending_copy_fence = -1;
        }

        const bool updated = UpdateFrameStore(buffer, acquire_fence_fd);
        AHardwareBuffer_release(buffer);
        if (updated) {
            const auto count = g_frame_count.fetch_add(1, std::memory_order_relaxed) + 1;
            if (count == 1) {
                g_capture_report_time = std::chrono::steady_clock::now();
                __android_log_print(ANDROID_LOG_INFO, kTag, "capture first frame received");
            } else if (count % 300 == 0) {
                const auto now = std::chrono::steady_clock::now();
                const auto elapsed = std::chrono::duration<double>(now - g_capture_report_time).count();
                __android_log_print(ANDROID_LOG_INFO, kTag,
                                    "capture frames=%lld, fps=%.1f", count, 299.0 / elapsed);
                g_capture_report_time = now;
            }
        }
    }

    std::scoped_lock lock(g_copy_mutex);
    ReleasePendingCopyLocked();
}

void StartCopyThread() {
    if (g_copying.exchange(true, std::memory_order_acq_rel)) return;
    g_copy_thread = std::thread(CopyLoop);
}

void StopCopyThread() {
    if (!g_copying.exchange(false, std::memory_order_acq_rel)) return;
    g_copy_changed.notify_all();
    if (g_copy_thread.joinable()) g_copy_thread.join();
    std::scoped_lock lock(g_copy_mutex);
    ReleasePendingCopyLocked();
}

void DispatchFrameCopy(AHardwareBuffer* buffer, int acquire_fence_fd) {
    if (!buffer || !g_copying.load(std::memory_order_acquire)) {
        if (acquire_fence_fd >= 0) close(acquire_fence_fd);
        return;
    }
    constexpr auto kCopyInterval = std::chrono::milliseconds(33);
    const auto now_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
    auto previous_ns = g_last_copy_dispatch_ns.load(std::memory_order_relaxed);
    if (now_ns - previous_ns <
        std::chrono::duration_cast<std::chrono::nanoseconds>(kCopyInterval).count() ||
        !g_last_copy_dispatch_ns.compare_exchange_strong(
            previous_ns, now_ns, std::memory_order_relaxed)) {
        if (acquire_fence_fd >= 0) close(acquire_fence_fd);
        return;
    }
    AHardwareBuffer_acquire(buffer);
    {
        std::scoped_lock lock(g_copy_mutex);
        ReleasePendingCopyLocked();
        g_pending_copy = buffer;
        g_pending_copy_fence = acquire_fence_fd;
    }
    g_copy_changed.notify_one();
}

void OnImageAvailable(void*, AImageReader* reader) {
    AImage* image = nullptr;
    int acquire_fence_fd = -1;
    if (AImageReader_acquireLatestImageAsync(reader, &image, &acquire_fence_fd) != AMEDIA_OK ||
        !image) {
        if (acquire_fence_fd >= 0) close(acquire_fence_fd);
        return;
    }
    AHardwareBuffer* buffer = nullptr;
    if (AImage_getHardwareBuffer(image, &buffer) == AMEDIA_OK && buffer) {
        DispatchPreview(buffer);
        DispatchFrameCopy(buffer, acquire_fence_fd);
        acquire_fence_fd = -1;
    }
    if (acquire_fence_fd >= 0) close(acquire_fence_fd);
    AImage_delete(image);
}
}

jobject SetupNativeCapturer(JNIEnv* env, int width, int height) {
    std::scoped_lock lock(g_capturer_mutex);
    ReleaseNativeCapturer();
    if (!env || width <= 0 || height <= 0) return nullptr;
    auto* capturer = new Capturer();
    const auto status = AImageReader_newWithUsage(
        width, height, AIMAGE_FORMAT_RGBA_8888,
        AHARDWAREBUFFER_USAGE_CPU_READ_OFTEN | AHARDWAREBUFFER_USAGE_GPU_SAMPLED_IMAGE,
        5, &capturer->reader);
    if (status != AMEDIA_OK || !capturer->reader) {
        delete capturer;
        return nullptr;
    }
    capturer->listener.context = capturer;
    capturer->listener.onImageAvailable = OnImageAvailable;
    if (AImageReader_setImageListener(capturer->reader, &capturer->listener) != AMEDIA_OK ||
        AImageReader_getWindow(capturer->reader, &capturer->window) != AMEDIA_OK ||
        !capturer->window) {
        AImageReader_delete(capturer->reader);
        delete capturer;
        return nullptr;
    }
    g_capturer = capturer;
    g_frame_count.store(0, std::memory_order_release);
    g_render_count.store(0, std::memory_order_release);
    g_last_copy_dispatch_ns.store(0, std::memory_order_release);
    g_last_preview_dispatch_ns.store(0, std::memory_order_release);
    StartCopyThread();
    __android_log_print(ANDROID_LOG_INFO, kTag, "native capturer ready: %dx%d", width, height);
    return ANativeWindow_toSurface(env, capturer->window);
}

void ReleaseNativeCapturer() {
    if (!g_capturer) return;
    if (g_capturer->reader) {
        AImageReader_setImageListener(g_capturer->reader, nullptr);
        AImageReader_delete(g_capturer->reader);
    }
    delete g_capturer;
    g_capturer = nullptr;
    StopCopyThread();
}

int SetNativePreviewSurface(JNIEnv* env, jobject surface) {
    std::scoped_lock preview_lock(g_preview_mutex);
    if (g_rendering.exchange(false, std::memory_order_acq_rel)) {
        g_queue_changed.notify_all();
        if (g_render_thread.joinable()) g_render_thread.join();
    }
    {
        std::scoped_lock queue_lock(g_queue_mutex);
        DrainQueueLocked();
        if (g_pending_window) {
            ANativeWindow_release(g_pending_window);
            g_pending_window = nullptr;
        }
    }
    if (!surface) return 0;
    auto* window = ANativeWindow_fromSurface(env, surface);
    if (!window) return -1;
    {
        std::scoped_lock queue_lock(g_queue_mutex);
        g_pending_window = window;
    }
    g_rendering.store(true, std::memory_order_release);
    g_render_thread = std::thread(RenderLoop);
    g_queue_changed.notify_one();
    return 0;
}

long long NativeFrameCount() {
    return g_frame_count.load(std::memory_order_acquire);
}

extern "C" JNIEXPORT jobject JNICALL
Java_com_fox_MFAAvalonia_MfaNativeBridge_setupCapturer(
    JNIEnv* env, jclass, jint width, jint height) {
    return SetupNativeCapturer(env, width, height);
}

extern "C" JNIEXPORT void JNICALL
Java_com_fox_MFAAvalonia_MfaNativeBridge_releaseCapturer(JNIEnv*, jclass) {
    std::scoped_lock lock(g_capturer_mutex);
    ReleaseNativeCapturer();
}

extern "C" JNIEXPORT void JNICALL
Java_com_fox_MFAAvalonia_MfaNativeBridge_setPreviewSurface(
    JNIEnv* env, jclass, jobject surface) {
    SetNativePreviewSurface(env, surface);
}

extern "C" JNIEXPORT jlong JNICALL
Java_com_fox_MFAAvalonia_MfaNativeBridge_getFrameCount(JNIEnv*, jclass) {
    return static_cast<jlong>(NativeFrameCount());
}
