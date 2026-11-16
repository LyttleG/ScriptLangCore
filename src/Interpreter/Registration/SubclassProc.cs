namespace ScriptLangCore;

/// <summary>
/// The callback comctl32 expects from SetWindowSubclass. It lives at file scope on purpose: the field that holds it
/// is on the Interpreter class and the installer is on Control, both in Ui.cs, and a declaration nested in one of
/// them is not visible to the other (measured twice: an in-class declaration failed to compile the field).
///
/// Why comctl32's own API rather than SetWindowLongPtr(GWLP_WNDPROC): replacing the window proc of a v6 common
/// control makes comctl32 lose the control's character mode. Measured with cdb on a TreeView, a TVM_EXPAND (a message
/// that carries no string) reached comctl32!TV_WndProc, which fell into comctl32!ProduceWFromA (an ANSI to wide
/// conversion) with a garbage code page of 0x7FFFFFFF and a null source pointer, and killed the process with
/// 0xC0000005. DefSubclassProc chains the way the control expects, so the mode survives.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate nint SubclassProcDelegate(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);
