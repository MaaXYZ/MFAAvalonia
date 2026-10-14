using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using System;

namespace MFAAvalonia.Android;

internal static class AndroidPythonServiceInterop
{
    public static void Prepare(Activity activity, string serviceClass) =>
        CallWithContext(activity, serviceClass, "prepare", "(Landroid/content/Context;)V");

    public static void Start(Activity activity, string serviceClass, string argument)
    {
        var classHandle = FindClass(serviceClass);
        using var javaArgument = new Java.Lang.String(argument);
        // The p4a-generated convenience method always requests a background
        // service. Build its intent instead so the Agent remains alive when
        // StartApp moves MFA's Activity behind the game.
        var getIntent = JNIEnv.GetStaticMethodID(
            classHandle,
            "getDefaultIntent",
            "(Landroid/content/Context;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)Landroid/content/Intent;");
        using var empty = new Java.Lang.String(string.Empty);
        using var title = new Java.Lang.String("MFA Agent");
        using var text = new Java.Lang.String("Python Agent is running");
        var intentHandle = JNIEnv.CallStaticObjectMethod(classHandle, getIntent,
        [
            new JValue(activity),
            new JValue(empty),
            new JValue(title),
            new JValue(text),
            new JValue(javaArgument),
        ]);
        using var intent = Java.Lang.Object.GetObject<Intent>(intentHandle, JniHandleOwnership.TransferLocalRef)
            ?? throw new InvalidOperationException("python-for-android service intent was not created.");
        intent.PutExtra("serviceStartAsForeground", "true");
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            activity.StartForegroundService(intent);
        else
            activity.StartService(intent);
    }

    public static void Stop(Activity activity, string serviceClass) =>
        CallWithContext(activity, serviceClass, "stop", "(Landroid/content/Context;)V");

    private static void CallWithContext(Activity activity, string serviceClass, string methodName, string signature)
    {
        var classHandle = FindClass(serviceClass);
        var method = JNIEnv.GetStaticMethodID(classHandle, methodName, signature);
        JNIEnv.CallStaticVoidMethod(classHandle, method, [new JValue(activity)]);
    }

    private static IntPtr FindClass(string serviceClass)
    {
        var jniName = serviceClass.Replace('.', '/');
        var classHandle = JNIEnv.FindClass(jniName);
        if (classHandle == IntPtr.Zero)
            throw new InvalidOperationException($"python-for-android service class was not found: {serviceClass}");
        return classHandle;
    }
}
