using System;
using System.Collections.Generic;
using System.Text;

#if !NET5_0_OR_GREATER 
namespace System.Runtime.CompilerServices
{
    internal sealed class IsExternalInit { }
}
#endif
