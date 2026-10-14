package com.fox.MFAAvalonia;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;
import android.os.Binder;
import android.os.Bundle;
import android.os.IBinder;
import android.os.Process;
import android.util.Log;

public final class MfaShellServiceProvider extends ContentProvider {
    private static volatile IBinder service;
    private static final IBinder appLifecycle = new Binder();

    @Override public boolean onCreate() { return true; }

    @Override
    public Bundle call(String method, String arg, Bundle extras) {
        Bundle result = new Bundle();
        if ("attach".equals(method)) {
            if (Binder.getCallingUid() != Process.SHELL_UID) {
                Log.w("MFAAvalonia", "Rejected helper binder from uid=" + Binder.getCallingUid());
                result.putBoolean("accepted", false);
                return result;
            }
            service = extras == null ? null : extras.getBinder("service");
            result.putBoolean("accepted", service != null);
            result.putBinder("lifecycle", appLifecycle);
            return result;
        }
        if ("take".equals(method)) {
            if (getContext() == null
                    || Binder.getCallingUid() != getContext().getApplicationInfo().uid)
                throw new SecurityException("Only the MFA application may acquire the helper binder");
            if (service != null && service.isBinderAlive())
                result.putBinder("service", service);
            return result;
        }
        return super.call(method, arg, extras);
    }

    @Override public String getType(Uri uri) { return null; }
    @Override public Cursor query(Uri uri, String[] p, String s, String[] a, String o) { return null; }
    @Override public Uri insert(Uri uri, ContentValues values) { return null; }
    @Override public int delete(Uri uri, String s, String[] a) { return 0; }
    @Override public int update(Uri uri, ContentValues v, String s, String[] a) { return 0; }
}
