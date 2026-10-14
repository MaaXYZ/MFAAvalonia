using Android.Runtime;
using Android.Views;
using System;

namespace MFAAvalonia.Android;

internal static class MfaOverlaySurfaceInterop
{
    private const string ClassName = "com/fox/MFAAvalonia/MfaOverlaySurface";

    internal static bool TryExcludeFromScreenshots(View view)
    {
        try
        {
            // Xamarin returns a managed/global class reference here. It must not be
            // passed to DeleteLocalRef; CheckJNI treats that as a fatal VM error.
            var classHandle = JNIEnv.FindClass(ClassName);
            var method = JNIEnv.GetStaticMethodID(classHandle, "excludeFromScreenshots",
                "(Landroid/view/View;)Z");
            return JNIEnv.CallStaticBooleanMethod(classHandle, method,
                new JValue(view));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("MfaCurrentScreenOverlay",
                $"Overlay screenshot JNI bridge unavailable: {ex.Message}");
            return false;
        }
    }
}
