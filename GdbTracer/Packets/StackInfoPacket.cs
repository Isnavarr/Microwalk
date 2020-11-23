using System.Runtime.InteropServices;

namespace GdbTracer.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct StackInfoPacket
    {
        public readonly ulong BaseAddress;
        public readonly ulong TopAddress;
    }
}