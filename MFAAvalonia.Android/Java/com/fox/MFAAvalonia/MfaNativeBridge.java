package com.fox.MFAAvalonia;

import android.view.Surface;

public final class MfaNativeBridge {
    static {
        System.loadLibrary("mfabridge");
    }

    private MfaNativeBridge() {
    }

    public static native Surface setupCapturer(int width, int height);

    public static native void releaseCapturer();

    public static native void setPreviewSurface(Surface surface);

    public static native long getFrameCount();
}
