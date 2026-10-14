package com.fox.MFAAvalonia;

import android.app.ActivityManager;
import android.app.ActivityOptions;
import android.content.AttributionSource;
import android.content.ComponentName;
import android.content.Context;
import android.content.ContextWrapper;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Rect;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.os.Binder;
import android.os.Build;
import android.os.Bundle;
import android.os.IBinder;
import android.os.IInterface;
import android.os.Parcel;
import android.os.Process;
import android.os.RemoteException;
import android.os.SystemClock;
import android.system.OsConstants;
import android.system.Os;
import android.system.ErrnoException;
import android.util.Log;
import android.view.InputDevice;
import android.view.InputEvent;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class MfaShizukuUserService extends Binder {
    private static final String TAG = "MFAAvalonia";
    private static final int HEALTH_TRANSACTION = 1;
    private static final int CREATE_DISPLAY_TRANSACTION = 2;
    private static final int RELEASE_DISPLAY_TRANSACTION = 3;
    private static final int START_APP_TRANSACTION = 4;
    private static final int CREATE_PRIMARY_CAPTURE_TRANSACTION = 5;
    private static final int GET_DISPLAY_INFO_TRANSACTION = 6;
    private static final int RESOLVE_CAPTURE_DISPLAY_TRANSACTION = 7;
    private static final int GET_FOCUSED_DISPLAY_TRANSACTION = 8;
    private static final int SET_GAME_KEEP_ALIVE_TRANSACTION = 9;
    private static final int OVERLAY_INPUT_STATE_TRANSACTION = 1;
    private static final int DESTROY_TRANSACTION = 16777115;

    private final Context context;
    private final InputServer inputServer;
    private VirtualDisplay virtualDisplay;
    private IBinder primaryCaptureToken;
    private Surface virtualDisplaySurface;
    private int primaryCaptureDisplayId = -1;
    private int primaryCaptureWidth;
    private int primaryCaptureHeight;
    private volatile int clientPid = -1;
    private static volatile String clientPackageName;
    // True only when the currently active virtual display was created with the
    // independent-focus flags. Recovery is based on the actual display, not on
    // whether Shizuku happens to be running as root or shell.
    private static volatile boolean activeDisplayOwnsFocus;
    private volatile boolean userActivityKeepAlive;
    private volatile boolean focusedDisplayReflectionFailed;
    private Thread userActivityThread;

    private static boolean requiresHostFocusRecovery() {
        return !activeDisplayOwnsFocus;
    }

    public MfaShizukuUserService(Context context) throws IOException {
        this.context = context;
        if (Process.myUid() == Process.ROOT_UID)
            launchShellHelper(context);
        inputServer = new InputServer(context, this::routeCaptureToDisplay);
        inputServer.start();
        startClientWatchdog();
        Log.i(TAG, "Shizuku UserService started, uid=" + Process.myUid()
                + ", port=" + inputServer.getPort());
    }

    private static void launchShellHelper(Context context) throws IOException {
        try {
            String apk = context.getApplicationInfo().sourceDir;
            String packageName = context.getPackageName();
            String command = "CLASSPATH=" + shellQuote(apk)
                    + " app_process /system/bin --nice-name="
                    + shellQuote(packageName + ":mfa_shell_service")
                    + " com.fox.MFAAvalonia.MfaShellServiceStarter "
                    + shellQuote(packageName + ".mfa.shell.bootstrap");
            new ProcessBuilder("su", Integer.toString(Process.SHELL_UID),
                    "sh", "-c", command).start();
            Log.i(TAG, "Started shell virtual-display helper before Binder initialization.");
        } catch (Throwable exception) {
            throw new IOException("Unable to launch shell virtual-display helper", exception);
        }
    }

    private static String shellQuote(String value) {
        return "'" + value.replace("'", "'\\\"'\\\"'") + "'";
    }

    @Override
    protected boolean onTransact(int code, Parcel data, Parcel reply, int flags)
            throws RemoteException {
        if (code == HEALTH_TRANSACTION) {
            int callingUid = Binder.getCallingUid();
            if (data != null && data.dataAvail() >= 4) {
                clientPid = data.readInt();
                if (data.dataAvail() > 0)
                    inputServer.setOverlayInputStateCallback(data.readStrongBinder());
            }
            try {
                String[] packages = context.getPackageManager().getPackagesForUid(callingUid);
                if (packages != null && packages.length > 0)
                    clientPackageName = packages[0];
            } catch (Throwable exception) {
                Log.w(TAG, "Unable to resolve MFA client package for uid=" + callingUid,
                        exception);
            }
            if (reply != null) {
                reply.writeInt(Process.myUid());
                reply.writeInt(inputServer.getPort());
            }
            return true;
        }
        if (code == CREATE_DISPLAY_TRANSACTION) {
            int width = data.readInt();
            int height = data.readInt();
            int dpi = data.readInt();
            Surface surface = Surface.CREATOR.createFromParcel(data);
            // Keep the inbound MFA Binder identity. The UserService is launched by
            // Shizuku with root/shell privileges, but DisplayManager validates the
            // attribution package against the calling Binder UID. The matching package
            // Context is selected in createVirtualDisplay(); clearing identity here
            // would pair the app package with UID 0 and trigger packageName mismatch.
            // Keep the inbound app identity. On Android 12 DisplayManager validates the
            // attribution package against that identity; a root process using the shell
            // package would otherwise fail with "packageName must match the calling uid".
            // Do not clear the inbound Binder identity here. A root process has no
            // package registered for uid 0 on Android, so DisplayManager rejects every
            // attribution context with "packageName must match the calling uid". Keeping
            // the MFA caller identity pairs com.fox.MFAAvalonia with its actual app uid;
            // if that identity lacks a required display permission, the client will
            // transparently retry through the shell helper.
            DisplayCreationResult result = createVirtualDisplay(width, height, dpi, surface);
            if (reply != null) {
                reply.writeInt(result.displayId);
                reply.writeString(result.error);
                reply.writeInt(result.flags);
            }
            return true;
        }
        if (code == RELEASE_DISPLAY_TRANSACTION) {
            releaseVirtualDisplay();
            return true;
        }
        if (code == START_APP_TRANSACTION) {
            int displayId = data.readInt();
            boolean forceStop = data.readInt() != 0;
            String target = data.readString();
            int result = inputServer.startApp(
                    displayId, target == null ? "" : target, forceStop);
            if (reply != null) {
                reply.writeInt(result);
            }
            return true;
        }
        if (code == CREATE_PRIMARY_CAPTURE_TRANSACTION) {
            int displayId = data.readInt();
            int width = data.readInt();
            int height = data.readInt();
            Surface surface = Surface.CREATOR.createFromParcel(data);
            String error = createPrimaryDisplayCapture(displayId, width, height, surface);
            if (reply != null)
                reply.writeString(error);
            return true;
        }
        if (code == GET_DISPLAY_INFO_TRANSACTION) {
            int displayId = data.readInt();
            int[] info;
            try {
                info = getLogicalDisplayInfo(displayId);
            } catch (Throwable exception) {
                Log.e(TAG, "Unable to query DisplayInfo for display " + displayId, exception);
                info = new int[] { 0, 0, 0, 0 };
            }
            if (reply != null) {
                reply.writeInt(info[0]);
                reply.writeInt(info[1]);
                reply.writeInt(info[2]);
                reply.writeInt(info[3]);
            }
            return true;
        }
        if (code == RESOLVE_CAPTURE_DISPLAY_TRANSACTION) {
            int fallbackDisplayId = data.readInt();
            int resolvedDisplayId = resolveCurrentScreenDisplayId(fallbackDisplayId);
            if (reply != null)
                reply.writeInt(resolvedDisplayId);
            return true;
        }
        if (code == GET_FOCUSED_DISPLAY_TRANSACTION) {
            int fallbackDisplayId = data.readInt();
            FocusedDisplayTarget target = getFocusedDisplayTarget(fallbackDisplayId);
            if (reply != null) {
                reply.writeInt(target.displayId);
                reply.writeString(target.packageName);
            }
            return true;
        }
        if (code == SET_GAME_KEEP_ALIVE_TRANSACTION) {
            int displayId = data.readInt();
            boolean enabled = data.readInt() != 0;
            if (enabled)
                inputServer.startAppWatchdog(displayId);
            else
                inputServer.stopAppWatchdog();
            return true;
        }
        if (code == DESTROY_TRANSACTION) {
            Log.i(TAG, "Destroy transaction received.");
            releaseVirtualDisplay();
            inputServer.shutdown();
            System.exit(0);
            return true;
        }
        return super.onTransact(code, data, reply, flags);
    }

    private synchronized DisplayCreationResult createVirtualDisplay(
            int width, int height, int dpi, Surface surface) {
        releaseVirtualDisplay();
        String lastError = "No compatible virtual display configuration was accepted.";
        int lastFlags = 0;
        try {
            Context shellPackageContext;
            try {
                shellPackageContext = context.createPackageContext(
                        ShellContext.PACKAGE_NAME, Context.CONTEXT_IGNORE_SECURITY);
            } catch (Throwable exception) {
                Log.w(TAG, "Shell package context is unavailable; using UserService context", exception);
                shellPackageContext = context;
            }
            Context systemContext = tryGetSystemContext();
            boolean shellIdentity = Process.myUid() == Process.SHELL_UID;
            // Shizuku UserService normally runs under the hosting application's UID.
            // In that case a shell-attributed Context is rejected by DisplayManager
            // before virtual-display permission checks even begin. Keep the package and
            // attribution tied to the real process UID; reserve ShellContext for a
            // genuinely shell/root/system UserService.
            Context appContext = contextForProcessUid(context);
            Log.i(TAG, "Virtual display identity: uid=" + Process.myUid()
                    + ", context=" + appContext.getPackageName()
                    + ", shellIdentity=" + shellIdentity);
            Context[] contextCandidates = shellIdentity
                    ? systemContext != null && systemContext != shellPackageContext
                            ? new Context[] {
                                    new ShellContext(systemContext),
                                    new ShellContext(shellPackageContext)
                            }
                            : new Context[] { new ShellContext(shellPackageContext) }
                    : systemContext != null && systemContext != appContext
                            ? new Context[] { appContext, systemContext }
                            : new Context[] { appContext };
            int basicFlags = DisplayManager.VIRTUAL_DISPLAY_FLAG_PUBLIC
                    | DisplayManager.VIRTUAL_DISPLAY_FLAG_PRESENTATION
                    | DisplayManager.VIRTUAL_DISPLAY_FLAG_OWN_CONTENT_ONLY
                    | (1 << 6); // SUPPORTS_TOUCH
            int destroyFlags = basicFlags | (1 << 8); // DESTROY_CONTENT_ON_REMOVAL
            int android13Flags = destroyFlags;
            if (Build.VERSION.SDK_INT >= 33) {
                android13Flags |= (1 << 10)  // TRUSTED
                        | (1 << 11) // OWN_DISPLAY_GROUP
                        | (1 << 12) // ALWAYS_UNLOCKED
                        | (1 << 13); // TOUCH_FEEDBACK_DISABLED
            }
            int fullFlags = android13Flags;
            if (Build.VERSION.SDK_INT >= 34) {
                fullFlags |= (1 << 14)  // OWN_FOCUS
                        | (1 << 15) // DEVICE_DISPLAY_GROUP
                        | (1 << 16); // STEAL_TOP_FOCUS_DISABLED
            }
            // Follow MaaFwApp's API gates exactly. Passing Android 14 focus flags to
            // MuMu's Android 12 fork is accepted numerically, but changes its task/tab
            // focus handling when the first directed touch reaches the display.
            int focusFlags = fullFlags;
            // Match MaaFwApp: request a trusted, own-focus display first. Android 15
            // builds that grant ADD_TRUSTED_DISPLAY to the wireless-debugging shell
            // keep the virtual task isolated from the host task. Older ROMs reject
            // these flags; the candidate loop below then falls back to plain flags.
            boolean trustedDisplayAvailable = Process.myUid() == 0 || Process.myUid() == 1000;
            int trustedFlags = focusFlags
                    | (1 << 10)  // TRUSTED
                    | (1 << 11)  // OWN_DISPLAY_GROUP
                    | (1 << 12)  // ALWAYS_UNLOCKED
                    | (1 << 13); // TOUCH_FEEDBACK_DISABLED
            int requiredFlags = trustedFlags;
            if (!trustedDisplayAvailable && Build.VERSION.SDK_INT >= 33)
                Log.i(TAG, "Trying MaaFwApp trusted virtual-display flags for uid="
                        + Process.myUid() + "; plain flags remain fallback.");
            int[] flagCandidates = requiredFlags == destroyFlags
                    ? new int[] { destroyFlags }
                    : new int[] { requiredFlags, destroyFlags };
            int previousFlags = Integer.MIN_VALUE;
            StringBuilder attemptErrors = new StringBuilder();
            for (Context candidateContext : contextCandidates) {
                previousFlags = Integer.MIN_VALUE;
                for (int candidateFlags : flagCandidates) {
                    if (candidateFlags == previousFlags)
                        continue;
                    previousFlags = candidateFlags;
                    lastFlags = candidateFlags;
                    try {
                        DisplayManager displayManager = createDisplayManager(candidateContext);
                        Log.i(TAG, "Creating Shizuku virtual display: context="
                                + candidateContext.getPackageName() + ", flags=0x"
                                + Integer.toHexString(candidateFlags));
                        VirtualDisplay display = displayManager.createVirtualDisplay(
                                "MaaFwVirtualDisplay", width, height, dpi, surface,
                                candidateFlags);
                        if (display == null || display.getDisplay() == null) {
                            if (display != null)
                                display.release();
                            String attemptError = "DisplayManager returned an empty VirtualDisplay"
                                    + " (context=" + candidateContext.getPackageName()
                                    + ", flags=0x" + Integer.toHexString(candidateFlags) + ").";
                            if (attemptErrors.length() > 0)
                                attemptErrors.append(" | ");
                            attemptErrors.append(attemptError);
                            lastError = attemptErrors.toString();
                            Log.w(TAG, attemptError);
                            continue;
                        }

                        virtualDisplay = display;
                        virtualDisplaySurface = surface;
                        activeDisplayOwnsFocus = (candidateFlags & (1 << 10)) != 0
                                && (candidateFlags & (1 << 14)) != 0
                                && (candidateFlags & (1 << 16)) != 0;
                        int displayId = display.getDisplay().getDisplayId();
                        inputServer.setCurrentScreenCapture(false);
                        startUserActivityKeepAlive(displayId);
                        Log.i(TAG, "Shizuku virtual display created: " + width + "x" + height
                                + ", dpi=" + dpi + ", display=" + displayId
                                + ", context=" + candidateContext.getPackageName()
                                + ", flags=0x" + Integer.toHexString(candidateFlags)
                                + ", ownsFocus=" + activeDisplayOwnsFocus);
                        return new DisplayCreationResult(displayId, null, candidateFlags);
                    } catch (Throwable exception) {
                        String attemptError = describeException(exception)
                                + " (context=" + candidateContext.getPackageName()
                                + ", flags=0x" + Integer.toHexString(candidateFlags) + ")";
                        if (attemptErrors.length() > 0)
                            attemptErrors.append(" | ");
                        attemptErrors.append(attemptError);
                        lastError = attemptErrors.toString();
                        Log.w(TAG, "Virtual display attempt failed: " + attemptError, exception);
                    }
                }
            }
        } catch (Throwable exception) {
            lastError = describeException(exception);
            Log.e(TAG, "Shizuku virtual display setup failed", exception);
        }
        surface.release();
        activeDisplayOwnsFocus = false;
        return new DisplayCreationResult(-1, lastError, lastFlags);
    }

    private synchronized String createPrimaryDisplayCapture(
            int displayId, int width, int height, Surface surface) {
        releaseVirtualDisplay();
        try {
            // MaaFwApp's PrimaryDisplayManager uses this shell-only hidden API to
            // mirror Display.DEFAULT_DISPLAY into the native capturer. This is a
            // capture-only VirtualDisplay and does not create/move an app task.
            Method method = DisplayManager.class.getMethod(
                    "createVirtualDisplay", String.class, int.class, int.class,
                    int.class, Surface.class);
            VirtualDisplay display = (VirtualDisplay) method.invoke(
                    null, "MFA_PRIMARY_CAPTURE", width, height, displayId, surface);
            if (display == null)
                throw new IllegalStateException("DisplayManager returned an empty primary capture");
            virtualDisplay = display;
            virtualDisplaySurface = surface;
            primaryCaptureDisplayId = displayId;
            primaryCaptureWidth = width;
            primaryCaptureHeight = height;
            inputServer.setCurrentScreenCapture(true);
            Log.i(TAG, "Primary display capture started: source=" + displayId
                    + ", size=" + width + "x" + height);
            return null;
        } catch (Throwable displayManagerException) {
            Log.w(TAG, "DisplayManager primary mirror is unavailable; using SurfaceControl",
                    displayManagerException);
            try {
                Class<?> surfaceControl = Class.forName("android.view.SurfaceControl");
                IBinder token = (IBinder) surfaceControl
                        .getDeclaredMethod("createDisplay", String.class, boolean.class)
                        .invoke(null, "MFA_PRIMARY_CAPTURE", false);
                if (token == null)
                    throw new IllegalStateException("SurfaceControl.createDisplay returned null");
                int layerStack = getDisplayLayerStack(displayId);
                Rect sourceRect = new Rect(0, 0, width, height);
                Method open = surfaceControl.getDeclaredMethod("openTransaction");
                Method close = surfaceControl.getDeclaredMethod("closeTransaction");
                open.invoke(null);
                try {
                    surfaceControl.getDeclaredMethod("setDisplaySurface", IBinder.class, Surface.class)
                            .invoke(null, token, surface);
                    surfaceControl.getDeclaredMethod("setDisplayProjection", IBinder.class, int.class,
                                    Rect.class, Rect.class)
                            .invoke(null, token, 0, sourceRect, sourceRect);
                    surfaceControl.getDeclaredMethod("setDisplayLayerStack", IBinder.class, int.class)
                            .invoke(null, token, layerStack);
                } finally {
                    close.invoke(null);
                }
                primaryCaptureToken = token;
                virtualDisplaySurface = surface;
                primaryCaptureDisplayId = displayId;
                primaryCaptureWidth = width;
                primaryCaptureHeight = height;
                inputServer.setCurrentScreenCapture(true);
                Log.i(TAG, "Primary display capture started with SurfaceControl: source="
                        + displayId + ", layerStack=" + layerStack + ", size="
                        + width + "x" + height);
                return null;
            } catch (Throwable surfaceControlException) {
                surface.release();
                String error = "DisplayManager=" + describeException(displayManagerException)
                        + "; SurfaceControl=" + describeException(surfaceControlException);
                Log.e(TAG, "Primary display capture failed: " + error, surfaceControlException);
                return error;
            }
        }
    }

    private static int getDisplayLayerStack(int displayId) throws Exception {
        return getLogicalDisplayInfo(displayId)[3];
    }

    private static int[] getLogicalDisplayInfo(int displayId) throws Exception {
        Class<?> globalClass = Class.forName("android.hardware.display.DisplayManagerGlobal");
        Object global = globalClass.getDeclaredMethod("getInstance").invoke(null);
        Object info = globalClass.getDeclaredMethod("getDisplayInfo", int.class)
                .invoke(global, displayId);
        if (info == null)
            throw new IllegalStateException("No DisplayInfo for display " + displayId);
        return new int[] {
                readIntField(info, "logicalWidth"),
                readIntField(info, "logicalHeight"),
                readIntField(info, "rotation"),
                readIntField(info, "layerStack")
        };
    }

    @SuppressWarnings("deprecation")
    private int resolveCurrentScreenDisplayId(int fallbackDisplayId) {
        try {
            Class<?> globalClass = Class.forName("android.hardware.display.DisplayManagerGlobal");
            Object global = globalClass.getDeclaredMethod("getInstance").invoke(null);
            int[] displayIds = (int[]) globalClass.getDeclaredMethod("getDisplayIds").invoke(global);
            // A normal phone has one logical display (occasionally one presentation
            // display). MuMu exposes every application tab as a separate physical
            // display, so the Activity display is not the game display.
            if (displayIds == null || displayIds.length <= 2)
                return fallbackDisplayId;

            ActivityManager manager = context.getSystemService(ActivityManager.class);
            if (manager == null)
                return 0;
            PackageManager packages = context.getPackageManager();
            Intent homeIntent = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME);
            ComponentName homeComponent = homeIntent.resolveActivity(packages);
            String homePackage = homeComponent == null ? null : homeComponent.getPackageName();

            String lastControlledPackage = inputServer.getLastControlledPackage();
            if (lastControlledPackage != null) {
                InputServer.TaskPlacement placement =
                        inputServer.findPackageTask(lastControlledPackage);
                if (placement != null && placement.displayId >= 0
                        && placement.displayId != fallbackDisplayId) {
                    Log.i(TAG, "Current-screen target restored from the last controlled app: package="
                            + lastControlledPackage + ", display=" + placement.displayId
                            + ", MFA display=" + fallbackDisplayId);
                    return placement.displayId;
                }
            }

            FocusedDisplayTarget focused = getFocusedDisplayTarget(fallbackDisplayId);
            if (focused.displayId >= 0 && focused.displayId != fallbackDisplayId
                    && isEligibleCurrentScreenPackage(
                            focused.packageName, packages, homePackage)) {
                Log.i(TAG, "Current-screen target resolved from global focus: package="
                        + focused.packageName + ", display=" + focused.displayId
                        + ", MFA display=" + fallbackDisplayId);
                return focused.displayId;
            }

            String candidatePackage = null;
            int candidateDisplayId = -1;
            boolean ambiguous = false;
            for (ActivityManager.RunningTaskInfo task : manager.getRunningTasks(100)) {
                ComponentName component = task.topActivity != null
                        ? task.topActivity : task.baseActivity;
                if (component == null)
                    continue;
                String packageName = component.getPackageName();
                if (!isEligibleCurrentScreenPackage(packageName, packages, homePackage))
                    continue;
                int displayId = readTaskDisplayId(task);
                if (displayId >= 0 && displayId != fallbackDisplayId) {
                    if (candidateDisplayId < 0) {
                        candidatePackage = packageName;
                        candidateDisplayId = displayId;
                    } else if (candidateDisplayId != displayId
                            || !packageName.equals(candidatePackage)) {
                        ambiguous = true;
                        break;
                    }
                }
            }
            if (!ambiguous && candidateDisplayId >= 0) {
                Log.i(TAG, "Current-screen target resolved from the only eligible task: package="
                        + candidatePackage + ", display=" + candidateDisplayId
                        + ", MFA display=" + fallbackDisplayId);
                return candidateDisplayId;
            }

            // MuMu gives every application tab its own physical display. Capturing the
            // activity display here would bind the controller to MFA itself and StartApp
            // would subsequently replace MFA's tab with the game. Display 0 is a neutral
            // launcher target until StartApp can discover the actual game package and
            // rebind capture/input to the display MuMu creates for it.
            Log.w(TAG, "No unambiguous non-MFA current-screen target is available"
                    + (ambiguous ? " (multiple eligible tasks)" : "")
                    + "; using neutral display 0 until StartApp resolves the game."
                    + " MFA display=" + fallbackDisplayId);
            return 0;
        } catch (Throwable exception) {
            Log.w(TAG, "Unable to resolve a multi-display current-screen target", exception);
        }
        return 0;
    }

    private boolean isEligibleCurrentScreenPackage(
            String packageName, PackageManager packages, String homePackage) {
        if (packageName == null || packageName.isEmpty()
                || context.getPackageName().equals(packageName)
                || packageName.equals(homePackage))
            return false;
        try {
            return packages.getApplicationInfo(packageName, 0).uid >= 10000;
        } catch (PackageManager.NameNotFoundException ignored) {
            return false;
        }
    }

    private static int readTaskDisplayId(ActivityManager.RunningTaskInfo task) {
        try {
            return readIntField(task, "displayId");
        } catch (Throwable ignored) {
            return -1;
        }
    }

    @SuppressWarnings("deprecation")
    private FocusedDisplayTarget getFocusedDisplayTarget(int fallbackDisplayId) {
        if (!focusedDisplayReflectionFailed) {
            try {
                Class<?> activityTaskManager = Class.forName("android.app.ActivityTaskManager");
                Object service = activityTaskManager.getDeclaredMethod("getService").invoke(null);
                Class<?> serviceInterface = Class.forName("android.app.IActivityTaskManager");
                Method focusedTask = serviceInterface.getDeclaredMethod("getFocusedRootTaskInfo");
                focusedTask.setAccessible(true);
                Object value = focusedTask.invoke(service);
                if (value instanceof ActivityManager.RunningTaskInfo) {
                    ActivityManager.RunningTaskInfo task = (ActivityManager.RunningTaskInfo) value;
                    ComponentName component = task.topActivity != null
                            ? task.topActivity : task.baseActivity;
                    int displayId = readTaskDisplayId(task);
                    return new FocusedDisplayTarget(
                            displayId >= 0 ? displayId : fallbackDisplayId,
                            component == null ? null : component.getPackageName());
                }
            } catch (Throwable exception) {
                focusedDisplayReflectionFailed = true;
                Log.w(TAG, "Unable to query the globally focused display; using task order", exception);
            }
        }

        try {
            ActivityManager manager = context.getSystemService(ActivityManager.class);
            if (manager != null) {
                List<ActivityManager.RunningTaskInfo> tasks = manager.getRunningTasks(1);
                if (tasks != null && !tasks.isEmpty()) {
                    ActivityManager.RunningTaskInfo task = tasks.get(0);
                    ComponentName component = task.topActivity != null
                            ? task.topActivity : task.baseActivity;
                    int displayId = readTaskDisplayId(task);
                    return new FocusedDisplayTarget(
                            displayId >= 0 ? displayId : fallbackDisplayId,
                            component == null ? null : component.getPackageName());
                }
            }
        } catch (Throwable exception) {
            Log.w(TAG, "Unable to resolve focused display from running tasks", exception);
        }
        return new FocusedDisplayTarget(fallbackDisplayId, null);
    }

    private static final class FocusedDisplayTarget {
        final int displayId;
        final String packageName;

        FocusedDisplayTarget(int displayId, String packageName) {
            this.displayId = displayId;
            this.packageName = packageName;
        }
    }

    private static int readIntField(Object value, String name) throws Exception {
        Class<?> type = value.getClass();
        while (type != null) {
            try {
                java.lang.reflect.Field field = type.getDeclaredField(name);
                field.setAccessible(true);
                return field.getInt(value);
            } catch (NoSuchFieldException ignored) {
                type = type.getSuperclass();
            }
        }
        throw new NoSuchFieldException(value.getClass().getName() + "." + name);
    }

    private static String describeException(Throwable exception) {
        Throwable current = exception;
        while (current.getCause() != null && current.getCause() != current)
            current = current.getCause();
        String message = current.getMessage();
        return current.getClass().getName()
                + (message == null || message.isEmpty() ? "" : ": " + message);
    }

    private static Context tryGetSystemContext() {
        try {
            Class<?> activityThreadClass = Class.forName("android.app.ActivityThread");
            Method currentActivityThread = activityThreadClass
                    .getDeclaredMethod("currentActivityThread");
            currentActivityThread.setAccessible(true);
            Object activityThread = currentActivityThread.invoke(null);
            if (activityThread == null)
                return null;
            Method getSystemContext = activityThreadClass.getDeclaredMethod("getSystemContext");
            getSystemContext.setAccessible(true);
            Context systemContext = (Context) getSystemContext.invoke(activityThread);
            if (systemContext != null)
                Log.i(TAG, "Using ActivityThread system context for virtual display attribution.");
            return systemContext;
        } catch (Throwable exception) {
            Log.w(TAG, "ActivityThread system context is unavailable", exception);
            return null;
        }
    }

    private static DisplayManager createDisplayManager(Context displayContext) throws Exception {
        // Match MaaFwApp: force DisplayManager to retain the shell-attributed
        // context. Context.getSystemService() would return a manager cached with
        // the app package and Android 15 rejects that package for the shell UID.
        Constructor<DisplayManager> constructor = DisplayManager.class
                .getDeclaredConstructor(Context.class);
        constructor.setAccessible(true);
        return constructor.newInstance(displayContext);
    }

    private static Context contextForProcessUid(Context fallback) {
        try {
            String[] packages = fallback.getPackageManager().getPackagesForUid(Process.myUid());
            if (packages != null) {
                for (String packageName : packages) {
                    if (packageName == null || packageName.equals(ShellContext.PACKAGE_NAME))
                        continue;
                    try {
                        return fallback.createPackageContext(packageName,
                                Context.CONTEXT_IGNORE_SECURITY);
                    } catch (Throwable ignored) {
                        // Try the next package registered to this UID.
                    }
                }
            }
        } catch (Throwable exception) {
            Log.w(TAG, "Unable to resolve package context for UserService UID", exception);
        }
        return fallback;
    }

    private static final class ShellContext extends ContextWrapper {
        static final String PACKAGE_NAME = "com.android.shell";

        ShellContext(Context base) {
            super(base);
        }

        @Override
        public String getPackageName() {
            return PACKAGE_NAME;
        }

        @Override
        public String getOpPackageName() {
            return PACKAGE_NAME;
        }

        @Override
        public Context getApplicationContext() {
            return this;
        }

        @Override
        public int checkCallingPermission(String permission) {
            return PackageManager.PERMISSION_GRANTED;
        }

        @Override
        public AttributionSource getAttributionSource() {
            if (Build.VERSION.SDK_INT >= 31) {
                return new AttributionSource.Builder(Process.SHELL_UID)
                        .setPackageName(PACKAGE_NAME)
                        .build();
            }
            return super.getAttributionSource();
        }

        @SuppressWarnings("unused")
        public int getDeviceId() {
            return 0;
        }
    }

    private static final class DisplayCreationResult {
        final int displayId;
        final String error;
        final int flags;

        DisplayCreationResult(int displayId, String error, int flags) {
            this.displayId = displayId;
            this.error = error;
            this.flags = flags;
        }
    }

    private synchronized void releaseVirtualDisplay() {
        activeDisplayOwnsFocus = false;
        inputServer.stopAppWatchdog();
        stopUserActivityKeepAlive();
        if (virtualDisplay != null) {
            virtualDisplay.release();
            virtualDisplay = null;
        }
        if (primaryCaptureToken != null) {
            try {
                Class.forName("android.view.SurfaceControl")
                        .getDeclaredMethod("destroyDisplay", IBinder.class)
                        .invoke(null, primaryCaptureToken);
            } catch (Throwable exception) {
                Log.w(TAG, "SurfaceControl primary capture release failed", exception);
            }
            primaryCaptureToken = null;
        }
        if (virtualDisplaySurface != null) {
            virtualDisplaySurface.release();
            virtualDisplaySurface = null;
        }
        primaryCaptureDisplayId = -1;
        inputServer.setCurrentScreenCapture(false);
    }

    private synchronized boolean rebindPrimaryCapture(int displayId) {
        if (displayId < 0 || displayId == primaryCaptureDisplayId)
            return true;
        if (virtualDisplaySurface == null || primaryCaptureWidth <= 0 || primaryCaptureHeight <= 0)
            return false;
        Surface surface = virtualDisplaySurface;
        // Keep the app-owned Surface alive while replacing only the mirror token.
        virtualDisplaySurface = null;
        releaseVirtualDisplay();
        String error = createPrimaryDisplayCapture(
                displayId, primaryCaptureWidth, primaryCaptureHeight, surface);
        if (error != null) {
            Log.e(TAG, "Unable to rebind primary capture to display " + displayId
                    + ": " + error);
            return false;
        }
        Log.i(TAG, "Primary capture rebound to game display " + displayId);
        return true;
    }

    private synchronized boolean routeCaptureToDisplay(int displayId) {
        if (primaryCaptureDisplayId >= 0)
            return rebindPrimaryCapture(displayId);
        // A real virtual display already captures into the bridge surface. It must not
        // be replaced by the primary-display mirror used by current-screen mode.
        // Replacing it here destroys the isolated display and its game task.
        return virtualDisplay != null
                && virtualDisplay.getDisplay() != null
                && virtualDisplay.getDisplay().getDisplayId() == displayId;
    }

    private void startClientWatchdog() {
        Thread watchdog = new Thread(() -> {
            while (true) {
                try {
                    Thread.sleep(5000);
                } catch (InterruptedException exception) {
                    Thread.currentThread().interrupt();
                    return;
                }
                int pid = clientPid;
                if (pid > 0 && !isProcessAlive(pid)) {
                    Log.i(TAG, "MFA process " + pid + " exited; stopping UserService.");
                    releaseVirtualDisplay();
                    inputServer.shutdown();
                    System.exit(0);
                    return;
                }
            }
        }, "MfaClientWatchdog");
        watchdog.setDaemon(true);
        watchdog.start();
    }

    private static boolean isProcessAlive(int pid) {
        if (new File("/proc/" + pid).exists())
            return true;
        try {
            // Signal 0 performs existence/permission checking without delivering a
            // signal. After dropping a root Shizuku service to shell, MuMu SELinux
            // hides app /proc entries and returns EPERM here even while the app is
            // alive. Only ESRCH is proof that the process is gone.
            Os.kill(pid, 0);
            return true;
        } catch (ErrnoException exception) {
            return exception.errno != OsConstants.ESRCH;
        }
    }

    private synchronized void startUserActivityKeepAlive(final int displayId) {
        stopUserActivityKeepAlive();
        userActivityKeepAlive = true;
        userActivityThread = new Thread(() -> {
            Log.i(TAG, "Starting display userActivity keep-alive for display " + displayId);
            while (userActivityKeepAlive) {
                try {
                    Thread.sleep(4000L);
                } catch (InterruptedException exception) {
                    Thread.currentThread().interrupt();
                    break;
                }
                if (userActivityKeepAlive)
                    sendUserActivity(displayId);
            }
            Log.i(TAG, "Stopped display userActivity keep-alive for display " + displayId);
        }, "mfa-display-user-activity");
        userActivityThread.setDaemon(true);
        userActivityThread.start();
    }

    private synchronized void stopUserActivityKeepAlive() {
        userActivityKeepAlive = false;
        if (userActivityThread != null) {
            userActivityThread.interrupt();
            userActivityThread = null;
        }
    }

    private void sendUserActivity(int displayId) {
        try {
            Class<?> serviceManager = Class.forName("android.os.ServiceManager");
            IBinder binder = (IBinder) serviceManager
                    .getDeclaredMethod("getService", String.class)
                    .invoke(null, "power");
            if (binder == null)
                return;
            Class<?> stub = Class.forName("android.os.IPowerManager$Stub");
            IInterface manager = (IInterface) stub.getMethod("asInterface", IBinder.class)
                    .invoke(null, binder);
            if (manager == null)
                return;
            long now = SystemClock.uptimeMillis();
            try {
                Method userActivity = manager.getClass().getMethod(
                        "userActivity", int.class, long.class, int.class, int.class);
                userActivity.invoke(manager, displayId, now, 0, 0);
            } catch (NoSuchMethodException ignored) {
                Method userActivity = manager.getClass().getMethod(
                        "userActivity", long.class, int.class, int.class);
                userActivity.invoke(manager, now, 0, 0);
            }
        } catch (Throwable exception) {
            Log.d(TAG, "Display userActivity keep-alive failed", exception);
        }
    }

    private interface DisplayCaptureRouter {
        boolean rebind(int displayId);
    }

    private static final class InputServer extends Thread {
        private static final int MAGIC = 0x4d464142;
        private static final Pattern DISPLAY_HEADER = Pattern.compile(
                "^\\s*Display\\s+#?(\\d+)(?:\\s|\\(|:|$)", Pattern.CASE_INSENSITIVE);
        private static final Pattern DISPLAY_ID = Pattern.compile(
                "(?:displayId|mDisplayId|display-id)\\s*[=:]\\s*(\\d+)", Pattern.CASE_INSENSITIVE);
        private final Context context;
        private final ServerSocket server;
        private final InputController inputController;
        private final DisplayCaptureRouter displayCaptureRouter;
        private volatile int redirectedDisplayId = -1;
        private volatile boolean currentScreenCapture;
        private volatile boolean appWatchdogRunning;
        private volatile int appWatchdogDisplayId = -1;
        private volatile String appWatchdogPackage;
        private volatile String lastControlledPackage;
        private volatile TaskPlacement backgroundForegroundTask;
        private volatile IBinder overlayInputStateCallback;
        private Thread appWatchdogThread;
        private long appWatchdogMissingSince;
        private long appWatchdogLastRecovery;
        private int appWatchdogRecoveryAttempts;

        InputServer(Context context, DisplayCaptureRouter displayCaptureRouter) throws IOException {
            super("MfaBridgeInput");
            this.context = context;
            this.displayCaptureRouter = displayCaptureRouter;
            inputController = new InputController(new ShellContext(context));
            server = new ServerSocket(0, 4, InetAddress.getByName("127.0.0.1"));
            setDaemon(true);
        }

        int getPort() {
            return server.getLocalPort();
        }

        String getLastControlledPackage() {
            return lastControlledPackage;
        }

        void setCurrentScreenCapture(boolean enabled) {
            currentScreenCapture = enabled;
            // A route belongs to one controller/capture lifetime. Carrying it from a
            // stopped background controller into current-screen mode (or vice versa)
            // sends input to the previous Display and can pin the game and MFA into the
            // same MuMu tab.
            redirectedDisplayId = -1;
            if (!enabled)
                setOverlayInputPassthrough(false);
        }

        void setOverlayInputStateCallback(IBinder callback) {
            overlayInputStateCallback = callback;
        }

        private boolean setOverlayInputPassthrough(boolean enabled) {
            // The overlay exists only in current-screen/foreground mode. Virtual-display
            // mode must not pay an IPC round trip or have its input state changed.
            if (!currentScreenCapture && enabled)
                return true;
            IBinder callback = overlayInputStateCallback;
            if (callback == null)
                return !enabled;
            Parcel data = Parcel.obtain();
            Parcel reply = Parcel.obtain();
            try {
                data.writeInt(enabled ? 1 : 0);
                callback.transact(OVERLAY_INPUT_STATE_TRANSACTION, data, reply, 0);
                boolean applied = reply.readInt() != 0;
                if (applied && enabled)
                    syncInputTransactions();
                return applied;
            } catch (Throwable exception) {
                Log.w(TAG, "Unable to switch foreground overlay input passthrough", exception);
                return false;
            } finally {
                reply.recycle();
                data.recycle();
            }
        }

        private void syncInputTransactions() {
            try {
                Class<?> serviceManager = Class.forName("android.os.ServiceManager");
                IBinder binder = (IBinder) serviceManager
                        .getMethod("getService", String.class).invoke(null, "window");
                Class<?> stub = Class.forName("android.view.IWindowManager$Stub");
                Object manager = stub.getMethod("asInterface", IBinder.class)
                        .invoke(null, binder);
                try {
                    manager.getClass().getMethod("syncInputTransactions", boolean.class)
                            .invoke(manager, false);
                } catch (NoSuchMethodException ignored) {
                    manager.getClass().getMethod("syncInputTransactions").invoke(manager);
                }
            } catch (Throwable exception) {
                // The managed callback already waits for two traversals. This hidden API
                // is the stronger ordering guarantee where the vendor exposes it.
                Log.d(TAG, "WindowManager input transaction sync is unavailable", exception);
            }
        }

        void shutdown() {
            stopAppWatchdog();
            try {
                server.close();
            } catch (IOException exception) {
                Log.w(TAG, "Input server close failed", exception);
            }
        }

        synchronized void startAppWatchdog(int displayId) {
            if (displayId < 0)
                return;
            if (appWatchdogRunning && appWatchdogDisplayId == displayId)
                return;
            stopAppWatchdog();
            appWatchdogDisplayId = displayId;
            appWatchdogPackage = null;
            appWatchdogMissingSince = 0;
            appWatchdogLastRecovery = 0;
            appWatchdogRecoveryAttempts = 0;
            appWatchdogRunning = true;
            appWatchdogThread = new Thread(() -> {
                Log.i(TAG, "Game process keep-alive started for display " + displayId);
                while (appWatchdogRunning && appWatchdogDisplayId == displayId) {
                    try {
                        Thread.sleep(5000L);
                    } catch (InterruptedException exception) {
                        if (!appWatchdogRunning)
                            break;
                        Thread.currentThread().interrupt();
                        break;
                    }
                    if (appWatchdogRunning)
                        tickAppWatchdog();
                }
                Log.i(TAG, "Game process keep-alive stopped for display " + displayId);
            }, "mfa-game-process-keep-alive");
            appWatchdogThread.setDaemon(true);
            appWatchdogThread.start();
        }

        synchronized void stopAppWatchdog() {
            appWatchdogRunning = false;
            appWatchdogDisplayId = -1;
            appWatchdogPackage = null;
            appWatchdogMissingSince = 0;
            appWatchdogLastRecovery = 0;
            appWatchdogRecoveryAttempts = 0;
            if (appWatchdogThread != null) {
                appWatchdogThread.interrupt();
                appWatchdogThread = null;
            }
        }

        private void watchStartedPackage(String packageName, int displayId) {
            if (!appWatchdogRunning || appWatchdogDisplayId != displayId)
                return;
            appWatchdogPackage = packageName;
            appWatchdogMissingSince = 0;
            appWatchdogLastRecovery = 0;
            appWatchdogRecoveryAttempts = 0;
            Log.i(TAG, "Game process keep-alive acquired package=" + packageName
                    + ", display=" + displayId);
        }

        private void tickAppWatchdog() {
            int displayId = appWatchdogDisplayId;
            if (displayId < 0)
                return;
            String packageName = appWatchdogPackage;
            if (packageName == null) {
                packageName = findTopPackageOnDisplay(displayId);
                if (packageName == null)
                    return;
                watchStartedPackage(packageName, displayId);
            }

            TaskPlacement placement = findPackageTask(packageName);
            if (placement != null && placement.displayId == displayId) {
                appWatchdogMissingSince = 0;
                appWatchdogRecoveryAttempts = 0;
                return;
            }

            long now = SystemClock.uptimeMillis();
            if (appWatchdogMissingSince == 0) {
                appWatchdogMissingSince = now;
                Log.w(TAG, "Game keep-alive detected package=" + packageName
                        + " outside display=" + displayId + "; allowing a 5s grace period.");
                return;
            }
            if (now - appWatchdogMissingSince < 5000L
                    || now - appWatchdogLastRecovery < 10000L)
                return;

            appWatchdogLastRecovery = now;
            appWatchdogRecoveryAttempts++;
            Intent intent = buildLaunchIntent(packageName);
            if (intent == null || intent.getComponent() == null)
                return;
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK
                    | Intent.FLAG_ACTIVITY_EXCLUDE_FROM_RECENTS);

            boolean recovered;
            if (placement != null && placement.taskId >= 0) {
                Log.w(TAG, "Game task drifted to display=" + placement.displayId
                        + "; moving it back to display=" + displayId
                        + ", attempt=" + appWatchdogRecoveryAttempts);
                recovered = repinTask(intent, placement.taskId, displayId);
            } else {
                Log.w(TAG, "Game task/process disappeared; relaunching package="
                        + packageName + " on display=" + displayId
                        + ", attempt=" + appWatchdogRecoveryAttempts);
                recovered = startActivityAsShell(intent, displayId, true);
            }
            if (recovered) {
                appWatchdogMissingSince = 0;
                Log.i(TAG, "Game keep-alive recovery requested successfully for package="
                        + packageName + ", display=" + displayId);
            }
        }

        @SuppressWarnings("deprecation")
        private String findTopPackageOnDisplay(int displayId) {
            try {
                ActivityManager manager = context.getSystemService(ActivityManager.class);
                if (manager == null)
                    return null;
                for (ActivityManager.RunningTaskInfo task : manager.getRunningTasks(100)) {
                    if (readTaskDisplayId(task) != displayId)
                        continue;
                    ComponentName component = task.topActivity != null
                            ? task.topActivity : task.baseActivity;
                    if (component == null || context.getPackageName().equals(component.getPackageName()))
                        continue;
                    return component.getPackageName();
                }
            } catch (Throwable exception) {
                Log.w(TAG, "Unable to acquire game package on display " + displayId, exception);
            }
            return null;
        }

        @Override
        public void run() {
            while (!server.isClosed()) {
                try (Socket socket = server.accept();
                     DataInputStream input = new DataInputStream(socket.getInputStream());
                     DataOutputStream output = new DataOutputStream(socket.getOutputStream())) {
                    // Keep one bridge connection alive for an entire gesture. The native
                    // side reuses this socket, avoiding a TCP handshake for every MOVE.
                    socket.setTcpNoDelay(true);
                    while (!server.isClosed()) {
                    if (input.readInt() != MAGIC) {
                        break;
                    }

                    int displayId = input.readInt();
                    int method = input.readInt();
                    int x = input.readInt();
                    int y = input.readInt();
                    int key = input.readInt();
                    int textLength = input.readInt();
                    String text = "";
                    if (textLength > 0 && textLength <= 4096) {
                        byte[] bytes = new byte[textLength];
                        input.readFully(bytes);
                        text = new String(bytes, StandardCharsets.UTF_8);
                    }
                    int result = execute(displayId, method, x, y, key, text);
                    output.writeInt(result);
                    output.flush();
                    }
                } catch (IOException exception) {
                    if (!server.isClosed()) {
                        Log.w(TAG, "Input server error", exception);
                    }
                } finally {
                    // A disconnected controller may leave a DOWN without UP. Never leave
                    // the user-facing ball non-touchable after that controller is gone.
                    if (currentScreenCapture)
                        setOverlayInputPassthrough(false);
                }
            }
        }

        private int execute(int displayId, int method, int x, int y, int key, String text) {
            int effectiveDisplayId = redirectedDisplayId >= 0 ? redirectedDisplayId : displayId;
            String display = effectiveDisplayId >= 0 ? "-d " + effectiveDisplayId + " " : "";
            switch (method) {
                case 1:
                    return startApp(displayId, text, key != 0);
                case 2:
                    if (text.equals(appWatchdogPackage))
                        stopAppWatchdog();
                    return forceStopPackage(text) ? 0 : -1;
                case 4:
                    // MAA-Meow does not implement Maa input-text either. Keep the
                    // Android shell route for now and report its real exit status.
                    return runShell("input " + display + "text " + shell(text)).exitCode;
                case 6:
                    boolean foregroundGesture = currentScreenCapture;
                    if (foregroundGesture && !setOverlayInputPassthrough(true)) {
                        setOverlayInputPassthrough(false);
                        return -5;
                    }
                    if (inputController.down(x, y, effectiveDisplayId)) {
                        if (!currentScreenCapture && requiresHostFocusRecovery())
                            restoreFocusedTask(backgroundForegroundTask, effectiveDisplayId);
                        return 0;
                    }
                    Log.w(TAG, "Direct DOWN injection failed: display=" + effectiveDisplayId
                            + ", currentScreen=" + currentScreenCapture);
                    if (!currentScreenCapture)
                        return -5;
                    int downResult = runShell("input " + display + "motionevent DOWN " + x + " " + y).exitCode;
                    if (downResult != 0 && foregroundGesture)
                        setOverlayInputPassthrough(false);
                    return downResult;
                case 7:
                    if (inputController.hasActiveGesture())
                        return inputController.move(x, y, effectiveDisplayId) ? 0 : -4;
                    if (!currentScreenCapture) {
                        Log.w(TAG, "Ignoring shell MOVE fallback in background mode: display="
                                + effectiveDisplayId);
                        return -5;
                    }
                    return runShell("input " + display + "motionevent MOVE " + x + " " + y).exitCode;
                case 8:
                    try {
                        boolean released;
                        if (inputController.hasActiveGesture())
                            released = inputController.up(x, y, effectiveDisplayId);
                        else if (!currentScreenCapture) {
                            Log.w(TAG, "Ignoring shell UP fallback in background mode: display="
                                    + effectiveDisplayId);
                            released = false;
                        } else
                            released = runShell("input " + display + "motionevent UP " + x + " " + y).exitCode == 0;
                        if (released && !currentScreenCapture && requiresHostFocusRecovery())
                            restoreFocusedTask(backgroundForegroundTask, effectiveDisplayId);
                        return released ? 0 : -4;
                    } finally {
                        if (currentScreenCapture)
                            setOverlayInputPassthrough(false);
                    }
                case 9:
                    boolean keyDown = inputController.key(key, KeyEvent.ACTION_DOWN, effectiveDisplayId);
                    return keyDown ? 0 : -4;
                case 10:
                    boolean keyUp = inputController.key(key, KeyEvent.ACTION_UP, effectiveDisplayId);
                    return keyUp ? 0 : -4;
                default:
                    return -2;
            }
        }

        private int startApp(int displayId, String target, boolean forceStop) {
            if (displayId < 0) {
                Log.w(TAG, "StartApp rejected because no virtual display is available.");
                return -3;
            }

            Intent intent = buildLaunchIntent(target);
            ComponentName componentName = intent == null ? null : intent.getComponent();
            if (intent == null || componentName == null) {
                Log.w(TAG, "StartApp could not resolve a launcher activity for " + target
                        + ".");
                return 2;
            }

            String packageName = componentName.getPackageName();
            boolean backgroundVirtualDisplay = !currentScreenCapture;
            TaskPlacement foregroundTask = null;
            if (backgroundVirtualDisplay) {
                String clientPackage = clientPackageName;
                if (clientPackage != null)
                    foregroundTask = findPackageTask(clientPackage);
                if (foregroundTask == null)
                    foregroundTask = getFocusedRootTaskPlacement();
                backgroundForegroundTask = foregroundTask;
            }
            if (forceStop && currentScreenCapture) {
                Log.i(TAG, "Ignoring force_stop for current-screen capture: package="
                        + packageName + ", controllerDisplay=" + displayId);
                forceStop = false;
            }
            Log.i(TAG, "StartApp request: package=" + packageName + ", display="
                    + displayId + ", forceStop=" + forceStop
                    + ", currentScreenCapture=" + currentScreenCapture);

            TaskPlacement existing = findPackageTask(packageName);
            if (forceStop && (!appWatchdogRunning || appWatchdogDisplayId != displayId))
                startAppWatchdog(displayId);
            if (forceStop && !forceStopPackage(packageName))
                return -1;

            if (!backgroundVirtualDisplay && !forceStop
                    && existing != null && existing.displayId >= 0) {
                // Current-screen mode follows the already running game instead of
                // moving its Unity task across MuMu displays. Virtual-display mode
                // arrives here with forceStop=true and retains its isolated process.
                Log.i(TAG, "Following existing game task: package=" + packageName
                        + ", display=" + existing.displayId + ", controllerDisplay="
                        + displayId);
                return activateGameDisplay(packageName, displayId, existing.displayId) ? 0 : -5;
            }

            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_EXCLUDE_FROM_RECENTS);
            // Match MaaFwApp's background launch semantics. launchDisplayId is enough to
            // place the task on the virtual display; MULTIPLE_TASK may make emulator
            // launchers (notably MuMu) expose a second foreground game task as well.
            // Background mode must always target its isolated virtual display. Whether
            // Maa requested force_stop only controls process lifetime; it must not
            // decide display placement. Current-screen mode intentionally launches on
            // the ordinary app display and follows the task MuMu creates for the game.
            int launchDisplayId = backgroundVirtualDisplay ? displayId : -1;
            // MaaFwApp forces a fullscreen task on its isolated display. Without the
            // windowing-mode hint MuMu's TaskTab bridge treats the launch as a normal
            // host-tab transition and publishes a game tab immediately.
            boolean started = startActivityAsShell(intent, launchDisplayId,
                    backgroundVirtualDisplay);
            if (!started) {
                String displayArgument = launchDisplayId >= 0
                        ? " --display " + launchDisplayId : "";
                ShellResult fallback = runShell("am start -W" + displayArgument
                        + " -n " + shell(componentName.flattenToShortString()));
                if (fallback.exitCode != 0)
                    return fallback.exitCode;
            }

            TaskPlacement placement = null;
            for (int attempt = 0; attempt < 20; attempt++) {
                placement = findPackageTask(packageName, displayId);
                if (placement != null) {
                    if (!backgroundVirtualDisplay && !forceStop
                            && placement.displayId >= 0)
                        return activateGameDisplay(
                                packageName, displayId, placement.displayId) ? 0 : -5;
                    if (placement.displayId == displayId)
                        return activateBackgroundGameDisplay(
                                packageName, displayId, placement.displayId,
                                foregroundTask) ? 0 : -5;
                    Log.w(TAG, "StartApp placed " + packageName + " on display "
                            + placement.displayId + " instead of " + displayId
                            + "; attempting to move task " + placement.taskId + " back.");
                    if (backgroundVirtualDisplay
                            && repinTask(intent, placement.taskId, displayId))
                        return activateBackgroundGameDisplay(
                                packageName, displayId, displayId,
                                foregroundTask) ? 0 : -5;
                    break;
                }
                try {
                    Thread.sleep(500);
                } catch (InterruptedException exception) {
                    Thread.currentThread().interrupt();
                    return -1;
                }
            }

            Log.e(TAG, "StartApp did not place " + packageName + " on virtual display "
                    + displayId + "; placement=" + (placement == null
                    ? "not-found"
                    : "display-" + placement.displayId) + ". Stopping the misplaced app.");
            if (forceStop)
                forceStopPackage(packageName);
            return placement == null ? 4 : 3;
        }

        private boolean activateGameDisplay(
                String packageName, int controllerDisplayId, int gameDisplayId) {
            if (gameDisplayId < 0)
                return false;
            if (!displayCaptureRouter.rebind(gameDisplayId))
                return false;
            redirectedDisplayId = gameDisplayId == controllerDisplayId ? -1 : gameDisplayId;
            lastControlledPackage = packageName;
            watchStartedPackage(packageName, gameDisplayId);
            Log.i(TAG, "Controller display route activated: controller=" + controllerDisplayId
                    + ", game=" + gameDisplayId);
            return true;
        }

        private boolean activateBackgroundGameDisplay(
                String packageName, int controllerDisplayId, int gameDisplayId,
                TaskPlacement foregroundTask) {
            if (!activateGameDisplay(packageName, controllerDisplayId, gameDisplayId))
                return false;
            // Flags are only a request; MuMu may accept them while still focusing the
            // newly opened game task. Verify the actual global focus and recover the
            // host task whenever the game has taken it, regardless of backend/flags.
            restoreHostFocusIfNeeded(foregroundTask, packageName, gameDisplayId);
            return true;
        }

        private void restoreHostFocusIfNeeded(
                TaskPlacement hostTask, String gamePackage, int gameDisplayId) {
            if (hostTask == null || hostTask.taskId < 0)
                return;
            TaskPlacement focused = getFocusedRootTaskPlacement();
            boolean gameHasFocus = focused != null && gamePackage != null
                    && gamePackage.equals(focused.packageName);
            if (requiresHostFocusRecovery() || gameHasFocus) {
                restoreFocusedTask(hostTask, gameDisplayId);
                // MuMu updates TaskTab/focus asynchronously after OPEN. Check several
                // times so a late focus notification cannot leave the game tab visible
                // on the host. The first check is intentionally after the usual
                // TaskTab NEW_TASK broadcast (roughly 150ms on MuMu).
                final TaskPlacement delayedHostTask = hostTask;
                final String delayedGamePackage = gamePackage;
                new Thread(() -> {
                    try {
                        int[] delays = { 100, 350, 800 };
                        for (int delay : delays) {
                            Thread.sleep(delay);
                            TaskPlacement lateFocused = getFocusedRootTaskPlacement();
                            boolean lateGameFocused = lateFocused != null
                                    && delayedGamePackage != null
                                    && delayedGamePackage.equals(lateFocused.packageName);
                            if (requiresHostFocusRecovery() || lateGameFocused)
                                restoreFocusedTask(delayedHostTask, gameDisplayId);
                        }
                    } catch (InterruptedException interrupted) {
                        Thread.currentThread().interrupt();
                    }
                }, "mfa-focus-recovery").start();
            }
        }

        private TaskPlacement getFocusedRootTaskPlacement() {
            try {
                Class<?> activityTaskManager = Class.forName("android.app.ActivityTaskManager");
                Object service = activityTaskManager.getDeclaredMethod("getService").invoke(null);
                Class<?> serviceInterface = Class.forName("android.app.IActivityTaskManager");
                Method method = serviceInterface.getDeclaredMethod("getFocusedRootTaskInfo");
                method.setAccessible(true);
                Object value = method.invoke(service);
                if (value instanceof ActivityManager.RunningTaskInfo) {
                    ActivityManager.RunningTaskInfo task =
                            (ActivityManager.RunningTaskInfo) value;
                    ComponentName component = task.topActivity != null
                            ? task.topActivity : task.baseActivity;
                    int taskId = readIntField(task, "taskId");
                    int displayId = readTaskDisplayId(task);
                    if (taskId >= 0 && displayId >= 0)
                        return new TaskPlacement(taskId, displayId,
                                component == null ? null : component.getPackageName());
                }
            } catch (Throwable exception) {
                Log.w(TAG, "Unable to remember the foreground task before background launch",
                        exception);
            }
            return null;
        }

        private void restoreFocusedTask(TaskPlacement task, int gameDisplayId) {
            if (task == null || task.taskId < 0 || task.displayId == gameDisplayId)
                return;
            try {
                Class<?> activityTaskManager = Class.forName("android.app.ActivityTaskManager");
                Object service = activityTaskManager.getDeclaredMethod("getService").invoke(null);
                Class<?> serviceInterface = Class.forName("android.app.IActivityTaskManager");
                Method method = serviceInterface.getDeclaredMethod("setFocusedTask", int.class);
                method.setAccessible(true);
                method.invoke(service, task.taskId);
                Log.i(TAG, "Restored foreground task after virtual-display launch: task="
                        + task.taskId + ", display=" + task.displayId + ", package="
                        + task.packageName + ", backgroundDisplay=" + gameDisplayId);
            } catch (Throwable exception) {
                Log.w(TAG, "Unable to restore foreground task after background launch",
                        exception);
            }
        }

        private boolean forceStopPackage(String packageName) {
            try {
                Class<?> activityManagerNative = Class.forName("android.app.ActivityManagerNative");
                IInterface manager = (IInterface) activityManagerNative
                        .getDeclaredMethod("getDefault")
                        .invoke(null);
                Method forceStop = manager.getClass().getMethod(
                        "forceStopPackage", String.class, int.class);
                forceStop.invoke(manager, packageName, -2);
                Log.i(TAG, "forceStopPackage succeeded for " + packageName);
                return true;
            } catch (Throwable exception) {
                Log.w(TAG, "Hidden forceStopPackage failed; using shell fallback", exception);
                return runShell("am force-stop " + shell(packageName)).exitCode == 0;
            }
        }

        private Intent buildLaunchIntent(String target) {
            if (target.contains("/")) {
                ComponentName component = ComponentName.unflattenFromString(target);
                return component == null ? null : new Intent(Intent.ACTION_MAIN).setComponent(component);
            }
            Intent intent = context.getPackageManager().getLaunchIntentForPackage(target);
            return intent != null ? intent : context.getPackageManager().getLeanbackLaunchIntentForPackage(target);
        }

        private boolean startActivityAsShell(Intent intent, int displayId, boolean forceFullscreen) {
            try {
                ActivityOptions options = displayId >= 0 ? ActivityOptions.makeBasic() : null;
                if (options != null)
                    options.setLaunchDisplayId(displayId);
                if (options != null && forceFullscreen) {
                    Method setWindowingMode = ActivityOptions.class.getDeclaredMethod(
                            "setLaunchWindowingMode", int.class);
                    setWindowingMode.setAccessible(true);
                    setWindowingMode.invoke(options, 1);
                }

                Class<?> activityManagerNative = Class.forName("android.app.ActivityManagerNative");
                IInterface manager = (IInterface) activityManagerNative
                        .getDeclaredMethod("getDefault")
                        .invoke(null);
                Class<?> applicationThread = Class.forName("android.app.IApplicationThread");
                Class<?> profilerInfo = Class.forName("android.app.ProfilerInfo");
                Method startActivity = manager.getClass().getMethod(
                        "startActivityAsUser",
                        applicationThread, String.class, Intent.class, String.class,
                        IBinder.class, String.class, int.class, int.class,
                        profilerInfo, Bundle.class, int.class);
                int result = (int) startActivity.invoke(
                        manager, null, "com.android.shell", intent, null, null, null,
                        0, 0, null, options == null ? null : options.toBundle(), -2);
                Log.i(TAG, "startActivityAsUser result=" + result + ", display="
                        + (displayId >= 0 ? Integer.toString(displayId) : "default")
                        + ", component=" + intent.getComponent());
                return result >= 0;
            } catch (Throwable exception) {
                Log.w(TAG, "startActivityAsUser failed", exception);
                return false;
            }
        }

        @SuppressWarnings("deprecation")
        private TaskPlacement findPackageTask(String packageName) {
            return findPackageTask(packageName, -1);
        }

        @SuppressWarnings("deprecation")
        private TaskPlacement findPackageTask(String packageName, int preferredDisplayId) {
            try {
                ActivityManager activityManager = context.getSystemService(ActivityManager.class);
                if (activityManager == null)
                    return findPackageTaskFromDump(packageName);
                List<ActivityManager.RunningTaskInfo> tasks = activityManager.getRunningTasks(100);
                TaskPlacement fallback = null;
                for (ActivityManager.RunningTaskInfo task : tasks) {
                    ComponentName top = task.topActivity;
                    ComponentName base = task.baseActivity;
                    if ((top != null && packageName.equals(top.getPackageName()))
                            || (base != null && packageName.equals(base.getPackageName()))) {
                        TaskPlacement placement = new TaskPlacement(task.id, readTaskDisplayId(task));
                        if (preferredDisplayId < 0 || placement.displayId == preferredDisplayId)
                            return placement;
                        if (fallback == null)
                            fallback = placement;
                    }
                }
                if (fallback != null)
                    return fallback;
            } catch (Throwable exception) {
                Log.w(TAG, "getRunningTasks failed; using dumpsys fallback", exception);
            }
            return findPackageTaskFromDump(packageName);
        }

        private int readTaskDisplayId(ActivityManager.RunningTaskInfo task) {
            try {
                Class<?> type = task.getClass();
                while (type != null) {
                    try {
                        java.lang.reflect.Field field = type.getDeclaredField("displayId");
                        field.setAccessible(true);
                        return field.getInt(task);
                    } catch (NoSuchFieldException ignored) {
                        type = type.getSuperclass();
                    }
                }
            } catch (Throwable exception) {
                Log.w(TAG, "RunningTaskInfo.displayId is unavailable", exception);
            }
            return -1;
        }

        private TaskPlacement findPackageTaskFromDump(String packageName) {
            ShellResult dump = runShell("dumpsys activity activities");
            if (dump.exitCode != 0)
                return null;
            String[] lines = dump.output.split("\\r?\\n");
            int currentDisplayId = -1;
            for (int index = 0; index < lines.length; index++) {
                Matcher header = DISPLAY_HEADER.matcher(lines[index]);
                if (header.find())
                    currentDisplayId = Integer.parseInt(header.group(1));
                if (!lines[index].contains(packageName))
                    continue;
                int displayId = findNearbyInt(lines, index, DISPLAY_ID, currentDisplayId);
                Matcher taskId = Pattern.compile("(?:taskId|Task|task)\\s*[#=]?\\s*(\\d+)")
                        .matcher(lines[index]);
                return new TaskPlacement(taskId.find() ? Integer.parseInt(taskId.group(1)) : -1, displayId);
            }
            return null;
        }

        private static int findNearbyInt(
                String[] lines, int center, Pattern pattern, int fallback) {
            int first = Math.max(0, center - 3);
            int last = Math.min(lines.length - 1, center + 3);
            for (int index = first; index <= last; index++) {
                Matcher matcher = pattern.matcher(lines[index]);
                if (matcher.find())
                    return Integer.parseInt(matcher.group(1));
            }
            return fallback;
        }

        private boolean repinTask(Intent intent, int taskId, int displayId) {
            if (taskId >= 0 && moveTaskToDisplay(taskId, displayId)) {
                sleepForTaskMove();
                TaskPlacement moved = findPackageTask(intent.getComponent().getPackageName());
                if (moved != null && moved.displayId == displayId)
                    return true;
            }
            if (startActivityAsShell(intent, displayId, true)) {
                sleepForTaskMove();
                TaskPlacement relaunched = findPackageTask(intent.getComponent().getPackageName());
                return relaunched != null && relaunched.displayId == displayId;
            }
            return false;
        }

        private boolean moveTaskToDisplay(int taskId, int displayId) {
            try {
                Class<?> serviceManager = Class.forName("android.os.ServiceManager");
                IBinder binder = (IBinder) serviceManager
                        .getDeclaredMethod("getService", String.class)
                        .invoke(null, "activity_task");
                Class<?> stub = Class.forName("android.app.IActivityTaskManager$Stub");
                Object manager = stub.getMethod("asInterface", IBinder.class).invoke(null, binder);
                for (String name : new String[] { "moveRootTaskToDisplay", "moveStackToDisplay" }) {
                    try {
                        Method move = manager.getClass().getMethod(name, int.class, int.class);
                        move.invoke(manager, taskId, displayId);
                        Log.i(TAG, name + "(" + taskId + ", " + displayId + ") succeeded.");
                        return true;
                    } catch (NoSuchMethodException ignored) {
                    }
                }
            } catch (Throwable exception) {
                Log.w(TAG, "Hidden task move API failed", exception);
            }
            ShellResult fallback = runShell(
                    "am display move-stack " + taskId + " " + displayId);
            return fallback.exitCode == 0;
        }

        private static void sleepForTaskMove() {
            try {
                Thread.sleep(1000);
            } catch (InterruptedException exception) {
                Thread.currentThread().interrupt();
            }
        }

        private static ShellResult runShell(String command) {
            Log.d(TAG, "shell input: " + command);
            try {
                java.lang.Process process = new ProcessBuilder("sh", "-c", command)
                        .redirectErrorStream(true)
                        .start();
                String commandOutput = readOutput(process.getInputStream());
                int exitCode = process.waitFor();
                Log.i(TAG, "Input command finished, exit=" + exitCode
                        + (commandOutput.isEmpty() ? "" : ", output=" + commandOutput));
                if (exitCode != 0) {
                    Log.w(TAG, "Input command exited with code " + exitCode);
                }
                return new ShellResult(exitCode, commandOutput);
            } catch (IOException | InterruptedException exception) {
                if (exception instanceof InterruptedException) {
                    Thread.currentThread().interrupt();
                }
                Log.w(TAG, "Input command failed", exception);
                return new ShellResult(-1, exception.getMessage() == null
                        ? exception.getClass().getSimpleName()
                        : exception.getMessage());
            }
        }

        private static final class ShellResult {
            final int exitCode;
            final String output;

            ShellResult(int exitCode, String output) {
                this.exitCode = exitCode;
                this.output = output;
            }
        }

        private static final class TaskPlacement {
            final int taskId;
            final int displayId;
            final String packageName;

            TaskPlacement(int taskId, int displayId) {
                this(taskId, displayId, null);
            }

            TaskPlacement(int taskId, int displayId, String packageName) {
                this.taskId = taskId;
                this.displayId = displayId;
                this.packageName = packageName;
            }
        }

        private static final class InputController {
            private static final int INJECT_ASYNC = 0;
            private static final int INJECT_WAIT_FOR_FINISH = 2;
            private static final int DEVICE_ID = 0;
            private static final int SOURCE = InputDevice.SOURCE_TOUCHSCREEN;

            private final Object manager;
            private Method injectInputEvent;
            private Method setDisplayId;
            private long currentDownTime;
            private int currentDisplayId = -1;

            InputController(Context context) {
                manager = context.getSystemService(Context.INPUT_SERVICE);
            }

            synchronized boolean down(int x, int y, int displayId) {
                if (currentDownTime != 0) {
                    MotionEvent cancel = obtainMotionEvent(
                            currentDownTime, SystemClock.uptimeMillis(),
                            MotionEvent.ACTION_CANCEL, x, y, 0f);
                    inject(cancel, currentDisplayId, INJECT_ASYNC);
                }
                currentDownTime = SystemClock.uptimeMillis();
                currentDisplayId = displayId;
                MotionEvent event = obtainMotionEvent(
                        currentDownTime, currentDownTime,
                        MotionEvent.ACTION_DOWN, x, y, 1f);
                boolean result = inject(event, displayId, INJECT_WAIT_FOR_FINISH);
                if (!result) {
                    currentDownTime = 0;
                    currentDisplayId = -1;
                }
                return result;
            }

            synchronized boolean move(int x, int y, int displayId) {
                if (currentDownTime == 0 || currentDisplayId != displayId)
                    return false;
                MotionEvent event = obtainMotionEvent(
                        currentDownTime, SystemClock.uptimeMillis(),
                        MotionEvent.ACTION_MOVE, x, y, 1f);
                return inject(event, displayId, INJECT_ASYNC);
            }

            synchronized boolean hasActiveGesture() {
                return currentDownTime != 0;
            }

            synchronized boolean up(int x, int y, int displayId) {
                if (currentDownTime == 0 || currentDisplayId != displayId)
                    return false;
                MotionEvent event = obtainMotionEvent(
                        currentDownTime, SystemClock.uptimeMillis(),
                        MotionEvent.ACTION_UP, x, y, 0f);
                boolean result = inject(event, displayId, INJECT_ASYNC);
                currentDownTime = 0;
                currentDisplayId = -1;
                return result;
            }

            boolean key(int keyCode, int action, int displayId) {
                long eventTime = SystemClock.uptimeMillis();
                KeyEvent event = new KeyEvent(
                        eventTime, eventTime, action, keyCode, 0);
                return inject(event, displayId,
                        action == KeyEvent.ACTION_DOWN
                                ? INJECT_WAIT_FOR_FINISH
                                : INJECT_ASYNC);
            }

            private MotionEvent obtainMotionEvent(
                    long downTime, long eventTime, int action,
                    float x, float y, float pressure) {
                MotionEvent.PointerProperties properties = new MotionEvent.PointerProperties();
                properties.id = 0;
                properties.toolType = MotionEvent.TOOL_TYPE_FINGER;
                MotionEvent.PointerCoords coordinates = new MotionEvent.PointerCoords();
                coordinates.x = Math.max(0, x);
                coordinates.y = Math.max(0, y);
                coordinates.pressure = pressure;
                coordinates.size = 1f;
                return MotionEvent.obtain(
                        downTime, eventTime, action,
                        1,
                        new MotionEvent.PointerProperties[] { properties },
                        new MotionEvent.PointerCoords[] { coordinates },
                        0, 0, 1f, 1f,
                        DEVICE_ID, 0, SOURCE, 0);
            }

            private boolean inject(InputEvent event, int displayId, int mode) {
                try {
                    if (manager == null)
                        return false;
                    if (displayId != 0) {
                        if (setDisplayId == null)
                            setDisplayId = InputEvent.class.getMethod("setDisplayId", int.class);
                        setDisplayId.invoke(event, displayId);
                    }
                    if (injectInputEvent == null) {
                        injectInputEvent = manager.getClass().getMethod(
                                "injectInputEvent", InputEvent.class, int.class);
                    }
                    Object result = injectInputEvent.invoke(manager, event, mode);
                    return result instanceof Boolean && (Boolean) result;
                } catch (Throwable exception) {
                    Log.w(TAG, "Direct input injection failed for display " + displayId,
                            exception);
                    return false;
                } finally {
                    if (event instanceof MotionEvent)
                        ((MotionEvent) event).recycle();
                }
            }
        }

        private static String readOutput(InputStream stream) throws IOException {
            byte[] buffer = new byte[1024];
            ByteArrayOutputStream output = new ByteArrayOutputStream();
            int read;
            while ((read = stream.read(buffer)) >= 0) {
                // ActivityManager dumps can easily exceed the old 8 KiB cap before
                // reaching a secondary display. Keep enough data to validate the
                // target task while still bounding memory in the privileged service.
                if (output.size() < 1024 * 1024) {
                    output.write(buffer, 0, Math.min(read, 1024 * 1024 - output.size()));
                }
            }
            return output.toString(StandardCharsets.UTF_8.name()).trim();
        }

        private static String shell(String value) {
            return "'" + value.replace("'", "'\\''") + "'";
        }
    }
}
