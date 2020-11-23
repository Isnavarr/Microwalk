using System.Runtime.InteropServices;

namespace GdbTracer.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TestcaseRequestPacket
    {
        public readonly ulong Address;
        public readonly long BufferSize;
        public long TestcaseSize;
    }
}