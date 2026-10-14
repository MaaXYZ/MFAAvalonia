using Android.Runtime;
using Android.Views;
using System;

namespace MFAAvalonia.Android;

internal static class NativeCaptureInterop
{
    private const string ClassName = "com/fox/MFAAvalonia/MfaNativeBridge";

    internal static Surface SetupCapturer(int width, int height)
    {
        var classHandle = JNIEnv.FindClass(ClassName);
        var method = JNIEnv.GetStaticMethodID(classHandle, "setupCapturer", "(II)Landroid/view/Surface;");
        var surfaceHandle = JNIEnv.CallStaticObjectMethod(classHandle, method,
        [
            new JValue(width),
            new JValue(height),
        ]);
        return Java.Lang.Object.GetObject<Surface>(surfaceHandle, JniHandleOwnership.TransferLocalRef)
               ?? throw new InvalidOperationException("Native Android frame capturer did not return a Surface.");
    }

    internal static void ReleaseCapturer() => CallStaticVoid("releaseCapturer", "()V");

    internal static void SetPreviewSurface(Surface? surface)
    {
        var classHandle = JNIEnv.FindClass(ClassName);
        var method = JNIEnv.GetStaticMethodID(classHandle, "setPreviewSurface", "(Landroid/view/Surface;)V");
        JNIEnv.CallStaticVoidMethod(classHandle, method, [new JValue(surface)]);
    }

    internal static long GetFrameCount()
    {
        var classHandle = JNIEnv.FindClass(ClassName);
        var method = JNIEnv.GetStaticMethodID(classHandle, "getFrameCount", "()J");
        return JNIEnv.CallStaticLongMethod(classHandle, method);
    }

    private static void CallStaticVoid(string name, string signature)
    {
        var classHandle = JNIEnv.FindClass(ClassName);
        var method = JNIEnv.GetStaticMethodID(classHandle, name, signature);
        JNIEnv.CallStaticVoidMethod(classHandle, method);
    }
}
