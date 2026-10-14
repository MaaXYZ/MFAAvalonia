package com.fox.MFAAvalonia;

import android.content.Context;
import android.content.AttributionSource;
import android.content.pm.ApplicationInfo;
import android.net.Uri;
import android.os.Bundle;
import android.os.IBinder;
import android.os.Binder;
import android.os.Looper;
import android.os.Process;
import android.util.Log;

import java.lang.reflect.Method;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;

/** Starts the controller backend as a real shell process, before Binder is initialized. */
public final class MfaShellServiceStarter {
    private static final String TAG = "MFAAvalonia";

    private MfaShellServiceStarter() { }

    public static void main(String[] args) {
        try {
            if (Process.myUid() != Process.SHELL_UID)
                throw new SecurityException("shell helper started with uid=" + Process.myUid());
            if (args.length != 1)
                throw new IllegalArgumentException("bootstrap authority is required");
            if (Looper.getMainLooper() == null)
                Looper.prepareMainLooper();

            Context systemContext = createShellSystemContext();
            Context shellContext = systemContext.createPackageContext(
                    "com.android.shell", Context.CONTEXT_IGNORE_SECURITY);
            IBinder service = new MfaShizukuUserService(shellContext);
            Bundle extras = new Bundle();
            extras.putBinder("service", service);
            Bundle result = attachViaExternalProvider(args[0], extras);
            if (result == null || !result.getBoolean("accepted", false))
                throw new IllegalStateException("application rejected shell helper binder");
            IBinder lifecycle = result.getBinder("lifecycle");
            if (lifecycle == null)
                throw new IllegalStateException("application lifecycle binder is missing");
            lifecycle.linkToDeath(() -> System.exit(0), 0);

            Log.i(TAG, "Shell helper attached: uid=" + Process.myUid());
            Looper.loop();
        } catch (Throwable exception) {
            Log.e(TAG, "Shell helper failed", exception);
            System.exit(1);
        }
    }

    private static Context createShellSystemContext() throws Exception {
        Class<?> activityThreadClass = Class.forName("android.app.ActivityThread");
        Constructor<?> constructor = activityThreadClass.getDeclaredConstructor();
        constructor.setAccessible(true);
        Object thread = constructor.newInstance();

        Field current = activityThreadClass.getDeclaredField("sCurrentActivityThread");
        current.setAccessible(true);
        current.set(null, thread);
        Field systemThread = activityThreadClass.getDeclaredField("mSystemThread");
        systemThread.setAccessible(true);
        systemThread.setBoolean(thread, true);

        Class<?> bindDataClass = Class.forName("android.app.ActivityThread$AppBindData");
        Constructor<?> bindDataConstructor = bindDataClass.getDeclaredConstructor();
        bindDataConstructor.setAccessible(true);
        Object bindData = bindDataConstructor.newInstance();
        ApplicationInfo appInfo = new ApplicationInfo();
        appInfo.packageName = "com.android.shell";
        Field appInfoField = bindDataClass.getDeclaredField("appInfo");
        appInfoField.setAccessible(true);
        appInfoField.set(bindData, appInfo);
        Field boundApplication = activityThreadClass.getDeclaredField("mBoundApplication");
        boundApplication.setAccessible(true);
        boundApplication.set(thread, bindData);

        Method getSystemContext = activityThreadClass.getDeclaredMethod("getSystemContext");
        getSystemContext.setAccessible(true);
        return (Context) getSystemContext.invoke(thread);
    }

    private static Bundle attachViaExternalProvider(String authority, Bundle extras)
            throws Exception {
        Class<?> serviceManagerClass = Class.forName("android.os.ServiceManager");
        IBinder activityBinder = (IBinder) serviceManagerClass
                .getMethod("getService", String.class).invoke(null, "activity");
        Class<?> stubClass = Class.forName("android.app.IActivityManager$Stub");
        Object activityManager = stubClass.getMethod("asInterface", IBinder.class)
                .invoke(null, activityBinder);
        IBinder providerToken = new Binder();
        Method external = activityManager.getClass().getMethod(
                "getContentProviderExternal", String.class, int.class,
                IBinder.class, String.class);
        Object holder = external.invoke(activityManager, authority, 0,
                providerToken, authority);
        if (holder == null)
            throw new IllegalStateException("bootstrap provider is unavailable");
        Field providerField = holder.getClass().getDeclaredField("provider");
        providerField.setAccessible(true);
        Object provider = providerField.get(holder);
        if (provider == null)
            throw new IllegalStateException("bootstrap provider binder is unavailable");

        AttributionSource source = new AttributionSource.Builder(Process.SHELL_UID)
                .setPackageName("com.android.shell").build();
        for (Method method : provider.getClass().getMethods()) {
            if (!"call".equals(method.getName()))
                continue;
            Class<?>[] types = method.getParameterTypes();
            if (types.length == 5 && types[0] == AttributionSource.class) {
                return (Bundle) method.invoke(provider, source, authority,
                        "attach", null, extras);
            }
        }
        throw new NoSuchMethodException("compatible IContentProvider.call");
    }
}
