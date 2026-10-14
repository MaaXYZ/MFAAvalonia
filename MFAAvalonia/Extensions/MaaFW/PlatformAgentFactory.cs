using System;
using System.Collections.Generic;
using MaaFramework.Binding;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// Describes an Agent launch that must be handled by the current platform instead of
/// <see cref="System.Diagnostics.Process"/>.
/// </summary>
public sealed record PlatformAgentStartRequest(
    string Identifier,
    string Program,
    IReadOnlyList<string> Arguments,
    string DataRoot,
    string InstanceId,
    string InstanceName,
    Action<string> Output);

/// <summary>
/// Represents the lifetime of a platform-hosted Agent, for example a python-for-android service.
/// </summary>
public interface IPlatformAgentSession : IDisposable
{
}

/// <summary>
/// Optional startup state exposed by an out-of-process platform Agent.  Android uses
/// this because a python-for-android service is not a child Process and therefore has
/// no Process.Exited/stderr channel for the shared Agent startup code to observe.
/// </summary>
public interface IPlatformAgentStartupStatus
{
    string? StartupFailure { get; }

    void CompleteStartup();
}

/// <summary>
/// Optional platform hook for launching an Agent without creating a desktop child process.
/// Desktop does not register this hook and continues to use the existing process launcher.
/// </summary>
public static class PlatformAgentFactory
{
    public static Func<PlatformAgentStartRequest, IPlatformAgentSession?>? Start { get; set; }

    /// <summary>
    /// Optional platform-safe Agent connection entry point. Android uses a native
    /// exception boundary because some vendor kernels can interrupt ZeroMQ polling.
    /// Desktop leaves this unset and continues to call the binding directly.
    /// </summary>
    public static Func<MaaAgentClient, bool>? LinkStart { get; set; }

    /// <summary>
    /// Optional platform-safe Agent disconnect entry point. Android keeps the
    /// ZeroMQ call behind the same native exception boundary used by LinkStart.
    /// </summary>
    public static Func<MaaAgentClient, bool>? LinkStop { get; set; }
}
