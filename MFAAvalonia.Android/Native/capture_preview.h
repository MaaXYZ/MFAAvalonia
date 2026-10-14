#pragma once

#include <jni.h>

jobject SetupNativeCapturer(JNIEnv* env, int width, int height);
void ReleaseNativeCapturer();
int SetNativePreviewSurface(JNIEnv* env, jobject surface);
long long NativeFrameCount();
