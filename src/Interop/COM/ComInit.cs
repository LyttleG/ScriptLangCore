namespace DispatchVtbl;

/// <summary>
/// Manages COM/OLE apartment initialization and shutdown for the current thread.
/// </summary>
/// <remarks>
/// Hosting ActiveX controls (OleSite / DoVerb) requires OLE initialization
/// (<c>OleInitialize</c>), which is a superset of <c>CoInitializeEx(STA)</c>.
/// The runtime often pre-initializes COM on the main thread via
/// <c>[STAThread]</c>; in that case <c>OleInitialize</c> still must be called
/// (it returns S_FALSE when COM is already initialized, but performs the OLE
/// initialization), so this class always ensures OLE is initialized on first
/// use, and only uninitializes when it performed the top-level init itself.
/// </remarks>
internal static class ComInit
{
    [ThreadStatic]
    private static bool _oleInitialized;

    /// <summary>True when this class performed the top-level OleInitialize and must
    /// call OleUninitialize on shutdown.</summary>
    [ThreadStatic]
    private static bool _ownsInitialization;

    /// <summary>
    /// Ensures COM/OLE is initialized for the calling thread as STA.
    /// Throws if the thread is in an incompatible apartment (MTA).
    /// </summary>
    internal static void EnsureInitialized()
    {
        if (_oleInitialized)
            return;

        var aptHr = NativeMethods.CoGetApartmentType(out var aptType, out _);
        switch (aptHr)
        {
            case 0 when aptType != ComConst.APTTYPE_STA && aptType != ComConst.APTTYPE_MAINSTA:
                throw new InvalidOperationException(
                    "COM Automation requires an STA thread. " +
                    "Ensure [STAThread] is on the entry point or create a dedicated STA thread.");

            case 0:
                // COM already initialized and is STA (e.g. by [STAThread] Main).
                // Still initialize OLE, hosting ActiveX controls needs it.
                // Returns S_FALSE when COM was already initialized; OLE state is set up.
                var alreadyHr = NativeMethods.OleInitialize(IntPtr.Zero);
                if (alreadyHr < 0)
                    throw new COMException("OleInitialize failed", alreadyHr);
                _oleInitialized = true;
                _ownsInitialization = false;
                return;

            case ComConst.CO_E_NOTINITIALIZED:
                {
                    // Thread has no COM, initialize as OLE/STA (OleInitialize is a superset
                    // of CoInitializeEx(STA) and enables OLE hosting of ActiveX controls).
                    var hr = NativeMethods.OleInitialize(IntPtr.Zero);
                    if (hr < 0)
                        throw new COMException("OleInitialize failed", hr);
                    _oleInitialized = true;
                    _ownsInitialization = true;
                    return;
                }

            default:
                throw new COMException("CoGetApartmentType failed", aptHr);
        }
    }

    /// <summary>
    /// Shuts down OLE on the current thread, but only if this class was the one
    /// that performed the top-level initialization. If OLE/COM was already
    /// initialized before our first call, shutdown is a no-op (the runtime owns
    /// cleanup and uninitializing would tear down the STA apartment for others).
    /// </summary>
    internal static void Shutdown(bool force = false)
    {
        if (_oleInitialized && (_ownsInitialization || force))
        {
            NativeMethods.OleUninitialize();
            _oleInitialized = false;
            _ownsInitialization = false;
        }
    }
}
