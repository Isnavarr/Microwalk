﻿using System.Runtime.InteropServices;

namespace GdbTracer.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct StepoverEndPacket
    {
    }
}