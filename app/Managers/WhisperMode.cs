using System;
using System.Runtime.InteropServices;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// The NVAPI "whisper mode" client request: <c>NvAPI_NvToppsJpacSetControl</c> (id 0xD2561B69).
///
/// This does **not** set the power cap. The enforced ceiling comes from the EC operating mode, and a
/// byte-faithful replay of these three calls left it at 115 W (TGP-NOTES §8). What the request does
/// is tell nvtopps that a client asked for WhisperMode, which is what the NVIDIA App reads before it
/// offers its WhisperMode FPS controls. Without it those controls are greyed out even though the
/// platform is genuinely sitting in Quiet at 80 W.
///
/// Protocol and values come from observed NitroSense behaviour, not from guesswork: enabling sends
/// ctl 0 = 1, ctl 1 = 1 (Quiet), ctl 2 = 45 (minimum fps); disabling sends ctl 0 = 0. NitroSense's
/// own native addon does exactly this from its own process.
///
/// Every call is best-effort. A machine with no NVIDIA GPU, an older driver, or a missing entry
/// point simply gets nothing: this must never fail a power-mode change.
/// </summary>
internal static class WhisperMode
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdSetControl = 0xD2561B69;

    private const int StructSize = 0x4C8;
    private const uint StructVersion = 0x000104C8;
    private const uint ModeQuiet = 1;
    private const uint MinFps = 45;

    private static readonly object Gate = new();

    private static SetControlFn? _setControl;
    private static bool _resolved;
    private static bool _unavailable;
    private static bool? _lastRequested;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterfaceFn(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetControlFn(byte[] control);

    /// <summary>
    /// Asks nvtopps to treat WhisperMode as requested, or to stop. Safe to call on every mode change:
    /// the request is only sent when the state actually changes, and never throws.
    /// </summary>
    public static void Apply(bool enable)
    {
        lock (Gate)
        {
            if (_lastRequested == enable) return;

            if (_unavailable) return;
            if (!Resolve()) return;

            try
            {
                var ok = enable
                    // The order matters: enable first, then the profile the client wants, then the
                    // frame target. This is the sequence NitroSense sends.
                    ? Send(0, 1) && Send(1, ModeQuiet) && Send(2, MinFps)
                    : Send(0, 0);

                if (!ok)
                {
                    // Record nothing: the dashboard shows this flag, and it must not claim a state
                    // the driver refused.
                    Logger.Warn($"Whisper mode: the driver refused the {(enable ? "enable" : "release")} request.");
                    return;
                }

                _lastRequested = enable;
                SettingsManager.Save("WhisperActive", enable ? 1 : 0);
                Logger.Info($"Whisper mode: {(enable ? "requested" : "released")} via NVAPI.");
            }
            catch (Exception ex)
            {
                _unavailable = true;
                Logger.Warn("Whisper mode: NVAPI request failed; the NVIDIA App will not offer it.", ex);
            }
        }
    }

    private static bool Send(uint control, uint value)
    {
        var buffer = new byte[StructSize];
        BitConverter.GetBytes(StructVersion).CopyTo(buffer, 0x00);
        BitConverter.GetBytes(1u).CopyTo(buffer, 0x04);
        BitConverter.GetBytes(control).CopyTo(buffer, 0x48);
        BitConverter.GetBytes(value).CopyTo(buffer, 0x4C);

        var rc = _setControl!(buffer);
        if (rc != 0)
        {
            Logger.Warn($"Whisper mode: set control {control} = {value} returned {rc}.");
            return false;
        }

        return true;
    }

    /// <summary>Resolves the NVAPI entry point once. Returns false when this machine cannot do it.</summary>
    private static bool Resolve()
    {
        if (_resolved) return _setControl != null;
        _resolved = true;

        try
        {
            var nvapi = NativeLibrary.Load("nvapi64.dll");
            var queryPtr = NativeLibrary.GetExport(nvapi, "nvapi_QueryInterface");
            var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(queryPtr);

            var initPtr = query(IdInitialize);
            var setPtr = query(IdSetControl);
            if (initPtr == IntPtr.Zero || setPtr == IntPtr.Zero)
            {
                _unavailable = true;
                Logger.Info("Whisper mode: NVAPI does not expose NvToppsJpacSetControl on this driver.");
                return false;
            }

            var init = Marshal.GetDelegateForFunctionPointer<InitFn>(initPtr);
            var initResult = init();
            if (initResult != 0)
            {
                _unavailable = true;
                Logger.Info($"Whisper mode: NvAPI_Initialize returned {initResult}; skipping.");
                return false;
            }

            _setControl = Marshal.GetDelegateForFunctionPointer<SetControlFn>(setPtr);
            return true;
        }
        catch (Exception ex)
        {
            _unavailable = true;
            Logger.Info($"Whisper mode: NVAPI unavailable ({ex.GetType().Name}); skipping.");
            return false;
        }
    }
}
