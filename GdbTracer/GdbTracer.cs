using System;
using GdbRspClientLibrary;

namespace GdbTracer
{
    /// <summary>
    /// GDB tracer controller class.
    /// </summary>
    /// <typeparam name="TDebugger">The <see cref="Debugger"/> type used for tracing.</typeparam>
    public class GdbTracer<TDebugger> where TDebugger : Debugger
    {
        private readonly TDebugger _debugger;
        /* Required features:
         Identify begin and end of testcase
         Identify begin and end of images
         Identify begin and end of stack
         Track allocations
         Optional: Intercept RDRAND?
         Trace:
           Branches
           Memory accesses
         */

        /// <summary>
        /// Creates a new GDB tracer with the given debugger instance.
        /// </summary>
        /// <param name="debugger">The debugger which is used for tracing.</param>
        public GdbTracer(TDebugger debugger)
        {
            _debugger = debugger ?? throw new ArgumentNullException(nameof(debugger));
        }
        
        
    }
}