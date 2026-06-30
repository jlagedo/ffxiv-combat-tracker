#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    // Lets C# init-only setters / records compile on .NET Framework 4.8, which lacks this
    // marker type. net10 supplies it in-box. Internal so it never leaks onto the contract.
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
#endif
