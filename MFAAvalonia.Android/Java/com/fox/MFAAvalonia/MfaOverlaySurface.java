package com.fox.MFAAvalonia;

import android.os.Build;
import android.util.Log;
import android.view.SurfaceControl;
import android.view.Surface;
import android.view.View;

import java.lang.reflect.Method;
import java.lang.reflect.Field;

/** Applies the SurfaceFlinger skip-screenshot bit to MFA's owned overlay surface. */
public final class MfaOverlaySurface {
    private static final String TAG = "MfaCurrentScreenOverlay";

    private MfaOverlaySurface() {
    }

    public static boolean excludeFromScreenshots(View view) {
        if (view == null || Build.VERSION.SDK_INT < 31)
            return false;
        try {
            Method getViewRoot = View.class.getDeclaredMethod("getViewRootImpl");
            getViewRoot.setAccessible(true);
            Object viewRoot = getViewRoot.invoke(view);
            if (viewRoot == null)
                return false;

            Object surface = findSurfaceControl(viewRoot);
            if (!(surface instanceof SurfaceControl) || !((SurfaceControl) surface).isValid())
                return false;

            SurfaceControl.Transaction transaction = new SurfaceControl.Transaction();
            try {
                Method setSkipScreenshot = findMethod(SurfaceControl.Transaction.class,
                        "setSkipScreenshot", SurfaceControl.class, boolean.class);
                setSkipScreenshot.setAccessible(true);
                setSkipScreenshot.invoke(transaction, surface, true);
                transaction.apply();
            } finally {
                // Transaction#close is not present on some old vendor APIs. Do not call
                // it directly because a missing optional method can escape verification.
                try {
                    Method close = SurfaceControl.Transaction.class.getMethod("close");
                    close.invoke(transaction);
                } catch (Throwable ignored) {
                    // The transaction has already been applied; GC can reclaim it.
                }
            }
            Log.i(TAG, "Overlay surface excluded from screenshots.");
            return true;
        } catch (Throwable exception) {
            Log.w(TAG, "Unable to exclude overlay surface from screenshots", exception);
            return false;
        }
    }

    private static SurfaceControl findSurfaceControl(Object owner) throws Exception {
        Class<?> current = owner.getClass();
        while (current != null) {
            for (Method method : current.getDeclaredMethods()) {
                if (method.getParameterTypes().length == 0
                        && SurfaceControl.class.isAssignableFrom(method.getReturnType())) {
                    method.setAccessible(true);
                    Object value = method.invoke(owner);
                    if (value instanceof SurfaceControl)
                        return (SurfaceControl) value;
                }
            }
            for (Field field : current.getDeclaredFields()) {
                if (!SurfaceControl.class.isAssignableFrom(field.getType()))
                    continue;
                field.setAccessible(true);
                Object value = field.get(owner);
                if (value instanceof SurfaceControl)
                    return (SurfaceControl) value;
            }
            current = current.getSuperclass();
        }

        // Some Android 12 vendor ViewRootImpl variants retain only a Surface. Scan
        // that object as well because vendors may expose its backing SurfaceControl.
        current = owner.getClass();
        while (current != null) {
            for (Field field : current.getDeclaredFields()) {
                if (!Surface.class.isAssignableFrom(field.getType()))
                    continue;
                field.setAccessible(true);
                Object value = field.get(owner);
                if (value != null)
                    return findSurfaceControl(value);
            }
            current = current.getSuperclass();
        }
        throw new NoSuchFieldException(owner.getClass().getName()
                + " contains no SurfaceControl member");
    }

    private static Method findMethod(Class<?> type, String name, Class<?>... parameterTypes)
            throws NoSuchMethodException {
        Class<?> current = type;
        while (current != null) {
            try {
                return current.getDeclaredMethod(name, parameterTypes);
            } catch (NoSuchMethodException ignored) {
                current = current.getSuperclass();
            }
        }
        throw new NoSuchMethodException(type.getName() + "." + name);
    }

    private static Field findField(Class<?> type, String name) throws NoSuchFieldException {
        Class<?> current = type;
        while (current != null) {
            try {
                return current.getDeclaredField(name);
            } catch (NoSuchFieldException ignored) {
                current = current.getSuperclass();
            }
        }
        throw new NoSuchFieldException(type.getName() + "." + name);
    }
}
